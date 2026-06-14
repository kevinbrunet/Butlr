# Alveus — Architecture

> Sous-projet Butlr : bac à sable d'orchestration d'agents LLM (.NET / Elsa / Microsoft.Agents.AI),
> distinct du pipeline vocal (`carlson`) et du serveur domotique (`mcp-home`).

**Marqueurs de confiance** utilisés dans ce doc :
- ✓ connaissance fiable
- ~ approximatif, à vérifier avant d'en dépendre
- ⚠ extrapolé, ne pas utiliser sans vérification

---

## 1. Objectif

Alveus est un projet `Alveus.Web` (ASP.NET Core, `net10.0`) qui orchestre des **agents LLM
agentiques** (shell + édition de fichiers) via des workflows [Elsa](https://elsa-workflows.io/) ~,
pour exécuter une tâche, (re)lancer son environnement, puis la faire valider par un agent
évaluateur indépendant — avec boucle de correction automatique en cas d'échec.

C'est un POC d'agent de développement autonome (façon "agent de code"), pas un composant du
pipeline domotique vocal décrit dans `../../docs/architecture.md`. Le LLM est servi par le même
backend llama.cpp que le reste de Butlr (✓ ADR 0006).

## 2. Vue d'ensemble du flux

```
RunPreTaskMeeting (BA + QA + Tech)
  --Done--> Alveus-Worker → Alveus-EnvironmentManager → Alveus-Evaluator
  --NeedsHelp--> AwaitPreTaskReply --Done--> PreTaskHelpLoopGuard --Continue--> RunPreTaskMeeting
                  ^                    |  Failed              |  Failed                 --LimitReached--> fin (Blocked)
                  |                    v                      v
                  +---------------- LoopGuard <---------------+

Alveus-Evaluator --Passed--> Alveus-UserDoc --Done--> RunFinalReviewMeeting (BA + QA + Tech)
                                                          --OK--> fin (succès)
                                                          --KO--> OuterLoopGuard --Continue--> RunPreTaskMeeting
                                                                                 --LimitReached--> fin (Blocked)
                                                          --NeedsHelp--> AwaitFinalReviewReply --Done--> FinalReviewHelpLoopGuard
                                                                                                          --Continue--> RunFinalReviewMeeting
                                                                                                          --LimitReached--> fin (Blocked)

Alveus-Worker / Alveus-EnvironmentManager / Alveus-Evaluator / Alveus-UserDoc
  --NeedsMoreInfo/Blocked--> Record*Escalation --Done--> AgentEscalationLoopGuard
                                                            --Continue--> RunPreTaskMeeting
                                                            --LimitReached--> fin (Blocked)
```

0. **Réunion de pré-tâche** (`RunPreTaskMeeting`, ADR 0024/0025) : avant le Worker,
   Alveus-BusinessAnalyst/Alveus-Qa/Alveus-Technical lisent `TaskPrompt`, mettent à jour leur
   documentation respective (règles métier, plan de test, architecture/ADRs), débattent
   (`Raise`/`Vote`) et préparent des instructions complémentaires (`WorkerInstructions`,
   `EvaluatorInstructions`, `UserDocInstructions`) pour les étapes en aval. Sortie `"Done"` →
   Worker ; `"NeedsHelp"` → fin (Blocked).
1. **Alveus-Worker** exécute la tâche décrite par `TaskPrompt` (+ `WorkerInstructions`) dans son
   workspace (`Agent:WorkspaceRoot`), avec accès shell (`CmdRunTool`) et édition de fichiers
   (`StrReplaceEditorTool`).
2. Une fois `Finish(outcome='done')`, le travail est vérifié objectivement
   (`IAgentWorkVerificationService`, ex. `dotnet test`) avant de sortir par `"Done"`. Échec →
   relance le Worker avec la sortie de la commande de vérification.
3. **Alveus-EnvironmentManager** (re)lance l'environnement local décrit par la consigne (mêmes
   outils, même workspace que le Worker) et rend un verdict (`pass`/`fail`/`needmoreinfo`) avec,
   si `pass`, des instructions d'usage (URL, ports, commandes) pour un agent sans accès au
   filesystem.
