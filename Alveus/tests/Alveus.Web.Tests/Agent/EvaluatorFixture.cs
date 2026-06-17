using Alveus.Web.Agents;
using Alveus.Web.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Alveus.Web.Tests.Agent;

/// <summary>
/// Reconstruit l'agent Alveus-Evaluator tel que défini dans Program.cs (mêmes tools que
/// Alveus-Worker, même configuration llama.cpp — cf. ADR 0006), dans un workspace temporaire
/// dédié et isolé de celui du worker — cf. ADR 0021.
/// <see cref="IsLlamaCppAvailable"/> permet aux tests d'intégration de se désactiver
/// proprement si aucun serveur llama.cpp n'écoute sur l'endpoint configuré.
/// </summary>
public sealed class EvaluatorFixture : IAsyncLifetime
{
    private const string AgentName = "AlveusEvaluator";

    public string WorkspaceRoot { get; } = Directory.CreateTempSubdirectory("alveus-evaluator-tests-").FullName;

    public CmdRunTool CmdRunTool { get; }

    public StrReplaceEditorTool EditorTool { get; }

    public FinishTool FinishTool { get; }

    public AIAgent Agent { get; }

    public bool IsLlamaCppAvailable { get; private set; }

    public EvaluatorFixture()
    {
        EvaluatorSkills.CopyInto(WorkspaceRoot, AppContext.BaseDirectory);

        CmdRunTool = new CmdRunTool(WorkspaceRoot);
        EditorTool = new StrReplaceEditorTool(WorkspaceRoot);
        FinishTool = new FinishTool();

        IChatClient chatClient = TestChatClientFactory.Create();

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(CmdRunTool.RunAsync),
            AIFunctionFactory.Create(EditorTool.Execute),
            AIFunctionFactory.Create(FinishTool.Finish),
        };

        Agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = AgentName,
            ChatOptions = new ChatOptions
            {
                Instructions = "Tu es Alveus-Evaluator, l'agent de validation de Butlr. Tu reçois la même consigne "
                    + "de tâche que l'agent d'exécution (Alveus-Worker), complétée par les instructions "
                    + "d'utilisation de l'environnement local fournies par Alveus-EnvironmentManager (URL, ports, "
                    + "commandes d'exemple), mais dans ton propre espace de travail, séparé du sien. Ton rôle : à "
                    + "partir de cette consigne, écris un jeu de test (scripts, assertions) qui vérifie "
                    + "objectivement que l'environnement décrit par les instructions d'utilisation répond à la "
                    + "consigne, en l'écrivant avec ton outil d'édition de fichiers dans ton espace de travail ; "
                    + "puis exécute ce jeu de test avec ton outil shell en interagissant avec l'environnement "
                    + "uniquement par le réseau (ex. curl) — tu n'as pas accès au système de fichiers du Worker. "
                    + "N'effectue pas la tâche toi-même. Des méthodologies de référence pertinentes pour cette "
                    + "tâche te sont fournies directement dans ce contexte ; pour aller plus loin, le dossier "
                    + "'skills/{nom}/references/' de ton espace de travail contient des fichiers détaillés "
                    + "consultables avec ton outil d'édition. Si le jeu de test repose sur le pattern "
                    + "snapshot/approval testing (skill dotnet-snapshot-testing) : (1) écris un test C# complet "
                    + "(Verify et/ou Playwright selon le besoin) ; (2) lance 'dotnet test' avec ton outil shell — "
                    + "le premier run produit des fichiers non commités ('*.received.json' pour Verify, capture "
                    + "'-actual.png' ou équivalent pour Playwright) ; (3) relis le contenu de ces fichiers avec ton "
                    + "outil d'édition et vérifie manuellement qu'il correspond au résultat attendu pour la "
                    + "consigne ; (4) si c'est correct, renomme ce fichier pour qu'il devienne le golden file de "
                    + "référence ('*.verified.json', ou l'équivalent Playwright — voir le skill). Si le résultat ne "
                    + "correspond pas à la consigne, corrige le test plutôt que de promouvoir un golden file "
                    + "incorrect. Quand tu arrêtes de travailler, tu DOIS appeler l'outil Finish avec "
                    + "outcome='done' et : verdict='pass' si le jeu de test confirme que l'environnement répond à "
                    + "la consigne ; verdict='fail' si ce n'est pas le cas (reason=rapport détaillé des problèmes "
                    + "rencontrés, transmis à Alveus-Worker pour correction) ; verdict='needmoreinfo' si tu ne peux "
                    + "pas trancher sans information supplémentaire (reason et questions). Si tu es bloqué avant "
                    + "d'avoir pu écrire ou exécuter le jeu de test, utilise outcome='blocked' (reason) — sinon on "
                    + "te redemandera de le faire.",
                Tools = tools,
            },
            AIContextProviders = [new EvaluatorSkillsContextProvider(WorkspaceRoot)],
        });
    }

    public async Task InitializeAsync()
    {
        var endpoint = TestLlamaCppConfig.Endpoint;
        if (endpoint is null)
            return;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            // ~ llama.cpp expose /v1/models (API OpenAI-compatible) — utilisé ici uniquement
            // comme sonde de disponibilité, pas pour vérifier le contenu de la réponse.
            using var response = await client.GetAsync(new Uri($"{endpoint.TrimEnd('/')}/models"));
            IsLlamaCppAvailable = response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            IsLlamaCppAvailable = false;
        }
    }

    public Task DisposeAsync()
    {
        CmdRunTool.Dispose();
        Directory.Delete(WorkspaceRoot, recursive: true);
        return Task.CompletedTask;
    }
}
