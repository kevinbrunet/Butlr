namespace Alveus.Web.Configuration;

/// <summary>
/// Déclaration d'une équipe dans <c>Teams</c> (appsettings, cf. ADR 0031). Chaque équipe correspond
/// à un endpoint de conversation OpenAI distinct (<c>/teams/{Name}/v1/conversations</c>) et à un
/// jeu d'agents DI isolés (clés <c>"{Name}:{role}"</c>).
/// </summary>
public sealed class TeamConfig
{
    public string Name { get; set; } = "";

    /// <summary>Contexte projet injecté en tête des instructions système de tous les agents de l'équipe.</summary>
    public string MissionPrompt { get; set; } = "";

    public string WorkspaceRoot { get; set; } = "workspace";
    public string EvaluatorWorkspaceRoot { get; set; } = "workspace-evaluator";
    public string UserDocWorkspaceRoot { get; set; } = "workspace-userdoc";

    /// <summary>Commande shell de vérification post-Worker (cf. ADR 0020). Null = pas de vérification.</summary>
    public string? VerificationCommand { get; set; }

    /// <summary>Spécialistes actifs pour cette équipe (clés du <see cref="Agents.SpecialistRoleCatalog"/>).</summary>
    public List<TeamSpecialistConfig> SpecialistRoles { get; set; } = [];

    /// <summary>
    /// Mode d'escalade des agents individuels bloqués (cf. ADR 0028/0032).
    /// "meeting" (défaut) = escalade via RunPreTaskMeeting.
    /// "tool" = l'agent dispose de AskExpertTool pour interroger un expert directement.
    /// </summary>
    public string EscalationMode { get; set; } = "meeting";
}

/// <summary>
/// Référence à un rôle spécialiste du catalogue C# (<see cref="Agents.SpecialistRoleCatalog"/>)
/// avec instructions projet optionnelles ajoutées à la fin des instructions système de l'agent.
/// </summary>
public sealed class TeamSpecialistConfig
{
    /// <summary>Clé dans <see cref="Agents.SpecialistRoleCatalog.Roles"/> (ex. "BusinessAnalyst").</summary>
    public string Key { get; set; } = "";

    /// <summary>Instructions projet spécifiques à cette équipe, ajoutées après les instructions structurelles du catalogue.</summary>
    public string? AdditionalInstructions { get; set; }
}
