# ADR 0021 — Agent Alveus-Evaluator dans un workspace isolé

## Status

Accepted

## Context

[ADR 0017](0017-alveus-agent-shell-editor-tools.md) à [ADR 0020](0020-agent-work-verification-before-done.md) définissent un agent unique, Alveus-Worker, qui exécute une tâche dans son workspace (`Agent:WorkspaceRoot`) via `CmdRunTool`/`StrReplaceEditorTool`, puis valide son propre travail via `IAgentWorkVerificationService` avant de sortir par `"Done"` (ADR 0020).

On veut un second agent, Alveus-Evaluator, qui reçoit la **même consigne de tâche** que le Worker mais dont le rôle est différent : écrire un jeu de test (scripts, assertions) qui permettrait de vérifier objectivement qu'un travail répondant à cette consigne est correct — sans effectuer la tâche lui-même. Deux questions structurantes :

- Le code partagé entre Worker et Evaluator (boucle de relance/compactage, parsing de `FinishTool`, gestion des outputs `Summary`/`Reason`/`Questions` — ADR 0019) doit-il être dupliqué ou extrait ?
- L'Evaluator doit-il voir le workspace du Worker pour écrire son jeu de test, ou travailler dans un espace totalement séparé ?

Pour la seconde question, le risque d'un workspace partagé est que l'Evaluator modifie ou s'appuie sur des fichiers produits par le Worker (ou l'inverse), ce qui fausserait l'indépendance du jeu de test vis-à-vis de l'implémentation qu'il est censé valider.

## Decision

1. **Extraction d'une classe de base `AgentPromptActivityBase`** (`Alveus.Web.Activities`), qui porte toute la logique commune précédemment dans `RunAgentPrompt` : constantes (`MaxIterations`, `ReminderPrompt`, `SessionStatePropertyPrefix`), inputs/outputs (`AgentName`, `Prompt`, `Summary`, `Reason`, `Questions`), restauration/persistance de session, boucle de relance et compactage (ADR 0019), parsing de l'appel `Finish`. Le point de variation est une méthode abstraite `HandleDoneAsync(ActivityExecutionContext, FinishCall)` appelée quand l'agent signale `outcome='done'`.

   - `RunAgentPrompt` étend cette base et implémente `HandleDoneAsync` en appelant `IAgentWorkVerificationService` (comportement ADR 0020, inchangé).
   - `RunEvaluatorPrompt` (nouvelle activité Elsa) étend la même base et implémente `HandleDoneAsync` en sortant directement par `"Done"`, sans vérification — son travail (un jeu de test écrit dans son workspace) n'a pas de critère de validation automatique au sens d'ADR 0020.

2. **Workspace totalement isolé.** L'Evaluator a son propre `CmdRunTool`/`StrReplaceEditorTool`/`AIAgent`, enregistrés en DI via une clé dédiée (`Agent:EvaluatorName`, défaut `AlveusEvaluator`), pointant vers `Agent:EvaluatorWorkspaceRoot` (défaut `workspace-evaluator`) — un répertoire distinct de `Agent:WorkspaceRoot`. L'Evaluator ne peut ni lire ni écrire dans le workspace du Worker, et inversement. `FinishTool` (sans état, ADR 0019) et `IAgentSessionCompactionService` restent partagés entre les deux agents.

3. **Skills méthodologiques dans le workspace de l'Evaluator.** Le repo expose des "skills" (méthodologies de référence, ex. `Alveus/skils/dotnet-snapshot-testing/`) destinées à guider l'écriture de jeux de tests. `EvaluatorSkills.CopyInto(evaluatorWorkspaceRoot, searchStartDirectory)` (`Alveus.Web.Agents`) copie ces skills dans `{workspace-evaluator}/skills/{nom-du-skill}/` au démarrage (et dans les fixtures de test), en remontant l'arborescence depuis `searchStartDirectory` pour localiser le dossier `skils/` du repo. Les instructions de l'Evaluator mentionnent explicitement ce dossier (`skills/dotnet-snapshot-testing/SKILL.md`) pour qu'il puisse le consulter via son `StrReplaceEditorTool` quand la consigne s'y prête. No-op si `skils/` n'est pas trouvé (ex. déploiement sans les sources du repo).

