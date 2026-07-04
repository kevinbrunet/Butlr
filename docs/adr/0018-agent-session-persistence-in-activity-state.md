# ADR 0018 — Persistance de la session agent dans l'état de l'activité Elsa

## Status

Accepted

## Context

L'activité Elsa `RunAgentPrompt` (`Alveus/src/Alveus.Web/Activities/RunAgentPrompt.cs`) envoie un prompt à l'agent Alveus-Worker (cf. ADR 0017) et renvoie sa réponse comme résultat de l'activité.

Un workflow Elsa peut être **suspendu** (ex. en attente d'un événement, d'un autre service) puis **repris** plus tard, potentiellement par un autre process. Si `RunAgentPrompt` est réexécutée dans ce contexte (boucle, retry, conversation multi-tours), on veut qu'elle **continue la même conversation** plutôt que d'en démarrer une nouvelle sans contexte — c'est-à-dire réinjecter le prompt précédent et la réponse de l'agent (texte, raisonnement ~, appels d'outils) dans le nouvel appel.

Microsoft.Agents.AI (cf. ADR 0006 pour le LLM sous-jacent) modélise déjà cet historique via `AgentSession` : `AIAgent.RunAsync(prompt, session, ...)` mute la session avec les nouveaux messages, et `AIAgent.SerializeSessionAsync` / `DeserializeSessionAsync` permettent de (dé)sérialiser cette session en `JsonElement`. C'est donc le SDK qui porte la "chaîne de pensée" — pas besoin de la reconstruire à la main.

## Decision

`RunAgentPrompt` sérialise l'`AgentSession` (via `SerializeSessionAsync`) après chaque appel et la stocke avec `ActivityExecutionContext.SetProperty("AgentSession::" + agentName, json)`, où `agentName` est le nom de l'agent ciblé (input `AgentName`, paramétrable — cf. ADR 0017). Au début de l'exécution, elle tente de la restaurer avec `GetProperty` + `DeserializeSessionAsync` ; si absente, elle crée une session neuve (`CreateSessionAsync`). Le préfixage par `agentName` évite que deux instances de `RunAgentPrompt` ciblant des agents différents ne collisionnent sur la même clé d'état.

~ Les propriétés posées via `ActivityExecutionContext.SetProperty`/`GetProperty` font partie de l'état de l'activité qu'Elsa persiste avec l'instance de workflow (mécanisme déjà utilisé par les activités composites/boucles pour survivre à une suspension) — comportement attendu mais pas re-vérifié en conditions réelles avec ce provider de persistance.

## Consequences

### Positif
- Réutilise directement le mécanisme de session du SDK agent : pas de format de "transcript" maison à maintenir, pas de désynchronisation entre ce qui est persisté et ce que l'agent utilise réellement comme contexte.
- La persistance est scopée à l'instance d'activité (un nœud `RunAgentPrompt` dans un workflow = une conversation), ce qui correspond à l'usage attendu (un nœud par étape de dialogue).
- Si l'activité n'est jamais suspendue, le coût additionnel est une (dé)sérialisation JSON par appel — négligeable face à la latence du LLM.

### Négatif
- ⚠ La session sérialisée peut contenir du contenu de conversation (PII potentielle, cf. avertissement de sécurité de `SerializeSessionAsync` dans Microsoft.Agents.AI). Elle hérite donc du niveau de protection du store de persistance Elsa configuré — pas de chiffrement applicatif additionnel au POC.
- Pas de purge/TTL sur cette donnée : une session longue fait grossir l'état persisté de l'instance de workflow sans borne.
- Le couplage entre `RunAgentPrompt` et la forme sérialisée d'`AgentSession` est implicite : une montée de version de Microsoft.Agents.AI qui changerait ce format casserait la restauration de sessions déjà persistées (pas de migration prévue au POC).

## Alternatives considérées

- **Reconstruire un "transcript" maison (liste de messages + raisonnement) et le réinjecter en préfixe du prompt** — écarté : duplique ce que `AgentSession` fait déjà, avec un risque de divergence entre le transcript stocké et l'état réel attendu par le `IChatClient`.
- **Stocker la session dans une variable de workflow (`Variable<string>`) plutôt que `ActivityExecutionContext.SetProperty`** — possible et probablement équivalent en persistance, mais expose la session sérialisée (potentiellement volumineuse et sensible) dans l'inspecteur de variables du workflow. `SetProperty` la garde dans l'état interne de l'activité, plus discret pour un POC.
- **Ne pas persister, recréer une session à chaque exécution** — écarté : revient à ne pas avoir de "réveil" cohérent, contraire à la demande (l'agent perdrait tout contexte si l'activité est reprise après suspension).

## Révisions

- 2026-06-13 — création.
- 2026-06-13 — clé de persistance corrigée (`"AgentSession::" + agentName`) suite au rendu paramétrable du nom de l'agent dans `RunAgentPrompt`.
