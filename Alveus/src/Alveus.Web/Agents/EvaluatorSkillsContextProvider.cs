using Microsoft.Agents.AI;

namespace Alveus.Web.Agents;

/// <summary>
/// Injecte le contenu des skills méthodologiques (<c>skills/{nom}/SKILL.md</c>, copiés par
/// <see cref="EvaluatorSkills.CopyInto"/>) dans <see cref="AIContext.Instructions"/> à chaque
/// invocation de l'agent évaluateur — cf. ADR 0022. Contrairement à une simple mention dans les
/// instructions statiques de l'agent (ADR 0021 §3), cette injection ne dépend pas de l'initiative
/// du modèle à ouvrir les fichiers avec son outil d'édition.
/// </summary>
public sealed class EvaluatorSkillsContextProvider : AIContextProvider
{
    private readonly string _skillsRoot;

    public EvaluatorSkillsContextProvider(string evaluatorWorkspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluatorWorkspaceRoot);
        _skillsRoot = Path.Combine(evaluatorWorkspaceRoot, "skills");
    }

    /// <summary>
    /// Concatène le contenu de chaque <c>SKILL.md</c> trouvé sous <c>skills/</c> dans
    /// <see cref="AIContext.Instructions"/>. Retourne un contexte vide si le dossier
    /// <c>skills/</c> n'existe pas ou ne contient aucun <c>SKILL.md</c> (ex. déploiement sans les
    /// skills du repo, cf. <see cref="EvaluatorSkills.CopyInto"/>).
    /// </summary>
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_skillsRoot))
        {
            return new AIContext();
        }

        var skillFiles = Directory.GetFiles(_skillsRoot, "SKILL.md", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (skillFiles.Length == 0)
        {
            return new AIContext();
        }

        var sections = new List<string>(skillFiles.Length);
        foreach (var skillFile in skillFiles)
        {
            var skillName = Path.GetFileName(Path.GetDirectoryName(skillFile)) ?? skillFile;
            var content = await File.ReadAllTextAsync(skillFile, cancellationToken);
            sections.Add($"## Skill : {skillName}\n\n{content}");
        }

        return new AIContext
        {
            Instructions = "Méthodologies de référence disponibles pour cette tâche :\n\n"
                + string.Join("\n\n---\n\n", sections),
        };
    }
}
