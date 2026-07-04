# ADR 0019 — FinishTool, issues multiples et compactage de session pour RunAgentPrompt

## Status

Accepted

## Context

`RunAgentPrompt` (cf. [ADR 0018](0018-agent-session-persistence-in-activity-state.md)) envoyait un prompt à l'agent Alveus-Worker (cf. [ADR 0017](0017-alveus-agent-shell-editor-tools.md)) et renvoyait simplement `response.Text` comme résultat d'une `CodeActivity<string>`. Trois limites se posent pour un agent qui exécute des tâches multi-tours avec tool-calling :

- **Pas de signal explicite de fin de tâche.** Rien ne distingue "l'agent a fini son travail", "l'agent a besoin d'une précision pour continuer" et "l'agent est bloqué (dépendance externe, permission, etc.)" — un workflow Elsa qui orchestre plusieurs `RunAgentPrompt` ne peut pas brancher sur ces trois cas.
- **Pas de garde-fou contre les boucles.** ⚠ Un modèle 7B (cf. ADR 0017) peut tourner en rond (répéter des appels d'outils sans jamais conclure) — sans limite, l'activité ne se termine jamais.
- **Pas de gestion de la taille de session.** La session persistée (ADR 0018) grossit à chaque tour ; au-delà d'une certaine taille, elle dégrade la latence et le coût du LLM, et peut dépasser la fenêtre de contexte du modèle.

## Decision

### FinishTool et issues multiples

Un nouveau tool `FinishTool` (`Alveus.Web.Tools.FinishTool`, méthode `Finish`) est ajouté aux tools de l'agent Alveus-Worker. Il prend :

- `summary` (obligatoire) : résumé de ce qui a été fait.
- `outcome` : `done` | `needsmoreinfo` | `blocked`.
- `reason` (optionnel, requis en pratique pour `needsmoreinfo`/`blocked`) : point de blocage ou information manquante.
- `questions` (optionnel, `needsmoreinfo`) : liste de questions à poser pour pouvoir continuer.

Les instructions de l'agent (`Program.cs`) lui imposent d'appeler `Finish` dès qu'il arrête de travailler, quelle qu'en soit la raison.

`RunAgentPrompt` devient une `CodeActivity` non générique (au lieu de `CodeActivity<string>`) avec trois sorties nommées (`[Output]`) : `Summary`, `Reason`, `Questions`. À chaque tour, elle inspecte `AgentResponse.Messages[*].Contents` à la recherche d'un `FunctionCallContent` dont `Name == FinishTool.FunctionName`, parse ses `Arguments` via `FinishCall.FromArguments` (qui gère aussi bien les types CLR natifs que les `JsonElement` produits par la désérialisation JSON des arguments d'outil), et termine via `ActivityExecutionContext.CompleteActivityWithOutcomesAsync` avec l'une de trois issues nommées : `"Done"`, `"NeedsMoreInfo"`, `"Blocked"`.

⚠ Cette approche suppose que le concepteur de workflow Elsa (Flowchart) découvre et expose correctement ces trois issues nommées pour une activité personnalisée utilisant `CompleteActivityWithOutcomesAsync` — comportement distinct du pattern `[Port]` observé sur les propriétés `IActivity` de `SendHttpRequest` (sous-activités composites), et non vérifié à l'exécution dans le designer.

### Boucle de relance et garde-fou anti-boucle

Si l'agent répond sans appeler `Finish`, `RunAgentPrompt` relance l'agent avec un `ReminderPrompt` lui rappelant d'appeler `Finish` selon l'issue appropriée. Une constante `MaxIterations = 6` borne le nombre de tours : si elle est atteinte sans appel à `Finish`, l'activité se termine elle-même via l'issue `"Blocked"`, avec un `Reason` fixe indiquant que la limite a été atteinte.

### Compactage de session

