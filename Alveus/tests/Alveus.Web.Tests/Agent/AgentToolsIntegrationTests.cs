using Xunit.Abstractions;

namespace Alveus.Web.Tests.Agent;

/// <summary>
/// Tests d'intégration : vérifient que l'agent Alveus-Worker déclenche effectivement
/// CmdRunTool et StrReplaceEditorTool via tool-calling, contre un vrai serveur llama.cpp
/// (cf. ADR 0006). Sautés (avec message dans la sortie de test) si
/// ALVEUS_TEST_LLAMACPP_ENDPOINT (défaut http://127.0.0.1:8083/v1) n'est pas joignable.
/// ~ le tool-calling d'un modèle 7B n'est pas garanti déterministe : ces tests valident
/// le câblage agent/outils, pas un comportement exact du modèle.
/// </summary>
public sealed class AgentToolsIntegrationTests : IClassFixture<AgentFixture>
{
    private readonly AgentFixture _fixture;
    private readonly ITestOutputHelper _output;

    public AgentToolsIntegrationTests(AgentFixture fixture, ITestOutputHelper output)
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
    public async Task Agent_UsesCmdRunTool_ToRunShellCommand()
    {
        if (SkipIfLlamaCppUnavailable())
        {
            return;
        }

        var response = await _fixture.Agent.RunAsync(
            "Exécute la commande shell `echo alveus-cmdrun-ok` avec ton outil d'exécution de commandes, "
            + "puis donne-moi exactement la sortie obtenue.");

        Assert.Contains("alveus-cmdrun-ok", response.Text);
    }

    [Fact]
    public async Task Agent_UsesEditorTool_ToCreateFile()
    {
        if (SkipIfLlamaCppUnavailable())
        {
            return;
        }

        const string fileName = "agent-created.txt";

        await _fixture.Agent.RunAsync(
            $"Avec ton outil d'édition de fichiers, crée un fichier nommé '{fileName}' "
            + "contenant exactement le texte 'agent-write-ok'.");

        var path = Path.Combine(_fixture.WorkspaceRoot, fileName);
        Assert.True(File.Exists(path));
        Assert.Contains("agent-write-ok", File.ReadAllText(path));
    }

    [Fact]
    public async Task Agent_UsesEditorTool_ToEditExistingFile()
    {
        if (SkipIfLlamaCppUnavailable())
        {
            return;
        }

        const string fileName = "agent-edit.txt";
        File.WriteAllText(Path.Combine(_fixture.WorkspaceRoot, fileName), "valeur=ancienne");

        await _fixture.Agent.RunAsync(
            $"Avec ton outil d'édition de fichiers, dans le fichier '{fileName}' remplace 'ancienne' par 'nouvelle'.");

        Assert.Contains("valeur=nouvelle", File.ReadAllText(Path.Combine(_fixture.WorkspaceRoot, fileName)));
    }
}
