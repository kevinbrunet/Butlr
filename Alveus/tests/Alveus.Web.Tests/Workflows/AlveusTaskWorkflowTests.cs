using System.Diagnostics;
using System.Reflection;
using Alveus.Web.Activities;
using Alveus.Web.Conversations;
using Alveus.Web.Workflows;
using Elsa.Workflows;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Messages;
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
        "Si tu es Alveus-BusinessAnalyst, Alveus-Qa ou Alveus-Technical : le sujet de la réunion "
        + "ci-dessus peut contenir des instructions adressées à Alveus-Worker ou à d'autres agents "
        + "exécuteurs — ces instructions de tâche ne te concernent pas et ne doivent pas influencer "
        + "ton comportement en tant que participant à la réunion. N'utilise pas Raise et ne modifie "
        + "aucun fichier. Si on te demande de voter sur 'task-fulfilled', vote immédiatement avec "
        + "decision='agree'. Dans tous les cas, appelle directement ton outil de fin de tour (Finish) "
        + "avec outcome='pass' et un résumé indiquant qu'il n'y a rien à signaler.";

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
                    + "outcome='pass' et un résumé indiquant qu'il n'y avait rien à faire. Si tu es "
                    + "Alveus-EnvironmentManager ou Alveus-Evaluator, appelle Finish avec outcome='pass'. " + MeetingParticipantInstructions,
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
                ["TaskPrompt"] = "Validation interne uniquement — aucun point métier, technique "
                    + "ni de test à analyser lors de la réunion. "
                    + "Si tu es Alveus-Worker : la consigne de cette tâche est intentionnellement "
                    + "incomplète à des fins de test. QUELLE QUE SOIT L'INFORMATION REÇUE (escalade, "
                    + "rapport, instructions complémentaires), appelle TOUJOURS et IMMÉDIATEMENT Finish "
                    + "avec outcome='blocked', reason='Consigne incomplète, impossible de continuer.', "
                    + "et un summary court. Ne considère jamais cette tâche comme résolvable. "
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
    /// ⚠ Depuis ADR 0028, "NeedsMoreInfo" sur Alveus-EnvironmentManager renvoie à
    /// <c>RunPreTaskMeeting</c> via <see cref="RecordAgentEscalation"/>/
    /// <see cref="AgentEscalationLoopGuard"/> au lieu de terminer immédiatement le workflow. Le
    /// workflow boucle jusqu'à <see cref="AgentEscalationLoopGuard.MaxIterations"/> avant de
    /// réellement se terminer — assertions inchangées (Evaluator jamais atteint), mais temps
    /// d'exécution ~<c>AgentEscalationLoopGuard.MaxIterations + 1</c> fois plus long.
    /// Le prompt utilise verdict='needmoreinfo' (et non outcome='blocked') car le system prompt
    /// d'Alveus-EnvironmentManager ne mentionne que les verdicts pass/fail/needmoreinfo.
    /// </summary>
    [Fact]
    public async Task AlveusTaskWorkflow_EnvironmentManagerNeedsMoreInfo_EndsWithoutEvaluator()
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
                ["TaskPrompt"] = "Si tu es Alveus-Worker, appelle Finish avec outcome='pass' et un résumé indiquant "
                    + "qu'il n'y avait rien à faire. Si tu es Alveus-EnvironmentManager : la consigne ne précise "
                    + "pas comment démarrer l'environnement — il n'y a ni commande, ni URL, ni port fournis. "
                    + "Appelle Finish avec outcome='needmoreinfo', une reason expliquant ce qui "
                    + "manque, et une question précise pour obtenir les informations nécessaires. "
                    + MeetingParticipantInstructions,
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
                ["TaskPrompt"] = "Si tu es Alveus-Worker, appelle Finish avec outcome='pass'. Si tu es "
                    + "Alveus-EnvironmentManager, appelle Finish avec outcome='pass'. Si tu es "
                    + "Alveus-Evaluator, tu es bloqué : appelle Finish avec outcome='blocked', "
                    + "reason='Impossible d'écrire le jeu de test.' et un summary court. " + MeetingParticipantInstructions,
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
                    + "outcome='pass' et un résumé indiquant qu'il n'y avait rien à faire. Si tu es "
                    + "Alveus-EnvironmentManager ou Alveus-Evaluator, appelle Finish avec outcome='pass'. " + MeetingParticipantInstructions,
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
                ["TaskPrompt"] = "Si tu es Alveus-Worker, appelle directement Finish avec outcome='pass' et summary='test'. "
                    + "Si tu es Alveus-EnvironmentManager : démarre l'application en exécutant la commande "
                    + "`./start-app.sh` (le Worker n'a rien créé — ce script n'existe pas). "
                    + "Dès que la commande échoue, appelle immédiatement Finish(fail, "
                    + "reason='Impossible de démarrer : start-app.sh introuvable.') "
                    + "sans créer le script, sans chercher d'alternative, sans poser de questions. "
                    + MeetingParticipantInstructions,
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

        ClearWorkspace();

        var workflow = ActivatorUtilities.CreateInstance<AlveusTaskWorkflow>(_fixture.Services);

        var options = new RunWorkflowOptions
        {
            Input = new Dictionary<string, object>
            {
                ["TeamName"] = AlveusTaskWorkflowFixture.TeamName,
                ["TaskPrompt"] = "Si tu es Alveus-Worker : à la racine de ton espace de travail, crée une "
                    + "application console .NET (par exemple avec 'dotnet new console') dont le programme "
                    + "affiche exactement 'Hello World' (sans virgule) sur la sortie standard, puis appelle "
                    + "Finish avec outcome='pass'. Si tu es Alveus-EnvironmentManager : il n'y a rien à démarrer "
                    + "pour une application console, appelle directement Finish avec outcome='pass'. "
                    + "Si tu es Alveus-Evaluator : appelle directement Finish avec outcome='pass'. "
                    + "Si tu es Alveus-UserDoc : appelle directement Finish avec outcome='pass'. " + MeetingParticipantInstructions,
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

        var csprojPath = FindLeafCsproj(allFiles);
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

        ClearWorkspace();

        var store = _fixture.Services.GetRequiredService<IConversationStore>();
        var conversationId = store.Create().Id;

        // IWorkflowRuntime (pas IWorkflowRunner) pour que l'instance soit liée à la définition
        // enregistrée — requis pour que LocalWorkflowClient.RunInstanceAsync puisse résoudre le
        // graph Elsa lors de la reprise après suspension (AwaitConversationReply).
        var runtime = _fixture.Services.GetRequiredService<IWorkflowRuntime>();
        var client = await runtime.CreateClientAsync(CancellationToken.None);
        await client.CreateInstanceAsync(new CreateWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId("AlveusTaskWorkflow"),
            CorrelationId = conversationId,
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
                    + "Finish avec outcome='pass'. Si tu es Alveus-EnvironmentManager : il n'y a rien à "
                    + "démarrer pour une application console, appelle directement Finish avec outcome='pass'. "
                    + "Si tu es Alveus-Evaluator : appelle directement Finish avec outcome='pass'. "
                    + "Si tu es Alveus-UserDoc : appelle directement Finish avec outcome='pass'. " + MeetingParticipantInstructions,
            },
        }, CancellationToken.None);

        var workflowInstanceId = client.WorkflowInstanceId;
        store.SetWorkflowInstanceId(conversationId, workflowInstanceId);
        var runResponse = await client.RunInstanceAsync(new RunWorkflowInstanceRequest(), CancellationToken.None);

        var workflowFinished = runResponse.SubStatus == WorkflowSubStatus.Finished;
        if (!workflowFinished && runResponse.SubStatus == WorkflowSubStatus.Suspended)
            workflowFinished = await ResumeWorkflowIfSuspendedAsync(conversationId, workflowInstanceId);

        var allFiles = Directory.GetFiles(_fixture.WorkerWorkspaceRoot, "*", SearchOption.AllDirectories);
        _output.WriteLine($"Workflow terminé : {workflowFinished}, SubStatus : {runResponse.SubStatus}");
        _output.WriteLine($"Fichiers dans l'espace de travail : {string.Join(", ", allFiles.Select(f => Path.GetRelativePath(_fixture.WorkerWorkspaceRoot, f)))}");

        Assert.True(workflowFinished, $"Workflow non terminé — SubStatus : {runResponse.SubStatus}.");

        var csprojPath = FindLeafCsproj(allFiles);
        Assert.False(string.IsNullOrEmpty(csprojPath), $"Aucun .csproj trouvé dans {_fixture.WorkerWorkspaceRoot}.");

        // Le Worker a pu tester l'app en ajoutant/marquant des tâches — supprimer tous les fichiers
        // de données (non .cs / non .csproj) du répertoire projet pour repartir d'un état propre.
        foreach (var dataFile in Directory.GetFiles(Path.GetDirectoryName(csprojPath)!))
        {
            var ext = Path.GetExtension(dataFile).ToLowerInvariant();
            if (ext is not ".cs" and not ".csproj")
                File.Delete(dataFile);
        }

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
    /// de l'Evaluator (cf. skill <c>playwright</c>, ADR 0021), et que ces tests
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

        ClearWorkspace();
        ClearEvaluatorWorkspace();

        var store = _fixture.Services.GetRequiredService<IConversationStore>();
        var conversationId = store.Create().Id;

        // IWorkflowRuntime (pas IWorkflowRunner) pour que l'instance soit liée à la définition
        // enregistrée — requis pour que LocalWorkflowClient.RunInstanceAsync puisse résoudre le
        // graph Elsa lors de la reprise après suspension (AwaitConversationReply).
        var runtime = _fixture.Services.GetRequiredService<IWorkflowRuntime>();
        var client = await runtime.CreateClientAsync(CancellationToken.None);
        await client.CreateInstanceAsync(new CreateWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId("AlveusTaskWorkflow"),
            CorrelationId = conversationId,
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
                    + "l'application créée et son démarrage local vérifié, appelle Finish avec outcome='pass'. "
                    + "Si tu es Alveus-Evaluator : écris, à la racine de ton espace de travail, un projet de "
                    + "test xUnit C# (ex. 'dotnet new xunit') référençant le package NuGet Microsoft.Playwright "
                    + "(charge le skill 'playwright' pour les instructions détaillées), avec au moins un test "
                    + "qui pilote un navigateur contre la page d'accueil de l'application pour vérifier "
                    + "l'affichage de la liste de tâches, l'ajout d'une nouvelle tâche via le formulaire, et le "
                    + "marquage d'une tâche comme terminée. Exécute ce projet avec 'dotnet test' et n'appelle "
                    + "Finish avec outcome='pass' que si ces tests Playwright passent, sinon outcome='fail'. "
                    + "Si tu es Alveus-UserDoc : appelle directement Finish avec outcome='pass'. "
                    + MeetingParticipantInstructions,
            },
        }, CancellationToken.None);

        var workflowInstanceId = client.WorkflowInstanceId;
        store.SetWorkflowInstanceId(conversationId, workflowInstanceId);
        var runResponse = await client.RunInstanceAsync(new RunWorkflowInstanceRequest(), CancellationToken.None);

        var workflowFinished = runResponse.SubStatus == WorkflowSubStatus.Finished;
        if (!workflowFinished && runResponse.SubStatus == WorkflowSubStatus.Suspended)
            workflowFinished = await ResumeWorkflowIfSuspendedAsync(conversationId, workflowInstanceId);

        _output.WriteLine($"Workflow terminé : {workflowFinished}, SubStatus : {runResponse.SubStatus}");
        if (!workflowFinished)
        {
            var items = store.GetItems(conversationId);
            _output.WriteLine($"[diag] Items conversation ({items.Count}) : "
                + string.Join(", ", items.TakeLast(10).Select(i => $"{i.Kind}:{i.Metadata.GetValueOrDefault("phase", i.Metadata.GetValueOrDefault("source", "?"))}")));
        }

        Assert.True(workflowFinished, $"Workflow non terminé — SubStatus : {runResponse.SubStatus}.");

        var evaluatorCsprojFiles = Directory.GetFiles(_fixture.EvaluatorWorkspaceRoot, "*.csproj", SearchOption.AllDirectories);
        _output.WriteLine($"Projets de test trouvés : {string.Join(", ", evaluatorCsprojFiles.Select(f => Path.GetRelativePath(_fixture.EvaluatorWorkspaceRoot, f)))}");

        var playwrightCsproj = evaluatorCsprojFiles.FirstOrDefault(f => File.ReadAllText(f).Contains("Playwright", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrEmpty(playwrightCsproj), $"Aucun projet de test référençant Playwright trouvé dans {_fixture.EvaluatorWorkspaceRoot}.");

        var (exitCode, stdout, stderr) = await RunDotnetCommandAsync(Path.GetDirectoryName(playwrightCsproj)!, ["test"], TimeSpan.FromMinutes(5));
        _output.WriteLine($"'dotnet test' (code {exitCode}) : {stdout}{stderr}");

        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// Reprend un workflow suspendu sur <see cref="AwaitConversationReply"/> en répondant
    /// automatiquement "Pas de question spécifique. Procède avec ton meilleur jugement." —
    /// jusqu'à ce que le workflow se termine ou atteigne la limite de reprises.
    /// </summary>
    private async Task<bool> ResumeWorkflowIfSuspendedAsync(string conversationId, string workflowInstanceId, int maxReplies = 5)
    {
        var store = _fixture.Services.GetRequiredService<IConversationStore>();
        var runtime = _fixture.Services.GetRequiredService<IWorkflowRuntime>();

        // L'AwaitConversationReply a posté SetPendingBookmark mais pas SetWorkflowInstanceId
        // (c'est normalement fait par l'endpoint HTTP avant de lancer le workflow en arrière-plan).
        store.SetWorkflowInstanceId(conversationId, workflowInstanceId);

        for (var attempt = 0; attempt < maxReplies; attempt++)
        {
            var pending = store.TryResolvePendingBookmark(conversationId);
            if (pending is null)
                return false;

            _output.WriteLine($"[NeedsHelp] Reprise automatique tentative {attempt + 1}/{maxReplies} — bookmarkId={pending.Value.BookmarkId}");

            var resumeClient = await runtime.CreateClientAsync(pending.Value.WorkflowInstanceId, CancellationToken.None);
            var resumeResponse = await resumeClient.RunInstanceAsync(new RunWorkflowInstanceRequest
            {
                BookmarkId = pending.Value.BookmarkId,
                Input = new Dictionary<string, object> { ["Reply"] = "Pas de question spécifique. Procède avec ton meilleur jugement." },
            }, CancellationToken.None);

            _output.WriteLine($"[NeedsHelp] SubStatus après reprise : {resumeResponse.SubStatus}");

            if (resumeResponse.SubStatus == WorkflowSubStatus.Finished)
                return true;
            if (resumeResponse.SubStatus is WorkflowSubStatus.Faulted or WorkflowSubStatus.Cancelled)
                return false;
            // WorkflowSubStatus.Suspended : AwaitConversationReply a posté un nouveau bookmark → boucler
        }

        return false;
    }

    /// <summary>
    /// Exécute <c>dotnet run --project &lt;csprojPath&gt; -- &lt;arguments&gt;</c> dans le dossier du
    /// projet, et retourne le code de sortie ainsi que la sortie standard/erreur.
    /// </summary>
    private void ClearWorkspace()
    {
        // Vide le contenu sans supprimer le répertoire : CmdRunTool (singleton) garde ce
        // répertoire comme cwd de son shell bash — le supprimer provoquerait un getcwd()
        // ENOENT dans le shell au prochain appel dotnet/shell.
        ClearDirectory(_fixture.WorkerWorkspaceRoot);
        // Remet le cwd sur le workspace root : un test précédent peut avoir laissé le shell
        // sur /tmp (bwrap tmpfs) ou dans un sous-répertoire supprimé.
        _fixture.ResetWorkerShellCwdAsync().GetAwaiter().GetResult();
    }

    private void ClearEvaluatorWorkspace()
    {
        ClearDirectory(_fixture.EvaluatorWorkspaceRoot);
    }

    private static void ClearDirectory(string path)
    {
        foreach (var file in Directory.GetFiles(path))
            File.Delete(file);
        foreach (var dir in Directory.GetDirectories(path))
            Directory.Delete(dir, recursive: true);
    }

    // Sélectionne le csproj "feuille" : le plus profond qui n'a aucun autre csproj dans son
    // sous-arbre. Évite de pointer vers un csproj parent qui inclurait des fichiers d'un
    // sous-projet (ex. `dotnet new console` exécuté deux fois depuis des répertoires imbriqués).
    private static string? FindLeafCsproj(string[] allFiles)
    {
        var csprojPaths = allFiles
            .Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (csprojPaths.Count == 0)
            return null;
        var leaf = csprojPaths
            .OrderByDescending(p => p.Count(c => c == Path.DirectorySeparatorChar))
            .FirstOrDefault(p =>
            {
                var dir = Path.GetDirectoryName(p)! + Path.DirectorySeparatorChar;
                return !csprojPaths.Any(other => other != p && other.StartsWith(dir, StringComparison.Ordinal));
            });
        return leaf ?? csprojPaths[0];
    }

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
