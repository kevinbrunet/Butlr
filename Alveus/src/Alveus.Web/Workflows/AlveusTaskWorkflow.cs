using Alveus.Web.Activities;
using Alveus.Web.Agents;
using Alveus.Web.Configuration;
using Alveus.Web.Tools;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Models;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Expressions.Models;
using Microsoft.Extensions.Configuration;
using Endpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

namespace Alveus.Web.Workflows;

/// <summary>
/// Orchestration de bout en bout d'une tâche Alveus (cf. ADR 0023, étendue par ADR 0024/0025/0026) :
/// <list type="number">
/// <item>Réunion de pré-tâche (<see cref="RunPreTaskMeeting"/>) : les spécialistes configurés
/// (<c>Agent:SpecialistRoleKeys</c>, cf. ADR 0030), Alveus-Qa et Alveus-Technical lisent le ticket,
/// mettent à jour leur documentation et préparent des instructions complémentaires pour le
/// Worker/Evaluator/UserDoc.</item>
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
/// comptes-rendus des participants, via <see cref="OuterLoopIterationGuard"/>.</item>
/// <item>"NeedsMoreInfo"/"Blocked" issus de <see cref="RunAgentPrompt"/> (Worker),
/// <see cref="RunEnvironmentPrompt"/> (EnvironmentManager), <see cref="RunEvaluatorPrompt"/>
/// (Evaluator) ou <see cref="RunUserDocPrompt"/> (UserDoc) sont mis en forme par
/// <see cref="RecordAgentEscalation"/> puis renvoient à <see cref="RunPreTaskMeeting"/> (via
/// <see cref="AgentEscalationLoopGuard"/>, jusqu'à <see cref="AgentEscalationLoopGuard.MaxIterations"/>)
/// pour que les spécialistes configurés, Alveus-Qa et Alveus-Technical traitent le sujet
/// conjointement (cf. ADR 0028).</item>
/// </list>
/// <para>
/// "NeedsHelp" (issue globale de <see cref="RunPreTaskMeeting"/> ou <see cref="RunFinalReviewMeeting"/>)
/// suspend en revanche réellement le workflow via <see cref="Activities.AwaitConversationReply"/> (cf.
/// ADR 0027) : la réunion poste ses questions dans la conversation associée
/// (<c>ConversationId</c>), attend une réponse humaine, puis relance la même réunion via
/// <see cref="HelpLoopIterationGuard"/> (jusqu'à <see cref="HelpLoopIterationGuard.MaxIterations"/>).
/// </para>
/// </summary>
public sealed class AlveusTaskWorkflow : WorkflowBase
{
    private readonly IAgentSessionCompactionService _compactionService;
    private readonly IReadOnlyDictionary<string, TeamConfig> _teams;

    public AlveusTaskWorkflow(IAgentSessionCompactionService compactionService, IConfiguration configuration)
    {
        _compactionService = compactionService;
        _teams = (configuration.GetSection("Teams").Get<TeamConfig[]>() ?? [])
            .ToDictionary(t => t.Name);
    }

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.WithDefinitionId("AlveusTaskWorkflow");

        builder.WithInput("TaskPrompt", typeof(string), "Consigne initiale de la tâche.");
        builder.WithInput("ConversationId", typeof(string), "Identifiant de la conversation associée (cf. ADR 0027).");
        builder.WithInput("TeamName", typeof(string), "Nom de l'équipe (TeamConfig:Name, cf. ADR 0031).");

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

        var specialistReports = builder.WithVariable<IReadOnlyDictionary<string, string>?>("SpecialistReports", null);
        var qaReport = builder.WithVariable<string?>("QaReport", null);
        var techReport = builder.WithVariable<string?>("TechReport", null);

        var preTaskReason = builder.WithVariable<string?>("PreTaskReason", null);
        var preTaskQuestions = builder.WithVariable<IReadOnlyList<string>?>("PreTaskQuestions", null);
        var preTaskHumanReply = builder.WithVariable("PreTaskHumanReply", string.Empty);
        var preTaskHelpLoopCount = builder.WithVariable("PreTaskHelpLoopCount", 0);

