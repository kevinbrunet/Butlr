# ADR 0024 — Réunions multi-agents hand-rolled dans Elsa (MeetingActivityBase)

## Status

Accepted

## Context

[ADR 0023](0023-worker-environment-evaluator-workflow.md) a posé un cycle séquentiel à 3 agents
(Worker → EnvironmentManager → Evaluator), chacun avec son propre `MaxIterations` (ADR 0019). Kevin
veut maintenant encadrer ce cycle par deux **réunions à 3 agents** (Alveus-BusinessAnalyst,
Alveus-Qa, Alveus-Technical) qui débattent et votent : une réunion de pré-tâche (avant le Worker)
et une réunion finale (après Evaluator/UserDoc, cf. [ADR 0026](0026-userdoc-agent-and-final-review-loop.md)).

Une réunion multi-agents n'est pas un enchaînement séquentiel d'activités indépendantes : les 3
participants doivent voir les interventions des autres dans un ordre stable (round-robin), pouvoir
ouvrir des points de désaccord (`Raise`) et voter dessus (`Vote`), avec une règle de tally et de
sortie de boucle. Aucune des activités existantes (`AgentPromptActivityBase` et dérivées) ne couvre
ce cas : elles orchestrent un seul agent avec relance interne.

Avant de concevoir un mécanisme maison, vérification de l'écosystème :
- ✓ `Microsoft.Agents.AI.Workflows` 1.10.0 contient bien `GroupChatWorkflowBuilder` /
  `GroupChatManager` / `RoundRobinGroupChatManager` (confirmé par exploration de l'assembly) — un
  mécanisme natif de "group chat" round-robin entre plusieurs `AIAgent`.
- ⚠ Mais ce builder produit un **graphe d'executors `Microsoft.Agents.AI.Workflows`**, un second
  moteur de workflow avec son propre modèle de checkpoint/état, distinct du `Flowchart` Elsa
  utilisé par `AlveusTaskWorkflow`. L'intégrer signifierait faire cohabiter deux moteurs de
  workflow dans le même processus, avec deux façons de persister l'état d'une activité/exécution.

Décision actée avec Kevin avant implémentation : **hand-roll la réunion dans Elsa**, comme une
`CodeActivity` qui orchestre elle-même la boucle round-robin et les appels aux 3 `AIAgent`,
réutilisant le mécanisme de session persistée existant (ADR 0018) et `FinishTool` (ADR 0019). C'est
une déviation explicite par rapport au mécanisme natif de `Microsoft.Agents.AI.Workflows`, tracée
ici.

⚠ Risque assumé : un débat à 3 agents LLM avec vote/correction est une charge de coordination plus
lourde que le cycle séquentiel existant, déjà ~50% flaky pour l'Evaluator seul sur un modèle 35B
(ADR 0021/0023). Le mécanisme est construit proprement, mais sa fiabilité en pratique n'est pas
garantie et sera observée via les tests gated par `IsLlamaCppAvailable`.

## Decision

1. **`MeetingActivityBase : CodeActivity`** (`Activities/MeetingActivityBase.cs`), classe abstraite
   commune à `RunPreTaskMeeting` et `RunFinalReviewMeeting`. Trois sessions `AgentSession`
   persistées en parallèle (`AgentSession::{agentName}`, même mécanisme et même clé de propriété
   que `AgentPromptActivityBase` — ADR 0018), une par rôle (`BusinessAnalyst`, `Qa`, `Technical`),
   noms résolus via `Agent:BusinessAnalystName`/`Agent:QaName`/`Agent:TechnicalName`.

2. **Boucle round-robin bornée** : `MaxRounds = 4`. À chaque round, les 3 participants
   s'expriment dans l'ordre fixe BusinessAnalyst → Qa → Technical. Au round 1, chaque agent reçoit
   `GetRoleTask(role)` (texte abstrait fourni par la sous-classe) + `Topic` + `ExtraContext`
   (comptes-rendus de la réunion finale précédente en cas de boucle KO, cf. ADR 0026). Aux rounds
   suivants, chaque agent reçoit le **transcript des nouveaux événements** depuis son dernier tour
   (texte des autres participants, `Raise`/`Vote` reçus), plus un rappel des topics qu'il doit
   reconsidérer (round de correction, cf. point 4).

3. **`MeetingTool` (`Tools/MeetingTool.cs`)** — outil dédié, distinct de `FinishTool` : `Finish`
   signale la fin du tour de *l'agent appelant*, tandis que `Raise`/`Vote` coordonnent les 3
   participants *entre eux* sur un topic. Deux fonctions :
   - `Raise(topic, comment)` — ouvre/alimente un topic de discussion, visible par les 2 autres
     participants à leur prochain tour.
   - `Vote(topic, decision, comment?)` — `decision` ∈ `agree`/`disagree` (`MeetingVoteDecision`),
     `comment` obligatoire si `disagree`.

   `MeetingCall.cs` parse ces appels (`RaiseCall`/`VoteCall`, deux records distincts) depuis les
   `FunctionCallContent` de la réponse, même style de parsing que `FinishCall.FromArguments` (ADR
   0019), y compris support `JsonElement` pour les arguments sérialisés.

4. **Règle de tally par topic**, calculée en fin de round pour chaque topic ayant reçu un vote des
   3 participants :
   - **3-0** (unanime, dans un sens ou l'autre) → topic **résolu**, retiré des topics ouverts,
     tally conservé (`MeetingVoteTally(Agree, Disagree)`).
   - **2-1** → si c'est le premier passage (`!InCorrectionRound`), les votes sont effacés et le
     topic repasse en **round de correction** : au round suivant, le dissident est invité (via le
     transcript) à reconsidérer sa position avant de revoter.
   - **2-1 persistant après le round de correction** → `MeetingOutcome.NeedsHelp` immédiat
     (escalade — pas de second round de correction).

5. **Fin de réunion** :
   - **`MeetingOutcome.Done`** : un round voit les 3 participants confirmer `Finish(outcome='done')`
     **et** aucun topic ouvert restant (un nouveau `Raise` dans ce round annule un `Finish(done)`
     déjà reçu ce round — l'agent doit reconfirmer à un round suivant, implicitement via la boucle
     puisque son `Finish` n'est compté que pour le round où il a lieu).
   - **`MeetingOutcome.NeedsHelp`** : `MaxRounds` atteint sans `Done`, ou désaccord persistant 2-1
     (point 4).
   - **`AgentTaskOutcome.NeedsMoreInfo`/`Blocked`** d'un participant individuel (via `FinishTool`,
     ADR 0019) → sortie immédiate de la réunion avec cet outcome ("NeedsMoreInfo"/"Blocked"), même
     traitement que dans `AgentPromptActivityBase` : un point bloquant d'un participant bloque la
     réunion entière.

6. **Points d'extension abstraits** pour les sous-classes (`RunPreTaskMeeting`,
   `RunFinalReviewMeeting`) :
   - `GetRoleTask(agentRole)` — consigne spécifique à la réunion pour BA/QA/Tech.
   - `SeedOpenTopics()` — topics ouverts dès le round 1, avant tout `Raise` (utilisé par
     `RunFinalReviewMeeting` pour le topic implicite `"task-fulfilled"`, cf. ADR 0026).
   - `OnAgentFinishAsync(context, agentRole, finish)` — capte les champs spécifiques d'un
     `Finish(done)` (ex. `DownstreamInstructions`, cf. ADR 0025).
   - `FinalizeAsync(context, outcome, topicTallies, finishSummaries)` — traduit `MeetingOutcome` +
     tallies en issues concrètes de l'activité (`"Done"`/`"NeedsHelp"` pour la pré-tâche ;
     `"OK"`/`"KO"`/`"NeedsHelp"` pour la finale, cf. ADR 0026).