Les deux agents partagent les mêmes classes d'outils `CmdRunTool`/`StrReplaceEditorTool` (et donc le recentrage "fichiers uniquement" de `StrReplaceEditorTool.View`, commit `b118129`) — seule la racine du workspace diffère.

## Consequences

### Positif
- La logique de boucle/compactage/parsing (ADR 0019) n'existe qu'une fois ; un futur troisième agent suit le même schéma (sous-classe + `HandleDoneAsync`) sans dupliquer ~150 lignes.
- Isolation totale des workspaces : impossible que l'Evaluator "triche" en lisant l'implémentation du Worker pour écrire un test qui colle exactement à celle-ci, ou que le Worker soit perturbé par les fichiers de test de l'Evaluator.
- `EvaluatorSkills` est un point d'extension simple pour ajouter d'autres skills au workspace de l'Evaluator sans toucher au câblage DI.

### Négatif
- Deux instances d'agent (Worker + Evaluator) doublent le nombre d'appels LLM si un workflow invoque les deux pour la même tâche — coût et latence multipliés par deux.
- ~ Sur le modèle 35B testé (`Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf`, endpoint `192.168.1.85:8083`), les tests d'intégration de l'Evaluator (`EvaluatorIntegrationTests`, `RunEvaluatorPromptTests`) sont flaky (~50 % de succès) : le modèle confond parfois `StrReplaceEditorTool` avec un shell. Cette flakiness est un problème de prompt/modèle, pas d'architecture — non résolue à la date de cet ADR, suivi séparé.
- `EvaluatorSkills.CopyInto` fait une copie physique des fichiers du skill à chaque démarrage/fixture (écrasement) ; pas de détection de changement — un skill modifié dans `Alveus/skils/` n'est repris par l'Evaluator qu'au prochain démarrage.
- Le nom du dossier `skils/` (sans deux "l") est une coquille existante dans le repo, conservée telle quelle par `EvaluatorSkills.FindRepoSkillsRoot` pour rester cohérent avec la structure réelle — à corriger globalement (renommage + mise à jour de cette ADR) si jugé utile, pas dans le scope de cet ADR.

## Alternatives considérées

- **Dupliquer `RunAgentPrompt` en `RunEvaluatorPrompt` sans extraction de base commune** — écarté : la boucle de relance/compactage (ADR 0019) et le parsing de `Finish` sont la majorité du code de l'activité ; dupliquer aurait signifié maintenir deux copies en cas de correctif (ex. futur ajustement de `MaxIterations` ou `ReminderPrompt`).
- **Workspace partagé entre Worker et Evaluator (avec sous-dossiers séparés, ex. `workspace/worker/` et `workspace/evaluator/`)** — écarté (choix explicite, cf. question posée) : un répertoire racine commun aurait permis à `CmdRunTool` de l'un de lister/parcourir les fichiers de l'autre via `..`, sauf ajout d'une contrainte de confinement supplémentaire. Deux racines distinctes (`Agent:WorkspaceRoot` / `Agent:EvaluatorWorkspaceRoot`) obtiennent la même isolation sans logique de confinement additionnelle.
- **Copier les skills à la compilation (asset embarqué / `MSBuild` copy-to-output) plutôt qu'au démarrage via `EvaluatorSkills`** — écarté pour le POC : `EvaluatorSkills.CopyInto` est trivial, fonctionne identiquement pour `Program.cs` et les fixtures de test (toutes deux ont un `searchStartDirectory` valide), et évite de complexifier le `.csproj` pour un besoin qui peut évoluer (plusieurs skills, sélection conditionnelle).

## Révisions

- 2026-06-13 — création.
- 2026-06-13 — §3 (exposition des skills à l'Evaluator via une mention dans les instructions
  statiques) complété par [ADR 0022](0022-evaluator-skill-injection-via-ai-context-provider.md) :
  le contenu des `SKILL.md` est désormais injecté à chaque invocation via
  `AIContextProvider`/`ChatClientAgentOptions.AIContextProviders`, en plus de la copie sur disque
  (`EvaluatorSkills.CopyInto`, inchangée). L'isolation des workspaces (§2) et l'extraction de
  `AgentPromptActivityBase` (§1) restent inchangées.
