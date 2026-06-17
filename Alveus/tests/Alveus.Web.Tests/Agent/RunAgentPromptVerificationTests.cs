using Alveus.Web.Activities;
using Elsa.Workflows;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Alveus.Web.Tests.Agent;

/// <summary>
/// Test d'intégration : exécute <see cref="RunAgentPrompt"/> via <see cref="IWorkflowRunner"/>
/// avec <c>Agent:VerificationCommand</c> configuré (cf. <see cref="RunAgentPromptVerificationFixture"/>
/// et ADR 0020). La commande de vérification échoue au premier appel puis réussit, ce qui doit
/// provoquer une relance de l'agent (boucle de relance ADR 0019/0020) avant la sortie "Done".
/// Sauté (avec message dans la sortie de test) si ALVEUS_TEST_LLAMACPP_ENDPOINT n'est pas joignable.
/// </summary>
public sealed class RunAgentPromptVerificationTests : IClassFixture<RunAgentPromptVerificationFixture>
{
    private readonly RunAgentPromptVerificationFixture _fixture;
    private readonly ITestOutputHelper _output;

    public RunAgentPromptVerificationTests(RunAgentPromptVerificationFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task RunAgentPrompt_WithFailingThenPassingVerification_RetriesThenCompletesAsDone()
    {
        if (!_fixture.IsLlamaCppAvailable)
        {
            _output.WriteLine("llama.cpp indisponible sur ALVEUS_TEST_LLAMACPP_ENDPOINT — test ignoré.");
            return;
        }

        const string activityId = "run-agent-prompt-under-test";

        var activity = ActivatorUtilities.CreateInstance<RunAgentPrompt>(_fixture.Services);
        activity.Id = activityId;
        activity.Prompt = new Input<string>(
            "Appelle directement ton outil de fin de tâche (Finish) avec outcome='done' et un résumé "
            + "indiquant qu'il n'y avait rien à faire.");

        var runner = _fixture.Services.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(activity, new RunWorkflowOptions(), CancellationToken.None);

        var counterPath = Path.Combine(_fixture.WorkerWorkspaceRoot, RunAgentPromptVerificationFixture.VerificationCounterFileName);
        Assert.True(File.Exists(counterPath), "La commande de vérification n'a jamais été exécutée.");

        var counter = int.Parse(File.ReadAllText(counterPath).Trim());
        Assert.True(counter >= 2, $"La vérification aurait dû échouer une première fois puis réussir (compteur={counter}).");

        var outputRegister = result.WorkflowExecutionContext.GetActivityOutputRegister();
        var summary = outputRegister.FindOutputByActivityId(activityId, nameof(RunAgentPrompt.Summary)) as string;
        Assert.False(string.IsNullOrWhiteSpace(summary));

        _output.WriteLine($"Statut workflow : {result.WorkflowState.Status}, compteur de vérification : {counter}, résumé : {summary}");
    }
}