4. **Alveus-Evaluator**, dans un workspace **isolé** (`Agent:EvaluatorWorkspaceRoot`), reçoit la
   même consigne que le Worker + les instructions d'usage de l'EnvironmentManager + `EvaluatorInstructions`,
   écrit un jeu de test et l'exécute **en réseau uniquement** (ex. `curl`), puis rend un verdict.
5. Verdict `fail` → `LoopGuard` renvoie au Worker avec un rapport (`FailureReport`), jusqu'à
   `MaxIterations` cycles (constante = 5), puis fin de workflow (Blocked).
6. Verdict `pass` → **Alveus-UserDoc** (ADR 0026) met à jour la documentation utilisateur
   (`Agent:UserDocWorkspaceRoot` + `UserDocInstructions`), sortie `"Done"` →
   **réunion finale** ; `"NeedsMoreInfo"/"Blocked"` → fin de workflow.
7. **Réunion finale** (`RunFinalReviewMeeting`, ADR 0024/0026) : Alveus-BusinessAnalyst/Alveus-Qa/
   Alveus-Technical votent sur le topic implicite `"task-fulfilled"` au vu des résumés de
   Worker/EnvironmentManager/Evaluator/UserDoc et de leur propre documentation. `"OK"` → fin de
   workflow (succès). `"KO"` → chaque agent écrit un compte-rendu
   (`BaReport`/`QaReport`/`TechReport`) et `OuterLoopGuard` renvoie vers `RunPreTaskMeeting`
   (`ExtraContext` = ces comptes-rendus), jusqu'à `OuterLoopIterationGuard.MaxIterations` cycles
   (constante = 3), puis fin de workflow (Blocked).
8. **`"NeedsHelp"`** (issue globale de `RunPreTaskMeeting` ou `RunFinalReviewMeeting`, ADR 0027) :
   au lieu de terminer le workflow, `AwaitConversationReply` **suspend réellement** l'exécution via
   un bookmark Elsa natif — la réunion poste ses questions dans la conversation associée
   (`item.metadata.kind == "needs_help_question"`), et attend une réponse `user` via
   `POST /v1/conversations/{id}/items`. La reprise relance la même réunion
   (`PreTaskHumanReply`/`FinalReviewHumanReply` injectés dans `ExtraContext`) via
   `PreTaskHelpLoopGuard`/`FinalReviewHelpLoopGuard` (`MaxIterations = 5`), puis fin de workflow
   (Blocked) si la limite est atteinte.
