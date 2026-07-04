namespace Alveus.Web.Tools;

/// <summary>
/// Issue unique d'un agent, signalée via <see cref="FinishTool"/> — cf. ADR 0019.
/// Unifie l'ancienne paire (outcome + verdict) en une seule valeur : l'agent exprime à la fois
/// l'état de son travail et son jugement dans le même paramètre.
/// </summary>
public enum AgentOutcome
{
    Pass,
    Fail,
    NeedMoreInfo,
    Blocked,
}
