using Microsoft.Agents.AI;

namespace Alveus.Web.Agents;

/// <summary>
/// Injecte dans <see cref="AIContext.Instructions"/> le catalogue des skills disponibles pour
/// cet agent (nom + première ligne descriptive extraite du frontmatter de chaque
/// <c>{name}.skill.md</c>). Le contenu complet est chargé à la demande via
/// <see cref="Tools.LoadSkillTool"/> — cf. ADR 0022.
/// </summary>
public sealed class SkillsContextProvider(string skillsRoot, IReadOnlyList<string> skillNames) : AIContextProvider
{
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        if (skillNames.Count == 0)
            return new AIContext();

        var entries = new List<string>(skillNames.Count);
        foreach (var name in skillNames)
        {
            var path = AgentSkillFiles.GetPath(skillsRoot, name);
            if (!File.Exists(path))
                continue;
            var content = await File.ReadAllTextAsync(path, cancellationToken);
            var description = ParseFrontmatterDescription(content) ?? name;
            entries.Add($"- **{name}** : {description}");
        }

        if (entries.Count == 0)
            return new AIContext();

        return new AIContext
        {
            Instructions = "Skills disponibles (charge avec load_skill(name) uniquement si ta tâche "
                + "en a besoin — ex. tests Verify, Playwright) :\n\n"
                + string.Join("\n", entries),
        };
    }

    private static string? ParseFrontmatterDescription(string content)
    {
        if (!content.StartsWith("---", StringComparison.Ordinal))
            return null;
        var end = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
            return null;
        foreach (var line in content[3..end].Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("description:", StringComparison.Ordinal))
                return trimmed["description:".Length..].Trim();
        }
        return null;
    }
}