9. **`"NeedsMoreInfo"`/`"Blocked"` d'un agent individuel** (Alveus-Worker,
   Alveus-EnvironmentManager, Alveus-Evaluator ou Alveus-UserDoc, ADR 0028) : au lieu de terminer le
   workflow, `Record*Escalation` met en forme `Reason`/`Questions` de l'agent dans
   `AgentEscalationReport` (préfixé par le nom de l'agent), puis `AgentEscalationLoopGuard`
   (`MaxIterations = 3`, budget séparé de `OuterLoopIterationGuard`) renvoie à `RunPreTaskMeeting`
   (`AgentEscalationReport` injecté dans `ExtraContext`) pour qu'Alveus-BusinessAnalyst/Alveus-Qa/
   Alveus-Technical traitent le sujet conjointement, jusqu'à `MaxIterations` cycles, puis fin de
   workflow (Blocked) si la limite est atteinte.

Le graphe complet est défini en C# dans `AlveusTaskWorkflow`
(`src/Alveus.Web/Workflows/AlveusTaskWorkflow.cs`), pas via le designer Elsa — cf. ADR 0023 pour le
cycle Worker/EnvironmentManager/Evaluator, ADR 0024/0025/0026 pour les réunions, Alveus-UserDoc et la
boucle externe, ADR 0027 pour l'API de conversation et la suspension `NeedsHelp`, et ADR 0028 pour
l'escalade des agents individuels vers la réunion de pré-tâche. Ports utilisés :
`Done`/`NeedsMoreInfo`/`Blocked`/`Passed`/`Failed`/`Continue`/`LimitReached`/`NeedsHelp`/`OK`/`KO`.

## 3. Agents

Sept `ChatClientAgent` (Microsoft.Agents.AI ✓ 1.10.0), enregistrés en DI dans `Program.cs`,
partageant le même `IChatClient` (llama.cpp) :

| Agent | Nom config | Workspace | Rôle |
|---|---|---|---|
| Alveus-Worker | `Agent:Name` | `Agent:WorkspaceRoot` | Exécute la tâche. |
| Alveus-EnvironmentManager | `Agent:EnvironmentManagerName` | `Agent:WorkspaceRoot` (partagé avec le Worker) | (Re)lance l'environnement local après le Worker. |
| Alveus-Evaluator | `Agent:EvaluatorName` | `Agent:EvaluatorWorkspaceRoot` (isolé) | Écrit et exécute un jeu de test contre l'environnement, sans accès filesystem au workspace du Worker. |
| Alveus-UserDoc | `Agent:UserDocName` | `Agent:UserDocWorkspaceRoot` | Met à jour la documentation utilisateur après le verdict `pass` de l'Evaluator (ADR 0026). |
| Alveus-BusinessAnalyst | `Agent:BusinessAnalystName` | `{UserDocWorkspaceRoot}/{Agent:BusinessAnalystWorkspaceSubdir}` (sous-répertoire de UserDoc) | Règles métier (markdown), participant aux réunions (ADR 0024/0025). |
| Alveus-Qa | `Agent:QaName` | `{EvaluatorWorkspaceRoot}/{Agent:QaWorkspaceSubdir}` (sous-répertoire de l'Evaluator) | Plan de test (markdown), participant aux réunions (ADR 0024/0025). |
| Alveus-Technical | `Agent:TechnicalName` | `{WorkspaceRoot}/{Agent:TechnicalWorkspaceSubdir}` (sous-répertoire du Worker) | Documentation d'architecture/ADRs, participant aux réunions (ADR 0024/0025). |

Tous partagent les mêmes classes d'outils (`CmdRunTool`, `StrReplaceEditorTool`, `FinishTool`) —
seule la racine du workspace varie (cf. ADR 0017, ADR 0021, ADR 0025). Alveus-BusinessAnalyst/
Alveus-Qa/Alveus-Technical ont en plus accès à `MeetingTool` (`Raise`/`Vote`, ADR 0024).

## 4. Tools (`src/Alveus.Web/Tools/`)

- **`CmdRunTool`** — shell persistant (`bash`), `WorkingDirectory` fixé au workspace de l'agent.
  ⚠ Timeout de 30s : tout process longue durée doit être lancé en arrière-plan (`nohup ... &
  disown`). Le scoping au workspace n'est qu'un point de départ, pas une garantie (`cd /` reste
  possible) — cf. ADR 0017.
- **`StrReplaceEditorTool`** — lecture/liste/création/modification de fichiers, **confinée** au
  workspace : tout chemin résolu hors de `WorkspaceRoot` est rejeté sans toucher au disque. C'est
  une garantie effective, contrairement à `CmdRunTool` — cf. ADR 0017. Les workspaces imbriqués
  (ADR 0025) réutilisent cette confinement sans modification : un sous-répertoire d'un workspace
  est nativement accessible à l'agent racine, et un agent enraciné sur le sous-répertoire reste
  confiné à celui-ci.
- **`FinishTool`** (`Finish`) — signal de fin de tour, sans état, partagé par tous les agents.
  Porte `summary`, `outcome` (`done`/`needsmoreinfo`/`blocked`, cf. `AgentTaskOutcome` — ADR 0019),
  `reason`, `questions`, un `verdict` optionnel (`pass`/`fail`/`needmoreinfo`, cf.
  `AgentVerdict` — ADR 0023) pertinent pour l'EnvironmentManager et l'Evaluator, et
  `downstreamInstructions` optionnel (liste de `DownstreamInstruction { Target, Instruction }`,
  cibles `worker`/`evaluator`/`userdoc`, cf. ADR 0025) pertinent pour Alveus-Technical et
  Alveus-Qa pendant la réunion de pré-tâche.
- **`MeetingTool`** (`Raise`/`Vote`) — outil de débat/vote exposé uniquement à
  Alveus-BusinessAnalyst/Alveus-Qa/Alveus-Technical pendant les réunions (ADR 0024). `Raise(topic,
  comment)` ouvre un point de discussion ; `Vote(topic, decision, comment?)` vote `agree`/`disagree`
  (`comment` obligatoire si `disagree`).
- **`ConversationAwareStrReplaceEditorTool`** (`src/Alveus.Web/Tools/`, ADR 0027) — wrapper autour
  de `StrReplaceEditorTool` (ne le modifie pas) : délègue `Execute(...)`, puis si `command !=
  "view"` et qu'une conversation est active (`IConversationContextAccessor.ConversationId`), poste
  un item `file_edit` (`agent`/`command`/`path`). Les 7 agents utilisent cette enveloppe au lieu de
  `StrReplaceEditorTool` directement.

## 5. Activities (`src/Alveus.Web/Activities/`)

- **`AgentPromptActivityBase`** — classe de base commune : restauration/persistance de
  l'`AgentSession` (ADR 0018), boucle de relance + compactage de session (`MaxIterations`, ADR
  0019), parsing de l'appel `Finish`, et routage par verdict (`HandleVerdictAsync`, ADR 0023). Le
  point de variation est `HandleDoneAsync`.
- **`RunAgentPrompt`** — implémente `HandleDoneAsync` en appelant
  `IAgentWorkVerificationService` avant de sortir par `"Done"` (ADR 0020).
- **`RunEnvironmentPrompt`** — implémente `HandleDoneAsync` via `HandleVerdictAsync` avec
  `passOutcome = "Done"` (ADR 0023).
- **`RunEvaluatorPrompt`** — implémente `HandleDoneAsync` via `HandleVerdictAsync` avec
  `passOutcome = "Passed"`, sans étape de vérification déterministe (ADR 0021, ADR 0023).
- **`RunUserDocPrompt`** — implémente `HandleDoneAsync` par une sortie directe `"Done"`, sans
  vérification (ADR 0026), même schéma que `RunEvaluatorPrompt` sans verdict.
- **`MeetingActivityBase`** — base commune aux réunions multi-agents hand-rolled (ADR 0024) :
  orchestration round-robin à 3 sessions (`AgentSession::{agentName}`, même mécanisme que
  `AgentPromptActivityBase`), protocole `Raise`/`Vote`/tally (`MaxRounds = 4`), points
  d'extension abstraits `GetRoleTask`/`SeedOpenTopics`/`OnAgentFinishAsync`/`FinalizeAsync`.
- **`RunPreTaskMeeting`** — réunion de pré-tâche : BA/QA/Tech mettent à jour leur documentation
  (`business-rules/`/`test-plan/`/`tech-docs/`), débattent, et produisent
  `WorkerInstructions`/`EvaluatorInstructions`/`UserDocInstructions` via
  `DownstreamInstruction` (ADR 0024/0025). Sorties `"Done"`/`"NeedsHelp"`.
- **`RunFinalReviewMeeting`** — réunion finale : BA/QA/Tech votent sur le topic implicite
  `"task-fulfilled"` au vu des résumés Worker/EnvironmentManager/Evaluator/UserDoc et de leur
  propre documentation (ADR 0024/0026). Sorties `"OK"`/`"KO"` (+ `BaReport`/`QaReport`/`TechReport`
  si `"KO"`)/`"NeedsHelp"`.
- **`AwaitConversationReply`** (ADR 0027) — `CodeActivity` qui suspend réellement le workflow via un
  bookmark Elsa natif (`context.CreateBookmark(new CreateBookmarkArgs { Callback = OnResumeAsync,
  AutoComplete = false })`) en réponse à une issue `"NeedsHelp"` : poste un item
  `needs_help_question` (label de source + raison + questions), enregistre
  `(workflowInstanceId, bookmarkId)` dans `IConversationStore.SetPendingBookmark`, et ne complète
  pas l'activité. La reprise (`OnResumeAsync`, déclenchée par
  `IWorkflowClient.RunInstanceAsync(... BookmarkId, Input = { "Reply" = text })`) poste un item
  `human_reply`, fixe `Output<string> HumanReply`, et complète par `"Done"`. Le `ConversationId` est
  résolu via `WorkflowExecutionContext.CorrelationId` (seul identifiant stable à travers la
  suspension/reprise — ⚠ les `Input` du workflow ne survivent pas à la reprise, vérifié
  empiriquement). Deux instances dans le graphe : `AwaitPreTaskReply`/`AwaitFinalReviewReply`.