## Consequences

### Positif
- Un seul mécanisme de réunion réutilisé pour pré-tâche et revue finale, cohérent avec le pattern
  `AgentPromptActivityBase`/sous-classes déjà en place (ADR 0023).
- Pas de second moteur de workflow : tout reste dans `Elsa.Workflows.Flowchart`, testable comme le
  reste (`AlveusTaskWorkflowTests`, ADR 0023).
- Le protocole `Raise`/`Vote`/tally est explicite et déterministe une fois les votes reçus —
  testable indépendamment du LLM via `MeetingToolTests`/`MeetingCallTests` (parsing, validation).

### Négatif
- ⚠ Charge de coordination significative : un round complet appelle 3 agents LLM, jusqu'à
  `MaxRounds = 4` rounds, soit jusqu'à 12 appels avant `NeedsHelp` — coût en tokens/latence
  important comparé au cycle séquentiel d'ADR 0023.
- ⚠ La fiabilité de `Raise`/`Vote` (un agent les utilise-t-il au bon moment, avec le bon `topic` ?)
  dépend du suivi d'instructions multi-tours par le LLM — flakiness attendue, à observer (cf.
  Révisions).
- Le round de correction (2-1 → un seul nouveau round) est une heuristique simple : un dissident
  qui change d'avis à tort (pression sociale simulée) n'est pas distingué d'un dissident qui avait
  raison — aucun mécanisme de "qui a raison" au-delà du vote majoritaire.
