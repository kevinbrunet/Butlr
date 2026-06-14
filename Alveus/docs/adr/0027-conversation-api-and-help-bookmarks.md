# ADR 0027 — API de conversation (format OpenAI, self-hosted), observabilité et aide humaine via bookmarks Elsa

## Status

Accepted

## Context

`AlveusTaskWorkflow` (ADR 0023-0026) orchestre 7 agents LLM via Elsa, mais c'est une boîte noire :
aucune visibilité en cours d'exécution sur les transitions du graphe, les rounds de réunion ou les
fichiers modifiés. De plus, les sorties `"NeedsHelp"` de `RunPreTaskMeeting`/`RunFinalReviewMeeting`
(ADR 0024/0026) étaient terminales — toute ambiguïté met fin au workflow (Blocked) sans recours.

Objectif : wrapper l'exécution dans une **API HTTP self-hosted au format OpenAI Conversations**
(sous-ensemble) — pas d'appel à `api.openai.com`, cohérent avec le principe local-first de
`CLAUDE.md` racine — qui serve de :
1. **point d'entrée** : `TaskPrompt` = premier message `user` de la conversation ;
2. **canal d'aide humaine** : quand une réunion sort en `"NeedsHelp"`, le workflow se met
   **réellement en pause**, poste les questions dans la conversation, et reprend dès qu'une réponse
   `user` arrive ;
3. **canal d'observabilité** : chaque transition d'activité du graphe, chaque édition de fichier
   (`StrReplaceEditorTool`), et chaque round de réunion (1 item/round, BA+QA+Tech) sont postés comme
   items de conversation, consultables via polling ou SSE.

Les sorties `"NeedsMoreInfo"`/`"Blocked"` d'un **agent individuel** (Worker/EnvironmentManager/
Evaluator/UserDoc, ou vote `disagree` isolé dans une réunion) restent **terminales, inchangées** —
hors scope de cet ADR. Seule l'issue **globale** `"NeedsHelp"` d'une réunion déclenche la
suspension.

## Decision

### Suspension via bookmarks Elsa natifs (`IWorkflowRuntime`/`IWorkflowClient`)

✓ vérifié par inspection des symboles (`Elsa.Workflows.Core`/`Elsa.Workflows.Runtime` 3.7.0,
probes de réflexion) :
- `ActivityExecutionContext.CreateBookmark(new CreateBookmarkArgs { Callback, AutoComplete = false,
  ... })` crée un bookmark portant un `Id`, et **ne complète pas l'activité** — l'absence de
  `CompleteActivityWithOutcomesAsync` suspend naturellement l'exécution à ce point (même pattern
  que les activités Elsa "Wait for signal"/"Event"). Le workflow est donc **réellement en pause**
  (aucun thread/Task bloqué côté serveur) tant qu'aucune reprise n'arrive.
- `IWorkflowRuntime.CreateClientAsync(CancellationToken)` (nouvelle instance) /
  `CreateClientAsync(workflowInstanceId, CancellationToken)` (instance existante) →
  `IWorkflowClient` avec `CreateInstanceAsync(CreateWorkflowInstanceRequest {
  WorkflowDefinitionHandle, CorrelationId, Input })` et `RunInstanceAsync(RunWorkflowInstanceRequest
  { BookmarkId, Input })` → `RunWorkflowInstanceResponse { WorkflowInstanceId, Status, SubStatus,
  ... }`.
- `IWorkflowRuntime.StartWorkflowAsync`/`ResumeWorkflowAsync` (API "legacy" envisagée dans le plan
  initial) sont **dépréciées en 3.7.0** — `CreateClientAsync`/`IWorkflowClient` est l'API courante,
  utilisée à la place dans `ConversationEndpoints`.
- `WorkflowDefinitionHandle` est dans `Elsa.Workflows.Models` (✓ confirmé par réflexion, pas
  `Elsa.Workflows.Runtime` comme on aurait pu s'y attendre).

**`AwaitConversationReply`** (`src/Alveus.Web/Activities/AwaitConversationReply.cs`) implémente ce
mécanisme :
- `ExecuteAsync` : poste un item `NeedsHelpQuestion` (label de source + raison + questions de la
  réunion), crée le bookmark (`Callback = OnResumeAsync`, `AutoComplete = false`), enregistre
  `(workflowInstanceId, bookmarkId)` dans `IConversationStore.SetPendingBookmark` (→
  `ConversationState.Status = "awaiting_input"`), et **retourne sans compléter l'activité**.
- `OnResumeAsync` (callback) : lit l'input de reprise (`"Reply"`, via
  `context.TryGetWorkflowInput<string?>("Reply", out var reply)`), poste un item `HumanReply`,
  fixe `Output<string> HumanReply`, et complète l'activité par `"Done"`.

