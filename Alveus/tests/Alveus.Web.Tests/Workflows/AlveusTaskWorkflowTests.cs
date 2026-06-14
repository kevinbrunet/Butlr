using Alveus.Web.Activities;
using Alveus.Web.Conversations;
using Alveus.Web.Workflows;
using Elsa.Workflows;
using Elsa.Workflows.Options;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Alveus.Web.Tests.Workflows;

/// <summary>
/// Test d'intégration de bout en bout de <see cref="AlveusTaskWorkflow"/> (cf. ADR 0023, étendu par
/// ADR 0024/0026) : RunPreTaskMeeting → Worker → EnvironmentManager → Evaluator, avec verdicts
/// "pass" déclenchés directement via FinishTool pour ne dépendre d'aucun environnement réel.
/// Vérifie que le graphe Flowchart enchaîne correctement les activités jusqu'au verdict "Passed".
/// Sauté (avec message dans la sortie de test) si ALVEUS_TEST_LLAMACPP_ENDPOINT n'est pas
/// joignable.
/// ⚠ Ce test dépend du comportement du LLM pour suivre des instructions multi-étapes — flakiness
/// possible (cf. ADR 0021).
/// </summary>
public sealed class AlveusTaskWorkflowTests : IClassFixture<AlveusTaskWorkflowFixture>
{
    /// <summary>
    /// Instructions ajoutées à chaque <c>TaskPrompt</c> pour qu'Alveus-BusinessAnalyst/Alveus-Qa/
    /// Alveus-Technical (réunions de pré-tâche et finale, cf. ADR 0024) confirment immédiatement
    /// sans débat, afin que ces tests restent concentrés sur le cycle
    /// Worker/EnvironmentManager/Evaluator (ADR 0023).
    /// </summary>
    private const string MeetingParticipantInstructions =
        "Si tu es Alveus-BusinessAnalyst, Alveus-Qa ou Alveus-Technical : n'utilise pas Raise et ne modifie aucun "
        + "fichier. Si on te demande de voter sur 'task-fulfilled', vote immédiatement avec decision='agree'. Dans "
        + "tous les cas, appelle directement ton outil de fin de tour (Finish) avec outcome='done' et un résumé "
        + "indiquant qu'il n'y a rien à signaler.";

    private readonly AlveusTaskWorkflowFixture _fixture;
    private readonly ITestOutputHelper _output;

