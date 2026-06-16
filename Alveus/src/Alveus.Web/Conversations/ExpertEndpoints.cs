using Alveus.Web.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Alveus.Web.Conversations;

/// <summary>
/// Endpoints HTTP de consultation directe des experts par équipe (cf. ADR 0032).
/// <c>POST /teams/{name}/experts/{role}/v1/ask</c> invoque l'agent expert en in-process et retourne
/// sa réponse — accessible depuis l'extérieur indépendamment du mode d'escalade de l'équipe.
/// </summary>
public static class ExpertEndpoints
{
    public static IEndpointRouteBuilder MapExpertEndpoints(this IEndpointRouteBuilder app, IEnumerable<string> teamNames)
    {
        foreach (var teamName in teamNames)
        {
            var capturedTeam = teamName;
            app.MapPost($"/teams/{capturedTeam}/experts/{{role}}/v1/ask",
                async (string role, AskExpertRequest req, IServiceProvider sp, CancellationToken ct) =>
                {
                    var tool = sp.GetRequiredKeyedService<AskExpertTool>(capturedTeam);
                    var answer = await tool.AskExpertAsync(role, req.Question, ct);
                    return Results.Ok(new AskExpertResponse(role, answer));
                })
                .WithName($"AskExpert-{teamName}");
        }

        return app;
    }
}

public sealed record AskExpertRequest(string Question);
public sealed record AskExpertResponse(string Expert, string Answer);
