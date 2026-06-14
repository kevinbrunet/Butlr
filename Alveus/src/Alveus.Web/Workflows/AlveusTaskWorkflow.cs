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
/// Orchestration de bout en bout d'une tâche Alveus (cf. ADR 0023) :
/// <list type="number">
/// <item>Alveus-Worker (<see cref="RunAgentPrompt"/>) exécute la tâche dans son workspace.</item>
/// <item>Si "Done", Alveus-EnvironmentManager (<see cref="RunEnvironmentPrompt"/>) (re)lance
/// l'environnement local décrit par la consigne, dans le même workspace que le Worker.</item>
/// <item>Si "Done", Alveus-Evaluator (<see cref="RunEvaluatorPrompt"/>) reçoit la consigne du
/// Worker complétée par les instructions d'utilisation de l'environnement, écrit et exécute un
/// jeu de test contre l'environnement réel, puis rend un verdict.</item>
/// <item>"Passed" termine le workflow. "Failed" (EnvironmentManager ou Evaluator) renvoie au
/// Worker avec un rapport des problèmes rencontrés, via <see cref="LoopIterationGuard"/> pour
/// éviter une boucle infinie.</item>
/// </list>
/// "NeedsMoreInfo"/"Blocked" à n'importe quelle étape terminent le workflow (consigne
/// insuffisante, non rattrapable par une boucle).
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

        var runWorker = new RunAgentPrompt(_compactionService, _workVerificationService)
        {
            Id = "RunWorker",
            Prompt = new Input<string>(context =>
            {
                var taskPrompt = context.GetInput<string>("TaskPrompt")!;
                var report = failureReport.Get(context);
                return string.IsNullOrEmpty(report)
                    ? taskPrompt
                    : $"{taskPrompt}\n\n---\nRapport de l'évaluation précédente (à corriger) :\n{report}";
            }),
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
                $"{context.GetInput<string>("TaskPrompt")}\n\n---\nInstructions d'utilisation de l'environnement :\n{envUsageInstructions.Get(context)}"),
            Reason = new Output<string?>(failureReport),
        };

        var loopGuard = new LoopIterationGuard
        {
            Id = "LoopGuard",
            LoopCount = loopCount,
        };

        builder.Root = new Flowchart
        {
            Start = runWorker,
            Activities = [runWorker, runEnvironmentManager, runEvaluator, loopGuard],
            Connections =
            [
                new Connection(new Endpoint(runWorker, "Done"), new Endpoint(runEnvironmentManager)),
                new Connection(new Endpoint(runEnvironmentManager, "Done"), new Endpoint(runEvaluator)),
                new Connection(new Endpoint(runEnvironmentManager, "Failed"), new Endpoint(loopGuard)),
                new Connection(new Endpoint(runEvaluator, "Failed"), new Endpoint(loopGuard)),
                new Connection(new Endpoint(loopGuard, "Continue"), new Endpoint(runWorker)),
            ],
        };
    }
}
