# ADR 0025 — Workspaces imbriqués (BA/QA/Tech) et instructions inter-agents via FinishTool

## Status

Accepted

## Context

[ADR 0024](0024-hand-rolled-multi-agent-meetings.md) introduit 3 nouveaux participants
(Alveus-BusinessAnalyst, Alveus-Qa, Alveus-Technical) qui maintiennent chacun une documentation
markdown — règles métier, plan de test, architecture/ADRs — en lien direct avec le travail d'un
agent existant :
- les règles métier doivent être visibles par **Alveus-UserDoc** (nouvel agent, cf.
  [ADR 0026](0026-userdoc-agent-and-final-review-loop.md)), qui rédige la documentation
  utilisateur ;
- le plan de test doit être visible par **Alveus-Evaluator** (ADR 0021), qui écrit et exécute le
  jeu de test ;
- la documentation d'architecture doit être visible par **Alveus-Worker** (ADR 0017), qui
  implémente.

Relations confirmées avec Kevin avant implémentation : `Alveus-Technical ⊂ Alveus-Worker`,
`Alveus-Qa ⊂ Alveus-Evaluator`, `Alveus-BusinessAnalyst ⊂ Alveus-UserDoc` (et non
`BA ⊂ Worker`).

Par ailleurs, pendant la réunion de pré-tâche, Alveus-Technical et Alveus-Qa peuvent avoir besoin
de transmettre des précisions à un agent en aval (Worker, UserDoc, Evaluator) qui n'a pas
participé à la réunion — par exemple une contrainte technique découverte en mettant à jour
`tech-docs/`, ou un cas limite identifié en mettant à jour `test-plan/`.

## Decision

1. **Workspaces imbriqués = sous-répertoires d'un workspace existant**, sans nouveau mécanisme de
   confinement : `StrReplaceEditorTool`/`CmdRunTool` (ADR 0017) confinent déjà tout chemin résolu
   à `resolved.StartsWith(_workspaceRoot + separator)` — un sous-répertoire d'un workspace est
   nativement accessible à l'agent racine, et un agent enraciné sur le sous-répertoire reste
   confiné à celui-ci. Chaque agent BA/QA/Tech reçoit donc sa **propre instance**
   `CmdRunTool`/`StrReplaceEditorTool` (clé DI dédiée, même pattern que l'Evaluator isolé — ADR
   0021), enracinée sur :
   - `{Agent:WorkspaceRoot}/tech-docs/` pour Alveus-Technical (config
     `Agent:TechnicalWorkspaceSubdir`),
   - `{Agent:EvaluatorWorkspaceRoot}/test-plan/` pour Alveus-Qa (config `Agent:QaWorkspaceSubdir`),
   - `{Agent:UserDocWorkspaceRoot}/business-rules/` pour Alveus-BusinessAnalyst (config
     `Agent:BusinessAnalystWorkspaceSubdir`).

   Alveus-Worker/Evaluator/UserDoc, enracinés sur le workspace parent, peuvent lire (et,
   accidentellement, écrire) ces sous-répertoires avec leurs outils habituels — aucune
   modification de `CmdRunTool`/`StrReplaceEditorTool` n'a été nécessaire.

2. **`DownstreamInstruction` (`Tools/DownstreamInstruction.cs`)** — `record DownstreamInstruction(
   string Target, string Instruction)`, `Target` ∈ `worker`/`evaluator`/`userdoc`
   (`DownstreamInstructionTarget`, validation par `Enum.TryParse` ignoreCase, même esprit que
   `verdict`/`outcome` — ADR 0019/0023).

3. **`FinishTool.Finish` étendu** d'un paramètre optionnel
   `IList<DownstreamInstruction>? downstreamInstructions = null`, documenté comme pertinent
   uniquement pour Alveus-Technical (cibles `worker`/`userdoc`) et Alveus-Qa (cible `evaluator`) —
   "sans objet" pour les autres agents, sans validation de cohérence par appelant (même politique
   que `verdict` pour Alveus-Worker, ADR 0023). Validation : chaque `Target` doit être un
   `DownstreamInstructionTarget` connu, sinon `ArgumentException`.

4. **`FinishCall` étendu** d'un champ `IReadOnlyList<DownstreamInstruction>? DownstreamInstructions`,
   parsé depuis un argument `"downstreamInstructions"` — tableau d'objets `{target, instruction}`,
   supportant à la fois `JsonElement` (arguments sérialisés) et `IEnumerable<object?>`/
   `IDictionary<string, object?>` (arguments déjà désérialisés), même double-chemin de parsing que
   `ReadStringList`. `null`/absent si non fourni.

