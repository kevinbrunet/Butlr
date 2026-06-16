# ADR 0031 — Équipes paramétrables via configuration (multi-team, multi-endpoint)

## Status

Accepted

## Context

[ADR 0030](0030-generic-specialist-agents.md) a généralisé le catalogue de spécialistes (clé de
rôle → persona C#), mais tous les agents restent enregistrés sous des clés DI globales
(`"AlveusWorker"`, `"AlveusBusinessAnalyst"`, etc.) et l'application n'expose qu'un seul endpoint
de conversation (`/v1/conversations`). L'équipe est fixée à la compilation.

Besoin : déclarer **plusieurs équipes** dans `appsettings.json` — par exemple une équipe
"frontend" (React, TypeScript) et une équipe "backend" (.NET, EF Core) — chacune avec son propre
endpoint `/teams/{name}/v1/conversations`, son propre `MissionPrompt` contextualisant les agents
sur le projet, et la possibilité d'enrichir les instructions d'un spécialiste avec des notes
spécifiques au projet (`AdditionalInstructions`). La structure du workflow (Elsa flowchart), les
personas de base des agents et le catalogue de rôles restent en C# (code-first).

## Decision

1. **`TeamConfig` / `TeamSpecialistConfig`** (`src/Alveus.Web/Configuration/TeamConfig.cs`,
   nouveau) : POCO de configuration. `TeamConfig` regroupe `Name`, `MissionPrompt`,
   `WorkspaceRoot`, `EvaluatorWorkspaceRoot`, `UserDocWorkspaceRoot`, `VerificationCommand?` et
   `SpecialistRoles` (liste de `TeamSpecialistConfig`). `TeamSpecialistConfig` référence une clé
   du `SpecialistRoleCatalog` (`Key`) et accepte un `AdditionalInstructions?` injecté en suffixe
   des instructions C# du spécialiste.

2. **`appsettings.json`** : section `Teams` (tableau) remplace l'ancienne section `Agent:*`. Chaque
   entrée définit une équipe. La section `LlamaCpp` (`Endpoint`/`Model`) reste partagée.

3. **Clé DI par équipe** : `"{teamName}:{role}"` (ex. `"frontend:Worker"`, `"backend:Qa"`). Chaque
   équipe enregistre ses propres instances de `CmdRunTool`, `StrReplaceEditorTool`, `AIAgent` et
   `IAgentWorkVerificationService` sous sa clé. L'isolation est garantie par le conteneur DI sans
   avoir à lancer plusieurs processus.

4. **`MissionPrompt`** : préfixé (`"{missionPrompt}\n\n---\n"`) sur toutes les instructions
   système des agents de l'équipe. Permet de donner un contexte projet (langage, architecture,
   conventions) sans dupliquer les personas de base.

5. **Endpoints par équipe** : `ConversationEndpoints.MapConversationEndpoints(IEnumerable<string>
   teamNames)` enregistre `/teams/{name}/v1/conversations` (et les routes dérivées) pour chaque
   équipe. `CreateConversationAsync` reçoit `teamName` et l'injecte dans les inputs workflow
   (`["TeamName"] = teamName`).

6. **`AlveusTaskWorkflow`** : lit `Teams` depuis `IConfiguration`, déclare `TeamName` comme input
   de workflow. Toutes les `Input<string> AgentName` et `Input<string> TeamName` des activités sont
   des lambdas `context => AgentKey(context, role)` / `context => context.GetInput<string>("TeamName")`,
   évaluées à l'exécution. `SpecialistRoleKeys` des réunions est aussi calculé dynamiquement.

7. **`RunAgentPrompt`** : l'`IAgentWorkVerificationService` est résolu à l'exécution depuis le
   conteneur DI, d'abord par clé équipe (`GetKeyedService<...>(teamName)`), puis fallback non-keyé
   (`GetService<...>()`). Permet aux tests existants d'enregistrer le service sans clé.

8. **`MeetingActivityBase`** : remplace `QaAgentName`/`TechnicalAgentName` par `TeamName` (input
   `string.Empty` par défaut). Les noms d'agents sont calculés : `string.IsNullOrEmpty(teamName) ?
   "Alveus" + roleKey : $"{teamName}:{roleKey}"` — rétrocompatibilité avec les fixtures de test
   qui ne définissent pas de `TeamName`.

## Consequences

### Positif
- Ajouter une équipe = entrée JSON dans `Teams` + dossier workspace ; zéro changement C#.
- Isolation garantie par DI : chaque équipe a ses propres instances d'outils et son service de
  vérification, pas de partage d'état entre équipes.
- `MissionPrompt` permet de spécialiser un agent sur un projet sans toucher aux personas C#.
- Endpoint séparé par équipe : observabilité, rate-limiting, RBAC futurs sont applicables par
  équipe.

### Négatif
- Un seul processus héberge toutes les équipes : un crash affecte toutes les équipes (isolation
  mémoire et disponibilité moindres qu'une architecture multi-processus).
- ~ Le contenu de `MissionPrompt` n'est pas validé à la compilation ; une faute de frappe ou un
  prompt mal structuré n'est détecté qu'à l'exécution.
- `TeamName` vide reste un chemin de code supporté (fallback legacy) : maintenir cette compat
  augmente la surface à tester.
- `AdditionalInstructions` per-spécialiste est un simple append de texte — pas de templating ni
  de validation de format.

## Alternatives considérées

- **Un processus par équipe** — offre une isolation stricte (mémoire, crash) mais complexifie
  le déploiement (N services, N ports, orchestration) et duplique la configuration LlamaCpp/Elsa
  pour chaque instance. Écarté au stade POC.
- **Workflow généré dynamiquement à partir de la config** — aurait permis des topologies de
  workflow différentes par équipe, mais rend le code non-vérifiable statiquement et introduit une
  complexité élevée dans `Build()`. Écarté : la structure fixe du workflow est un invariant voulu
  (cf. ADR 0023/0026).
- **Agents 100% config-driven (personas en JSON)** — écarté dès ADR 0030 (voir section
  Alternatives de 0030). ADR 0031 ne revient pas sur ce choix : seul `AdditionalInstructions`
  vient du JSON, le reste reste en C#.

## Révisions

- 2026-06-16 — création. Étend [ADR 0030](0030-generic-specialist-agents.md) en ajoutant le
  périmètre "équipe" (team scoping) au-dessus du catalogue de rôles.
