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
/// Test déterministe (sans LLM) de <see cref="AgentEscalationLoopGuard"/> (cf. ADR 0028) : un
/// workflow minimal le boucle sur lui-même via l'issue "Continue" jusqu'à dépasser
/// <see cref="AgentEscalationLoopGuard.MaxIterations"/>, où il doit basculer sur "LimitReached".
/// </summary>
public sealed class AgentEscalationLoopGuardTests
{
    private sealed class SelfLoopingAgentEscalationGuardWorkflow : WorkflowBase
    {
        protected override void Build(IWorkflowBuilder builder)
        {
            builder.WithDefinitionId("SelfLoopingAgentEscalationGuardWorkflow");

            var agentEscalationLoopCount = builder.WithVariable("AgentEscalationLoopCount", 0);
            var guard = new AgentEscalationLoopGuard { Id = "Guard", AgentEscalationLoopCount = agentEscalationLoopCount };

            builder.Root = new Flowchart
            {
                Start = guard,
                Connections = [new Connection(new Endpoint(guard, "Continue"), new Endpoint(guard))],
            };
        }
    }

    [Fact]
    public async Task AgentEscalationLoopGuard_SelfLooping_StopsAtLimitReached()
    {
        var services = new ServiceCollection();
        services.AddElsa(elsa => elsa.UseWorkflowRuntime(runtime => runtime.AddWorkflow<SelfLoopingAgentEscalationGuardWorkflow>()));
        var provider = services.BuildServiceProvider();

        var workflow = ActivatorUtilities.CreateInstance<SelfLoopingAgentEscalationGuardWorkflow>(provider);
        var runner = provider.GetRequiredService<IWorkflowRunner>();

        var result = await runner.RunAsync(workflow, new RunWorkflowOptions(), CancellationToken.None);

        var iteration = result.WorkflowExecutionContext.GetActivityOutputRegister()
            .FindOutputByActivityId("Guard", nameof(AgentEscalationLoopGuard.Iteration));

        Assert.Equal(AgentEscalationLoopGuard.MaxIterations + 1, iteration);
    }
}
