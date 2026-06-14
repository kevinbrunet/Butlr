# ADR 0023 — Workflow Worker → EnvironmentManager → Evaluator avec boucle de correction

## Status

Accepted

## Context

[ADR 0017](0017-agent-shell-and-file-tools-with-workspace-confinement.md) à
[ADR 0022](0022-evaluator-skill-injection-via-ai-context-provider.md) ont construit, l'une après
l'autre, les briques d'un agent d'exécution (Alveus-Worker, `RunAgentPrompt`) et d'un agent de
validation (Alveus-Evaluator, `RunEvaluatorPrompt`), chacun avec son cycle de relance interne
(`MaxIterations`, ADR 0019), sa vérification de travail (ADR 0020) et son isolation de workspace
(ADR 0021/0022). Mais **aucun graphe de workflow ne les relie** : `management.AddActivity<...>()`
les enregistre comme activités disponibles, sans `IWorkflow` qui les enchaîne.

Le besoin : une orchestration de bout en bout d'une tâche Alveus —
1. Alveus-Worker exécute la tâche.
2. Une fois "Done", un agent relance/démarre l'environnement local décrit par la consigne (ex.
   `dotnet run`, serveur HTTP), pour le rendre utilisable par un autre agent.
3. Alveus-Evaluator reçoit la même consigne que le Worker, complétée par les instructions
   d'utilisation de cet environnement, écrit **et exécute** un jeu de test contre l'environnement
   réel (réseau uniquement, isolation ADR 0021 préservée), puis rend un verdict.
4. Verdict positif → fin de workflow. Verdict négatif → retour à Alveus-Worker avec un rapport des
   problèmes rencontrés.

Décisions actées avec Kevin avant implémentation :
- Le verdict est porté par l'agent lui-même (extension de `FinishTool`), pas par une étape de
  vérification déterministe séparée — cohérent avec le choix ADR 0019 (issue déclarée par l'agent
  via un tool plutôt que parsée depuis le texte de réponse).
- Le graphe est défini en C# (`WorkflowBase`, `AddWorkflow<T>()`), versionné et testable comme le
  reste du code — pas via le designer/API JSON Elsa.