        var finalReviewReason = builder.WithVariable<string?>("FinalReviewReason", null);
        var finalReviewQuestions = builder.WithVariable<IReadOnlyList<string>?>("FinalReviewQuestions", null);
        var finalReviewHumanReply = builder.WithVariable("FinalReviewHumanReply", string.Empty);
        var finalReviewHelpLoopCount = builder.WithVariable("FinalReviewHelpLoopCount", 0);

        var workerEscalationReason = builder.WithVariable<string?>("WorkerEscalationReason", null);
        var workerEscalationQuestions = builder.WithVariable<IReadOnlyList<string>?>("WorkerEscalationQuestions", null);
        var envManagerEscalationQuestions = builder.WithVariable<IReadOnlyList<string>?>("EnvManagerEscalationQuestions", null);
        var evaluatorEscalationQuestions = builder.WithVariable<IReadOnlyList<string>?>("EvaluatorEscalationQuestions", null);
        var userDocEscalationReason = builder.WithVariable<string?>("UserDocEscalationReason", null);
        var userDocEscalationQuestions = builder.WithVariable<IReadOnlyList<string>?>("UserDocEscalationQuestions", null);
        var agentEscalationReport = builder.WithVariable<string?>("AgentEscalationReport", null);
        var agentEscalationLoopCount = builder.WithVariable("AgentEscalationLoopCount", 0);

        IReadOnlyList<string> GetSpecialistRoleKeys(string teamName)
            => _teams.TryGetValue(teamName, out var team)
                ? team.SpecialistRoles.Select(s => s.Key).ToList()
                : ["BusinessAnalyst"];

        string AgentKey(ExpressionExecutionContext ctx, string role)
        {
            var teamName = ctx.GetInput<string>("TeamName") ?? string.Empty;
            return string.IsNullOrEmpty(teamName) ? $"Alveus{role}" : $"{teamName}:{role}";
        }

        var runPreTaskMeeting = new RunPreTaskMeeting(_compactionService)
        {
            Id = "RunPreTaskMeeting",
            TeamName = new Input<string>(context => context.GetInput<string>("TeamName") ?? string.Empty),
            SpecialistRoleKeys = new Input<IReadOnlyList<string>>(context =>
                GetSpecialistRoleKeys(context.GetInput<string>("TeamName") ?? string.Empty)),
            Topic = new Input<string>(context => context.GetInput<string>("TaskPrompt")!),
            ExtraContext = new Input<string?>(context =>
            {
                var reports = (specialistReports.Get(context)?.Values ?? [])
                    .Concat([qaReport.Get(context), techReport.Get(context), preTaskHumanReply.Get(context), agentEscalationReport.Get(context)])
                    .Where(r => !string.IsNullOrWhiteSpace(r));
                return string.Join("\n\n---\n", reports);
            }),
            WorkerInstructions = new Output<string>(workerInstructions),
            EvaluatorInstructions = new Output<string>(evaluatorInstructions),
            UserDocInstructions = new Output<string>(userDocInstructions),
            Reason = new Output<string?>(preTaskReason),
            Questions = new Output<IReadOnlyList<string>?>(preTaskQuestions),
        };

        var runWorker = new RunAgentPrompt(_compactionService)
        {
            Id = "RunWorker",
            AgentName = new Input<string>(context => AgentKey(context, "Worker")),
            TeamName = new Input<string>(context => context.GetInput<string>("TeamName") ?? string.Empty),
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
            Reason = new Output<string?>(workerEscalationReason),
            Questions = new Output<IReadOnlyList<string>?>(workerEscalationQuestions),
        };

        var runEnvironmentManager = new RunEnvironmentPrompt(_compactionService)
        {
            Id = "RunEnvironmentManager",
            AgentName = new Input<string>(context => AgentKey(context, "EnvironmentManager")),
            TeamName = new Input<string>(context => context.GetInput<string>("TeamName") ?? string.Empty),
            Prompt = new Input<string>(context => context.GetInput<string>("TaskPrompt")!),
            Summary = new Output<string>(envUsageInstructions),
            Reason = new Output<string?>(failureReport),
            Questions = new Output<IReadOnlyList<string>?>(envManagerEscalationQuestions),
        };

