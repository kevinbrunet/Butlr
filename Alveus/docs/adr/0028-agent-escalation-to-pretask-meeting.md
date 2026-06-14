# ADR 0028 — Escalade NeedsMoreInfo/Blocked des agents Worker/EnvironmentManager/Evaluator/UserDoc vers la réunion de pré-tâche

## Status

Accepted

## Context

Dans `AlveusTaskWorkflow` (ADR 0023-0027), les quatre activités agent individuelles —
`RunAgentPrompt` (Alveus-Worker), `RunEnvironmentPrompt` (Alveus-EnvironmentManager),
`RunEvaluatorPrompt` (Alveus-Evaluator) et `RunUserDocPrompt` (Alveus-UserDoc) — peuvent compléter
avec l'issue `"NeedsMoreInfo"` (consigne ambiguë, questions à poser) ou `"Blocked"` (impossible de
continuer), via `AgentPromptActivityBase.TryCompleteAsync`/`HandleVerdictAsync`. Ces deux issues
n'ont **aucune connexion** dans le `Flowchart` : elles terminent implicitement le workflow.

Demande : ces ambiguïtés/blocages individuels ne doivent pas systématiquement clore la tâche —
**BA/QA/Tech (réunion de pré-tâche, ADR 0024) doivent pouvoir les traiter conjointement** et fournir
des instructions complémentaires, avant de relancer le cycle. Décisions retenues en amont :
- Scope = les **4 activités** (Worker, EnvironmentManager, Evaluator, UserDoc) — pas seulement
  Alveus-Evaluator.
- Un **nouveau garde de boucle dédié**, avec son propre budget, distinct de
  `OuterLoopIterationGuard` (ADR 0026, cycle "verdict KO de la réunion finale → réunion de
  pré-tâche") : les deux causes de retour à `RunPreTaskMeeting` (verdict KO global vs. escalade d'un
  agent individuel) sont sémantiquement différentes et ne doivent pas partager un budget de
  tentatives.

Ce mécanisme est distinct de "NeedsHelp" (issue globale de `RunPreTaskMeeting`/
`RunFinalReviewMeeting`, ADR 0027) : "NeedsHelp" suspend le workflow et attend une réponse humaine
via `AwaitConversationReply` ; ici, c'est un agent individuel qui bloque, et la réunion de pré-tâche
est sollicitée pour trancher **sans intervention humaine** (elle peut éventuellement déboucher sur
un "NeedsHelp" si BA/QA/Tech ne trouvent pas de consensus — comportement ADR 0027 inchangé).

## Decision

1. **Nouvelle activité `RecordAgentEscalation : CodeActivity`**
   (`src/Alveus.Web/Workflows/RecordAgentEscalation.cs`). Met en forme l'escalade d'un agent dans
   une variable de rapport partagée :
   - `Input<string> SourceLabel` (constante par instance, ex. `"Alveus-Worker"`).
   - `Input<string?> Reason`, `Input<IReadOnlyList<string>?> Questions`.
   - `Variable<string?> Report` (sortie, écrasée — un seul agent escalade par cycle).
   - `ExecuteAsync` produit un texte `"Escalade de {SourceLabel}"` (+ `Reason` si présent, +
     `Questions` formatées en liste si présentes), l'écrit dans `Report`, complète par `"Done"`.

   Quatre instances dans `AlveusTaskWorkflow` — `RecordWorkerEscalation`,
   `RecordEnvironmentManagerEscalation`, `RecordEvaluatorEscalation`, `RecordUserDocEscalation` —
   toutes écrivant dans la même variable `AgentEscalationReport`.

2. **Nouveau garde `AgentEscalationLoopGuard : CodeActivity`**
   (`src/Alveus.Web/Workflows/AgentEscalationLoopGuard.cs`), copie structurelle de
   `OuterLoopIterationGuard` : `Variable<int> AgentEscalationLoopCount`, `MaxIterations = 3`,
   `Output<int> Iteration`, sort `"Continue"`/`"LimitReached"`. Budget indépendant de
   `OuterLoopIterationGuard.MaxIterations` (également 3, mais variable séparée — les deux cycles ne
   se "consomment" pas l'un l'autre).

