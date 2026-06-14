using Alveus.Web.Activities;
using Alveus.Web.Agents;
using Alveus.Web.Tools;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Models;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Endpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

namespace Alveus.Web.Workflows;

/// <summary>
/// Orchestration de bout en bout d'une tâche Alveus (cf. ADR 0023, étendue par ADR 0024/0025/0026) :
/// <list type="number">
/// <item>Réunion de pré-tâche (<see cref="RunPreTaskMeeting"/>) : Alveus-BusinessAnalyst,
/// Alveus-Qa et Alveus-Technical lisent le ticket, mettent à jour leur documentation et préparent
/// des instructions complémentaires pour le Worker/Evaluator/UserDoc.</item>
/// <item>Alveus-Worker (<see cref="RunAgentPrompt"/>) exécute la tâche dans son workspace.</item>
/// <item>Si "Done", Alveus-EnvironmentManager (<see cref="RunEnvironmentPrompt"/>) (re)lance
/// l'environnement local décrit par la consigne, dans le même workspace que le Worker.</item>
/// <item>Si "Done", Alveus-Evaluator (<see cref="RunEvaluatorPrompt"/>) reçoit la consigne du
/// Worker complétée par les instructions d'utilisation de l'environnement et les instructions
/// complémentaires d'Alveus-Qa, écrit et exécute un jeu de test contre l'environnement réel, puis
/// rend un verdict.</item>
/// <item>"Failed" (EnvironmentManager ou Evaluator) renvoie au Worker avec un rapport des
/// problèmes rencontrés, via <see cref="LoopIterationGuard"/> pour éviter une boucle infinie.</item>
/// <item>"Passed" → Alveus-UserDoc (<see cref="RunUserDocPrompt"/>) met à jour la documentation
/// utilisateur, puis la réunion finale (<see cref="RunFinalReviewMeeting"/>) vote sur le fait que
/// la tâche est correctement remplie.</item>
/// <item>"OK" termine le workflow (succès). "KO" renvoie à la réunion de pré-tâche avec les
/// comptes-rendus des 3 participants, via <see cref="OuterLoopIterationGuard"/>.</item>
/// </list>
/// "NeedsMoreInfo"/"Blocked"/"NeedsHelp" à n'importe quelle étape terminent le workflow (consigne
/// insuffisante ou absence de consensus, non rattrapable par une boucle).
/// </summary>
public sealed class AlveusTaskWorkflow : WorkflowBase
{
    private readonly IAgentSessionCompactionService _compactionService;
    private readonly IAgentWorkVerificationService _workVerificationService;

    public AlveusTaskWorkflow(IAgentSessionCompactionService compactionService, IAgentWorkVerificationService workVerificationService)
    {
        _compactionService = compactionService;
        _workVerificationService = workVerificationService;
    }

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.WithDefinitionId("AlveusTaskWorkflow");

        builder.WithInput("TaskPrompt", typeof(string), "Consigne initiale de la tâche.");

        var envUsageInstructions = builder.WithVariable("EnvUsageInstructions", string.Empty);
        var failureReport = builder.WithVariable<string?>("FailureReport", null);
        var loopCount = builder.WithVariable("LoopCount", 0);

        var workerInstructions = builder.WithVariable("WorkerInstructions", string.Empty);
        var evaluatorInstructions = builder.WithVariable("EvaluatorInstructions", string.Empty);
        var userDocInstructions = builder.WithVariable("UserDocInstructions", string.Empty);
        var outerLoopCount = builder.WithVariable("OuterLoopCount", 0);

        var workerSummary = builder.WithVariable("WorkerSummary", string.Empty);
        var evaluatorSummary = builder.WithVariable("EvaluatorSummary", string.Empty);
        var userDocSummary = builder.WithVariable("UserDocSummary", string.Empty);

        var baReport = builder.WithVariable<string?>("BaReport", null);
        var qaReport = builder.WithVariable<string?>("QaReport", null);
        var techReport = builder.WithVariable<string?>("TechReport", null);

        var runPreTaskMeeting = new RunPreTaskMeeting(_compactionService)
        {
            Id = "RunPreTaskMeeting",
            Topic = new Input<string>(context => context.GetInput<string>("TaskPrompt")!),
            ExtraContext = new Input<string?>(context =>
            {
                var reports = new[] { baReport.Get(context), qaReport.Get(context), techReport.Get(context) }
                    .Where(r => !string.IsNullOrWhiteSpace(r));
                return string.Join("\n\n---\n", reports);
            }),
            WorkerInstructions = new Output<string>(workerInstructions),
            EvaluatorInstructions = new Output<string>(evaluatorInstructions),
            UserDocInstructions = new Output<string>(userDocInstructions),
        };