        var runEvaluator = new RunEvaluatorPrompt(_compactionService)
        {
            Id = "RunEvaluator",
            AgentName = new Input<string>(context => AgentKey(context, "Evaluator")),
            TeamName = new Input<string>(context => context.GetInput<string>("TeamName") ?? string.Empty),
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
            Questions = new Output<IReadOnlyList<string>?>(evaluatorEscalationQuestions),
        };

        var loopGuard = new LoopIterationGuard
        {
            Id = "LoopGuard",
            LoopCount = loopCount,
        };

        var runUserDoc = new RunUserDocPrompt(_compactionService)
        {
            Id = "RunUserDoc",
            AgentName = new Input<string>(context => AgentKey(context, "UserDoc")),
            TeamName = new Input<string>(context => context.GetInput<string>("TeamName") ?? string.Empty),
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
            Reason = new Output<string?>(userDocEscalationReason),
            Questions = new Output<IReadOnlyList<string>?>(userDocEscalationQuestions),
        };

        var runFinalReviewMeeting = new RunFinalReviewMeeting(_compactionService)
        {
            Id = "RunFinalReviewMeeting",
            TeamName = new Input<string>(context => context.GetInput<string>("TeamName") ?? string.Empty),
            SpecialistRoleKeys = new Input<IReadOnlyList<string>>(context =>
                GetSpecialistRoleKeys(context.GetInput<string>("TeamName") ?? string.Empty)),
            Topic = new Input<string>(context =>
                $"{context.GetInput<string>("TaskPrompt")}"
                + $"\n\n---\nRésumé Alveus-Worker :\n{workerSummary.Get(context)}"
                + $"\n\n---\nInstructions d'utilisation de l'environnement (Alveus-EnvironmentManager) :\n{envUsageInstructions.Get(context)}"
                + $"\n\n---\nRésumé Alveus-Evaluator :\n{evaluatorSummary.Get(context)}"
                + $"\n\n---\nRésumé Alveus-UserDoc :\n{userDocSummary.Get(context)}"),
            ExtraContext = new Input<string?>(context => finalReviewHumanReply.Get(context) ?? string.Empty),
            SpecialistReports = new Output<IReadOnlyDictionary<string, string>?>(specialistReports),
            QaReport = new Output<string?>(qaReport),
            TechReport = new Output<string?>(techReport),
            Reason = new Output<string?>(finalReviewReason),
            Questions = new Output<IReadOnlyList<string>?>(finalReviewQuestions),
        };

        var outerLoopGuard = new OuterLoopIterationGuard
        {
            Id = "OuterLoopGuard",
            OuterLoopCount = outerLoopCount,
        };

        var awaitPreTaskReply = new AwaitConversationReply
        {
            Id = "AwaitPreTaskReply",
            SourceLabel = new Input<string>("RunPreTaskMeeting"),
            Reason = new Input<string?>(context => preTaskReason.Get(context)),
            Questions = new Input<IReadOnlyList<string>?>(context => preTaskQuestions.Get(context)),
            HumanReply = new Output<string>(preTaskHumanReply),
        };

        var preTaskHelpGuard = new HelpLoopIterationGuard
        {
            Id = "PreTaskHelpLoopGuard",
            HelpLoopCount = preTaskHelpLoopCount,
        };

        var awaitFinalReviewReply = new AwaitConversationReply
        {
            Id = "AwaitFinalReviewReply",
            SourceLabel = new Input<string>("RunFinalReviewMeeting"),
            Reason = new Input<string?>(context => finalReviewReason.Get(context)),
            Questions = new Input<IReadOnlyList<string>?>(context => finalReviewQuestions.Get(context)),
            HumanReply = new Output<string>(finalReviewHumanReply),
        };

        var finalReviewHelpGuard = new HelpLoopIterationGuard
        {
            Id = "FinalReviewHelpLoopGuard",
            HelpLoopCount = finalReviewHelpLoopCount,
        };

        var recordWorkerEscalation = new RecordAgentEscalation
        {
            Id = "RecordWorkerEscalation",
            SourceLabel = new Input<string>("Alveus-Worker"),
            Reason = new Input<string?>(context => workerEscalationReason.Get(context)),
            Questions = new Input<IReadOnlyList<string>?>(context => workerEscalationQuestions.Get(context)),
            Report = agentEscalationReport,
        };