3. **Bindings de sortie ajoutés** sur les 4 activités agent, pour exposer `Reason`/`Questions` à
   `RecordAgentEscalation` :
   - `RunWorker` : `Reason → WorkerEscalationReason`, `Questions → WorkerEscalationQuestions`
     (variables dédiées, jusqu'ici non liées).
   - `RunEnvironmentManager`/`RunEvaluator` : `Questions → EnvManagerEscalationQuestions`/
     `EvaluatorEscalationQuestions` (variables dédiées) ; `Reason` reste lié à `FailureReport`
     (partagé avec l'issue "Failed" existante, ADR 0023 — pas de conflit, un seul des deux chemins
     est pris par cycle).
   - `RunUserDoc` : `Reason → UserDocEscalationReason`, `Questions → UserDocEscalationQuestions`
     (variables dédiées, jusqu'ici non liées).

4. **Nouvelles connexions** :
   ```
   RunWorker --NeedsMoreInfo/Blocked--> RecordWorkerEscalation
   RunEnvironmentManager --NeedsMoreInfo/Blocked--> RecordEnvironmentManagerEscalation
   RunEvaluator --NeedsMoreInfo/Blocked--> RecordEvaluatorEscalation
   RunUserDoc --NeedsMoreInfo/Blocked--> RecordUserDocEscalation

   Record*Escalation --Done--> AgentEscalationLoopGuard
   AgentEscalationLoopGuard --Continue--> RunPreTaskMeeting
                            --LimitReached--> (fin, terminal — comme les autres gardes)
   ```

5. **`RunPreTaskMeeting.ExtraContext`** intègre `AgentEscalationReport` dans la concaténation
   existante (`BaReport`/`QaReport`/`TechReport`/`PreTaskHumanReply`), même pattern : reports non
   vides joints par `"\n\n---\n"`.

6. **`ConversationTransitionNotificationHandler.TrackedActivityIds`** (ADR 0027) étendu avec les 5
   nouvelles activités, pour que leurs transitions apparaissent dans le flux d'observabilité de la
   conversation.

## Consequences

### Positif
- Une ambiguïté ou un blocage signalé par n'importe lequel des 4 agents devient **rattrapable** :
  BA/QA/Tech peuvent répondre aux questions, ajuster les instructions complémentaires
  (`WorkerInstructions`/`EvaluatorInstructions`/`UserDocInstructions`), et la tâche reprend sans
  intervention humaine immédiate.
- `RecordAgentEscalation` est un `CodeActivity` minimal, sans dépendance LLM — coût d'exécution
  négligeable par rapport au cycle qu'il déclenche.
- Réutilise le mécanisme `ExtraContext` de `RunPreTaskMeeting` (ADR 0024/0026), déjà conçu pour
  recevoir des comptes-rendus de cycles précédents — aucune nouvelle voie de communication avec les
  agents de réunion.
- Budget de boucle dédié (`AgentEscalationLoopGuard.MaxIterations = 3`, distinct de
  `OuterLoopIterationGuard`) : un agent qui ré-escalade systématiquement n'épuise pas le budget du
  cycle "verdict KO", et inversement.

### Négatif
- Un cycle d'escalade complet (RunPreTaskMeeting → jusqu'à l'agent qui re-bloque) peut être presque
  aussi coûteux qu'un cycle normal — `AgentEscalationLoopGuard.MaxIterations = 3` borne ce coût,
  au prix d'un abandon plus rapide en cas de blocage persistant (comme pour les autres gardes).
- ⚠ Les 3 tests `IsLlamaCppAvailable` existants qui vérifient qu'un "Blocked" individuel termine le
  workflow (`AlveusTaskWorkflow_WorkerBlocked_EndsWithoutEnvironmentManager`,
  `..._EnvironmentManagerBlocked_EndsWithoutEvaluator`, `..._EvaluatorBlocked_EndsWithoutLooping`)
  bouclent désormais jusqu'à `AgentEscalationLoopGuard.MaxIterations + 1` cycles avant de réellement
  se terminer (le même agent re-bloque sur la même consigne à chaque passage) — assertions
  inchangées, mais temps d'exécution ~4× plus long et surface de flakiness accrue (cf. ADR 0021).
- `RunEnvironmentManager`/`RunEvaluator` partagent `FailureReport` entre l'issue "Failed" (boucle
  interne `LoopGuard`, ADR 0023) et l'issue "NeedsMoreInfo" (nouvelle escalade) : si l'un de ces
  deux chemins est emprunté juste après l'autre lors d'un même cycle de pré-tâche, l'ancien contenu
  de `FailureReport` pourrait être lu par `RunWorker` (`failureReport.Get(context)`, prompt "Rapport
  de l'évaluation précédente") avant d'être écrasé — ⚠ pas de scénario identifié où cela se produit
  en pratique (un seul chemin de sortie par exécution d'activité), mais à surveiller si un nouveau
  cas d'usage introduit un chemin combiné.

## Alternatives considérées

- **Réutiliser `OuterLoopIterationGuard` pour ce nouveau cycle** — écarté : les deux causes de
  retour à `RunPreTaskMeeting` (verdict KO global de la réunion finale vs. escalade d'un agent
  individuel en cours de cycle) sont déclenchées à des moments très différents du graphe ; partager
  un compteur aurait mélangé deux budgets sans rapport, et un agent escaladant répétitivement
  aurait pu épuiser le budget réservé au cycle KO (et réciproquement).
- **Router directement les issues `"NeedsMoreInfo"`/`"Blocked"` vers `RunPreTaskMeeting` sans étape
  intermédiaire** — écarté : `RunPreTaskMeeting.ExtraContext` n'aurait alors reçu que
  `Reason`/`Questions` bruts de l'agent source, sans label identifiant la provenance ; avec 4
  sources possibles (+ `BaReport`/`QaReport`/`TechReport`/`PreTaskHumanReply` déjà présents), un
  texte non préfixé aurait nui à la lisibilité pour BA/QA/Tech. `RecordAgentEscalation` reste un
  `CodeActivity` trivial, coût quasi nul.
- **Scope limité à Alveus-Evaluator seul** (option initialement envisagée) — écarté sur demande
  explicite : Worker/EnvironmentManager/UserDoc peuvent tout autant rencontrer une ambiguïté
  nécessitant l'arbitrage de BA/QA/Tech (ex. Worker bloqué sur une règle métier non précisée).
- **Variables `Reason`/`Questions` séparées vs. réutilisation de `FailureReport` pour
  EnvironmentManager/Evaluator** — réutilisation retenue pour `Reason` (même sémantique "rapport de
  problème" que l'issue "Failed" existante, pas de variable redondante) ; `Questions` reste séparé
  (pas d'équivalent côté "Failed").

## Révisions

- 2026-06-14 — création.
