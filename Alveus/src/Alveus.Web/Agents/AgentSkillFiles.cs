namespace Alveus.Web.Agents;

/// <summary>
/// Localise le dossier <c>skils/</c> du repo contenant les fichiers <c>{nom}.skill.md</c>.
/// </summary>
public static class AgentSkillFiles
{
    /// <summary>
    /// Remonte l'arborescence depuis <paramref name="searchStartDirectory"/> jusqu'à trouver
    /// un dossier <c>skils/</c>. Retourne <c>null</c> si aucun n'est trouvé (ex. déploiement
    /// sans les sources du repo).
    /// </summary>
    public static string? FindRoot(string searchStartDirectory)
    {
        for (var dir = new DirectoryInfo(searchStartDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "skils");
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>Chemin complet vers le fichier d'un skill.</summary>
    public static string GetPath(string skillsRoot, string skillName)
        => Path.Combine(skillsRoot, $"{skillName}.skill.md");
}
