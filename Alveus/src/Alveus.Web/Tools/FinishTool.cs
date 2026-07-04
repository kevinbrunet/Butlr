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
        + "et un résultat. outcome='pass' si la tâche est terminée et réussie. outcome='fail' si la tâche est "
        + "terminée mais a échoué ou la vérification a échoué (donne reason). outcome='needmoreinfo' si tu as besoin "
        + "d'informations supplémentaires pour continuer (donne reason et questions). outcome='blocked' si tu ne "
        + "peux pas continuer (donne reason).")]
    public string Finish(
        [Description("Résumé de ce qui a été fait, ou de la situation actuelle si la tâche n'est pas terminée.")] string summary,
        [Description("Résultat : 'pass', 'fail', 'needmoreinfo' ou 'blocked'.")] string outcome,
        [Description("Pour 'fail', 'needmoreinfo' ou 'blocked' : explique précisément le point de blocage ou d'échec.")] string? reason = null,
        [Description("Pour 'needmoreinfo' : questions précises à poser pour pouvoir continuer.")] IList<string>? questions = null,
        [Description("Instructions complémentaires pour un ou plusieurs agents en aval, en plus du ticket. "
            + "Chaque instruction précise sa cible ('worker', 'evaluator' ou 'userdoc') et son contenu. "
            + "Pertinent uniquement pour Alveus-Technical (cibles 'worker'/'userdoc') et Alveus-Qa (cible "
            + "'evaluator') — sans objet pour les autres agents.")] IList<DownstreamInstruction>? downstreamInstructions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        if (!Enum.TryParse<AgentOutcome>(outcome, ignoreCase: true, out _))
        {
            throw new ArgumentException($"outcome inconnu : '{outcome}'. Attendu : pass, fail, needmoreinfo ou blocked.", nameof(outcome));
        }

        if (downstreamInstructions is not null)
        {
            foreach (var instruction in downstreamInstructions)
            {
                if (!Enum.TryParse<DownstreamInstructionTarget>(instruction.Target, ignoreCase: true, out _))
                {
                    throw new ArgumentException(
                        $"target d'instruction inconnue : '{instruction.Target}'. Attendu : worker, evaluator ou userdoc.",
                        nameof(downstreamInstructions));
                }
            }
        }

        return "Issue enregistrée.";
    }
}
