namespace Alveus.Web.Agents;

/// <summary>
/// Met à disposition de l'agent évaluateur les "skills" méthodologiques du repo
/// (<c>Alveus/skils/</c>), en les copiant dans son workspace pour qu'il puisse les consulter
/// avec ses outils habituels (édition de fichiers) — cf. ADR 0021.
/// </summary>
public static class EvaluatorSkills
{
    public const string DotnetSnapshotTestingSkillName = "dotnet-snapshot-testing";

    /// <summary>
    /// Copie le skill <see cref="DotnetSnapshotTestingSkillName"/> dans
    /// <c>{evaluatorWorkspaceRoot}/skills/{nom du skill}</c>. No-op si le dossier <c>skils/</c>
    /// du repo n'est pas trouvé en remontant depuis <paramref name="searchStartDirectory"/>
    /// (ex. déploiement sans les sources du repo).
    /// </summary>
    public static void CopyInto(string evaluatorWorkspaceRoot, string searchStartDirectory)
    {
        var repoSkillsRoot = FindRepoSkillsRoot(searchStartDirectory);
        if (repoSkillsRoot is null)
        {
            return;
        }

        var source = Path.Combine(repoSkillsRoot, DotnetSnapshotTestingSkillName);
        if (!Directory.Exists(source))
        {
            return;
        }

        CopyDirectory(source, Path.Combine(evaluatorWorkspaceRoot, "skills", DotnetSnapshotTestingSkillName));
    }

    private static string? FindRepoSkillsRoot(string searchStartDirectory)
    {
        for (var dir = new DirectoryInfo(searchStartDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "skils");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var filePath in Directory.GetFiles(sourceDir))
        {
            File.Copy(filePath, Path.Combine(destinationDir, Path.GetFileName(filePath)), overwrite: true);
        }

        foreach (var directoryPath in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(directoryPath, Path.Combine(destinationDir, Path.GetFileName(directoryPath)));
        }
    }
}
