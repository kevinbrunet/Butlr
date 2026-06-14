namespace Alveus.Web.Tools;

/// <summary>
/// Instruction complémentaire émise par Alveus-Technical (cibles <see cref="DownstreamInstructionTarget.Worker"/>
/// / <see cref="DownstreamInstructionTarget.UserDoc"/>) ou Alveus-Qa (cible
/// <see cref="DownstreamInstructionTarget.Evaluator"/>) pendant <c>RunPreTaskMeeting</c>, signalée
/// via <see cref="FinishTool.Finish"/> et routée par <c>MeetingActivityBase.OnAgentFinishAsync</c>
/// vers la variable de sortie correspondant à <see cref="Target"/> (cf. ADR 0025).
/// </summary>
public sealed record DownstreamInstruction(string Target, string Instruction);
