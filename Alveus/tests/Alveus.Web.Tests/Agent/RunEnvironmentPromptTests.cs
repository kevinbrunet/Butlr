using Alveus.Web.Activities;
using Elsa.Workflows;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Alveus.Web.Tests.Agent;

/// <summary>
/// Test d'intégration : exécute <see cref="RunEnvironmentPrompt"/> via
/// <see cref="IWorkflowRunner"/> et vérifie le routage par verdict
/// (cf. ADR 0023) — verdict='pass' ne reporte pas de
/// <see cref="AgentPromptActivityBase.Reason"/>, verdict='fail' et verdict='needmoreinfo'
/// reportent <see cref="AgentPromptActivityBase.Reason"/> (et <see cref="AgentPromptActivityBase.Questions"/>
/// pour ce dernier). Sauté (avec message dans la sortie de test) si
/// ALVEUS_TEST_LLAMACPP_ENDPOINT n'est pas joignable.
/// </summary>
public sealed class RunEnvironmentPromptTests : IClassFixture<RunEnvironmentPromptFixture>
{
    private readonly RunEnvironmentPromptFixture _fixture;
    private readonly ITestOutputHelper _output;

    public RunEnvironmentPromptTests(RunEnvironmentPromptFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task RunEnvironmentPrompt_VerdictPass_CompletesWithoutReason()
    {
        if (!_fixture.IsLlamaCppAvailable)
        {
            _output.WriteLine("llama.cpp indisponible sur ALVEUS_TEST_LLAMACPP_ENDPOINT — test ignoré.");
            return;
        }

        const string activityId = "run-environment-prompt-pass";

        var activity = ActivatorUtilities.CreateInstance<RunEnvironmentPrompt>(_fixture.Services);
        activity.Id = activityId;
        activity.Prompt = new Input<string>(
            "Appelle directement ton outil de fin de tâche (Finish) avec outcome='pass' et un "
            + "résumé contenant des instructions d'utilisation fictives (ex. \"Serveur disponible sur "
            + "http://localhost:1234\").");

        var runner = _fixture.Services.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(activity, new RunWorkflowOptions(), CancellationToken.None);

        var outputRegister = result.WorkflowExecutionContext.GetActivityOutputRegister();
        var summary = outputRegister.FindOutputByActivityId(activityId, nameof(RunEnvironmentPrompt.Summary)) as string;
        var reason = outputRegister.FindOutputByActivityId(activityId, nameof(RunEnvironmentPrompt.Reason)) as string;

        Assert.False(string.IsNullOrWhiteSpace(summary));
        Assert.Null(reason);

        _output.WriteLine($"Statut workflow : {result.WorkflowState.Status}, résumé : {summary}");
    }

    [Fact]
    public async Task RunEnvironmentPrompt_VerdictFail_ReportsReason()
    {
        if (!_fixture.IsLlamaCppAvailable)
        {
            _output.WriteLine("llama.cpp indisponible sur ALVEUS_TEST_LLAMACPP_ENDPOINT — test ignoré.");
            return;
        }

        const string activityId = "run-environment-prompt-fail";

        var activity = ActivatorUtilities.CreateInstance<RunEnvironmentPrompt>(_fixture.Services);
        activity.Id = activityId;
        activity.Prompt = new Input<string>(
            "Tu viens de tenter de démarrer l'environnement avec la commande 'npm start' mais elle a échoué "
            + "avec l'erreur 'port 3000 already in use'. Tu ne peux pas démarrer l'environnement. "
            + "Appelle l'outil Finish avec outcome='fail', "
            + "une reason décrivant l'erreur rencontrée, et un summary résumant la situation.");

        var runner = _fixture.Services.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(activity, new RunWorkflowOptions(), CancellationToken.None);

        var outputRegister = result.WorkflowExecutionContext.GetActivityOutputRegister();
        var reason = outputRegister.FindOutputByActivityId(activityId, nameof(RunEnvironmentPrompt.Reason)) as string;

        Assert.False(string.IsNullOrWhiteSpace(reason));

        _output.WriteLine($"Statut workflow : {result.WorkflowState.Status}, raison : {reason}");
    }

    [Fact]
    public async Task RunEnvironmentPrompt_VerdictNeedMoreInfo_ReportsReasonAndQuestions()
    {
        if (!_fixture.IsLlamaCppAvailable)
        {
            _output.WriteLine("llama.cpp indisponible sur ALVEUS_TEST_LLAMACPP_ENDPOINT — test ignoré.");
            return;
        }

        const string activityId = "run-environment-prompt-needmoreinfo";

        var activity = ActivatorUtilities.CreateInstance<RunEnvironmentPrompt>(_fixture.Services);
        activity.Id = activityId;
        activity.Prompt = new Input<string>(
            "La consigne de tâche ne précise pas comment démarrer l'environnement local : il n'y a ni commande, "
            + "ni port, ni instructions de lancement. Tu ne peux pas savoir quoi lancer. "
            + "Appelle l'outil Finish avec outcome='needmoreinfo', "
            + "une reason expliquant ce qui manque, au moins une question précise pour obtenir les informations "
            + "nécessaires, et un summary.");

        var runner = _fixture.Services.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(activity, new RunWorkflowOptions(), CancellationToken.None);

        var outputRegister = result.WorkflowExecutionContext.GetActivityOutputRegister();
        var reason = outputRegister.FindOutputByActivityId(activityId, nameof(RunEnvironmentPrompt.Reason)) as string;
        var questions = outputRegister.FindOutputByActivityId(activityId, nameof(RunEnvironmentPrompt.Questions)) as IReadOnlyList<string>;

        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.NotNull(questions);
        Assert.NotEmpty(questions!);

        _output.WriteLine($"Statut workflow : {result.WorkflowState.Status}, raison : {reason}, questions : {string.Join(" | ", questions!)}");
    }
}