⚠ **Stores en mémoire** : `elsa.UseWorkflowRuntime(...)` enregistre par défaut des
`IWorkflowInstanceStore`/`IBookmarkStore` **en mémoire** en l'absence d'un package de persistance
(`Elsa.Persistence.*`, non référencé dans `Alveus.Web.csproj`). Un redémarrage du process perd donc
les conversations/workflows en cours. Passer à une vraie durabilité plus tard ne demande qu'un
changement de provider de persistance (`Elsa.Persistence.EntityFrameworkCore.*`), sans toucher au
code applicatif (`AwaitConversationReply`, `ConversationEndpoints`).

### `CorrelationId` plutôt qu'un input `ConversationId`

⚠ **Vérifié empiriquement** : les entrées (`Input`) d'un workflow ne sont **pas conservées** à
travers une suspension/reprise — `context.GetInput<string>("ConversationId")` retourne `null` dans
`OnResumeAsync` même si `ConversationId` était fourni à `CreateInstanceAsync`. En revanche,
`WorkflowExecutionContext.CorrelationId` (fixé une fois à `CreateInstanceAsync` via `CorrelationId =
conversationId`, ✓ confirmé stable) **survit** à la suspension/reprise.

Décision : **toute** résolution de `conversationId` dans le code applicatif
(`AwaitConversationReply`, `ConversationTransitionNotificationHandler`,
`AgentPromptActivityBase`/`MeetingActivityBase` via `IConversationContextAccessor`) passe par
`context.WorkflowExecutionContext.CorrelationId`, jamais par un input `"ConversationId"`.
`builder.WithInput("ConversationId", ...)` reste déclaré dans `AlveusTaskWorkflow` pour
documentation/rétro-compatibilité du contrat d'entrée mais n'est lu par aucun code — seul
`CorrelationId` (positionné par `ConversationEndpoints.CreateConversationAsync` à
`CreateInstanceAsync`) fait foi.

### Nouveaux composants (`src/Alveus.Web/Conversations/`)

- **`ConversationItem`/`ConversationItemKind`** — `enum { UserMessage, AssistantMessage,
  ActivityTransition, FileEdit, MeetingRound, NeedsHelpQuestion, HumanReply }` + `record
  ConversationItem(Id, ConversationId, Role, Text, Kind, Metadata, CreatedAt)`.
- **`ConversationState`** — état mutable : `Id`, `Items` (liste append-only, `lock`), `Status`
  (`running|awaiting_input|completed|failed`), `WorkflowInstanceId`, `PendingBookmarkId`, abonnés
  SSE (`ConcurrentDictionary<Guid, Channel<ConversationItem>>`).
- **`IConversationStore`/`ConversationStore`** — singleton, `ConcurrentDictionary<string,
  ConversationState>`. `Create`, `Get`, `AddItem`, `GetItems` (avec `after`/`limit`),
  `SetWorkflowInstanceId`, `SetPendingBookmark(conversationId, bookmarkId)` (→
  `Status = "awaiting_input"`), `TryResolvePendingBookmark(conversationId)` →
  `(workflowInstanceId, bookmarkId)?` + reset `Status = "running"`, `SubscribeAsync`
  (`IAsyncEnumerable`, pour SSE), `Complete`.
- **`IConversationContextAccessor`/`ConversationContextAccessor`** — `AsyncLocal<string?>
  ConversationId` derrière une interface, singleton. ✓ `AsyncLocal` traverse les `await` d'une même
  chaîne d'appel (`ExecuteAsync → agent.RunAsync → tool call`), donc le `ConversationId` fixé en
  tête de `AgentPromptActivityBase.ExecuteAsync`/`MeetingActivityBase.ExecuteAsync` (depuis
  `WorkflowExecutionContext.CorrelationId`) reste disponible pour
  `ConversationAwareStrReplaceEditorTool` sans transiter par les signatures d'outils exposées au
  LLM. Ne fuite pas entre des `Task.Run` indépendants — d'où la limitation documentée "une
  conversation à la fois par chaîne d'exécution" (`ConversationContextAccessorTests`).