Un service injecté `IAgentSessionCompactionService` (interface à une méthode `CompactIfNeededAsync(AIAgent, AgentSession, CancellationToken) : ValueTask<AgentSession>`) est appelé en début de chaque tour, avant l'appel à `agent.RunAsync`. `RunAgentPrompt` reçoit cette dépendance par injection de constructeur (⚠ suppose que `AddActivity<T>()` d'Elsa instancie les activités via le conteneur DI et résout les dépendances de constructeur — vérifié uniquement par compilation/exécution des tests, pas par un test ciblant spécifiquement ce point d'extension).

L'implémentation par défaut, `SummarizingAgentSessionCompactionService`, sérialise la session (`agent.SerializeSessionAsync`) et compare sa taille UTF-8 à un seuil configurable (`maxSerializedSessionSizeBytes`, défaut 32 000 octets — ⚠ valeur de départ choisie arbitrairement, pas issue d'un benchmark sur la fenêtre de contexte de Qwen 2.5 7B). Si le seuil est dépassé, elle demande à l'agent de résumer la conversation, puis crée une session neuve amorcée avec ce résumé comme contexte. L'interface permet de substituer d'autres stratégies (troncature, résumé partiel, etc.) sans changer `RunAgentPrompt`.

## Consequences

### Positif
- Un workflow Elsa peut brancher explicitement sur les trois issues (`Done`/`NeedsMoreInfo`/`Blocked`) de `RunAgentPrompt`, ce qui permet de construire des chaînes d'agents qui s'arrêtent proprement pour demander une information humaine, plutôt que de produire un texte libre à interpréter.
- Le garde-fou `MaxIterations` rend le pire cas (boucle infinie d'un modèle 7B peu fiable) borné en nombre d'appels LLM, donc en coût et en latence.
- Le compactage de session est un point d'extension isolé (`IAgentSessionCompactionService`), testable indépendamment et remplaçable sans toucher à `RunAgentPrompt`.

### Négatif
- Le compactage par résumé coûte deux appels LLM supplémentaires (résumé + amorçage de la nouvelle session) au moment où il se déclenche — latence perceptible sur ce tour-là.
- `FindFinishCall` dépend de la forme de `AgentResponse.Messages`/`FunctionCallContent` exposée par Microsoft.Agents.AI ; une évolution de cette forme casserait silencieusement la détection (l'activité tomberait dans la boucle de relance puis `Blocked` après `MaxIterations`, sans erreur explicite).
- Le seuil de compactage (32 000 octets) est arbitraire et non calibré sur la fenêtre de contexte réelle du modèle servi (cf. ADR 0006) — à ajuster une fois mesuré.
- ⚠ Les deux points marqués ci-dessus (injection de constructeur dans une activité Elsa, exposition des issues nommées dans le designer Flowchart) restent des hypothèses de conception non vérifiées en conditions réelles.

## Alternatives considérées

- **Garder `CodeActivity<string>` et encoder l'issue dans le texte de retour (ex. préfixe `[BLOCKED]`)** — écarté : fragile (dépend du LLM pour produire un format exact), et ne permet pas de branchement natif dans le designer Flowchart.
- **Détecter la fin de tâche par heuristique sur `response.Text` (absence de nouvel appel d'outil, mots-clés)** — écarté : peu fiable avec un modèle 7B, et ne donne aucun moyen structuré de transmettre `reason`/`questions`.
- **Pas de garde-fou anti-boucle (laisser le workflow Elsa gérer un timeout global)** — écarté : un timeout global ne distingue pas "boucle de l'agent" d'un appel LLM simplement lent, et ne donne pas de `Reason` exploitable.
- **Compactage par troncature de l'historique brut plutôt que par résumé LLM** — écarté comme implémentation par défaut : `AgentSession` est opaque (cf. ADR 0018, `ChatClientAgentSession` n'expose pas de liste de messages publique), donc une troncature nécessiterait de dépendre du format JSON interne. Reste une stratégie possible derrière `IAgentSessionCompactionService` si le besoin se précise.

## Révisions

- 2026-06-13 — création.
