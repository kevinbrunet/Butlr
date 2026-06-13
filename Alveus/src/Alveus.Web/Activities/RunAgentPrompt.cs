using System.Text.Json;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Alveus.Web.Activities;

/// <summary>
/// Envoie <see cref="Prompt"/> à l'agent désigné par <see cref="AgentName"/> (résolu via les
/// services à clé enregistrés dans <c>Program.cs</c>, cf. configuration <c>Agent:Name</c>).
/// La session (prompt + réponse, y compris raisonnement et appels d'outils) est persistée dans
/// l'état de l'activité : si l'activité est réexécutée après une suspension du workflow, elle
/// reprend la même session plutôt que d'en démarrer une nouvelle — cf. ADR 0018.
/// </summary>
[Activity("Alveus", "AI", "Envoie un prompt à un agent et persiste la session pour la réinjecter en cas de reprise de l'activité.")]
public sealed class RunAgentPrompt : CodeActivity<string>
{
    private const string SessionStatePropertyPrefix = "AgentSession::";
    private const string DefaultAgentName = "AlveusWorker";

    [Input(Description = "Nom de l'agent à appeler (clé d'enregistrement DI, cf. Agent:Name en configuration).")]
    public Input<string> AgentName { get; set; } = new(DefaultAgentName);

    [Input(Description = "Prompt envoyé à l'agent.")]
    public Input<string> Prompt { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var agentName = context.Get(AgentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        var prompt = context.Get(Prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var agent = context.GetRequiredService<IServiceProvider>().GetRequiredKeyedService<AIAgent>(agentName);
        var sessionStatePropertyName = SessionStatePropertyPrefix + agentName;

        var session = await RestoreSessionAsync(context, agent, sessionStatePropertyName);
        var response = await agent.RunAsync(prompt, session, cancellationToken: context.CancellationToken);
        await PersistSessionAsync(context, agent, session, sessionStatePropertyName);

        context.Set(Result, response.Text);
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
