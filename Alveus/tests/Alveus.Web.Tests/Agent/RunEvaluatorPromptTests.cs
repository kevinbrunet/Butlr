using Alveus.Web.Activities;
using Elsa.Workflows;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Alveus.Web.Tests.Agent;

/// <summary>
/// Test d'intégration : exécute <see cref="RunEvaluatorPrompt"/> via
/// <see cref="IWorkflowRunner"/> et vérifie qu'à partir d'une consigne de tâche, l'agent écrit un
/// jeu de test dans son workspace isolé, sans déclencher de vérification de son propre travail
/// (contrairement à <see cref="RunAgentPrompt"/>) — cf. ADR 0021. Sauté (avec message dans la
/// sortie de test) si ALVEUS_TEST_LLAMACPP_ENDPOINT n'est pas joignable.
/// </summary>
public sealed class RunEvaluatorPromptTests : IClassFixture<RunEvaluatorPromptFixture>
{
    private readonly RunEvaluatorPromptFixture _fixture;
    private readonly ITestOutputHelper _output;

    public RunEvaluatorPromptTests(RunEvaluatorPromptFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task RunEvaluatorPrompt_GivenTaskPrompt_WritesTestSuiteAndCompletesWithVerdict()
    {
        if (!_fixture.IsLlamaCppAvailable)
        {
            _output.WriteLine("llama.cpp indisponible sur ALVEUS_TEST_LLAMACPP_ENDPOINT — test ignoré.");
            return;
        }

        const string activityId = "run-evaluator-prompt-under-test";

        var activity = ActivatorUtilities.CreateInstance<RunEvaluatorPrompt>(_fixture.Services);
        activity.Id = activityId;
        activity.Prompt = new Input<string>(
            "Voici la consigne de tâche donnée à l'agent d'exécution : \"Crée un fichier nommé 'hello.txt' "
            + "contenant exactement le texte 'hello'.\". Avec ton outil d'édition de fichiers, crée dans ton "
            + "espace de travail un fichier nommé 'test_hello.sh' contenant un script de test qui vérifierait "
            + "qu'un travail répondant à cette consigne est correct, puis appelle l'outil de fin de tâche "
            + "(Finish) avec outcome='pass'.");

        var runner = _fixture.Services.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(activity, new RunWorkflowOptions(), CancellationToken.None);

        var writtenFiles = Directory.GetFiles(_fixture.WorkspaceRoot);
        Assert.NotEmpty(writtenFiles);

        var outputRegister = result.WorkflowExecutionContext.GetActivityOutputRegister();
        var summary = outputRegister.FindOutputByActivityId(activityId, nameof(RunEvaluatorPrompt.Summary)) as string;
        var reason = outputRegister.FindOutputByActivityId(activityId, nameof(RunEvaluatorPrompt.Reason)) as string;
        Assert.False(string.IsNullOrWhiteSpace(summary));
        Assert.Null(reason);

        _output.WriteLine($"Statut workflow : {result.WorkflowState.Status}, fichiers écrits : {string.Join(", ", writtenFiles.Select(Path.GetFileName))}, résumé : {summary}");
    }

    [Fact]
    public async Task RunEvaluatorPrompt_VerdictFail_ReportsReason()
    {
        if (!_fixture.IsLlamaCppAvailable)
        {
            _output.WriteLine("llama.cpp indisponible sur ALVEUS_TEST_LLAMACPP_ENDPOINT — test ignoré.");
            return;
        }

        const string activityId = "run-evaluator-prompt-fail";

        var activity = ActivatorUtilities.CreateInstance<RunEvaluatorPrompt>(_fixture.Services);
        activity.Id = activityId;
        activity.Prompt = new Input<string>(
            "Tu viens de vérifier le résultat de la tâche 'crée un fichier hello.txt contenant hello'. "
            + "Le fichier existe mais son contenu est 'world' au lieu de 'hello' — le test a échoué. "
            + "Appelle l'outil Finish avec outcome='fail', "
            + "une reason décrivant l'écart constaté, et un summary résumant la situation.");

        var runner = _fixture.Services.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(activity, new RunWorkflowOptions(), CancellationToken.None);

        var outputRegister = result.WorkflowExecutionContext.GetActivityOutputRegister();
        var reason = outputRegister.FindOutputByActivityId(activityId, nameof(RunEvaluatorPrompt.Reason)) as string;
        Assert.False(string.IsNullOrWhiteSpace(reason));

        _output.WriteLine($"Statut workflow : {result.WorkflowState.Status}, raison : {reason}");
    }

    [Fact]
    public async Task RunEvaluatorPrompt_VerdictNeedMoreInfo_ReportsReasonAndQuestions()
    {
        if (!_fixture.IsLlamaCppAvailable)
        {
            _output.WriteLine("llama.cpp indisponible sur ALVEUS_TEST_LLAMACPP_ENDPOINT — test ignoré.");
            return;
        }

        const string activityId = "run-evaluator-prompt-needmoreinfo";

        var activity = ActivatorUtilities.CreateInstance<RunEvaluatorPrompt>(_fixture.Services);
        activity.Id = activityId;
        activity.Prompt = new Input<string>(
            "Tu dois vérifier que la tâche a été accomplie, mais les instructions d'utilisation de "
            + "l'environnement ne précisent aucune URL ni port pour accéder au service — tu ne peux pas "
            + "exécuter les tests sans ces informations. "
            + "Appelle l'outil Finish avec outcome='needmoreinfo', "
            + "une reason expliquant ce qui manque, au moins une question précise pour obtenir les informations "
            + "nécessaires, et un summary.");

        var runner = _fixture.Services.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(activity, new RunWorkflowOptions(), CancellationToken.None);

        var outputRegister = result.WorkflowExecutionContext.GetActivityOutputRegister();
        var reason = outputRegister.FindOutputByActivityId(activityId, nameof(RunEvaluatorPrompt.Reason)) as string;
        var questions = outputRegister.FindOutputByActivityId(activityId, nameof(RunEvaluatorPrompt.Questions)) as IReadOnlyList<string>;

        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.NotNull(questions);
        Assert.NotEmpty(questions!);

        _output.WriteLine($"Statut workflow : {result.WorkflowState.Status}, raison : {reason}, questions : {string.Join(" | ", questions!)}");
    }
}
