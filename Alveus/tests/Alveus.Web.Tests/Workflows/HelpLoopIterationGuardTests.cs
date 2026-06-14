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
/// Test déterministe (sans LLM) de <see cref="HelpLoopIterationGuard"/> (cf. ADR 0027) : un workflow
/// minimal le boucle sur lui-même via l'issue "Continue" jusqu'à dépasser
/// <see cref="HelpLoopIterationGuard.MaxIterations"/>, où il doit basculer sur "LimitReached".
/// </summary>
public sealed class HelpLoopIterationGuardTests
{
    private sealed class SelfLoopingHelpGuardWorkflow : WorkflowBase
    {
        protected override void Build(IWorkflowBuilder builder)
        {
            builder.WithDefinitionId("SelfLoopingHelpGuardWorkflow");

            var helpLoopCount = builder.WithVariable("HelpLoopCount", 0);
            var guard = new HelpLoopIterationGuard { Id = "Guard", HelpLoopCount = helpLoopCount };

            builder.Root = new Flowchart
            {
                Start = guard,
                Connections = [new Connection(new Endpoint(guard, "Continue"), new Endpoint(guard))],
            };
        }
    }

    [Fact]
    public async Task HelpLoopIterationGuard_SelfLooping_StopsAtLimitReached()
    {
        var services = new ServiceCollection();
        services.AddElsa(elsa => elsa.UseWorkflowRuntime(runtime => runtime.AddWorkflow<SelfLoopingHelpGuardWorkflow>()));
        var provider = services.BuildServiceProvider();

        var workflow = ActivatorUtilities.CreateInstance<SelfLoopingHelpGuardWorkflow>(provider);
        var runner = provider.GetRequiredService<IWorkflowRunner>();

        var result = await runner.RunAsync(workflow, new RunWorkflowOptions(), CancellationToken.None);

        var iteration = result.WorkflowExecutionContext.GetActivityOutputRegister()
            .FindOutputByActivityId("Guard", nameof(HelpLoopIterationGuard.Iteration));

        Assert.Equal(HelpLoopIterationGuard.MaxIterations + 1, iteration);
    }
}
