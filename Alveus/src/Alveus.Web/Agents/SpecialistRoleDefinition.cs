namespace Alveus.Web.Agents;

/// <summary>
/// Définition d'un rôle "spécialiste" : participant aux réunions de pré-tâche/finale (ADR
/// 0024), espace de travail enraciné sur un sous-dossier de celui d'Alveus-UserDoc (ADR 0025),
/// au même titre qu'Alveus-Qa (sous-dossier d'Alveus-Evaluator) et Alveus-Technical (sous-dossier
/// d'Alveus-Worker) — cf. ADR 0030.
/// </summary>
/// <param name="DisplayName">Nom affiché de l'agent (ex. "Alveus-BusinessAnalyst").</param>
/// <param name="WorkspaceSubdir">Sous-dossier d'<c>Agent:UserDocWorkspaceRoot</c> réservé à ce spécialiste.</param>
/// <param name="SystemInstructions">Persona de l'agent (instructions système du <c>ChatClientAgent</c>).</param>
/// <param name="PreTaskRoleTask">Consigne donnée par <see cref="Alveus.Web.Activities.RunPreTaskMeeting"/> à ce rôle.</param>
/// <param name="FinalReviewRoleTask">Consigne donnée par <see cref="Alveus.Web.Activities.RunFinalReviewMeeting"/> à ce rôle.</param>
public sealed record SpecialistRoleDefinition(
    string DisplayName,
    string WorkspaceSubdir,
    string SystemInstructions,
    string PreTaskRoleTask,
    string FinalReviewRoleTask);