- `MeetingActivityBase` duplique une partie de la logique de session/compaction de
  `AgentPromptActivityBase` (pas de classe de base commune entre les deux) — accepté pour éviter de
  complexifier une base déjà utilisée par 3 activités existantes avec un cas d'usage différent
  (1 agent vs 3 agents en parallèle).

## Alternatives considérées

- **`GroupChatWorkflowBuilder`/`RoundRobinGroupChatManager` natifs de
  `Microsoft.Agents.AI.Workflows` 1.10.0** — écarté : second moteur de workflow (graphe
  d'executors, modèle de checkpoint propre) distinct du `Flowchart` Elsa. L'utiliser aurait exigé
  soit d'exécuter ce sous-graphe *dans* une activité Elsa (boîte noire difficile à tester avec les
  fixtures existantes), soit de migrer `AlveusTaskWorkflow` entier vers ce moteur (hors scope,
  remettrait en cause ADR 0023 dans son ensemble). Le gain (moins de code maison) ne compensait pas
  la complexité de faire cohabiter deux moteurs de workflow et deux modèles de persistance d'état.
- **Étendre `FinishTool` avec `Raise`/`Vote`** — écarté : `FinishTool.Finish` qualifie la fin du
  tour de l'agent *appelant* (ADR 0019) ; `Raise`/`Vote` qualifient une coordination *entre*
  agents sur un sujet partagé. Les fusionner aurait surchargé `Finish` avec des paramètres sans
  rapport et aurait empêché un agent d'appeler les deux dans le même tour (un agent peut à la fois
  voter sur un topic et terminer son tour avec `Finish(done)`).
- **Réutiliser/étendre `AgentPromptActivityBase`** — écarté : cette base orchestre un seul agent
  avec relance interne (`MaxIterations`, ADR 0019) ; une réunion à 3 agents avec ordre fixe, tally
  inter-agents et issues spécifiques (`Done`/`NeedsHelp`/`OK`/`KO`) est un problème suffisamment
  différent pour justifier une base séparée plutôt que des paramètres conditionnels ajoutés à la
  base existante.
- **Tally "1 round de correction max" remplacé par une boucle de correction non bornée** (jusqu'à
  consensus ou `MaxRounds`) — écarté : aurait pu consommer tout le budget de `MaxRounds` sur un
  seul topic récalcitrant au détriment des autres ; un seul round de correction puis escalade
  (`NeedsHelp`) donne une limite prévisible et un signal clair (désaccord persistant = besoin
  d'arbitrage humain).

## Révisions

- 2026-06-14 — création.