## 6. Services (`src/Alveus.Web/Agents/`)

- **`IAgentSessionCompactionService`** / `SummarizingAgentSessionCompactionService` — compacte la
  session agent quand elle grossit trop (ADR 0019).
- **`IAgentWorkVerificationService`** / `CmdAgentWorkVerificationService` — exécute
  `Agent:VerificationCommand` (ex. `dotnet test`) dans le workspace du Worker ; no-op (valide
  toujours) si non configuré (ADR 0020).
- **`EvaluatorSkills.CopyInto`** — copie les "skills" méthodologiques du repo
  (`Alveus/skils/dotnet-snapshot-testing/`) dans `{workspace-evaluator}/skills/{nom}/` au démarrage
  (ADR 0021).
- **`EvaluatorSkillsContextProvider`** (`: AIContextProvider`) — injecte le contenu des
  `skills/*/SKILL.md` dans le contexte de l'Evaluator à chaque invocation, via
  `ChatClientAgentOptions.AIContextProviders` (ADR 0022).

## 7. Workflow (`src/Alveus.Web/Workflows/`)

- **`AlveusTaskWorkflow : WorkflowBase`** — graphe RunPreTaskMeeting → Worker →
  EnvironmentManager → Evaluator (boucle de correction interne) → UserDoc → RunFinalReviewMeeting
  (boucle externe en cas de KO ; suspension via bookmark en cas de NeedsHelp, ADR 0027), enregistré
  via `elsa.UseWorkflowRuntime(runtime => runtime.AddWorkflow<AlveusTaskWorkflow>())`. Variables :
  `TaskPrompt` (entrée), `EnvUsageInstructions`, `FailureReport`, `LoopCount` (ADR 0023) ;
  `WorkerInstructions`, `EvaluatorInstructions`, `UserDocInstructions` (sorties de
  `RunPreTaskMeeting`, ADR 0025) ; `BaReport`/`QaReport`/`TechReport` (sorties de
  `RunFinalReviewMeeting`, ADR 0026) ; `OuterLoopCount` (ADR 0026) ;
  `PreTaskHumanReply`/`FinalReviewHumanReply` (sorties de `AwaitPreTaskReply`/`AwaitFinalReviewReply`,
  intégrées dans `ExtraContext`) et `PreTaskHelpLoopCount`/`FinalReviewHelpLoopCount` (ADR 0027) ;
  `WorkerEscalationReason`/`WorkerEscalationQuestions`, `EnvManagerEscalationQuestions`,
  `EvaluatorEscalationQuestions`, `UserDocEscalationReason`/`UserDocEscalationQuestions` (sorties
  `Reason`/`Questions` de Worker/EnvironmentManager/Evaluator/UserDoc — `Reason` d'EnvironmentManager
  et Evaluator reste lié à `FailureReport`), `AgentEscalationReport` (sortie de
  `Record*Escalation`, intégrée dans `ExtraContext`) et `AgentEscalationLoopCount` (ADR 0028).
  L'input déclaré `ConversationId` (cf. ADR 0027) n'est lu par aucun code — seul
  `WorkflowExecutionContext.CorrelationId` (positionné par `ConversationEndpoints` à
  `CreateInstanceAsync`) fait foi. ⚠ Toute activité référencée uniquement comme cible d'une
  `Connection` doit figurer dans `Flowchart.Activities`, sinon `InvalidOperationException` au
  runtime (cf. ADR 0023).
