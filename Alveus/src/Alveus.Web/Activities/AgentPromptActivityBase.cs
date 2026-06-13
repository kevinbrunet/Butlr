using System.Text.Json;
using Alveus.Web.Agents;
using Alveus.Web.Tools;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Alveus.Web.Activities;

/// <summary>
/// Base commune à <see cref="RunAgentPrompt"/> et <see cref="RunEvaluatorPrompt"/> : envoie
/// <see cref="Prompt"/> à l'agent désigné par <see cref="AgentName"/> (résolu via les services à
/// clé enregistrés dans <c>Program.cs</c>), relance l'agent tant qu'il n'a pas appelé
/// <see cref="FinishTool"/> (cf. ADR 0019), et gère la persistance de session entre exécutions de
/// l'activité (cf. ADR 0018). Le traitement de l'issue "Done" — seule étape qui diffère entre les
/// deux activités — est délégué à <see cref="HandleDoneAsync"/>.
/// </summary>
public abstract class AgentPromptActivityBase : CodeActivity
{
    private const string SessionStatePropertyPrefix = "AgentSession::";

    /// <summary>Nombre maximal de relances sans appel à <see cref="FinishTool"/> avant de sortir en Blocked.</summary>
    private const int MaxIterations = 6;

    private const string ReminderPrompt =
        "Tu n'as pas appelé l'outil de fin de tâche (Finish). Si la tâche est terminée, appelle-le avec "
        + "outcome='done'. Si tu as besoin d'informations pour continuer, appelle-le avec outcome='needsmoreinfo' "
        + "en précisant reason et questions. Si tu ne peux pas continuer, appelle-le avec outcome='blocked' en "
        + "précisant reason.";

    private readonly IAgentSessionCompactionService _compactionService;