- Le mécanisme de verdict est **partagé** entre l'agent qui (re)lance l'environnement
  ("EnvironmentManager") et Alveus-Evaluator : les deux qualifient un résultat externe ("ce
  résultat est-il bon ?"), à la différence d'`AgentTaskOutcome` (ADR 0019) qui qualifie l'état du
  travail de l'agent lui-même ("ai-je terminé ma tâche ?").

`Elsa.Workflows.Activities.Flowchart.Activities.Flowchart` (✓ `Elsa.Workflows.Core` 3.7.0,
confirmé par exploration de l'assembly) supporte des graphes cycliques via
`ICollection<Connection>`, chaque `Connection` reliant un `Endpoint(IActivity, string? port)`
source (le port = le nom de l'outcome déclaré par
`ActivityExecutionContext.CompleteActivityWithOutcomesAsync`) à un `Endpoint` cible.
`Elsa.Extensions.ModuleExtensions.AddWorkflow<T>(IModule module)` (`Elsa.Workflows.Runtime`)
enregistre un `WorkflowBase` (T : IWorkflow, pas de contrainte `new()` — résolution via DI) auprès
de `elsa.UseWorkflowRuntime(runtime => runtime.AddWorkflow<T>())`.

## Decision

1. **`AgentVerdict { Pass, Fail, NeedMoreInfo }`** (`Alveus.Web.Tools`), distinct de
   `AgentTaskOutcome` (ADR 0019, reste `Done`/`NeedsMoreInfo`/`Blocked`). `FinishCall` gagne un
   champ optionnel `AgentVerdict? Verdict`, parsé depuis un argument `"verdict"`
   (`"pass"`/`"fail"`/`"needmoreinfo"`, absent et toujours `null` pour Alveus-Worker).
   `FinishTool.Finish` gagne un paramètre optionnel `string? verdict = null`, documenté comme
   pertinent uniquement pour l'EnvironmentManager et l'Evaluator.

2. **Logique de routage par verdict factorisée** dans
   `AgentPromptActivityBase.HandleVerdictAsync(context, finish, passOutcome)` : `Pass` complète
   avec `passOutcome` (`"Done"` pour l'EnvironmentManager, `"Passed"` pour l'Evaluator) et efface
   `Reason` ; `Fail` complète avec `"Failed"` et reporte `Reason` ; `NeedMoreInfo` complète avec
   `"NeedsMoreInfo"` et reporte `Reason`/`Questions` ; `null` (verdict absent) retourne un message
   de relance ("Précise verdict='pass', 'fail' ou 'needmoreinfo'..."), réutilisant la boucle de
   relance existante (ADR 0019) sans nouveau mécanisme.

3. **Nouvel agent Alveus-EnvironmentManager** (config `Agent:EnvironmentManagerName`, défaut
   `"AlveusEnvironmentManager"`) et nouvelle activité `RunEnvironmentPrompt`. Il réutilise **les
   mêmes instances** `CmdRunTool`/`StrReplaceEditorTool`/`FinishTool` (donc le même workspace,
   `Agent:WorkspaceRoot`) qu'Alveus-Worker — pas d'isolation, contrairement à l'Evaluator. Son
   prompt système : (re)lancer l'environnement local décrit par la consigne, en arrière-plan
   (`nohup ... & disown`, car `CmdRunTool` a un timeout de 30s — ADR 0017), puis rapporter via
   `Finish(outcome='done', verdict=...)` : `pass` avec dans `summary` des instructions
   d'utilisation précises (URL, ports, exemples de requêtes/commandes) pour un agent sans accès au
   filesystem ; `fail` si le démarrage échoue (`reason`) ; `needmoreinfo` si la consigne ne précise
   pas comment démarrer l'environnement (`reason`, `questions`).

4. **Instructions Alveus-Evaluator étendues** (`Program.cs`, `EvaluatorFixture`,
   `RunEvaluatorPromptFixture`) : il reçoit désormais la consigne du Worker **concaténée** aux
   instructions d'utilisation de l'EnvironmentManager (assemblage fait au niveau du workflow, §5),
   dans son workspace isolé (ADR 0021). Il écrit le jeu de test **et l'exécute** contre
   l'environnement réel via son `CmdRunTool`, en réseau uniquement (ex. `curl`) — il n'a pas accès
   au filesystem du Worker. Il termine par
   `Finish(outcome='done', verdict='pass'|'fail'|'needmoreinfo', reason=...)` (`fail` porte un
   rapport détaillé transmis au Worker ; `needmoreinfo` porte `reason`/`questions`) ou
   `outcome='blocked'` s'il est bloqué avant d'avoir pu écrire/exécuter le jeu de test.

5. **Workflow C# `AlveusTaskWorkflow : WorkflowBase`**
   (`Alveus/src/Alveus.Web/Workflows/AlveusTaskWorkflow.cs`), enregistré via
   `elsa.UseWorkflowRuntime(runtime => runtime.AddWorkflow<AlveusTaskWorkflow>())`. Variables :
   `TaskPrompt` (entrée, via `RunWorkflowOptions.Variables`), `EnvUsageInstructions` (alimentée par
   `RunEnvironmentPrompt.Summary`), `FailureReport` (alimentée par `Reason` de
   `RunEnvironmentPrompt`/`RunEvaluatorPrompt` en cas d'échec), `LoopCount` (garde-fou, §6).
   Graphe (`Flowchart.Connections`, ports = noms d'outcomes) :
   ```
   RunWorker (Prompt = TaskPrompt, ou TaskPrompt + FailureReport si non vide)
     -- "Done" --> RunEnvironmentManager (Prompt = TaskPrompt)
     -- "NeedsMoreInfo"/"Blocked" --> (pas de connexion : fin de workflow)

   RunEnvironmentManager
     -- "Done" --> RunEvaluator (Prompt = TaskPrompt + instructions d'utilisation)
     -- "Failed" --> LoopGuard (FailureReport = Reason)
     -- "NeedsMoreInfo"/"Blocked" --> (fin de workflow)

   RunEvaluator
     -- "Passed" --> (fin de workflow, succès)
     -- "Failed" --> LoopGuard (FailureReport = Reason)
     -- "NeedsMoreInfo"/"Blocked" --> (fin de workflow)

   LoopGuard
     -- "Continue" --> RunWorker (cycle)
     -- "LimitReached" --> (fin de workflow)
   ```
   `RunEnvironmentPrompt.Summary`/`Reason` et `RunEvaluatorPrompt.Reason` sont branchés
   directement sur les variables `EnvUsageInstructions`/`FailureReport` via
   `new Output<T>(variable)`, sans code de câblage explicite.

   ✓ `Flowchart` (`Elsa.Workflows.Activities.Container`) expose une collection `Activities`
   distincte de `Start`/`Connections` : toute activité référencée uniquement comme cible d'une
   `Connection` (donc pas `Start`) **doit** y figurer, sinon le scheduler lève
   `InvalidOperationException: "The specified activity is not part of the workflow."` au moment
   de router vers cette activité. `AlveusTaskWorkflow` déclare donc
   `Activities = [runWorker, runEnvironmentManager, runEvaluator, loopGuard]`.

6. **`LoopIterationGuard`** (`Alveus.Web.Workflows`), `CodeActivity` minimal : incrémente
   `LoopCount` et complète avec `"Continue"` tant que `LoopCount <= MaxIterations` (constante = 5),
   sinon `"LimitReached"`. Garde-fou global distinct du `MaxIterations` interne à chaque activité
   (ADR 0019, qui borne les relances d'un seul agent au sein d'une étape) : ici, il borne le
   nombre de cycles Worker → EnvironmentManager → Evaluator. Expose aussi `Output<int> Iteration`
   (numéro du cycle qui vient de s'exécuter) — utilisé par `LoopIterationGuardTests` pour observer
   le comportement sans dépendre d'un LLM.

## Consequences

### Positif
- Boucle de correction end-to-end automatisée : un échec de l'EnvironmentManager ou de
  l'Evaluator renvoie automatiquement au Worker avec un rapport exploitable, sans intervention
  manuelle.
- L'isolation de l'Evaluator (ADR 0021) est préservée : il interagit avec l'environnement
  uniquement par le réseau, jamais par le filesystem du Worker.
- Le graphe est versionné et testable comme le reste du code (`AlveusTaskWorkflowTests`), pas
  dépendant du designer Elsa.
- `AgentVerdict` et `HandleVerdictAsync` sont génériques : un futur agent "juge" (EnvironmentManager,
  Evaluator, ou un futur rôle) suit le même contrat sans nouveau mécanisme.

### Négatif
- Un cycle complet appelle potentiellement 3 agents LLM (Worker, EnvironmentManager, Evaluator),
  chacun avec son propre `MaxIterations` (ADR 0019) — coût en tokens/latence multiplié par rapport
  à un seul agent.
- ⚠ La flakiness ~50% documentée par ADR 0021 pour l'Evaluator seul s'applique a fortiori à un
  enchaînement de 3 agents suivant des instructions multi-étapes — `AlveusTaskWorkflowTests` est
  gated par `IsLlamaCppAvailable` et peut être instable selon le modèle.
- `CmdRunTool` a un timeout de 30s (ADR 0017) : l'EnvironmentManager **doit** lancer tout process
  long en arrière-plan (`nohup ... & disown`). ⚠ La robustesse de ce pattern (process orphelin
  survivant à la fin de l'activité, log accessible) n'est pas testée par
  `RunEnvironmentPromptTests` (qui n'exerce que le routage par verdict, sans process réel).
- `AgentVerdict.NeedMoreInfo` et `AgentTaskOutcome.NeedsMoreInfo` sont deux enums distincts qui se
  traduisent par le même outcome de workflow `"NeedsMoreInfo"` — légère duplication conceptuelle,
  justifiée en Alternatives ci-dessous.

## Alternatives considérées

- **Vérification déterministe séparée du verdict** (une étape de workflow distincte qui parse la
  réponse de l'agent ou exécute une commande de vérification, comme `IAgentWorkVerificationService`
  pour ADR 0020) — écartée : le verdict porte sur un jugement qualitatif ("l'environnement
  répond-il à la consigne ?") que seul le modèle peut évaluer après avoir lui-même écrit et
  exécuté le test ; une vérification déterministe séparée dupliquerait ce travail sans pouvoir
  remplacer le jugement du modèle.
- **Workflow défini via JSON/designer Elsa** — écarté : le reste du pipeline d'activités
  (`RunAgentPrompt`, `RunEvaluatorPrompt`, leurs fixtures de test) est en C# versionné ; un
  graphe JSON séparé introduirait une deuxième source de vérité et ne bénéficierait pas de la
  vérification par le compilateur (référence d'activité valide, type de variable correct).
- **EnvironmentManager = le Worker lui-même avec un second prompt, plutôt qu'un agent distinct** —
  écarté : nécessiterait de partager la session agent (ADR 0018) entre deux rôles avec des
  instructions système différentes, ce qui complexifierait `AgentPromptActivityBase` (la session
  encode déjà les instructions système de l'agent). Un agent distinct partageant tools/workspace
  est plus simple, au prix d'un agent LLM supplémentaire par cycle.
- **Fusionner `AgentVerdict.NeedMoreInfo` avec `AgentTaskOutcome.NeedsMoreInfo`** (un seul enum,
  réutilisé pour les deux usages) — écarté : `AgentTaskOutcome` qualifie l'état du *travail de
  l'agent* ("je n'ai pas terminé ma tâche, j'ai besoin d'information pour continuer"),
  `AgentVerdict` qualifie un *jugement sur un résultat externe* ("j'ai terminé ma vérification,
  mais je ne peux pas trancher sans information supplémentaire"). Les fusionner aurait empêché un
  agent EnvironmentManager/Evaluator de distinguer ces deux situations dans son appel `Finish`
  (un seul champ `outcome` ne peut pas porter les deux significations à la fois). Le coût (deux
  enums qui se traduisent par le même nom d'outcome de workflow `"NeedsMoreInfo"`) est jugé
  acceptable face à cette perte de signal.

## Révisions

- 2026-06-13 — création.
- 2026-06-13 — ajout de `LoopIterationGuardTests` (test déterministe, sans LLM, du cycle
  "Continue" → "LimitReached") et, dans `AlveusTaskWorkflowTests`, de tests couvrant le cycle de
  correction complet (`RunEnvironmentManager` en échec permanent jusqu'à `LimitReached`) et les
  issues "Blocked" à chaque étape (Worker, EnvironmentManager, Evaluator).
- 2026-06-14 — `AlveusTaskWorkflowTests` exécutés contre un vrai serveur llama.cpp (Qwen3.6 35B) :
  les 5 tests passent. Au passage, deux corrections nécessaires pour que l'exécution réelle
  fonctionne (pas seulement la compilation) : `TaskPrompt` doit être déclaré via
  `builder.WithInput(...)` et lu via `context.GetInput<string>(...)` (un
  `builder.WithVariable("TaskPrompt", ...)` n'est pas alimenté par
  `RunWorkflowOptions.Input`/`Variables`) ; et `Flowchart.Activities` doit lister explicitement
  toutes les activités non-`Start` (cf. point 5).
