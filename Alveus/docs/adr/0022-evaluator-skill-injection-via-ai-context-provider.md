# ADR 0022 — Injection des skills Evaluator via `AIContextProvider`

## Status

Accepted

## Context

[ADR 0021](0021-evaluator-agent-isolated-workspace.md) §3 expose les skills méthodologiques du
repo (`Alveus/skils/dotnet-snapshot-testing/`) à Alveus-Evaluator en les copiant dans
`{workspace-evaluator}/skills/{nom}/` via `EvaluatorSkills.CopyInto`. La mise à disposition
s'arrêtait là : les instructions statiques du `ChatClientAgent` se limitaient à mentionner
*"Ton espace de travail contient un dossier 'skills/'... consulte-les si la consigne s'y
prête"*, en comptant sur l'agent pour ouvrir lui-même `SKILL.md` avec
`StrReplaceEditorTool` quand il le juge utile.

C'est une simple indication textuelle dans le prompt, pas une intégration du framework : rien ne
garantit que le modèle agisse sur cette indication. ADR 0021 §Négatif documente déjà ~50% de
flakiness des tests d'intégration de l'évaluateur sur le modèle 35B testé, en partie attribuable à
cette dépendance à l'initiative du modèle.

`Microsoft.Agents.AI` 1.10.0 (✓ déjà référencé dans `Alveus.Web.csproj`) expose
`Microsoft.Agents.AI.AIContextProvider` : une classe de base dont `ProvideAIContextAsync` est
appelée par le framework à chaque invocation de l'agent et peut retourner un `AIContext` dont la
propriété `Instructions` est fusionnée avec les instructions de l'agent pour cette invocation.
`ChatClientAgentOptions.AIContextProviders` permet d'enregistrer une liste de tels providers. C'est
le mécanisme prévu par le framework pour injecter du contexte dynamique — par opposition à une
mention statique dans le prompt.

## Decision

1. **`EvaluatorSkillsContextProvider`** (`Alveus.Web.Agents`), `: AIContextProvider`. Construit
   avec la racine du workspace evaluator. À chaque invocation, `ProvideAIContextAsync` lit tous
   les `skills/*/SKILL.md` présents (copiés par `EvaluatorSkills.CopyInto`, inchangé) et retourne
   un `AIContext` dont `Instructions` contient leur contenu concaténé, préfixé par un en-tête
   ("Méthodologies de référence disponibles pour cette tâche :"). Si `skills/` est absent ou vide,
   retourne un `AIContext` vide (`Instructions = null`) — même tolérance que
   `EvaluatorSkills.CopyInto` pour les déploiements sans les sources du repo.

2. **Câblage** : l'agent Alveus-Evaluator (`Program.cs`, `EvaluatorFixture`,
   `RunEvaluatorPromptFixture`) est construit via le constructeur `ChatClientAgent(IChatClient,
   ChatClientAgentOptions, ...)` plutôt que le constructeur simplifié
   `(IChatClient, instructions, name, tools)`, pour pouvoir renseigner
   `AIContextProviders = [new EvaluatorSkillsContextProvider(evaluatorWorkerWorkspaceRoot)]`. Les
   instructions statiques (`ChatOptions.Instructions`) sont allégées : elles indiquent que des
   méthodologies pertinentes sont fournies directement dans le contexte, et que
   `skills/{nom}/references/*.md` reste consultable via `StrReplaceEditorTool` pour le détail.

3. **`EvaluatorSkills.CopyInto` inchangé** : les fichiers restent copiés sur disque, à la fois
   pour que `EvaluatorSkillsContextProvider` puisse les lire et pour que l'agent puisse encore
   ouvrir `references/*.md` avec son outil d'édition si la consigne s'y prête.

⚠ `AIContextProvider.InvokingContext` (utilisé dans les tests pour invoquer
`InvokingAsync` directement) est marqué `[Experimental("MAAI001")]` dans
`Microsoft.Agents.AI.Abstractions` 1.10.0 — l'avertissement est supprimé localement dans
`EvaluatorSkillsContextProviderTests`. À surveiller lors d'une montée de version du package.

## Consequences

### Positif
- Le contenu du skill est garanti présent dans le contexte envoyé au modèle à chaque tour,
  indépendamment de l'initiative du modèle à utiliser `StrReplaceEditorTool` — supprime une des
  causes de flakiness documentées par ADR 0021.
- `EvaluatorSkillsContextProvider` est testable unitairement sans LLM
  (`EvaluatorSkillsContextProviderTests`) : on vérifie la lecture/concatenation des `SKILL.md`
  indépendamment du modèle.
- Le câblage suit le mécanisme d'extension prévu par `Microsoft.Agents.AI` plutôt qu'une
  convention ad hoc dans le prompt — un futur skill ou provider (ex. mémoire, RAG) suit le même
  point d'extension (`AIContextProviders`).

### Négatif
- Le contenu de **tous** les `SKILL.md` du workspace est injecté à **chaque** invocation, même si
  la tâche ne s'y prête pas — coût en tokens à chaque tour. Acceptable pour le POC avec un seul
  skill (~90 lignes) ; si plusieurs skills sont ajoutés, il faudra un mécanisme de sélection
  (par mots-clés de la consigne, par exemple à partir du front-matter `description` des
  `SKILL.md`) — non traité ici.
- `EvaluatorSkillsContextProvider` duplique la connaissance du chemin `skills/{nom}/SKILL.md` déjà
  présente dans `EvaluatorSkills` et dans les instructions statiques — couplage faible mais réel
  entre les trois.

## Alternatives considérées

- **Statu quo (mention textuelle dans les instructions)** — écarté : c'est précisément le problème
  identifié (pas d'intégration framework, dépendance à l'initiative du modèle, flakiness).
- **Exposer le skill comme `AITool` appelable à la demande** (ex. `get_skill("dotnet-snapshot-testing")`) —
  écarté pour l'instant : dépend encore de l'initiative du modèle à appeler l'outil, donc
  n'élimine pas la cause principale de flakiness ; piste d'amélioration future si l'injection
  systématique devient trop coûteuse en tokens avec plusieurs skills.
- **Injecter le contenu du skill directement dans la string d'instructions statique** (au lieu
  d'un `AIContextProvider`) — écarté : couple le code de câblage de l'agent au contenu d'un
  fichier externe, lu une seule fois à la construction (pas de rechargement si le skill change),
  et ne fournit pas de point d'extension pour une future sélection multi-skills.

## Révisions

- 2026-06-13 — création.