- **`ConversationTransitionNotificationHandler`** — `INotificationHandler<ActivityExecuting>` +
  `INotificationHandler<ActivityExecuted>`, enregistré via
  `builder.Services.AddNotificationHandler<...>()` (✓ existe dans `Elsa.Mediator`). Filtre sur un
  `HashSet<string>` (`TrackedActivityIds`) des `Id` d'activités du graphe, poste un item
  `ActivityTransition` (`"[ActivityId] starting"` / `"[ActivityId] <Status>"`) résolu via
  `CorrelationId`. ⚠ `ActivityExecuted` ne porte pas les "outcomes" de l'activité (vérifié par
  inspection des symboles 3.7.0) — seul `ActivityExecutionContext.Status`
  (`Completed`/`Faulted`/`Canceled`) est rapporté ; le détail de la transition (round de réunion,
  édition de fichier) est posté séparément par `MeetingActivityBase`/
  `ConversationAwareStrReplaceEditorTool`.
- **`ConversationEndpoints`** (+ DTOs dans `ConversationDtos.cs`) — minimal API, 5 endpoints (voir
  contrats JSON ci-dessous). `WorkflowDefinitionId` et les 4 handlers principaux sont `internal`
  (`<InternalsVisibleTo Include="Alveus.Web.Tests" />` ajouté à `Alveus.Web.csproj`) pour permettre
  des tests de contrat sans `WebApplicationFactory`.

### `AwaitConversationReply` dans le graphe

Deux instances : `AwaitPreTaskReply`/`AwaitFinalReviewReply` (`SourceLabel` =
`"RunPreTaskMeeting"`/`"RunFinalReviewMeeting"`), chacune suivie d'un
`HelpLoopIterationGuard` dédié (`PreTaskHelpLoopGuard`/`FinalReviewHelpLoopGuard`, variables
`PreTaskHelpLoopCount`/`FinalReviewHelpLoopCount`, `MaxIterations = 5` — généreux car la boucle est
pilotée par un humain, pas par des retries agent) qui renvoie vers la réunion d'origine
(`"Continue"`) ou termine (`"LimitReached"`). `PreTaskHumanReply`/`FinalReviewHumanReply`
(sorties de `AwaitConversationReply.HumanReply`) sont intégrées dans
`RunPreTaskMeeting.ExtraContext`/`RunFinalReviewMeeting.ExtraContext` au même titre que
`BaReport`/`QaReport`/`TechReport` (ADR 0026).

### `ConversationAwareStrReplaceEditorTool`

Wrapper (`src/Alveus.Web/Tools/ConversationAwareStrReplaceEditorTool.cs`), ne modifie pas
`StrReplaceEditorTool` (préserve ses tests existants). Constructeur : `(StrReplaceEditorTool inner,
IConversationContextAccessor, IConversationStore, string agentDisplayName)`. `Execute(...)` (même
signature/`[Description]` que l'outil interne — c'est ce contrat que voit le LLM) délègue à
`inner.Execute`, puis si `command != "view"` et `ConversationContextAccessor.ConversationId` est
défini, poste un item `FileEdit` (métadonnées `agent`/`command`/`path`). Les 7 agents
(`Program.cs`/`AlveusTaskWorkflowFixture.cs`) utilisent cette enveloppe au lieu de
`StrReplaceEditorTool` directement.

### Items `MeetingRound`

`MeetingActivityBase.ExecuteAsync` mémorise `roundStartIndex = transcript.Count` avant
`foreach (role in AgentRoles)` à chaque round ; après la boucle, si `CorrelationId` non vide, poste
un item `MeetingRound` = concaténation de `transcript[roundStartIndex..]`, métadonnées
`{meeting: GetType().Name, round}`. 1 item/round (BA+QA+Tech combinés), pas 1 item/agent.

## Contrats JSON (format OpenAI Conversations, sous-ensemble)

- `POST /v1/conversations` — body `{ "items": [{"role":"user","content":[{"type":"input_text",
  "text":"<TaskPrompt>"}]}] }` → crée la conversation, démarre `AlveusTaskWorkflow` via
  `IWorkflowClient.CreateInstanceAsync` (`CorrelationId = conversationId`, `Input = { TaskPrompt,
  ConversationId }`) puis `RunInstanceAsync`. Réponse : `{ "id", "object":"conversation",
  "created_at", "status", "metadata":{} }` (`status` ∈ `running|awaiting_input|completed|failed`,
  reflète l'état après le premier `RunInstanceAsync` — qui peut suspendre immédiatement sur
  `NeedsHelp`).
- `GET /v1/conversations/{id}` → état courant.
- `GET /v1/conversations/{id}/items?after=&limit=` → liste paginée d'items
  `{ id, object:"conversation.item", type:"message", role, content:[{type:"output_text", text}],
  created_at, metadata:{kind, ...} }`. `metadata.kind` ∈ `user_message | assistant_message |
  activity_transition | file_edit | meeting_round | needs_help_question | human_reply`.
