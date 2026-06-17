using Microsoft.Extensions.Configuration;

namespace Alveus.Web.Tests;

/// <summary>
/// Lecture centralisée de la configuration LLM pour les tests d'intégration.
/// Source unique : <c>appsettings.json</c> / <c>appsettings.Development.json</c>
/// (copiés en sortie via le ProjectReference vers Alveus.Web) — section <c>AIModel</c>.
/// Si <see cref="Endpoint"/> ou <see cref="Model"/> est <see langword="null"/>,
/// les fixtures positionnent <c>IsLlamaCppAvailable = false</c> et les tests sont ignorés.
/// </summary>
internal static class TestLlamaCppConfig
{
    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile("appsettings.Development.json", optional: true)
        .Build();

    public static string? Endpoint => Config["AIModel:Endpoint"];
    public static string? Model => Config["AIModel:Model"];
}
