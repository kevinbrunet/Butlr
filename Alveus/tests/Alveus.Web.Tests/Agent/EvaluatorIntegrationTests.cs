using Xunit.Abstractions;

namespace Alveus.Web.Tests.Agent;

/// <summary>
/// Tests d'intégration : vérifient que l'agent Alveus-Evaluator écrit effectivement un jeu de
/// test dans son propre workspace à partir de la même consigne que l'agent d'exécution — cf.
/// ADR 0021. Sautés (avec message dans la sortie de test) si ALVEUS_TEST_LLAMACPP_ENDPOINT
/// (défaut http://127.0.0.1:8083/v1) n'est pas joignable.
/// ~ le tool-calling d'un modèle 7B n'est pas garanti déterministe : ces tests valident le
/// câblage agent/outils/workspace, pas un contenu exact de jeu de test.
/// </summary>
public sealed class EvaluatorIntegrationTests : IClassFixture<EvaluatorFixture>
{
    private readonly EvaluatorFixture _fixture;
    private readonly ITestOutputHelper _output;

    public EvaluatorIntegrationTests(EvaluatorFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    private bool SkipIfLlamaCppUnavailable()
    {
        if (_fixture.IsLlamaCppAvailable)
        {
            return false;
        }

        _output.WriteLine("llama.cpp indisponible sur ALVEUS_TEST_LLAMACPP_ENDPOINT — test ignoré.");
        return true;
    }

    [Fact]
    public async Task Evaluator_WritesTestSuite_InOwnWorkspace_BasedOnTaskPrompt()
    {
        if (SkipIfLlamaCppUnavailable())
        {
            return;
        }

        const string taskPrompt =
            "Crée un fichier nommé 'hello.txt' contenant exactement le texte 'hello'.";

        // Timeout de 5 minutes : sans borne explicite, ChatClientAgent boucle indéfiniment si
        // le modèle ne termine pas par un appel Finish.
        var agentTask = _fixture.Agent.RunAsync(
            $"Voici la consigne de tâche donnée à l'agent d'exécution : \"{taskPrompt}\". "
            + "Avec ton outil d'édition de fichiers, crée dans ton espace de travail un fichier nommé "
            + "'test_hello.sh' contenant un script de test qui vérifierait qu'un travail répondant à cette "
            + "consigne est correct.");
        if (await Task.WhenAny(agentTask, Task.Delay(TimeSpan.FromMinutes(5))) == agentTask)
        {
            await agentTask; // propage les exceptions éventuelles
        }
        else
        {
            _output.WriteLine("Agent RunAsync timeout après 5 minutes — vérification des fichiers créés.");
        }

        var writtenFiles = Directory.GetFiles(_fixture.WorkspaceRoot);
        Assert.NotEmpty(writtenFiles);
    }
}