        var recordEnvironmentManagerEscalation = new RecordAgentEscalation
        {
            Id = "RecordEnvironmentManagerEscalation",
            SourceLabel = new Input<string>("Alveus-EnvironmentManager"),
            Reason = new Input<string?>(context => failureReport.Get(context)),
            Questions = new Input<IReadOnlyList<string>?>(context => envManagerEscalationQuestions.Get(context)),
            Report = agentEscalationReport,
        };

        var recordEvaluatorEscalation = new RecordAgentEscalation
        {
            Id = "RecordEvaluatorEscalation",
            SourceLabel = new Input<string>("Alveus-Evaluator"),
            Reason = new Input<string?>(context => failureReport.Get(context)),
            Questions = new Input<IReadOnlyList<string>?>(context => evaluatorEscalationQuestions.Get(context)),
            Report = agentEscalationReport,
        };

        var recordUserDocEscalation = new RecordAgentEscalation
        {
            Id = "RecordUserDocEscalation",
            SourceLabel = new Input<string>("Alveus-UserDoc"),
            Reason = new Input<string?>(context => userDocEscalationReason.Get(context)),
            Questions = new Input<IReadOnlyList<string>?>(context => userDocEscalationQuestions.Get(context)),
            Report = agentEscalationReport,
        };

        var agentEscalationLoopGuard = new AgentEscalationLoopGuard
        {
            Id = "AgentEscalationLoopGuard",
            AgentEscalationLoopCount = agentEscalationLoopCount,
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
                awaitPreTaskReply,
                preTaskHelpGuard,
                awaitFinalReviewReply,
                finalReviewHelpGuard,
                recordWorkerEscalation,
                recordEnvironmentManagerEscalation,
                recordEvaluatorEscalation,
                recordUserDocEscalation,
                agentEscalationLoopGuard,
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

                new Connection(new Endpoint(runPreTaskMeeting, "NeedsHelp"), new Endpoint(awaitPreTaskReply)),
                new Connection(new Endpoint(awaitPreTaskReply, "Done"), new Endpoint(preTaskHelpGuard)),
                new Connection(new Endpoint(preTaskHelpGuard, "Continue"), new Endpoint(runPreTaskMeeting)),

                new Connection(new Endpoint(runFinalReviewMeeting, "NeedsHelp"), new Endpoint(awaitFinalReviewReply)),
                new Connection(new Endpoint(awaitFinalReviewReply, "Done"), new Endpoint(finalReviewHelpGuard)),
                new Connection(new Endpoint(finalReviewHelpGuard, "Continue"), new Endpoint(runFinalReviewMeeting)),

                new Connection(new Endpoint(runWorker, "NeedsMoreInfo"), new Endpoint(recordWorkerEscalation)),
                new Connection(new Endpoint(runWorker, "Blocked"), new Endpoint(recordWorkerEscalation)),
                new Connection(new Endpoint(runEnvironmentManager, "NeedsMoreInfo"), new Endpoint(recordEnvironmentManagerEscalation)),
                new Connection(new Endpoint(runEnvironmentManager, "Blocked"), new Endpoint(recordEnvironmentManagerEscalation)),
                new Connection(new Endpoint(runEvaluator, "NeedsMoreInfo"), new Endpoint(recordEvaluatorEscalation)),
                new Connection(new Endpoint(runEvaluator, "Blocked"), new Endpoint(recordEvaluatorEscalation)),
                new Connection(new Endpoint(runUserDoc, "NeedsMoreInfo"), new Endpoint(recordUserDocEscalation)),
                new Connection(new Endpoint(runUserDoc, "Blocked"), new Endpoint(recordUserDocEscalation)),

                new Connection(new Endpoint(recordWorkerEscalation, "Done"), new Endpoint(agentEscalationLoopGuard)),
                new Connection(new Endpoint(recordEnvironmentManagerEscalation, "Done"), new Endpoint(agentEscalationLoopGuard)),
                new Connection(new Endpoint(recordEvaluatorEscalation, "Done"), new Endpoint(agentEscalationLoopGuard)),
                new Connection(new Endpoint(recordUserDocEscalation, "Done"), new Endpoint(agentEscalationLoopGuard)),
                new Connection(new Endpoint(agentEscalationLoopGuard, "Continue"), new Endpoint(runPreTaskMeeting)),
            ],
        };
    }
}
