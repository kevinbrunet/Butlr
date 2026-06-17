namespace Alveus.Web.Configuration;

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
