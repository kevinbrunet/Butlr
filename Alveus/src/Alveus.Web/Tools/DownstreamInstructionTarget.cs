namespace Alveus.Web.Tools;

/// <summary>
/// Destinataire d'une <see cref="DownstreamInstruction"/> signalée via <see cref="FinishTool"/>
/// par Alveus-Technical ou Alveus-Qa pendant une réunion (cf. ADR 0025).
/// </summary>
public enum DownstreamInstructionTarget
{
    Worker,
    Evaluator,
    UserDoc,
}
