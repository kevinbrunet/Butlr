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
/// Envoie <see cref="Prompt"/> à l'agent désigné par <see cref="AgentName"/> (résolu via les
/// services à clé enregistrés dans <c>Program.cs</c>, cf. configuration <c>Agent:Name</c>) et
/// attend que l'agent appelle <see cref="FinishTool"/> pour se terminer — cf. ADR 0019.
/// La session (prompt + réponse, y compris raisonnement et appels d'outils) est persistée dans
/// l'état de l'activité : si l'activité est réexécutée après une suspension du workflow, elle
/// reprend la même session plutôt que d'en démarrer une nouvelle — cf. ADR 0018.
/// </summary>
[Activity("Alveus", "AI", "Envoie un prompt à un agent et attend son verdict (terminé, besoin d'informations, ou bloqué) via FinishTool.")]
public sealed class RunAgentPrompt : CodeActivity
{
    private const string SessionStatePropertyPrefix = "AgentSession::";
    private const string DefaultAgentName = "AlveusWorker";

    /// <summary>Nombre maximal de relances sans appel à <see cref="FinishTool"/> avant de sortir en Blocked.</summary>
    private const int MaxIterations = 6;

    private const string ReminderPrompt =
        "Tu n'as pas appelé l'outil de fin de tâche (Finish). Si la tâche est terminée, appelle-le avec "
        + "outcome='done'. Si tu as besoin d'informations pour continuer, appelle-le avec outcome='needsmoreinfo' "
        + "en précisant reason et questions. Si tu ne peux pas continuer, appelle-le avec outcome='blocked' en "
        + "précisant reason.";

    private readonly IAgentSessionCompactionService _compactionService;

    public RunAgentPrompt(IAgentSessionCompactionService compactionService)
    {
        ArgumentNullException.ThrowIfNull(compactionService);
        _compactionService = compactionService;
    }

    [Input(Description = "Nom de l'agent à appeler (clé d'enregistrement DI, cf. Agent:Name en configuration).")]
    public Input<string> AgentName { get; set; } = new(DefaultAgentName);

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
                await CompleteWithFinishAsync(context, finish);
                return;
            }

            nextMessage = ReminderPrompt;
        }

        context.Set(Summary, string.Empty);
        context.Set(Reason, "Nombre maximal de relances atteint sans appel à l'outil de fin de tâche (boucle évitée).");
        await context.CompleteActivityWithOutcomesAsync(["Blocked"]);
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

    private async ValueTask CompleteWithFinishAsync(ActivityExecutionContext context, FinishCall finish)
    {
        context.Set(Summary, finish.Summary);

        switch (finish.Outcome)
        {
            case AgentTaskOutcome.Done:
                await context.CompleteActivityWithOutcomesAsync(["Done"]);
                break;

            case AgentTaskOutcome.NeedsMoreInfo:
                context.Set(Reason, finish.Reason);
                context.Set(Questions, finish.Questions);
                await context.CompleteActivityWithOutcomesAsync(["NeedsMoreInfo"]);
                break;

            case AgentTaskOutcome.Blocked:
                context.Set(Reason, finish.Reason);
                await context.CompleteActivityWithOutcomesAsync(["Blocked"]);
                break;

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
