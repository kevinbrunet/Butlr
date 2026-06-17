using System.Diagnostics;
using System.Reflection;
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
                ["TeamName"] = AlveusTaskWorkflowFixture.TeamName,
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
                ["TeamName"] = AlveusTaskWorkflowFixture.TeamName,
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
                ["TeamName"] = AlveusTaskWorkflowFixture.TeamName,
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
                ["TeamName"] = AlveusTaskWorkflowFixture.TeamName,
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
                ["TeamName"] = AlveusTaskWorkflowFixture.TeamName,
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
                ["TeamName"] = AlveusTaskWorkflowFixture.TeamName,
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

    /// <summary>
    /// Test de bout en bout : demande la création d'une application console .NET "Hello World",
    /// vérifie que le workflow se termine (<see cref="WorkflowStatus.Finished"/>) puis exécute le
    /// programme produit (<c>dotnet run</c> dans <see cref="AlveusTaskWorkflowFixture.WorkerWorkspaceRoot"/>)
    /// pour confirmer qu'il affiche bien "Hello World" sur la sortie standard.
    /// </summary>
    [Fact]
    public async Task AlveusTaskWorkflow_HelloWorldConsoleApp_CompletesAndProgramPrintsHelloWorld()
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
                ["TeamName"] = AlveusTaskWorkflowFixture.TeamName,
                ["TaskPrompt"] = "Si tu es Alveus-Worker : à la racine de ton espace de travail, crée une "
                    + "application console .NET (par exemple avec 'dotnet new console') dont le programme "
                    + "affiche exactement 'Hello World' (sans virgule) sur la sortie standard, puis appelle "
                    + "Finish avec outcome='done'. Si tu es Alveus-EnvironmentManager : il n'y a rien à démarrer "
                    + "pour une application console, appelle directement Finish avec outcome='done' et "
                    + "verdict='pass'. Si tu es Alveus-Evaluator : appelle directement Finish avec outcome='done' "
                    + "et verdict='pass'. Si tu es Alveus-UserDoc : appelle directement Finish avec "
                    + "outcome='done'. " + MeetingParticipantInstructions,
            },
        };

        var runner = _fixture.Services.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(workflow, options, CancellationToken.None);

        var outputRegister = result.WorkflowExecutionContext.GetActivityOutputRegister();
        var workerSummary = outputRegister.FindOutputByActivityId("RunWorker", nameof(RunAgentPrompt.Summary)) as string;

        var allFiles = Directory.GetFiles(_fixture.WorkerWorkspaceRoot, "*", SearchOption.AllDirectories);
        _output.WriteLine($"Statut workflow : {result.WorkflowState.Status}, résumé worker : {workerSummary}");
        _output.WriteLine($"Fichiers dans l'espace de travail : {string.Join(", ", allFiles.Select(f => Path.GetRelativePath(_fixture.WorkerWorkspaceRoot, f)))}");

        Assert.Equal(WorkflowStatus.Finished, result.WorkflowState.Status);

        var csprojPath = allFiles.FirstOrDefault(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrEmpty(csprojPath), $"Aucun .csproj trouvé dans {_fixture.WorkerWorkspaceRoot}.");

        var (exitCode, stdout, stderr) = await RunDotnetAsync(csprojPath, [], TimeSpan.FromMinutes(2));

        _output.WriteLine($"sortie 'dotnet run' (code {exitCode}) : {stdout}{stderr}");

        Assert.Equal(0, exitCode);
        Assert.Contains("Hello World", stdout);
    }

    /// <summary>
    /// Test de bout en bout : demande la création d'une application console .NET de gestion de
    /// liste de tâches (to-do list), pilotable via arguments de ligne de commande ('add', 'list',
    /// 'done'), avec persistance entre exécutions. Vérifie que le workflow se termine
    /// (<see cref="WorkflowStatus.Finished"/>) puis pilote l'application produite pour confirmer
    /// que l'ajout, l'affichage et le marquage "terminé" fonctionnent réellement.
    /// </summary>
    [Fact]
    public async Task AlveusTaskWorkflow_TodoListConsoleApp_CompletesAndCliWorks()
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
                ["TeamName"] = AlveusTaskWorkflowFixture.TeamName,
                ["TaskPrompt"] = "Si tu es Alveus-Worker : à la racine de ton espace de travail, crée une "
                    + "application console .NET de gestion de liste de tâches (to-do list), pilotable "
                    + "uniquement via des arguments de ligne de commande (pas de mode interactif), avec les "
                    + "commandes suivantes : 'add <description>' (ajoute une nouvelle tâche non terminée, "
                    + "avec un identifiant entier commençant à 1 et incrémenté de 1 à chaque ajout) ; "
                    + "'list' (affiche une ligne par tâche, au format exact '<id> [ ] <description>' pour "
                    + "une tâche non terminée et '<id> [x] <description>' pour une tâche terminée) ; "
                    + "'done <id>' (marque la tâche d'identifiant <id> comme terminée). Les tâches doivent "
                    + "être persistées dans un fichier à côté du projet pour être conservées entre deux "
                    + "exécutions. Une fois l'application créée et son bon fonctionnement vérifié, appelle "
                    + "Finish avec outcome='done'. Si tu es Alveus-EnvironmentManager : il n'y a rien à "
                    + "démarrer pour une application console, appelle directement Finish avec outcome='done' "
                    + "et verdict='pass'. Si tu es Alveus-Evaluator : appelle directement Finish avec "
                    + "outcome='done' et verdict='pass'. Si tu es Alveus-UserDoc : appelle directement Finish "
                    + "avec outcome='done'. " + MeetingParticipantInstructions,
            },
        };

        var runner = _fixture.Services.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(workflow, options, CancellationToken.None);

        var outputRegister = result.WorkflowExecutionContext.GetActivityOutputRegister();
        var workerSummary = outputRegister.FindOutputByActivityId("RunWorker", nameof(RunAgentPrompt.Summary)) as string;

        var allFiles = Directory.GetFiles(_fixture.WorkerWorkspaceRoot, "*", SearchOption.AllDirectories);
        _output.WriteLine($"Statut workflow : {result.WorkflowState.Status}, résumé worker : {workerSummary}");
        _output.WriteLine($"Fichiers dans l'espace de travail : {string.Join(", ", allFiles.Select(f => Path.GetRelativePath(_fixture.WorkerWorkspaceRoot, f)))}");

        Assert.Equal(WorkflowStatus.Finished, result.WorkflowState.Status);

        var csprojPath = allFiles.FirstOrDefault(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrEmpty(csprojPath), $"Aucun .csproj trouvé dans {_fixture.WorkerWorkspaceRoot}.");

        var timeout = TimeSpan.FromMinutes(2);

        var (addExit1, addOut1, addErr1) = await RunDotnetAsync(csprojPath, ["add", "Acheter du lait"], timeout);
        _output.WriteLine($"add 1 (code {addExit1}) : {addOut1}{addErr1}");
        Assert.Equal(0, addExit1);

        var (addExit2, addOut2, addErr2) = await RunDotnetAsync(csprojPath, ["add", "Faire les courses"], timeout);
        _output.WriteLine($"add 2 (code {addExit2}) : {addOut2}{addErr2}");
        Assert.Equal(0, addExit2);

        var (listExit1, listOut1, listErr1) = await RunDotnetAsync(csprojPath, ["list"], timeout);
        _output.WriteLine($"list 1 (code {listExit1}) : {listOut1}{listErr1}");
        Assert.Equal(0, listExit1);
        Assert.Contains("Acheter du lait", listOut1);
        Assert.Contains("Faire les courses", listOut1);
        Assert.DoesNotContain("[x]", listOut1);

        var (doneExit, doneOut, doneErr) = await RunDotnetAsync(csprojPath, ["done", "1"], timeout);
        _output.WriteLine($"done 1 (code {doneExit}) : {doneOut}{doneErr}");
        Assert.Equal(0, doneExit);

        var (listExit2, listOut2, listErr2) = await RunDotnetAsync(csprojPath, ["list"], timeout);
        _output.WriteLine($"list 2 (code {listExit2}) : {listOut2}{listErr2}");
        Assert.Equal(0, listExit2);

        var lines = listOut2.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var line1 = lines.Single(l => l.Contains("Acheter du lait"));
        var line2 = lines.Single(l => l.Contains("Faire les courses"));
        Assert.Contains("[x]", line1);
        Assert.Contains("[ ]", line2);
    }

    /// <summary>
    /// Test de bout en bout sur le pipeline complet (Worker, EnvironmentManager et Evaluator réels,
    /// sans verdict forcé) : demande une application Web ASP.NET Core de gestion de liste de
    /// tâches. Vérifie que le workflow se termine, que la tâche atteint <c>RunUserDoc</c> (donc que
    /// l'Evaluator a rendu un verdict "pass" après avoir écrit et exécuté un jeu de test), qu'un
    /// projet de tests référençant Microsoft.Playwright a bien été écrit dans l'espace de travail
    /// de l'Evaluator (cf. skill <c>dotnet-snapshot-testing</c>, ADR 0021), et que ces tests
    /// d'interface passent réellement lors d'une ré-exécution indépendante (<c>dotnet test</c>).
    /// ⚠ Pipeline complet incluant l'écriture et l'exécution de tests Playwright (installation des
    /// navigateurs comprise) — temps d'exécution potentiellement très long et flakiness élevée (cf.
    /// ADR 0021).
    /// </summary>
    [Fact]
    public async Task AlveusTaskWorkflow_AspNetTodoWebApp_HasWorkingUiTests()
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
                ["TeamName"] = AlveusTaskWorkflowFixture.TeamName,
                ["TaskPrompt"] = "Crée, à la racine de ton espace de travail, une application Web ASP.NET Core "
                    + "(Razor Pages ou Minimal API avec pages HTML, au choix) de gestion de liste de tâches "
                    + "(to-do list), avec une page d'accueil unique qui : affiche la liste des tâches "
                    + "existantes (description + statut terminé/à faire) ; propose un formulaire (champ texte "
                    + "+ bouton 'Ajouter') pour créer une nouvelle tâche ; permet de marquer une tâche comme "
                    + "terminée via une case à cocher ou un bouton (rechargement de page accepté). Les tâches "
                    + "sont conservées en mémoire (pas de base de données). Si tu es Alveus-Worker : une fois "
                    + "l'application créée et son démarrage local vérifié, appelle Finish avec outcome='done'. "
                    + "Si tu es Alveus-Evaluator : écris, à la racine de ton espace de travail, un projet de "
                    + "test xUnit C# (ex. 'dotnet new xunit') référençant le package NuGet Microsoft.Playwright "
                    + "(cf. skills/dotnet-snapshot-testing/references/playwright-ui.md), avec au moins un test "
                    + "qui pilote un navigateur contre la page d'accueil de l'application pour vérifier "
                    + "l'affichage de la liste de tâches, l'ajout d'une nouvelle tâche via le formulaire, et le "
                    + "marquage d'une tâche comme terminée. Exécute ce projet avec 'dotnet test' et n'appelle "
                    + "Finish avec verdict='pass' que si ces tests Playwright passent. "
                    + "Si tu es Alveus-UserDoc : appelle directement Finish avec outcome='done'. "
                    + MeetingParticipantInstructions,
            },
        };

        var runner = _fixture.Services.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(workflow, options, CancellationToken.None);

        var outputRegister = result.WorkflowExecutionContext.GetActivityOutputRegister();
        var workerSummary = outputRegister.FindOutputByActivityId("RunWorker", nameof(RunAgentPrompt.Summary)) as string;
        var envSummary = outputRegister.FindOutputByActivityId("RunEnvironmentManager", nameof(RunEnvironmentPrompt.Summary)) as string;
        var evaluatorSummary = outputRegister.FindOutputByActivityId("RunEvaluator", nameof(RunEvaluatorPrompt.Summary)) as string;
        var userDocSummary = outputRegister.FindOutputByActivityId("RunUserDoc", nameof(RunUserDocPrompt.Summary)) as string;

        _output.WriteLine($"Statut workflow : {result.WorkflowState.Status}");
        _output.WriteLine($"Résumé worker : {workerSummary}");
        _output.WriteLine($"Résumé environment manager : {envSummary}");
        _output.WriteLine($"Résumé evaluator : {evaluatorSummary}");
        _output.WriteLine($"Résumé userdoc : {userDocSummary}");

        // Diagnostics en cas d'échec : si l'Evaluator (ou un autre agent) sort par "NeedsMoreInfo"/"Blocked"
        // au lieu de "Passed", AgentEscalationLoopGuard renvoie vers RunPreTaskMeeting jusqu'à
        // AgentEscalationLoopGuard.MaxIterations (cf. ADR 0028), puis le workflow se termine sans
        // jamais atteindre RunUserDoc — sans ces sorties, la cause (quel agent, pourquoi) est invisible.
        if (string.IsNullOrWhiteSpace(userDocSummary))
        {
            var workerReason = outputRegister.FindOutputByActivityId("RunWorker", nameof(RunAgentPrompt.Reason)) as string;
            var workerQuestions = outputRegister.FindOutputByActivityId("RunWorker", nameof(RunAgentPrompt.Questions)) as IReadOnlyList<string>;
            var envReason = outputRegister.FindOutputByActivityId("RunEnvironmentManager", nameof(RunEnvironmentPrompt.Reason)) as string;
            var evaluatorReason = outputRegister.FindOutputByActivityId("RunEvaluator", nameof(RunEvaluatorPrompt.Reason)) as string;
            var evaluatorQuestions = outputRegister.FindOutputByActivityId("RunEvaluator", nameof(RunEvaluatorPrompt.Questions)) as IReadOnlyList<string>;
            var escalationIteration = outputRegister.FindOutputByActivityId("AgentEscalationLoopGuard", nameof(AgentEscalationLoopGuard.Iteration));

            _output.WriteLine($"[diag] Reason worker : {workerReason}");
            _output.WriteLine($"[diag] Questions worker : {(workerQuestions is null ? null : string.Join(" | ", workerQuestions))}");
            _output.WriteLine($"[diag] Reason environment manager : {envReason}");
            _output.WriteLine($"[diag] Reason evaluator : {evaluatorReason}");
            _output.WriteLine($"[diag] Questions evaluator : {(evaluatorQuestions is null ? null : string.Join(" | ", evaluatorQuestions))}");
            _output.WriteLine($"[diag] Itération AgentEscalationLoopGuard : {escalationIteration}");

            // Liste brute (réflexion) des (ActivityId, OutputName) pour lesquels une sortie a été
            // enregistrée — permet de voir jusqu'où le flowchart a réellement progressé même quand
            // les accesseurs typés ci-dessus renvoient tous null/vide.
            var recordsField = outputRegister.GetType().GetField("_recordsByActivityIdAndOutputName", BindingFlags.NonPublic | BindingFlags.Instance);
            if (recordsField?.GetValue(outputRegister) is System.Collections.IDictionary records)
            {
                var keys = records.Keys.Cast<object>().Select(k => k.ToString());
                _output.WriteLine($"[diag] Sorties enregistrées (ActivityId:OutputName) : {string.Join(", ", keys)}");
            }

            // SubStatus + incidents : un incident (ex. exception non gérée dans une activité)
            // laisse WorkflowStatus="Finished" mais SubStatus peut signaler "Faulted" — invisible
            // sans ça.
            _output.WriteLine($"[diag] SubStatus workflow : {result.WorkflowState.SubStatus}");
            foreach (var incident in result.WorkflowState.Incidents)
            {
                _output.WriteLine($"[diag] Incident sur {incident.ActivityId} ({incident.ActivityType}) : {incident.Message}");
                if (incident.Exception is not null)
                {
                    _output.WriteLine($"[diag]   Exception : {incident.Exception.Type} - {incident.Exception.Message}");
                    _output.WriteLine($"[diag]   StackTrace : {incident.Exception.StackTrace}");
                }
            }
        }

        Assert.Equal(WorkflowStatus.Finished, result.WorkflowState.Status);
        Assert.False(string.IsNullOrWhiteSpace(userDocSummary), "Alveus-UserDoc jamais atteint — l'Evaluator n'a probablement pas rendu verdict='pass'.");

        var evaluatorCsprojFiles = Directory.GetFiles(_fixture.EvaluatorWorkspaceRoot, "*.csproj", SearchOption.AllDirectories);
        _output.WriteLine($"Projets de test trouvés : {string.Join(", ", evaluatorCsprojFiles.Select(f => Path.GetRelativePath(_fixture.EvaluatorWorkspaceRoot, f)))}");

        var playwrightCsproj = evaluatorCsprojFiles.FirstOrDefault(f => File.ReadAllText(f).Contains("Playwright", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrEmpty(playwrightCsproj), $"Aucun projet de test référençant Playwright trouvé dans {_fixture.EvaluatorWorkspaceRoot}.");

        var (exitCode, stdout, stderr) = await RunDotnetCommandAsync(Path.GetDirectoryName(playwrightCsproj)!, ["test"], TimeSpan.FromMinutes(5));
        _output.WriteLine($"'dotnet test' (code {exitCode}) : {stdout}{stderr}");

        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// Exécute <c>dotnet run --project &lt;csprojPath&gt; -- &lt;arguments&gt;</c> dans le dossier du
    /// projet, et retourne le code de sortie ainsi que la sortie standard/erreur.
    /// </summary>
    private static Task<(int ExitCode, string StdOut, string StdErr)> RunDotnetAsync(string csprojPath, IEnumerable<string> arguments, TimeSpan timeout)
    {
        var dotnetArguments = new List<string> { "run", "--project", csprojPath, "--" };
        dotnetArguments.AddRange(arguments);

        return RunDotnetCommandAsync(Path.GetDirectoryName(csprojPath)!, dotnetArguments, timeout);
    }

    /// <summary>
    /// Exécute <c>dotnet &lt;arguments&gt;</c> dans <paramref name="workingDirectory"/>, et retourne
    /// le code de sortie ainsi que la sortie standard/erreur.
    /// </summary>
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunDotnetCommandAsync(string workingDirectory, IEnumerable<string> arguments, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        using var cts = new CancellationTokenSource(timeout);
        var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);

        return (process.ExitCode, stdout, stderr);
    }
}
