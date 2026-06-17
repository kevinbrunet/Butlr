using System.ComponentModel;

namespace Alveus.Web.Tools;

/// <summary>
/// Permet à l'agent de charger à la demande le contenu d'un skill méthodologique. Seul le
/// catalogue (nom + description) est injecté dans le contexte système via
/// <see cref="Agents.SkillsContextProvider"/> ; le contenu complet est chargé ici explicitement
/// pour ne pas polluer le contexte avec des méthodologies non pertinentes pour la tâche en cours.
/// </summary>
public sealed class LoadSkillTool(string skillsRoot, IReadOnlyList<string> availableSkills)
{
    [Description("Charge le contenu complet d'un skill méthodologique listé dans ton contexte. "
        + "Appelle-le quand le catalogue indique qu'un skill est pertinent pour la tâche — avant de commencer à travailler.")]
    public string load_skill(
        [Description("Nom du skill à charger (ex. 'verify', 'playwright').")] string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Erreur : le nom du skill est requis.";

        var path = Path.Combine(skillsRoot, $"{name.Trim()}.skill.md");
        if (!File.Exists(path))
        {
            return availableSkills.Count == 0
                ? $"Skill '{name}' introuvable. Aucun skill disponible dans ce contexte."
                : $"Skill '{name}' introuvable. Skills disponibles : {string.Join(", ", availableSkills)}";
        }

        return File.ReadAllText(path);
    }
}