- **`LoopIterationGuard`** — `CodeActivity` minimal : incrémente `LoopCount`, sort par
  `"Continue"` tant que `LoopCount <= 5`, sinon `"LimitReached"`. Borne le cycle interne
  Worker/EnvironmentManager/Evaluator (ADR 0023).
- **`OuterLoopIterationGuard`** — même principe, variable `OuterLoopCount`, `MaxIterations = 3`.
  Borne le cycle externe RunFinalReviewMeeting → RunPreTaskMeeting (ADR 0026).
- **`HelpLoopIterationGuard`** (ADR 0027) — même principe, `MaxIterations = 5`. Deux instances
  (`PreTaskHelpLoopGuard`/`FinalReviewHelpLoopGuard`), variables séparées
  (`PreTaskHelpLoopCount`/`FinalReviewHelpLoopCount`) pour ne pas mélanger les deux budgets de
  boucle d'aide humaine.
- **`RecordAgentEscalation`** (ADR 0028) — `CodeActivity` minimal : met en forme
  `Reason`/`Questions` d'un agent (`SourceLabel` + raison + questions formatées) dans
  `AgentEscalationReport`, complète par `"Done"`. Quatre instances
  (`RecordWorkerEscalation`/`RecordEnvironmentManagerEscalation`/`RecordEvaluatorEscalation`/
  `RecordUserDocEscalation`), une par agent source, toutes écrivant dans la même variable
  `AgentEscalationReport`.