5. **Routage dans `RunPreTaskMeeting.OnAgentFinishAsync`** (hook ADR 0024) : pour chaque
   `DownstreamInstruction` reçue d'Alveus-Technical/Alveus-Qa lors de `Finish(outcome='done')`,
   l'instruction est ajoutée à une liste interne par cible (`_workerInstructions`,
   `_evaluatorInstructions`, `_userDocInstructions`) ; plusieurs instructions pour la même cible
   sont concaténées (`string.Join("\n\n", ...)`) dans les outputs
   `WorkerInstructions`/`EvaluatorInstructions`/`UserDocInstructions` de l'activité.
   `AlveusTaskWorkflow` injecte ces outputs dans les prompts de `RunWorker`/`RunEvaluator`/
   `RunUserDoc` respectivement, en append (même pattern que `FailureReport`, ADR 0023) — vide si
   aucune instruction n'a été émise.

## Consequences

### Positif
- Aucune extension du mécanisme de confinement (ADR 0017) : les sous-répertoires sont une
  conséquence directe de `StartsWith`, déjà couverte par les tests existants.
- `DownstreamInstruction` est une liste générique à 3 cibles plutôt que 3 champs dédiés
  (`instructionsForWorker`/`instructionsForUserDoc`/`instructionsForEvaluator`) — un agent peut
  émettre 0, 1 ou plusieurs instructions vers des cibles différentes dans le même `Finish`, sans
  champs vides à ignorer.
- Le routage est centralisé dans `RunPreTaskMeeting` (un seul endroit qui connaît la
  correspondance rôle → cible → variable de sortie), pas dispersé dans `FinishTool`/`FinishCall`
  qui restent agnostiques du contexte (réunion vs cycle Worker/Evaluator).

### Négatif
- Alveus-Worker/Evaluator/UserDoc peuvent **techniquement** écrire dans le sous-répertoire
  documentaire de BA/QA/Tech (`tech-docs/`, `test-plan/`, `business-rules/`) — aucune protection
  en écriture au-delà des instructions système de chaque agent ("ne modifie pas ce
  sous-dossier"). Accepté : cohérent avec le niveau de confinement existant (ADR 0017 protège
  contre l'évasion *hors* du workspace, pas les conflits *à l'intérieur*).
- `downstreamInstructions` n'est validé que sur la forme (`Target` connu) — un agent peut émettre
  une instruction incohérente avec son rôle (ex. Alveus-BusinessAnalyst émettant `target='worker'`)
  sans erreur ; le routage la traiterait comme si elle venait de Tech. Accepté pour rester simple
  (même politique que `verdict` "sans objet pour Worker" sans l'empêcher, ADR 0023) — si ça
  s'avère un problème en pratique, une validation par rôle pourra être ajoutée sans changer le
  contrat `FinishTool`.
- `RunFinalReviewMeeting.OnAgentFinishAsync` est un no-op (`ValueTask.CompletedTask`) :
  `downstreamInstructions` émis pendant la réunion finale sont silencieusement ignorés — pas de
  cas d'usage identifié pour l'instant (la réunion finale produit des comptes-rendus, pas des
  instructions vers un agent en aval qui a déjà tourné).

## Alternatives considérées

- **Champs dédiés `instructionsForWorker`/`instructionsForEvaluator`/`instructionsForUserDoc` sur
  `FinishTool.Finish`** (mentionnés dans le plan initial) — écarté au profit d'une liste unique
  `downstreamInstructions` typée par `Target` : un seul paramètre à documenter/parser, extensible
  à de futures cibles sans ajouter de paramètres à `Finish`, et un agent qui n'a rien à dire pour
  une cible ne fournit simplement pas d'entrée (vs. un champ vide explicite).
- **BA enraciné sur `business-rules/` mais avec accès en lecture étendu à tout
  `workspace-userdoc/`** (second jeu d'outils en lecture, comme envisagé dans le plan pour la
  réunion finale) — écarté pour la réunion de pré-tâche : BA n'a pas besoin de lire le travail
  d'Alveus-UserDoc à ce stade (il écrit en amont). Le besoin symétrique pour la réunion finale est
  traité séparément par ADR 0026 (option "résumés uniquement", pas de second jeu d'outils).
- **Un seul workspace partagé par BA/QA/Tech** (pas d'imbrication, dossier `meeting-docs/` séparé
  des workspaces Worker/Evaluator/UserDoc) — écarté : casserait la lecture native par
  Worker/Evaluator/UserDoc de la documentation qui les concerne (ils devraient recevoir le contenu
  via le prompt plutôt que de le lire eux-mêmes), et introduirait un 4ᵉ répertoire racine sans
  bénéfice — l'imbrication réutilise le confinement existant sans rien ajouter.

## Révisions

- 2026-06-14 — création.
- 2026-06-15 — [ADR 0030](0030-generic-specialist-agents.md) généralise Alveus-BusinessAnalyst en
  un catalogue de rôles "spécialiste" interchangeables/cumulables (`SpecialistRoleCatalog`,
  `Agent:SpecialistRoleKeys`). La relation d'imbrication des workspaces décrite ici
  (`{spécialiste} ⊂ Alveus-UserDoc`, `Alveus-Qa ⊂ Alveus-Evaluator`, `Alveus-Technical ⊂
  Alveus-Worker`) reste valide, simplement généralisée à N spécialistes au lieu d'un seul BA.
