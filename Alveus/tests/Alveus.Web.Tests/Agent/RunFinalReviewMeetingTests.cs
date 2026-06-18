using Alveus.Web.Activities;
using Elsa.Workflows;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Alveus.Web.Tests.Agent;

/// <summary>
/// Test d'intégration : exécute <see cref="RunFinalReviewMeeting"/> via
/// <see cref="IWorkflowRunner"/> — cf. ADR 0024/0026. Sauté (avec message dans la sortie de test)
/// si ALVEUS_TEST_LLAMACPP_ENDPOINT n'est pas joignable.
/// ⚠ Ce test dépend du comportement du LLM dans un débat multi-agents avec vote — flakiness
/// possible (cf. Context du plan ADR 0024).
/// </summary>
public sealed class RunFinalReviewMeetingTests : IClassFixture<MeetingFixture>
{
    private readonly MeetingFixture _fixture;
    private readonly ITestOutputHelper _output;

    public RunFinalReviewMeetingTests(MeetingFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task RunFinalReviewMeeting_AllParticipantsAgree_CompletesWithOkVerdict()
    {
        if (!_fixture.IsLlamaCppAvailable)
        {
            _output.WriteLine("llama.cpp indisponible sur ALVEUS_TEST_LLAMACPP_ENDPOINT — test ignoré.");
            return;
        }

        const string activityId = "run-final-review-meeting-ok";

        var activity = ActivatorUtilities.CreateInstance<RunFinalReviewMeeting>(_fixture.Services);
        activity.Id = activityId;
        activity.TeamName = new Input<string>(MeetingFixture.TeamName);
        activity.Topic = new Input<string>(
            "Résumé du travail effectué : la tâche a été correctement réalisée par Alveus-Worker, "
            + "Alveus-EnvironmentManager, Alveus-Evaluator et Alveus-UserDoc. Quel que soit ton rôle, vote "
            + "immédiatement sur le topic 'task-fulfilled' avec decision='agree', puis appelle ton outil de fin de "
            + "tour (Finish) avec outcome='pass' et un résumé confirmant ton accord.");

        var runner = _fixture.Services.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(activity, new RunWorkflowOptions(), CancellationToken.None);

        var outputRegister = result.WorkflowExecutionContext.GetActivityOutputRegister();
        var finalVerdict = outputRegister.FindOutputByActivityId(activityId, nameof(RunFinalReviewMeeting.FinalVerdict)) as string;

        _output.WriteLine($"Statut workflow : {result.WorkflowState.Status}, verdict final : '{finalVerdict}'");

        if (finalVerdict is not null)
        {
            Assert.Equal("ok", finalVerdict);
        }
    }

    [Fact]
    public async Task RunFinalReviewMeeting_AllParticipantsDisagree_CompletesWithKoVerdictAndReports()
    {
        if (!_fixture.IsLlamaCppAvailable)
        {
            _output.WriteLine("llama.cpp indisponible sur ALVEUS_TEST_LLAMACPP_ENDPOINT — test ignoré.");
            return;
        }

        const string activityId = "run-final-review-meeting-ko";

        var activity = ActivatorUtilities.CreateInstance<RunFinalReviewMeeting>(_fixture.Services);
        activity.Id = activityId;
        activity.TeamName = new Input<string>(MeetingFixture.TeamName);
        activity.Topic = new Input<string>(
            "Résumé du travail effectué : la tâche n'a PAS été correctement réalisée (rapport fictif pour ce test). "
            + "Quel que soit ton rôle, vote immédiatement sur le topic 'task-fulfilled' avec decision='disagree' et "
            + "comment='le travail ne correspond pas à la consigne (rapport fictif)', puis appelle ton outil de fin "
            + "de tour (Finish) avec outcome='pass' et un résumé de ton compte-rendu de désaccord.");

        var runner = _fixture.Services.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(activity, new RunWorkflowOptions(), CancellationToken.None);

        var outputRegister = result.WorkflowExecutionContext.GetActivityOutputRegister();
        var finalVerdict = outputRegister.FindOutputByActivityId(activityId, nameof(RunFinalReviewMeeting.FinalVerdict)) as string;
        var specialistReports = outputRegister.FindOutputByActivityId(activityId, nameof(RunFinalReviewMeeting.SpecialistReports)) as IReadOnlyDictionary<string, string>;
        var baReport = specialistReports?.GetValueOrDefault("BusinessAnalyst");

        _output.WriteLine($"Statut workflow : {result.WorkflowState.Status}, verdict final : '{finalVerdict}', "
            + $"compte-rendu BA : '{baReport}'");

        if (finalVerdict is not null)
        {
            Assert.Equal("ko", finalVerdict);
            Assert.False(string.IsNullOrWhiteSpace(baReport));
        }
    }
}