- **`AgentEscalationLoopGuard`** (ADR 0028) — même principe que les autres gardes, variable
  `AgentEscalationLoopCount`, `MaxIterations = 3`. Borne le cycle "escalade d'un agent individuel →
  RunPreTaskMeeting", budget séparé de `OuterLoopIterationGuard` (cycle "verdict KO → RunPreTaskMeeting").

## 7bis. API de conversation et observabilité (`src/Alveus.Web/Conversations/`, ADR 0027)

API HTTP self-hosted au format OpenAI Conversations (sous-ensemble, aucun appel à
`api.openai.com`) — point d'entrée, canal d'aide humaine et canal d'observabilité pour
`AlveusTaskWorkflow` :

- **`IConversationStore`/`ConversationStore`** — singleton en mémoire
  (`ConcurrentDictionary<string, ConversationState>`). Items append-only par conversation
  (`ConversationItem`/`ConversationItemKind` : `UserMessage`, `AssistantMessage`,
  `ActivityTransition`, `FileEdit`, `MeetingRound`, `NeedsHelpQuestion`, `HumanReply`), statut
  (`running|awaiting_input|completed|failed`), bookmark en attente
  (`SetPendingBookmark`/`TryResolvePendingBookmark`), abonnés SSE (`SubscribeAsync`).