    public AlveusTaskWorkflowTests(AlveusTaskWorkflowFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task AlveusTaskWorkflow_AllVerdictsPass_CompletesAsPassed()
    {
        if (!_fixture.IsLlamaCppAvailable)
        {
            _output.WriteLine("llama.cpp indisponible sur ALVEUS_TEST_LLAMACPP_ENDPOINT — test ignoré.");
            return;
        }

        var workflow = ActivatorUtilities.CreateInstance<AlveusTaskWorkflow>(_fixture.Services);

        var options = new RunWorkflowOptions
        {
            Input = new Dictionary<string, object>
            {
                ["TaskPrompt"] = "Si tu es Alveus-Worker, appelle directement ton outil de fin de tâche (Finish) avec "
                    + "outcome='done' et un résumé indiquant qu'il n'y avait rien à faire. Si tu es "
                    + "Alveus-EnvironmentManager ou Alveus-Evaluator, appelle Finish avec outcome='done' et "
                    + "verdict='pass'. " + MeetingParticipantInstructions,
            },
        };

        var runner = _fixture.Services.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(workflow, options, CancellationToken.None);

        var outputRegister = result.WorkflowExecutionContext.GetActivityOutputRegister();
        var workerSummary = outputRegister.FindOutputByActivityId("RunWorker", nameof(RunAgentPrompt.Summary)) as string;
        var envSummary = outputRegister.FindOutputByActivityId("RunEnvironmentManager", nameof(RunEnvironmentPrompt.Summary)) as string;
        var evaluatorSummary = outputRegister.FindOutputByActivityId("RunEvaluator", nameof(RunEvaluatorPrompt.Summary)) as string;

        Assert.False(string.IsNullOrWhiteSpace(workerSummary));
        Assert.False(string.IsNullOrWhiteSpace(envSummary));
        Assert.False(string.IsNullOrWhiteSpace(evaluatorSummary));

        _output.WriteLine($"Statut workflow : {result.WorkflowState.Status}, "
            + $"résumé worker : {workerSummary}, résumé env : {envSummary}, résumé evaluator : {evaluatorSummary}");
    }

    /// <summary>
    /// ⚠ Depuis ADR 0028, "Blocked" sur Alveus-Worker renvoie à <c>RunPreTaskMeeting</c> via
    /// <see cref="RecordAgentEscalation"/>/<see cref="AgentEscalationLoopGuard"/> au lieu de
    /// terminer immédiatement le workflow. Comme le prompt rebloque Alveus-Worker à chaque
    /// itération, le workflow boucle jusqu'à <see cref="AgentEscalationLoopGuard.MaxIterations"/>
    /// avant de réellement se terminer — assertions inchangées (EnvironmentManager jamais atteint),
    /// mais temps d'exécution ~<c>AgentEscalationLoopGuard.MaxIterations + 1</c> fois plus long.
    /// </summary>
    [Fact]
    public async Task AlveusTaskWorkflow_WorkerBlocked_EndsWithoutEnvironmentManager()
    {
        if (!_fixture.IsLlamaCppAvailable)
        {
            _output.WriteLine("llama.cpp indisponible sur ALVEUS_TEST_LLAMACPP_ENDPOINT — test ignoré.");
            return;
        }

        var workflow = ActivatorUtilities.CreateInstance<AlveusTaskWorkflow>(_fixture.Services);

        var options = new RunWorkflowOptions
        {
            Input = new Dictionary<string, object>
            {
                ["TaskPrompt"] = "Si tu es Alveus-Worker, tu es bloqué : appelle immédiatement Finish avec "
                    + "outcome='blocked' et reason='Consigne ambiguë, impossible de continuer.'. "
                    + MeetingParticipantInstructions,
            },
        };

        var runner = _fixture.Services.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(workflow, options, CancellationToken.None);

        var outputRegister = result.WorkflowExecutionContext.GetActivityOutputRegister();
        var workerReason = outputRegister.FindOutputByActivityId("RunWorker", nameof(RunAgentPrompt.Reason)) as string;
        var envSummary = outputRegister.FindOutputByActivityId("RunEnvironmentManager", nameof(RunEnvironmentPrompt.Summary));

        Assert.False(string.IsNullOrWhiteSpace(workerReason));
        Assert.Null(envSummary);

        _output.WriteLine($"Statut workflow : {result.WorkflowState.Status}, raison worker : {workerReason}");
    }

    /// <summary>
    /// ⚠ Depuis ADR 0028, "Blocked" sur Alveus-EnvironmentManager renvoie à
    /// <c>RunPreTaskMeeting</c> via <see cref="RecordAgentEscalation"/>/
    /// <see cref="AgentEscalationLoopGuard"/> au lieu de terminer immédiatement le workflow. Le
    /// workflow boucle jusqu'à <see cref="AgentEscalationLoopGuard.MaxIterations"/> avant de
    /// réellement se terminer — assertions inchangées (Evaluator jamais atteint), mais temps
    /// d'exécution ~<c>AgentEscalationLoopGuard.MaxIterations + 1</c> fois plus long.
    /// </summary>
    [Fact]
    public async Task AlveusTaskWorkflow_EnvironmentManagerBlocked_EndsWithoutEvaluator()
    {
        if (!_fixture.IsLlamaCppAvailable)
        {
            _output.WriteLine("llama.cpp indisponible sur ALVEUS_TEST_LLAMACPP_ENDPOINT — test ignoré.");
            return;
        }

        var workflow = ActivatorUtilities.CreateInstance<AlveusTaskWorkflow>(_fixture.Services);

        var options = new RunWorkflowOptions
        {
            Input = new Dictionary<string, object>
            {
                ["TaskPrompt"] = "Si tu es Alveus-Worker, appelle Finish avec outcome='done' et un résumé indiquant "
                    + "qu'il n'y avait rien à faire. Si tu es Alveus-EnvironmentManager, tu es bloqué : appelle "
                    + "Finish avec outcome='blocked' et reason='Impossible de déterminer comment démarrer "
                    + "l'environnement.'. " + MeetingParticipantInstructions,
            },
        };

        var runner = _fixture.Services.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(workflow, options, CancellationToken.None);

        var outputRegister = result.WorkflowExecutionContext.GetActivityOutputRegister();
        var workerSummary = outputRegister.FindOutputByActivityId("RunWorker", nameof(RunAgentPrompt.Summary)) as string;
        var envReason = outputRegister.FindOutputByActivityId("RunEnvironmentManager", nameof(RunEnvironmentPrompt.Reason)) as string;
        var evaluatorSummary = outputRegister.FindOutputByActivityId("RunEvaluator", nameof(RunEvaluatorPrompt.Summary));

        Assert.False(string.IsNullOrWhiteSpace(workerSummary));
        Assert.False(string.IsNullOrWhiteSpace(envReason));
        Assert.Null(evaluatorSummary);

        _output.WriteLine($"Statut workflow : {result.WorkflowState.Status}, raison env : {envReason}");
    }

    /// <summary>
    /// ⚠ Depuis ADR 0028, "Blocked" sur Alveus-Evaluator renvoie à <c>RunPreTaskMeeting</c> via
    /// <see cref="RecordAgentEscalation"/>/<see cref="AgentEscalationLoopGuard"/> au lieu de
    /// terminer immédiatement le workflow — <see cref="LoopIterationGuard"/> (cycle interne
    /// Worker/EnvironmentManager/Evaluator, ADR 0023) reste bien inutilisé (nom du test conservé),
    /// mais le workflow boucle désormais jusqu'à <see cref="AgentEscalationLoopGuard.MaxIterations"/>
    /// via le nouveau garde avant de réellement se terminer — temps d'exécution
    /// ~<c>AgentEscalationLoopGuard.MaxIterations + 1</c> fois plus long.
    /// </summary>
    [Fact]
    public async Task AlveusTaskWorkflow_EvaluatorBlocked_EndsWithoutLooping()
    {
        if (!_fixture.IsLlamaCppAvailable)
        {
            _output.WriteLine("llama.cpp indisponible sur ALVEUS_TEST_LLAMACPP_ENDPOINT — test ignoré.");
            return;
        }

        var workflow = ActivatorUtilities.CreateInstance<AlveusTaskWorkflow>(_fixture.Services);

        var options = new RunWorkflowOptions
        {
            Input = new Dictionary<string, object>
            {
                ["TaskPrompt"] = "Si tu es Alveus-Worker, appelle Finish avec outcome='done'. Si tu es "
                    + "Alveus-EnvironmentManager, appelle Finish avec outcome='done' et verdict='pass'. Si tu es "
                    + "Alveus-Evaluator, tu es bloqué : appelle Finish avec outcome='blocked' et "
                    + "reason='Impossible d'écrire le jeu de test.'. " + MeetingParticipantInstructions,
            },
        };

        var runner = _fixture.Services.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(workflow, options, CancellationToken.None);

        var outputRegister = result.WorkflowExecutionContext.GetActivityOutputRegister();
        var evaluatorReason = outputRegister.FindOutputByActivityId("RunEvaluator", nameof(RunEvaluatorPrompt.Reason)) as string;
        var loopGuardIteration = outputRegister.FindOutputByActivityId("LoopGuard", nameof(LoopIterationGuard.Iteration));

        Assert.False(string.IsNullOrWhiteSpace(evaluatorReason));
        Assert.Null(loopGuardIteration);

        _output.WriteLine($"Statut workflow : {result.WorkflowState.Status}, raison evaluator : {evaluatorReason}");
    }

    /// <summary>
    /// ⚠ Vérifie que <see cref="AlveusTaskWorkflow.RunPreTaskMeeting"/> poste un item
    /// <see cref="ConversationItemKind.MeetingRound"/> par round (cf. ADR 0027) dès qu'un
    /// <c>CorrelationId</c> est fourni à <see cref="IWorkflowRunner.RunAsync"/> — propagé via
    /// <c>WorkflowExecutionContext.CorrelationId</c> jusqu'à <see cref="IConversationContextAccessor"/>.
    /// </summary>
    [Fact]
    public async Task AlveusTaskWorkflow_WithCorrelationId_PostsMeetingRoundItems()
    {
        if (!_fixture.IsLlamaCppAvailable)
        {
            _output.WriteLine("llama.cpp indisponible sur ALVEUS_TEST_LLAMACPP_ENDPOINT — test ignoré.");
            return;
        }

        var workflow = ActivatorUtilities.CreateInstance<AlveusTaskWorkflow>(_fixture.Services);

        var store = _fixture.Services.GetRequiredService<IConversationStore>();
        var conversationId = store.Create().Id;

        var options = new RunWorkflowOptions
        {
            CorrelationId = conversationId,
            Input = new Dictionary<string, object>
            {
                ["TaskPrompt"] = "Si tu es Alveus-Worker, appelle directement ton outil de fin de tâche (Finish) avec "
                    + "outcome='done' et un résumé indiquant qu'il n'y avait rien à faire. Si tu es "
                    + "Alveus-EnvironmentManager ou Alveus-Evaluator, appelle Finish avec outcome='done' et "
                    + "verdict='pass'. " + MeetingParticipantInstructions,
            },
        };

        var runner = _fixture.Services.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(workflow, options, CancellationToken.None);

        var items = store.GetItems(conversationId);

        Assert.Contains(items, i => i.Kind == ConversationItemKind.MeetingRound
            && i.Metadata.TryGetValue("meeting", out var meeting) && meeting == "RunPreTaskMeeting");

        _output.WriteLine($"Statut workflow : {result.WorkflowState.Status}, items conversation : {items.Count}");
    }

    /// <summary>
    /// ⚠ Cycle complet de correction (RunEnvironmentManager "Failed" → LoopGuard → retour à
    /// RunWorker) jusqu'à <see cref="LoopIterationGuard.MaxIterations"/> : l'EnvironmentManager
    /// renvoie systématiquement verdict='fail', donc Alveus-Evaluator n'est jamais sollicité.
    /// Ce test enchaîne <c>MaxIterations + 1</c> cycles Worker/EnvironmentManager — sensiblement
    /// plus lent que les autres tests d'intégration de ce fichier.
    /// </summary>
    [Fact]
    public async Task AlveusTaskWorkflow_EnvironmentManagerAlwaysFails_LoopsUntilLimitReached()
    {
        if (!_fixture.IsLlamaCppAvailable)
        {
            _output.WriteLine("llama.cpp indisponible sur ALVEUS_TEST_LLAMACPP_ENDPOINT — test ignoré.");
            return;
        }

        var workflow = ActivatorUtilities.CreateInstance<AlveusTaskWorkflow>(_fixture.Services);

        var options = new RunWorkflowOptions
        {
            Input = new Dictionary<string, object>
            {
                ["TaskPrompt"] = "Si tu es Alveus-Worker, appelle Finish avec outcome='done' et un résumé indiquant "
                    + "qu'il n'y avait rien à faire, même si un rapport d'évaluation précédent est joint au message. "
                    + "Si tu es Alveus-EnvironmentManager, l'environnement ne démarre jamais : appelle "
                    + "systématiquement Finish avec outcome='done', verdict='fail' et reason='L'environnement ne "
                    + "démarre pas.'. N'appelle jamais Alveus-Evaluator. " + MeetingParticipantInstructions,
            },
        };

        var runner = _fixture.Services.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(workflow, options, CancellationToken.None);

        var outputRegister = result.WorkflowExecutionContext.GetActivityOutputRegister();
        var failureReport = outputRegister.FindOutputByActivityId("RunEnvironmentManager", nameof(RunEnvironmentPrompt.Reason)) as string;
        var loopGuardIteration = outputRegister.FindOutputByActivityId("LoopGuard", nameof(LoopIterationGuard.Iteration));
        var evaluatorSummary = outputRegister.FindOutputByActivityId("RunEvaluator", nameof(RunEvaluatorPrompt.Summary));

        Assert.False(string.IsNullOrWhiteSpace(failureReport));
        Assert.Equal(LoopIterationGuard.MaxIterations + 1, loopGuardIteration);
        Assert.Null(evaluatorSummary);

        _output.WriteLine($"Statut workflow : {result.WorkflowState.Status}, itérations LoopGuard : {loopGuardIteration}, "
            + $"rapport d'échec : {failureReport}");
    }
}
