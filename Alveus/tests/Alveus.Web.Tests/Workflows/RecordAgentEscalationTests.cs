using Alveus.Web.Workflows;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Models;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using Microsoft.Extensions.DependencyInjection;
using Endpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

namespace Alveus.Web.Tests.Workflows;

/// <summary>
/// Test déterministe (sans LLM) de <see cref="RecordAgentEscalation"/> (cf. ADR 0028) : vérifie le
/// texte écrit dans <see cref="RecordAgentEscalation.Report"/> selon que <c>Reason</c>/<c>Questions</c>
/// sont fournis ou non, ainsi que l'issue "Done".
/// </summary>
public sealed class RecordAgentEscalationTests
{
    /// <summary>Expose la valeur d'une variable en sortie, pour assertion via <c>FindOutputByActivityId</c>.</summary>
    private sealed class ReadVariable : CodeActivity
    {
        public required Variable<string?> Source { get; set; }

        public Output<string?> Value { get; set; } = new();

        protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
        {
            context.Set(Value, context.Get(Source));
            await context.CompleteActivityWithOutcomesAsync(["Done"]);
        }
    }

    private sealed class RecordEscalationWorkflow : WorkflowBase
    {
        private readonly string? _reason;
        private readonly IReadOnlyList<string>? _questions;

        public RecordEscalationWorkflow(string? reason, IReadOnlyList<string>? questions)
        {
            _reason = reason;
            _questions = questions;
        }

        protected override void Build(IWorkflowBuilder builder)
        {
            builder.WithDefinitionId("RecordEscalationWorkflow_" + Guid.NewGuid());

            var report = builder.WithVariable<string?>("Report", null);

            var record = new RecordAgentEscalation
            {
                Id = "Record",
                SourceLabel = new Input<string>("Alveus-Worker"),
                Reason = new Input<string?>(_reason),
                Questions = new Input<IReadOnlyList<string>?>(_questions),
                Report = report,
            };

            var read = new ReadVariable { Id = "Read", Source = report };

            builder.Root = new Flowchart
            {
                Start = record,
                Activities = [record, read],
                Connections = [new Connection(new Endpoint(record, "Done"), new Endpoint(read))],
            };
        }
    }

    private static async Task<string?> RunAsync(string? reason, IReadOnlyList<string>? questions)
    {
        var services = new ServiceCollection();
        services.AddElsa(elsa => elsa.UseWorkflowRuntime());
        var provider = services.BuildServiceProvider();

        var workflow = new RecordEscalationWorkflow(reason, questions);
        var runner = provider.GetRequiredService<IWorkflowRunner>();

        var result = await runner.RunAsync(workflow, new RunWorkflowOptions(), CancellationToken.None);

        return result.WorkflowExecutionContext.GetActivityOutputRegister()
            .FindOutputByActivityId("Read", nameof(ReadVariable.Value)) as string;
    }

    [Fact]
    public async Task RecordAgentEscalation_WithReasonAndQuestions_FormatsReport()
    {
        var report = await RunAsync("Consigne ambiguë.", ["Quel format de date attendre ?", "Faut-il gérer le fuseau horaire ?"]);

        Assert.NotNull(report);
        Assert.Contains("Alveus-Worker", report);
        Assert.Contains("Consigne ambiguë.", report);
        Assert.Contains("Quel format de date attendre ?", report);
        Assert.Contains("Faut-il gérer le fuseau horaire ?", report);
    }

    [Fact]
    public async Task RecordAgentEscalation_WithoutReasonOrQuestions_OnlyMentionsSource()
    {
        var report = await RunAsync(null, null);

        Assert.NotNull(report);
        Assert.Contains("Alveus-Worker", report);
        Assert.DoesNotContain("Questions", report);
    }
}
