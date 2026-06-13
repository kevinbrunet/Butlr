using System.ComponentModel;

namespace Alveus.Web.Tools;

/// <summary>
/// Outil que l'agent doit appeler pour signaler la fin de sa tâche — cf. ADR 0019.
/// <see cref="Activities.RunAgentPrompt"/> inspecte les appels de fonction de la réponse pour
/// retrouver cet appel (via <see cref="FinishCall.FromArguments"/>) et choisir sa sortie ;
/// la valeur retournée ici ne sert qu'à donner un accusé de réception au modèle.
/// </summary>
public sealed class FinishTool
{
    /// <summary>Nom de la fonction exposée à l'agent (nom de la méthode <see cref="Finish"/>).</summary>
    public const string FunctionName = "Finish";

    [Description("À appeler obligatoirement quand tu arrêtes de travailler : indique un résumé de ce qui a été fait "
        + "et un résultat. outcome='done' si la tâche est terminée. outcome='needsmoreinfo' si tu as besoin "
        + "d'informations supplémentaires pour continuer (donne reason et questions). outcome='blocked' si tu ne "
        + "peux pas continuer (donne reason).")]
    public string Finish(
        [Description("Résumé de ce qui a été fait, ou de la situation actuelle si la tâche n'est pas terminée.")] string summary,
        [Description("Résultat : 'done', 'needsmoreinfo' ou 'blocked'.")] string outcome,
        [Description("Pour 'needsmoreinfo' ou 'blocked' : explique précisément le point de blocage.")] string? reason = null,
        [Description("Pour 'needsmoreinfo' : questions précises à poser pour pouvoir continuer.")] IList<string>? questions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        if (!Enum.TryParse<AgentTaskOutcome>(outcome, ignoreCase: true, out _))
        {
            throw new ArgumentException($"outcome inconnu : '{outcome}'. Attendu : done, needsmoreinfo ou blocked.", nameof(outcome));
        }

        return "Issue enregistrée.";
    }
}
