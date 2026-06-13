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
    public async Task RunEvaluatorPrompt_GivenTaskPrompt_WritesTestSuiteAndCompletesAsDone()
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
            + "(Finish) avec outcome='done'.");

        var runner = _fixture.Services.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(activity, new RunWorkflowOptions(), CancellationToken.None);

        var writtenFiles = Directory.GetFiles(_fixture.WorkspaceRoot);
        Assert.NotEmpty(writtenFiles);

        var outputRegister = result.WorkflowExecutionContext.GetActivityOutputRegister();
        var summary = outputRegister.FindOutputByActivityId(activityId, nameof(RunEvaluatorPrompt.Summary)) as string;
        Assert.False(string.IsNullOrWhiteSpace(summary));

        _output.WriteLine($"Statut workflow : {result.WorkflowState.Status}, fichiers écrits : {string.Join(", ", writtenFiles.Select(Path.GetFileName))}, résumé : {summary}");
    }
}
