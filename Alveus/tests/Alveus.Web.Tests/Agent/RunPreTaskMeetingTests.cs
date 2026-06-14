using Alveus.Web.Activities;
using Elsa.Workflows;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Alveus.Web.Tests.Agent;

/// <summary>
/// Test d'intégration : exécute <see cref="RunPreTaskMeeting"/> via <see cref="IWorkflowRunner"/>
/// — cf. ADR 0024. Sauté (avec message dans la sortie de test) si ALVEUS_TEST_LLAMACPP_ENDPOINT
/// n'est pas joignable.
/// ⚠ Ce test dépend du comportement du LLM dans un débat multi-agents — flakiness possible
/// (cf. Context du plan ADR 0024).
/// </summary>
public sealed class RunPreTaskMeetingTests : IClassFixture<MeetingFixture>
{
    private readonly MeetingFixture _fixture;
    private readonly ITestOutputHelper _output;

    public RunPreTaskMeetingTests(MeetingFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task RunPreTaskMeeting_AllParticipantsConfirmDone_CompletesWithEmptyInstructions()
    {
        if (!_fixture.IsLlamaCppAvailable)
        {
            _output.WriteLine("llama.cpp indisponible sur ALVEUS_TEST_LLAMACPP_ENDPOINT — test ignoré.");
            return;
        }

        const string activityId = "run-pre-task-meeting-under-test";

        var activity = ActivatorUtilities.CreateInstance<RunPreTaskMeeting>(_fixture.Services);
        activity.Id = activityId;
        activity.Topic = new Input<string>(
            "Ticket : aucune mise à jour de documentation n'est nécessaire pour ce ticket. Quel que soit ton rôle, "
            + "n'utilise pas Raise et appelle directement ton outil de fin de tour (Finish) avec outcome='done' et "
            + "un résumé indiquant qu'il n'y a rien à mettre à jour.");

        var runner = _fixture.Services.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(activity, new RunWorkflowOptions(), CancellationToken.None);

        var outputRegister = result.WorkflowExecutionContext.GetActivityOutputRegister();
        var workerInstructions = outputRegister.FindOutputByActivityId(activityId, nameof(RunPreTaskMeeting.WorkerInstructions)) as string;
        var evaluatorInstructions = outputRegister.FindOutputByActivityId(activityId, nameof(RunPreTaskMeeting.EvaluatorInstructions)) as string;
        var userDocInstructions = outputRegister.FindOutputByActivityId(activityId, nameof(RunPreTaskMeeting.UserDocInstructions)) as string;

        Assert.NotNull(workerInstructions);
        Assert.NotNull(evaluatorInstructions);
        Assert.NotNull(userDocInstructions);

        _output.WriteLine($"Statut workflow : {result.WorkflowState.Status}, "
            + $"WorkerInstructions : '{workerInstructions}', EvaluatorInstructions : '{evaluatorInstructions}', "
            + $"UserDocInstructions : '{userDocInstructions}'");
    }
}
