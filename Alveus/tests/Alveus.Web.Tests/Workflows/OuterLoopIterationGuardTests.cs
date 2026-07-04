using Alveus.Web.Workflows;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Models;
using Elsa.Workflows.Options;
using Microsoft.Extensions.DependencyInjection;
using Endpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

namespace Alveus.Web.Tests.Workflows;

/// <summary>
/// Test déterministe (sans LLM) de <see cref="OuterLoopIterationGuard"/> (cf. ADR 0026) : un
/// workflow minimal le boucle sur lui-même via l'issue "Continue" jusqu'à dépasser
/// <see cref="OuterLoopIterationGuard.MaxIterations"/>, où il doit basculer sur "LimitReached".
/// </summary>
public sealed class OuterLoopIterationGuardTests
{
    private sealed class SelfLoopingOuterGuardWorkflow : WorkflowBase
    {
        protected override void Build(IWorkflowBuilder builder)
        {
            builder.WithDefinitionId("SelfLoopingOuterGuardWorkflow");

            var outerLoopCount = builder.WithVariable("OuterLoopCount", 0);
            var guard = new OuterLoopIterationGuard { Id = "Guard", OuterLoopCount = outerLoopCount };

            builder.Root = new Flowchart
            {
                Start = guard,
                Connections = [new Connection(new Endpoint(guard, "Continue"), new Endpoint(guard))],
            };
        }
    }

    [Fact]
    public async Task OuterLoopIterationGuard_SelfLooping_StopsAtLimitReached()
    {
        var services = new ServiceCollection();
        services.AddElsa(elsa => elsa.UseWorkflowRuntime(runtime => runtime.AddWorkflow<SelfLoopingOuterGuardWorkflow>()));
        var provider = services.BuildServiceProvider();

        var workflow = ActivatorUtilities.CreateInstance<SelfLoopingOuterGuardWorkflow>(provider);
        var runner = provider.GetRequiredService<IWorkflowRunner>();

        var result = await runner.RunAsync(workflow, new RunWorkflowOptions(), CancellationToken.None);

        var iteration = result.WorkflowExecutionContext.GetActivityOutputRegister()
            .FindOutputByActivityId("Guard", nameof(OuterLoopIterationGuard.Iteration));

        Assert.Equal(OuterLoopIterationGuard.MaxIterations + 1, iteration);
    }
}
