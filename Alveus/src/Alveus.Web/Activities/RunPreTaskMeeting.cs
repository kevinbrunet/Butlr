using Alveus.Web.Agents;
using Alveus.Web.Tools;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;

namespace Alveus.Web.Activities;

/// <summary>
/// Réunion de pré-tâche (cf. ADR 0024/0025) : avant qu'Alveus-Worker ne commence, Alveus-BusinessAnalyst,
/// Alveus-Qa et Alveus-Technical lisent le ticket (<see cref="MeetingActivityBase.Topic"/>), mettent à
/// jour leur documentation respective (règles métier, plan de test, architecture/ADRs) et débattent
/// (<c>Raise</c>/<c>Vote</c>) des points de contention. Alveus-Technical et Alveus-Qa peuvent émettre des
/// <see cref="DownstreamInstruction"/> via <see cref="FinishTool.Finish"/>, routées vers
/// <see cref="WorkerInstructions"/>/<see cref="UserDocInstructions"/>/<see cref="EvaluatorInstructions"/>.
/// Sortie "Done" si les 3 participants confirment sans topic ouvert, "NeedsHelp" sinon (escalade).
/// </summary>
[Activity("Alveus", "AI", "Réunion de pré-tâche : Alveus-BusinessAnalyst/Alveus-Qa/Alveus-Technical mettent à jour leur documentation, débattent et préparent des instructions pour le Worker/Evaluator/UserDoc.")]
public sealed class RunPreTaskMeeting : MeetingActivityBase
{
    private readonly List<string> _workerInstructions = [];
    private readonly List<string> _evaluatorInstructions = [];
    private readonly List<string> _userDocInstructions = [];

    public RunPreTaskMeeting(IAgentSessionCompactionService compactionService)
        : base(compactionService)
    {
    }

    [Output(Description = "Instructions complémentaires d'Alveus-Technical pour Alveus-Worker, à ajouter à la consigne de tâche.")]
    public Output<string> WorkerInstructions { get; set; } = new();

    [Output(Description = "Instructions complémentaires d'Alveus-Qa pour Alveus-Evaluator, à ajouter à la consigne de tâche.")]
    public Output<string> EvaluatorInstructions { get; set; } = new();

    [Output(Description = "Instructions complémentaires d'Alveus-Technical pour Alveus-UserDoc, à ajouter à la consigne de tâche.")]
    public Output<string> UserDocInstructions { get; set; } = new();

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        _workerInstructions.Clear();
        _evaluatorInstructions.Clear();
        _userDocInstructions.Clear();

        await base.ExecuteAsync(context);
    }

    protected override string GetRoleTask(string agentRole) => agentRole switch
    {
        "BusinessAnalyst" => "Tu es Alveus-BusinessAnalyst. Lis le ticket ci-dessous. S'il fait évoluer une règle "
            + "métier existante, mets à jour l'arborescence markdown de 'business-rules/' (un fichier par domaine "
            + "métier) pour refléter ce changement. S'il introduit une nouvelle règle, documente-la dans la même "
            + "arborescence. Si la mise à jour soulève un point à trancher avec Alveus-Qa ou Alveus-Technical "
            + "(ambiguïté, incohérence avec une règle existante), utilise Raise pour le signaler.",

        "Qa" => "Tu es Alveus-Qa. Lis le ticket ci-dessous. S'il fait évoluer un comportement existant, mets à jour "
            + "le plan de test markdown de 'test-plan/' (cas passants et non passants concernés). S'il introduit un "
            + "nouveau comportement, ajoute les cas de test correspondants. Si la mise à jour soulève un point à "
            + "trancher avec Alveus-BusinessAnalyst ou Alveus-Technical, utilise Raise. Si Alveus-Evaluator a besoin "
            + "d'instructions complémentaires pour vérifier ce ticket (cas limites à tester, scénarios spécifiques), "
            + "précise-les dans downstreamInstructions de ton Finish final, avec target='evaluator'.",

        "Technical" => "Tu es Alveus-Technical. Lis le ticket ci-dessous. S'il fait évoluer l'architecture, mets à "
            + "jour la documentation et les ADR de 'tech-docs/' (cf. conventions ADR du repo : un ADR par décision "
            + "non-triviale, numérotation monotone croissante). Si la mise à jour soulève un point à trancher avec "
            + "Alveus-BusinessAnalyst ou Alveus-Qa, utilise Raise. Dans ton Finish final, utilise "
            + "downstreamInstructions pour donner à Alveus-Worker (target='worker') des précisions techniques "
            + "nécessaires à l'implémentation, et/ou à Alveus-UserDoc (target='userdoc') des précisions pour la "
            + "documentation utilisateur — uniquement si nécessaire.",

        _ => throw new ArgumentOutOfRangeException(nameof(agentRole), agentRole, "Rôle de réunion inconnu."),
    };

    protected override ValueTask OnAgentFinishAsync(ActivityExecutionContext context, string agentRole, FinishCall finish)
    {
        if (finish.DownstreamInstructions is null)
        {
            return ValueTask.CompletedTask;
        }

        foreach (var instruction in finish.DownstreamInstructions)
        {
            if (!Enum.TryParse<DownstreamInstructionTarget>(instruction.Target, ignoreCase: true, out var target))
            {
                continue;
            }

            switch (target)
            {
                case DownstreamInstructionTarget.Worker:
                    _workerInstructions.Add(instruction.Instruction);
                    break;
                case DownstreamInstructionTarget.Evaluator:
                    _evaluatorInstructions.Add(instruction.Instruction);
                    break;
                case DownstreamInstructionTarget.UserDoc:
                    _userDocInstructions.Add(instruction.Instruction);
                    break;
            }
        }

        return ValueTask.CompletedTask;
    }

    protected override ValueTask FinalizeAsync(
        ActivityExecutionContext context,
        MeetingOutcome outcome,
        IReadOnlyDictionary<string, MeetingVoteTally> topicTallies,
        IReadOnlyDictionary<string, string> finishSummaries)
    {
        context.Set(WorkerInstructions, string.Join("\n\n", _workerInstructions));
        context.Set(EvaluatorInstructions, string.Join("\n\n", _evaluatorInstructions));
        context.Set(UserDocInstructions, string.Join("\n\n", _userDocInstructions));

        return context.CompleteActivityWithOutcomesAsync([outcome == MeetingOutcome.Done ? "Done" : "NeedsHelp"]);
    }
}
