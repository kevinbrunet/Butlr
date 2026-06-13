namespace Alveus.Web.Tools;

/// <summary>
/// Jugement porté par l'agent EnvironmentManager ou Evaluator sur le résultat d'une étape
/// précédente du workflow, signalé via <see cref="FinishTool"/> (cf. ADR 0023). Distinct de
/// <see cref="AgentTaskOutcome"/> : <see cref="AgentTaskOutcome"/> qualifie l'état du travail de
/// l'agent ("ai-je terminé ce que je devais faire ?"), <see cref="AgentVerdict"/> qualifie le
/// jugement attendu sur un résultat externe ("ce résultat est-il bon ?"). Sans objet pour
/// Alveus-Worker.
/// </summary>
public enum AgentVerdict
{
    Pass,
    Fail,
    NeedMoreInfo,
}