- **`IConversationContextAccessor`/`ConversationContextAccessor`** — `AsyncLocal<string?>
  ConversationId`, fixé en tête de `AgentPromptActivityBase.ExecuteAsync`/
  `MeetingActivityBase.ExecuteAsync` depuis `WorkflowExecutionContext.CorrelationId`. ✓ traverse les
  `await` d'une même chaîne (`ExecuteAsync → agent.RunAsync → tool call`) ; ne fuite pas entre
  `Task.Run` indépendants (limitation documentée : une conversation à la fois par chaîne
  d'exécution).
- **`ConversationTransitionNotificationHandler`** — poste un item `activity_transition` au
  démarrage et à la fin de chaque activité suivie du graphe (`TrackedActivityIds`).
- **`ConversationEndpoints`** — `POST /v1/conversations`, `GET /v1/conversations/{id}`,
  `GET /v1/conversations/{id}/items`, `POST /v1/conversations/{id}/items`,
  `GET /v1/conversations/{id}/stream` (SSE). Démarre/reprend le workflow via
  `IWorkflowRuntime.CreateClientAsync(...)` → `IWorkflowClient.CreateInstanceAsync`/
  `RunInstanceAsync` (jamais `StartWorkflowAsync`/`ResumeWorkflowAsync`, dépréciées en 3.7.0). La
  reprise après `"NeedsHelp"` résout `(workflowInstanceId, bookmarkId)` via
  `IConversationStore.TryResolvePendingBookmark`, posé par `AwaitConversationReply`.

## 8. Configuration (`appsettings.json`)

Clés requises (toutes lèvent une `InvalidOperationException` au démarrage si absentes, sauf
mention contraire) :

| Clé | Rôle |
|---|---|
| `LlamaCpp:Endpoint`, `LlamaCpp:Model` | Backend LLM (cf. ADR 0006). |
| `Agent:Name` | Nom de l'agent Worker (DI + ciblage par `RunAgentPrompt`). |
| `Agent:WorkspaceRoot` | Racine du workspace Worker/EnvironmentManager (défaut dev : `workspace/`). |
| `Agent:EnvironmentManagerName` | Nom de l'agent EnvironmentManager. |
| `Agent:EvaluatorName` | Nom de l'agent Evaluator. |
| `Agent:EvaluatorWorkspaceRoot` | Racine du workspace isolé de l'Evaluator (défaut dev : `workspace-evaluator/`). |
| `Agent:VerificationCommand` | Optionnelle — commande de vérification du Worker (ADR 0020). Non configurée = no-op. |
| `Agent:UserDocName` | Nom de l'agent Alveus-UserDoc (ADR 0026). |
| `Agent:UserDocWorkspaceRoot` | Racine du workspace Alveus-UserDoc (défaut dev : `workspace-userdoc/`). |
| `Agent:BusinessAnalystName` | Nom de l'agent Alveus-BusinessAnalyst (ADR 0024/0025). |
| `Agent:BusinessAnalystWorkspaceSubdir` | Sous-répertoire de `UserDocWorkspaceRoot` réservé à Alveus-BusinessAnalyst (défaut dev : `business-rules`). |
| `Agent:QaName` | Nom de l'agent Alveus-Qa (ADR 0024/0025). |
| `Agent:QaWorkspaceSubdir` | Sous-répertoire de `EvaluatorWorkspaceRoot` réservé à Alveus-Qa (défaut dev : `test-plan`). |
| `Agent:TechnicalName` | Nom de l'agent Alveus-Technical (ADR 0024/0025). |
| `Agent:TechnicalWorkspaceSubdir` | Sous-répertoire de `WorkspaceRoot` réservé à Alveus-Technical (défaut dev : `tech-docs`). |
| `Elsa:Identity:SigningKey` | Signing key pour `UseIdentity()` (API Elsa). |

## 9. Skills (`skils/`)

`skils/dotnet-snapshot-testing/` — méthodologie de référence pour l'écriture de tests
snapshot/approval (.NET, Verify et/ou Playwright), copiée dans le workspace de l'Evaluator et
injectée dans son contexte (ADR 0021, ADR 0022).

⚠ Le dossier s'appelle `skils/` (sans le second `l`) dans le repo — cf. `EvaluatorSkills.CopyInto`,
qui localise ce dossier par ce nom exact.

## 10. Tests

`tests/Alveus.Web.Tests/` (xUnit). Les tests qui dépendent d'un vrai serveur llama.cpp sont gated
par `IsLlamaCppAvailable` ~. ADR 0021/0023 documentent ~50% de flakiness observée sur l'Evaluator
seul et sur le workflow complet avec un modèle 35B — comportement attendu d'un LLM 7B–35B en
tool-calling multi-tours, pas un bug du harnais. ADR 0024 documente le même risque, accru, pour les
réunions multi-agents (`RunPreTaskMeetingTests`/`RunFinalReviewMeetingTests`, fixture commune
`MeetingFixture`).

Tests ADR 0027 (`Conversations/`, `Activities/AwaitConversationReplyTests.cs`,
`Tools/ConversationAwareStrReplaceEditorToolTests.cs`, `Workflows/HelpLoopIterationGuardTests.cs`) —
sans LLM, registre Elsa minimal local (pas la fixture `AlveusTaskWorkflowFixture`). Les handlers
`internal` de `ConversationEndpoints` sont appelés directement (`InternalsVisibleTo`), sans
`WebApplicationFactory`.

Tests ADR 0028 (`Workflows/RecordAgentEscalationTests.cs`, `Workflows/AgentEscalationLoopGuardTests.cs`)
— sans LLM, même registre Elsa minimal local. Les 3 tests gated existants vérifiant qu'un
`"Blocked"` individuel termine le workflow (`AlveusTaskWorkflow_WorkerBlocked_*`,
`..._EnvironmentManagerBlocked_*`, `..._EvaluatorBlocked_*`) bouclent désormais jusqu'à
`AgentEscalationLoopGuard.MaxIterations + 1` cycles via `RunPreTaskMeeting` avant de réellement se
terminer — ~4× plus lents, flakiness accrue (ADR 0021/0028).

---

## ADR liées

Cette architecture est documentée par les ADR Butlr 0017 à 0026 (copies dans `adr/` de ce dossier
pour référence locale — la source canonique reste `../../docs/adr/`, ne pas modifier l'une sans
l'autre) :

- [0017 — Outils agent Alveus : shell persistant et éditeur de fichiers](adr/0017-alveus-agent-shell-editor-tools.md)
- [0018 — Persistance de la session agent dans l'état de l'activité Elsa](adr/0018-agent-session-persistence-in-activity-state.md)
- [0019 — FinishTool, issues multiples et compactage de session](adr/0019-agent-finish-tool-multi-outcome-compaction.md)
- [0020 — Vérification du travail avant l'issue "Done"](adr/0020-agent-work-verification-before-done.md)
- [0021 — Agent Alveus-Evaluator dans un workspace isolé](adr/0021-evaluator-agent-isolated-workspace.md)
- [0022 — Injection des skills Evaluator via `AIContextProvider`](adr/0022-evaluator-skill-injection-via-ai-context-provider.md)
- [0023 — Workflow Worker → EnvironmentManager → Evaluator avec boucle de correction](adr/0023-worker-environment-evaluator-workflow.md)
- [0024 — Réunions multi-agents hand-rolled dans Elsa (MeetingActivityBase)](adr/0024-hand-rolled-multi-agent-meetings.md)
- [0025 — Workspaces imbriqués et instructions inter-agents via FinishTool](adr/0025-nested-workspaces-and-downstream-instructions.md)
- [0026 — Agent Alveus-UserDoc, réunion finale et boucle de retour sur verdict KO](adr/0026-userdoc-agent-and-final-review-loop.md)
- [0027 — API de conversation (format OpenAI, self-hosted), observabilité et aide humaine via bookmarks Elsa](adr/0027-conversation-api-and-help-bookmarks.md)
- [0028 — Escalade NeedsMoreInfo/Blocked des agents Worker/EnvironmentManager/Evaluator/UserDoc vers la réunion de pré-tâche](adr/0028-agent-escalation-to-pretask-meeting.md)

## Révisions

- 2026-06-14 — création.
- 2026-06-14 — extension pour les réunions multi-agents (pré-tâche et revue finale),
  Alveus-UserDoc, workspaces imbriqués et boucle externe (ADR 0024/0025/0026).
- 2026-06-14 — API de conversation au format OpenAI self-hosted, suspension réelle sur
  "NeedsHelp" via bookmarks Elsa, observabilité (transitions, rounds, édits de fichiers) (ADR 0027).
- 2026-06-14 — escalade des issues "NeedsMoreInfo"/"Blocked" de Worker/EnvironmentManager/
  Evaluator/UserDoc vers RunPreTaskMeeting via Record*Escalation/AgentEscalationLoopGuard (ADR 0028).
