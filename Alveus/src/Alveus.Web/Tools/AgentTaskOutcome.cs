namespace Alveus.Web.Tools;

/// <summary>
/// Issue d'une tâche déléguée à l'agent, signalée via <see cref="FinishTool"/> et interprétée
/// par <see cref="Activities.RunAgentPrompt"/> pour choisir sa sortie (cf. ADR 0019).
/// </summary>
public enum AgentTaskOutcome
{
    Done,
    NeedsMoreInfo,
    Blocked,
}