        var runWorker = new RunAgentPrompt(_compactionService, _workVerificationService)
        {
            Id = "RunWorker",
            Prompt = new Input<string>(context =>
            {
                var taskPrompt = context.GetInput<string>("TaskPrompt")!;
                var instructions = workerInstructions.Get(context);
                var report = failureReport.Get(context);

                var result = taskPrompt;
                if (!string.IsNullOrEmpty(instructions))
                {
                    result += $"\n\n---\nInstructions complémentaires (Alveus-Technical) :\n{instructions}";
                }

                if (!string.IsNullOrEmpty(report))
                {
                    result += $"\n\n---\nRapport de l'évaluation précédente (à corriger) :\n{report}";
                }

                return result;
            }),
            Summary = new Output<string>(workerSummary),
        };

        var runEnvironmentManager = new RunEnvironmentPrompt(_compactionService)
        {
            Id = "RunEnvironmentManager",
            Prompt = new Input<string>(context => context.GetInput<string>("TaskPrompt")!),
            Summary = new Output<string>(envUsageInstructions),
            Reason = new Output<string?>(failureReport),
        };

        var runEvaluator = new RunEvaluatorPrompt(_compactionService)
        {
            Id = "RunEvaluator",
            Prompt = new Input<string>(context =>
            {
                var result = $"{context.GetInput<string>("TaskPrompt")}\n\n---\nInstructions d'utilisation de l'environnement :\n{envUsageInstructions.Get(context)}";
                var instructions = evaluatorInstructions.Get(context);
                if (!string.IsNullOrEmpty(instructions))
                {
                    result += $"\n\n---\nInstructions complémentaires (Alveus-Qa) :\n{instructions}";
                }

                return result;
            }),
            Summary = new Output<string>(evaluatorSummary),
            Reason = new Output<string?>(failureReport),
        };

        var loopGuard = new LoopIterationGuard
        {
            Id = "LoopGuard",
            LoopCount = loopCount,
        };

        var runUserDoc = new RunUserDocPrompt(_compactionService)
        {
            Id = "RunUserDoc",
            Prompt = new Input<string>(context =>
            {
                var result = context.GetInput<string>("TaskPrompt")!;
                var instructions = userDocInstructions.Get(context);
                if (!string.IsNullOrEmpty(instructions))
                {
                    result += $"\n\n---\nInstructions complémentaires (Alveus-Technical) :\n{instructions}";
                }

                return result;
            }),
            Summary = new Output<string>(userDocSummary),
        };

        var runFinalReviewMeeting = new RunFinalReviewMeeting(_compactionService)
        {
            Id = "RunFinalReviewMeeting",
            Topic = new Input<string>(context =>
                $"{context.GetInput<string>("TaskPrompt")}"
                + $"\n\n---\nRésumé Alveus-Worker :\n{workerSummary.Get(context)}"
                + $"\n\n---\nInstructions d'utilisation de l'environnement (Alveus-EnvironmentManager) :\n{envUsageInstructions.Get(context)}"
                + $"\n\n---\nRésumé Alveus-Evaluator :\n{evaluatorSummary.Get(context)}"
                + $"\n\n---\nRésumé Alveus-UserDoc :\n{userDocSummary.Get(context)}"),
            BaReport = new Output<string?>(baReport),
            QaReport = new Output<string?>(qaReport),
            TechReport = new Output<string?>(techReport),
        };

        var outerLoopGuard = new OuterLoopIterationGuard
        {
            Id = "OuterLoopGuard",
            OuterLoopCount = outerLoopCount,
        };

        builder.Root = new Flowchart
        {
            Start = runPreTaskMeeting,
            Activities =
            [
                runPreTaskMeeting,
                runWorker,
                runEnvironmentManager,
                runEvaluator,
                loopGuard,
                runUserDoc,
                runFinalReviewMeeting,
                outerLoopGuard,
            ],
            Connections =
            [
                new Connection(new Endpoint(runPreTaskMeeting, "Done"), new Endpoint(runWorker)),

                new Connection(new Endpoint(runWorker, "Done"), new Endpoint(runEnvironmentManager)),
                new Connection(new Endpoint(runEnvironmentManager, "Done"), new Endpoint(runEvaluator)),
                new Connection(new Endpoint(runEnvironmentManager, "Failed"), new Endpoint(loopGuard)),
                new Connection(new Endpoint(runEvaluator, "Failed"), new Endpoint(loopGuard)),
                new Connection(new Endpoint(loopGuard, "Continue"), new Endpoint(runWorker)),

                new Connection(new Endpoint(runEvaluator, "Passed"), new Endpoint(runUserDoc)),
                new Connection(new Endpoint(runUserDoc, "Done"), new Endpoint(runFinalReviewMeeting)),

                new Connection(new Endpoint(runFinalReviewMeeting, "KO"), new Endpoint(outerLoopGuard)),
                new Connection(new Endpoint(outerLoopGuard, "Continue"), new Endpoint(runPreTaskMeeting)),
            ],
        };
    }
}