    protected AgentPromptActivityBase(IAgentSessionCompactionService compactionService, string defaultAgentName)
    {
        ArgumentNullException.ThrowIfNull(compactionService);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultAgentName);
        _compactionService = compactionService;
        AgentName = new Input<string>(defaultAgentName);
    }

    [Input(Description = "Nom de l'agent à appeler (clé d'enregistrement DI, cf. configuration Agent:* ).")]
    public Input<string> AgentName { get; set; }

    [Input(Description = "Prompt envoyé à l'agent.")]
    public Input<string> Prompt { get; set; } = default!;

    [Output(Description = "Résumé fourni par l'agent via FinishTool, quelle que soit l'issue.")]
    public Output<string> Summary { get; set; } = new();

    [Output(Description = "Point de blocage signalé par l'agent (issues NeedsMoreInfo et Blocked).")]
    public Output<string?> Reason { get; set; } = new();

    [Output(Description = "Questions posées par l'agent pour pouvoir continuer (issue NeedsMoreInfo).")]
    public Output<IReadOnlyList<string>?> Questions { get; set; } = new();

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var agentName = context.Get(AgentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        var prompt = context.Get(Prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var agent = context.GetRequiredService<IServiceProvider>().GetRequiredKeyedService<AIAgent>(agentName);
        var sessionStatePropertyName = SessionStatePropertyPrefix + agentName;

        var session = await RestoreSessionAsync(context, agent, sessionStatePropertyName);

        var nextMessage = prompt;
        for (var iteration = 1; iteration <= MaxIterations; iteration++)
        {
            session = await _compactionService.CompactIfNeededAsync(agent, session, context.CancellationToken);

            var response = await agent.RunAsync(nextMessage, session, cancellationToken: context.CancellationToken);
            await PersistSessionAsync(context, agent, session, sessionStatePropertyName);

            var finish = FindFinishCall(response);
            if (finish is not null)
            {
                var retryMessage = await TryCompleteAsync(context, finish);
                if (retryMessage is null)
                {
                    return;
                }

                nextMessage = retryMessage;
            }
            else
            {
                nextMessage = ReminderPrompt;
            }
        }

        context.Set(Summary, string.Empty);
        context.Set(Reason, "Nombre maximal de relances atteint sans confirmation finale validée (boucle évitée).");
        await context.CompleteActivityWithOutcomesAsync(["Blocked"]);
    }

    /// <summary>
    /// Traite l'issue "Done" d'un appel à <see cref="FinishTool"/>. Retourne <c>null</c> si
    /// l'activité s'est terminée (issue posée via
    /// <see cref="ActivityExecutionContext.CompleteActivityWithOutcomesAsync"/>), ou un message à
    /// renvoyer à l'agent si la boucle doit continuer.
    /// </summary>
    protected abstract ValueTask<string?> HandleDoneAsync(ActivityExecutionContext context, FinishCall finish);

    /// <summary>
    /// Traite <see cref="FinishCall.Verdict"/> pour les activités EnvironmentManager et Evaluator
    /// (cf. ADR 0023) : <see cref="AgentVerdict.Pass"/> termine avec <paramref name="passOutcome"/>,
    /// <see cref="AgentVerdict.Fail"/> avec "Failed" (en reportant <see cref="FinishCall.Reason"/>),
    /// <see cref="AgentVerdict.NeedMoreInfo"/> avec "NeedsMoreInfo" (en reportant
    /// <see cref="FinishCall.Reason"/> et <see cref="FinishCall.Questions"/>). Si
    /// <see cref="FinishCall.Verdict"/> est absent, retourne un message de relance demandant à
    /// l'agent de le préciser.
    /// </summary>
    protected async ValueTask<string?> HandleVerdictAsync(ActivityExecutionContext context, FinishCall finish, string passOutcome)
    {
        switch (finish.Verdict)
        {
            case AgentVerdict.Pass:
                context.Set(Reason, null);
                await context.CompleteActivityWithOutcomesAsync([passOutcome]);
                return null;

            case AgentVerdict.Fail:
                context.Set(Reason, finish.Reason);
                await context.CompleteActivityWithOutcomesAsync(["Failed"]);
                return null;

            case AgentVerdict.NeedMoreInfo:
                context.Set(Reason, finish.Reason);
                context.Set(Questions, finish.Questions);
                await context.CompleteActivityWithOutcomesAsync(["NeedsMoreInfo"]);
                return null;

            default:
                return "Précise verdict='pass', 'fail' ou 'needmoreinfo' dans ton appel Finish.";
        }
    }

    private static FinishCall? FindFinishCall(AgentResponse response)
    {
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent { Name: FinishTool.FunctionName } call)
                {
                    var finish = FinishCall.FromArguments(call.Arguments);
                    if (finish is not null)
                    {
                        return finish;
                    }
                }
            }
        }

        return null;
    }

    private async ValueTask<string?> TryCompleteAsync(ActivityExecutionContext context, FinishCall finish)
    {
        context.Set(Summary, finish.Summary);

        switch (finish.Outcome)
        {
            case AgentTaskOutcome.Done:
                return await HandleDoneAsync(context, finish);

            case AgentTaskOutcome.NeedsMoreInfo:
                context.Set(Reason, finish.Reason);
                context.Set(Questions, finish.Questions);
                await context.CompleteActivityWithOutcomesAsync(["NeedsMoreInfo"]);
                return null;

            case AgentTaskOutcome.Blocked:
                context.Set(Reason, finish.Reason);
                await context.CompleteActivityWithOutcomesAsync(["Blocked"]);
                return null;

            default:
                throw new ArgumentOutOfRangeException(nameof(finish), finish.Outcome, "Outcome inattendu.");
        }
    }

    private static async Task<AgentSession> RestoreSessionAsync(ActivityExecutionContext context, AIAgent agent, string sessionStatePropertyName)
    {
        var serializedSession = context.GetProperty<string?>(sessionStatePropertyName);
        if (string.IsNullOrEmpty(serializedSession))
        {
            return await agent.CreateSessionAsync(context.CancellationToken);
        }

        using var document = JsonDocument.Parse(serializedSession);
        return await agent.DeserializeSessionAsync(document.RootElement, cancellationToken: context.CancellationToken);
    }

    private static async Task PersistSessionAsync(ActivityExecutionContext context, AIAgent agent, AgentSession session, string sessionStatePropertyName)
    {
        var serializedSession = await agent.SerializeSessionAsync(session, cancellationToken: context.CancellationToken);
        context.SetProperty(sessionStatePropertyName, serializedSession.GetRawText());
    }
}
