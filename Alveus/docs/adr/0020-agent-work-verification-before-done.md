# ADR 0020 — Vérification du travail avant l'issue "Done" de RunAgentPrompt

## Status

Accepted

## Context

Depuis [ADR 0019](0019-agent-finish-tool-multi-outcome-compaction.md), `RunAgentPrompt` se termine via l'issue `"Done"` dès que l'agent appelle `FinishTool` avec `outcome='done'`. Rien ne garantit cependant que le travail annoncé comme terminé est effectivement correct : ⚠ un modèle (7B comme 35B, cf. ADR 0017) peut appeler `Finish(outcome='done')` alors que le code généré ne compile pas, qu'un test échoue, ou que le fichier attendu n'a pas le contenu demandé — il "croit" avoir fini.

On veut une étape de vérification objective entre "l'agent dit avoir fini" et "l'activité sort par Done", avec la possibilité de renvoyer l'agent corriger le problème plutôt que de terminer sur un résultat invalide ou de marquer l'activité en échec.

## Decision

Un nouveau service injecté `IAgentWorkVerificationService` (`Alveus.Web.Agents`), à une méthode `VerifyAsync(CancellationToken) : ValueTask<AgentWorkVerificationResult>` (`Success: bool`, `Output: string`), est appelé par `RunAgentPrompt` lorsque l'agent appelle `Finish` avec `outcome='done'`, **avant** de sortir par l'issue `"Done"`.

- Si `Success`, l'activité sort par `"Done"` comme avant (ADR 0019).
- Si `!Success`, l'activité **ne se termine pas** : elle relance l'agent avec un prompt composé du préfixe `VerificationFailedPrompt` + `Output`, et reprend la boucle de relance/compactage déjà en place (ADR 0019). Une vérification qui échoue de manière répétée consomme donc des itérations de `MaxIterations`, jusqu'à sortir par `"Blocked"` comme tout autre cas de boucle.

`RunAgentPrompt` reçoit l'implémentation par injection de constructeur, au même titre que `IAgentSessionCompactionService` (ADR 0019) — pas de couplage à une stratégie de vérification particulière.

### Implémentation par défaut : `CmdAgentWorkVerificationService`

Exécute une commande shell configurée (`Agent:VerificationCommand`, ex. `dotnet test`, `npm test`, un script de lint...) dans le `WorkerWorkspaceRoot` de l'agent (ADR 0017), et considère le travail validé si le code de sortie est `0`. La sortie combinée (stdout+stderr) devient `Output`. **Si `Agent:VerificationCommand` n'est pas configuré, la vérification est un no-op qui valide toujours** — comportement par défaut pour les déploiements/tests qui n'ont pas de script de validation.

## Consequences

### Positif
- Un échec de vérification redonne immédiatement à l'agent le détail de ce qui ne va pas (sortie de la commande), sans intervention humaine — exploite le tool-calling déjà en place plutôt que d'ajouter un mécanisme séparé.
- Le no-op par défaut ne change rien au comportement existant tant que `Agent:VerificationCommand` n'est pas configuré : adoption incrémentale, pas de breaking change pour les workflows déjà écrits.
- L'interface ouvre la porte à d'autres stratégies (appel à un agent "reviewer", vérification structurée d'un output JSON, etc.) sans toucher à `RunAgentPrompt`.

### Négatif
- Une commande de vérification longue (ex. suite de tests complète) ajoute sa durée à **chaque** tentative de `Finish(outcome='done')`, y compris quand le travail est effectivement correct — coût systématique, pas seulement en cas d'échec.
- ⚠ Le timeout de `CmdAgentWorkVerificationService` (5 minutes, constante `CommandTimeout`) est une valeur de départ arbitraire, non calibrée sur un script de vérification réel.
- Une commande de vérification qui échoue **systématiquement** (script cassé, dépendance manquante) transforme tout `Finish(outcome='done')` en boucle jusqu'à `MaxIterations` puis `"Blocked"` — pas de distinction entre "le travail de l'agent est mauvais" et "le script de vérification lui-même est cassé". À surveiller si ce cas devient fréquent.
- La sortie brute de la commande (potentiellement volumineuse) est réinjectée telle quelle dans le prompt — pas de troncature au-delà de ce que `IAgentSessionCompactionService` (ADR 0019) gère déjà au niveau de la session globale.

## Alternatives considérées

- **Laisser l'activité sortir par "Done" sans vérification, et faire la vérification dans une activité Elsa séparée du workflow** — écarté pour le cas d'usage immédiat (boucler automatiquement l'agent sur l'échec) : nécessiterait que le workflow modélise explicitement la boucle de correction, alors que `RunAgentPrompt` la gère déjà pour `ReminderPrompt` (ADR 0019). Reste une option pour des vérifications qui nécessitent une intervention humaine plutôt qu'une auto-correction.
- **Vérification synchrone obligatoire (`Agent:VerificationCommand` requis)** — écarté : casserait tous les workflows existants tant qu'aucun script de validation n'est défini, alors que beaucoup de tâches (ex. édition de config, recherche d'information) n'ont pas de "test" naturel.
- **Faire vérifier le travail par un second appel au même agent (LLM-as-judge) plutôt qu'un script** — écarté comme implémentation par défaut : ⚠ un jugement LLM sur sa propre sortie est notoirement peu fiable (biais de confirmation), et ajoute un appel LLM coûteux à chaque `Finish`. Reste une stratégie possible derrière `IAgentWorkVerificationService` si un script de validation n'est pas applicable à une tâche donnée.

## Révisions

- 2026-06-13 — création.
