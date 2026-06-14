using Alveus.Web.Activities;
using Elsa.Workflows;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Alveus.Web.Tests.Agent;

/// <summary>
/// Test d'intégration : exécute <see cref="RunUserDocPrompt"/> via <see cref="IWorkflowRunner"/>
/// et vérifie qu'il complète directement avec l'issue "Done" via FinishTool (pas de vérification
/// ADR 0020) — cf. ADR 0026. Sauté (avec message dans la sortie de test) si
/// ALVEUS_TEST_LLAMACPP_ENDPOINT n'est pas joignable.
/// </summary>
public sealed class RunUserDocPromptTests : IClassFixture<RunUserDocPromptFixture>
{
    private readonly RunUserDocPromptFixture _fixture;
    private readonly ITestOutputHelper _output;

    public RunUserDocPromptTests(RunUserDocPromptFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task RunUserDocPrompt_GivenTaskPrompt_CompletesWithSummary()
    {
        if (!_fixture.IsLlamaCppAvailable)
        {
            _output.WriteLine("llama.cpp indisponible sur ALVEUS_TEST_LLAMACPP_ENDPOINT — test ignoré.");
            return;
        }

        const string activityId = "run-userdoc-prompt-under-test";

        var activity = ActivatorUtilities.CreateInstance<RunUserDocPrompt>(_fixture.Services);
        activity.Id = activityId;
        activity.Prompt = new Input<string>(
            "Appelle directement ton outil de fin de tâche (Finish) avec outcome='done' et un résumé indiquant "
            + "qu'il n'y avait rien à documenter pour cette tâche.");

        var runner = _fixture.Services.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(activity, new RunWorkflowOptions(), CancellationToken.None);

        var outputRegister = result.WorkflowExecutionContext.GetActivityOutputRegister();
        var summary = outputRegister.FindOutputByActivityId(activityId, nameof(RunUserDocPrompt.Summary)) as string;

        Assert.False(string.IsNullOrWhiteSpace(summary));

        _output.WriteLine($"Statut workflow : {result.WorkflowState.Status}, résumé : {summary}");
    }

    [Fact]
    public async Task RunUserDocPrompt_NeedsMoreInfo_ReportsReasonAndQuestions()
    {
        if (!_fixture.IsLlamaCppAvailable)
        {
            _output.WriteLine("llama.cpp indisponible sur ALVEUS_TEST_LLAMACPP_ENDPOINT — test ignoré.");
            return;
        }

        const string activityId = "run-userdoc-prompt-needsmoreinfo";

        var activity = ActivatorUtilities.CreateInstance<RunUserDocPrompt>(_fixture.Services);
        activity.Id = activityId;
        activity.Prompt = new Input<string>(
            "Appelle directement ton outil de fin de tâche (Finish) avec outcome='needsmoreinfo', "
            + "reason='le périmètre de la documentation à mettre à jour n'est pas précisé' et "
            + "questions=['Quelle fonctionnalité documenter ?'].");

        var runner = _fixture.Services.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(activity, new RunWorkflowOptions(), CancellationToken.None);

        var outputRegister = result.WorkflowExecutionContext.GetActivityOutputRegister();
        var reason = outputRegister.FindOutputByActivityId(activityId, nameof(RunUserDocPrompt.Reason)) as string;
        var questions = outputRegister.FindOutputByActivityId(activityId, nameof(RunUserDocPrompt.Questions)) as IReadOnlyList<string>;

        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.NotNull(questions);
        Assert.NotEmpty(questions!);

        _output.WriteLine($"Statut workflow : {result.WorkflowState.Status}, raison : {reason}, questions : {string.Join(" | ", questions!)}");
    }
}
