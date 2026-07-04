# ADR 0032 — AskExpertTool : escalade individuelle via consultation directe d'expert

## Status

Accepted

## Context

[ADR 0028](0028-agent-escalation-to-pretask-meeting.md) décrit le mécanisme d'escalade actuel :
quand Worker, EnvironmentManager, Evaluator ou UserDoc appelle `Finish(outcome='needsmoreinfo')`
ou `outcome='blocked'`, le workflow enregistre son rapport, puis `AgentEscalationLoopGuard` relance
`RunPreTaskMeeting` — une réunion complète (tous les spécialistes + Qa + Technical) pour traiter la
question. Ce mécanisme est correct mais lourd : une question ciblée à un BusinessAnalyst déclenche un
tour complet des 4-5 participants de réunion, même si seul le BA est pertinent.

Besoin d'un A/B test : comparer l'escalade via réunion (ADR 0028, `EscalationMode="meeting"`) avec
une escalade directe agent-à-agent via outil (`EscalationMode="tool"`). Les métriques cibles sont le
temps total d'exécution et la qualité de la sortie sur la même liste de tâches.

## Decision

1. **`TeamConfig.EscalationMode`** (`"meeting"` | `"tool"`, défaut `"meeting"`) — propriété de
   configuration par équipe permettant les deux modes en parallèle dans la même instance.

2. **`AskExpertTool`** (`src/Alveus.Web/Tools/AskExpertTool.cs`) — outil exposé au Worker (et
   extensible aux autres agents) quand `EscalationMode = "tool"` :
   - Paramètres LLM : `expertRole` (clé de rôle, ex. `"BusinessAnalyst"`) + `question` (texte libre).
   - Résout l'agent expert via DI keyed `"{teamName}:{expertRole}"`, crée une session fraîche (pas de
     persistance entre consultations), exécute un loop agent (max 4 iterations) jusqu'à l'appel de
     `Finish(outcome='done')` de l'expert.
   - Poste un item `ExpertQuestion` dans la conversation avant l'invocation et un item `ExpertAnswer`
     après — observabilité identique au reste du pipeline.
   - Retourne `finish.Summary` à l'agent appelant comme résultat du tool call.

3. **`ConversationItemKind.ExpertQuestion` / `ExpertAnswer`** — deux nouvelles valeurs d'enum avec
   métadonnée `expert` (nom du rôle) dans les items de conversation.

4. **Endpoints HTTP experts** (`ExpertEndpoints.cs`) — `POST /teams/{name}/experts/{role}/v1/ask`
   expose le même mécanisme depuis l'extérieur (toujours disponible, indépendamment de
   `EscalationMode`). Permet des consultations ponctuelles depuis n'importe quel client OpenAI-compat.

5. **`AskExpertTool` toujours enregistré** (keyed par équipe) dans DI, même en mode `"meeting"` —
   seul son ajout à la liste d'outils du Worker est conditionnel. Cela garantit que les endpoints HTTP
   experts fonctionnent pour toutes les équipes.

6. **Mode `"meeting"` inchangé** — aucune modification au chemin `NeedsMoreInfo`/`Blocked` →
   `Record*Escalation` → `AgentEscalationLoopGuard` → `RunPreTaskMeeting`.

## Consequences

### Positif
- A/B test réel : deux équipes dans `appsettings.json` (`"default"` + `"EscalationMode":"tool"`),
  même tâche, comparaison temps/qualité sans modifier le code entre les runs.
- Escalade ciblée : l'agent interroge l'expert précis dont il a besoin, pas une réunion plénière. La
  latence théorique devrait être moindre (~1 tour agent vs ~N tours pour N participants de réunion).
- L'expert peut consulter sa documentation avant de répondre (il a accès à son workspace via ses
  tools `CmdRunTool`/`StrReplaceEditorTool`).
- Observabilité : les consultations apparaissent dans la conversation (`expert_question` /
  `expert_answer`) et dans le stream SSE.

### Négatif
- L'expert invoqué via `AskExpertTool` a encore accès à `MeetingTool` (`Raise`/`Vote`) dans ses
  outils enregistrés — il pourrait l'appeler hors contexte de réunion. En pratique improbable car son
  prompt de consultation ne mentionne pas ces outils, mais c'est une surface non contrôlée.
- Pas de budget de boucle pour les consultations : si le Worker pose N questions, N loops experts
  s'enchaînent sans garde-fou global. L'`AgentPromptActivityBase.MaxIterations` (6) du Worker limite
  indirectement le total.
- Le mode `"tool"` ne déclenche plus `RunPreTaskMeeting` sur escalade — les spécialistes ne voient
  pas les questions au fil des échanges, seulement dans les items de conversation. Cela peut réduire
  la cohérence documentaire si un expert répond sans avoir coordiné avec les autres.
- ~ La qualité de la réponse expert dépend du LLM 7B en one-shot sans contexte de réunion — peut
  être inférieure à une réunion avec débat.

## Alternatives considérées

- **Appel HTTP entre services** — l'outil appellerait `/teams/{name}/experts/{role}/v1/ask` via
  `HttpClient` (loopback). Plus interopérable (experts externalisables), mais ajoute de la latence
  réseau pour un appel local, et complique les tests. Écarté pour le A/B test ; les endpoints HTTP
  sont créés en parallèle pour l'accès externe sans que le tool en dépende.
- **Session persistante pour les consultations** — l'expert garderait un historique de ses
  consultations. Écarté : surcoût tokens, et une consultation est par nature ponctuelle (pas un
  dialogue continu).
- **Exposer uniquement les spécialistes configurés** (`SpecialistRoles`) en tant qu'experts HTTP —
  écarté : Qa et Technical sont également consultables et leur endpoint HTTP peut être utile. Tous les
  rôles de l'équipe sont exposés ; une clé inconnue lève une 500 (à améliorer en 404 si nécessaire).

## Révisions

- 2026-06-16 — création. Étend [ADR 0028](0028-agent-escalation-to-pretask-meeting.md) avec un
  mode d'escalade alternatif agent-à-agent.