- `POST /v1/conversations/{id}/items` — `{ "items":[{"role":"user","content":[{"type":"input_text",
  "text":"..."}]}] }`. Si `Status == "awaiting_input"` (bookmark en attente via
  `IConversationStore.TryResolvePendingBookmark`), appelle
  `IWorkflowClient.RunInstanceAsync(new RunWorkflowInstanceRequest { BookmarkId, Input = { "Reply" =
  text } })` — reprend via `AwaitConversationReply.OnResumeAsync`. Sinon, item enregistré comme
  trace (`UserMessage`) sans effet sur le workflow. Réponse `202 Accepted`.
- `GET /v1/conversations/{id}/stream` — SSE, un `data: {...}` par item (items existants puis flux
  via `IConversationStore.SubscribeAsync`), même shape que `items[]`.

## Consequences

### Positif
- Le mécanisme de suspension est **natif Elsa** : le workflow n'occupe aucun thread/Task pendant la
  pause, contrairement à un `await`/`TaskCompletionSource` in-process (option écartée).
- `AwaitConversationReply`/`ConversationEndpoints` sont découplés du provider de persistance —
  passer à `Elsa.Persistence.EntityFrameworkCore.*` plus tard ne change rien au code applicatif.
- Observabilité complète (transitions, rounds, édits de fichiers) sans modifier
  `StrReplaceEditorTool` ni `MeetingActivityBase`/`AgentPromptActivityBase` au-delà de
  l'initialisation de `IConversationContextAccessor`.
- Tests de contrat (`ConversationEndpointsTests`, `AwaitConversationReplyTests`) sans LLM et sans
  `WebApplicationFactory`, via `InternalsVisibleTo` + handlers `internal`.

### Négatif
- ⚠ Stores Elsa en mémoire (cf. Context) : un redémarrage perd toutes les conversations/workflows en
  cours — acceptable au stade POC, à documenter pour un déploiement réel.
- `builder.WithInput("ConversationId", ...)` est déclaré mais non lu (seul `CorrelationId` fait
  foi) — source de confusion potentielle si un futur lecteur s'attend à ce que cet input soit
  consommé ; documenté explicitement ici et dans les commentaires XML.
- `ConversationTransitionNotificationHandler` ne capture pas les "outcomes" d'activité (limitation
  Elsa 3.7.0) — un lecteur du flux `activity_transition` ne voit donc pas directement par quel port
  une activité s'est terminée (il faut croiser avec `meeting_round`/`file_edit`/issues finales).
- `HelpLoopIterationGuard.MaxIterations = 5` borne le nombre d'allers-retours humains par réunion ;
  au-delà, le workflow se termine en Blocked malgré la disponibilité d'un humain — accepté comme
  garde-fou contre une boucle de réunion qui ne convergerait jamais.

## Alternatives considérées

- **Suspension in-process via `await`/`TaskCompletionSource`** — proposée dans une première
  itération du plan, **rejetée explicitement** : le workflow occuperait un thread/Task par
  conversation en pause, et la pause ne survivrait pas à un redémarrage même avec une future
  persistance Elsa. Les bookmarks natifs résolvent les deux points.
- **`IWorkflowRuntime.StartWorkflowAsync`/`ResumeWorkflowAsync`** — API présente dans les symboles
  mais dépréciée en 3.7.0 ; `IWorkflowClient` (via `CreateClientAsync`) est l'API courante et c'est
  celle utilisée.
- **Lire `ConversationId` via `context.GetInput<string>("ConversationId")`** — ne survit pas à la
  suspension/reprise (vérifié empiriquement, cf. Decision) ; `WorkflowExecutionContext.CorrelationId`
  retenu à la place pour toute résolution de conversation.
- **1 item de conversation par tour d'agent dans une réunion** (plutôt que par round) — écarté :
  le besoin d'observabilité porte sur la progression de la réunion (round N terminé), pas sur le
  détail tour-par-tour de chaque agent (déjà visible via `activity_transition` +
  `file_edit` si l'agent édite des fichiers).
- **`WebApplicationFactory` pour les tests de contrat** — écarté : nécessiterait de configurer
  `appsettings` complet (`LlamaCpp:Endpoint`, `Agent:*`, requis par `Program.cs` via
  `InvalidOperationException`) pour un test qui ne dépend d'aucun LLM ; `InternalsVisibleTo` +
  handlers `internal` appelés directement est plus léger et suffisant.

## Révisions

- 2026-06-14 — création.
