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
/// Test déterministe (sans LLM) de <see cref="LoopIterationGuard"/> (cf. ADR 0023) : un workflow
/// minimal le boucle sur lui-même via l'issue "Continue" jusqu'à dépasser
/// <see cref="LoopIterationGuard.MaxIterations"/>, où il doit basculer sur "LimitReached".
/// </summary>
public sealed class LoopIterationGuardTests
{
    private sealed class SelfLoopingGuardWorkflow : WorkflowBase
    {
        protected override void Build(IWorkflowBuilder builder)
        {
            builder.WithDefinitionId("SelfLoopingGuardWorkflow");

            var loopCount = builder.WithVariable("LoopCount", 0);
            var guard = new LoopIterationGuard { Id = "Guard", LoopCount = loopCount };

            builder.Root = new Flowchart
            {
                Start = guard,
                Connections = [new Connection(new Endpoint(guard, "Continue"), new Endpoint(guard))],
            };
        }
    }

    [Fact]
    public async Task LoopIterationGuard_SelfLooping_StopsAtLimitReached()
    {
        var services = new ServiceCollection();
        services.AddElsa(elsa => elsa.UseWorkflowRuntime(runtime => runtime.AddWorkflow<SelfLoopingGuardWorkflow>()));
        var provider = services.BuildServiceProvider();

        var workflow = ActivatorUtilities.CreateInstance<SelfLoopingGuardWorkflow>(provider);
        var runner = provider.GetRequiredService<IWorkflowRunner>();

        var result = await runner.RunAsync(workflow, new RunWorkflowOptions(), CancellationToken.None);

        var iteration = result.WorkflowExecutionContext.GetActivityOutputRegister()
            .FindOutputByActivityId("Guard", nameof(LoopIterationGuard.Iteration));

        Assert.Equal(LoopIterationGuard.MaxIterations + 1, iteration);
    }
}
