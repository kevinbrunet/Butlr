# ADR 0030 — Catalogue de rôles "spécialiste" générique et multi-spécialistes

## Status

Accepted

## Context

[ADR 0025](0025-nested-workspaces-and-downstream-instructions.md) câble en dur un unique
participant "spécialiste" — Alveus-BusinessAnalyst — dans `MeetingActivityBase` :
`AgentRoles = ["BusinessAnalyst", "Qa", "Technical"]` (constante statique, 3 participants fixes),
persona/instructions codées dans `Program.cs`, workspace `business-rules/` fixé par
`Agent:BusinessAnalystName`/`Agent:BusinessAnalystWorkspaceSubdir`, et `GetRoleTask("BusinessAnalyst")`
codé en dur dans `RunPreTaskMeeting`/`RunFinalReviewMeeting`.

Besoin : pouvoir mettre à la place du BA un UX/designer ou un autre profil "spécialiste", **et**
pouvoir activer plusieurs spécialistes simultanément (ex. BA + UX) sans toucher au cœur de
`MeetingActivityBase`/`AlveusTaskWorkflow` pour chaque nouveau rôle.

Décision de scope actée avant implémentation (avec Kevin) : pas de "persona 100% config-driven"
(JSON) — les personas multi-paragraphes en français resteraient verbeuses et fragiles en JSON.
Seule la **liste des rôles activés** est pilotée par configuration ; les personas restent en C#.

## Decision

1. **`SpecialistRoleCatalog`** (`src/Alveus.Web/Agents/SpecialistRoleCatalog.cs`, nouveau) :
   dictionnaire statique `IReadOnlyDictionary<string, SpecialistRoleDefinition>` où la clé est un
   "role key" (ex. `"BusinessAnalyst"`, `"UxDesigner"`) et `SpecialistRoleDefinition` regroupe
   `DisplayName` (ex. `"Alveus-BusinessAnalyst"`), `WorkspaceSubdir` (ex. `"business-rules"`),
   `SystemInstructions` (persona pour `ChatClientAgent`), `PreTaskRoleTask` et
   `FinalReviewRoleTask` (textes consignés par `RunPreTaskMeeting`/`RunFinalReviewMeeting`). Le
   catalogue contient `BusinessAnalyst` (migration verbatim du comportement précédent) et
   `UxDesigner` (second exemple, non activé par défaut, prouvant la généricité — workspace
   `ux-notes/`, persona ergonomie/parcours utilisateur).

2. **`Agent:SpecialistRoleKeys`** (config, `appsettings.json`) — tableau de clés du catalogue,
   défaut `["BusinessAnalyst"]` (comportement inchangé par rapport à avant ce changement). Remplace
   `Agent:BusinessAnalystName`/`Agent:BusinessAnalystWorkspaceSubdir`.

3. **`Program.cs`** : boucle sur `Agent:SpecialistRoleKeys` → pour chaque clé, résout
   `SpecialistRoleCatalog.Roles[clé]` (sinon `InvalidOperationException` si clé inconnue),
   enregistre `CmdRunTool`/`StrReplaceEditorTool`/`AIAgent` keyed par `"Alveus" + clé`, workspace =
   sous-dossier `WorkspaceSubdir` de `Agent:UserDocWorkspaceRoot` (même relation d'imbrication
   qu'avant — ADR 0025). Les instructions d'Alveus-UserDoc/Alveus-Technical/Alveus-Qa, qui
   mentionnaient nominativement Alveus-BusinessAnalyst, sont généralisées ("les spécialistes
   configurés"/"les autres participants de la réunion").

4. **`MeetingActivityBase`** : `AgentRoles` (constante statique 3 rôles) et l'`Input<string>
   BusinessAnalystAgentName` sont remplacés par `Input<IReadOnlyList<string>> SpecialistRoleKeys`
   (défaut `["BusinessAnalyst"]`). À l'exécution, `roles = SpecialistRoleKeys.Concat(["Qa",
   "Technical"])` — N participants au lieu de 3 fixes. Toute la logique round-robin/quorum
   (`lastSeenIndex`, tally de vote, `confirmedDone.Count == roles.Count`, etc.) est paramétrée par
   `roles`/`roles.Count` au lieu de `AgentRoles`/`AgentRoles.Length`.

5. **`GetRoleTask`** (`RunPreTaskMeeting`/`RunFinalReviewMeeting`) : conserve les cases explicites
   `"Qa"`/`"Technical"` ; le case `"BusinessAnalyst"` et le `default => throw` sont remplacés par un
   lookup dans `SpecialistRoleCatalog.Roles` (`PreTaskRoleTask`/`FinalReviewRoleTask` selon la
   réunion), `ArgumentOutOfRangeException` si la clé n'est pas dans le catalogue.

6. **`RunFinalReviewMeeting`** : `Output<string?> BaReport` devient `Output<IReadOnlyDictionary<string,
   string>?> SpecialistReports` — un compte-rendu par clé de rôle spécialiste actif (en cas de
   verdict "ko"), construit par filtrage de `finishSummaries` (tout sauf `"Qa"`/`"Technical"`).
   `QaReport`/`TechReport` inchangés.

7. **`AlveusTaskWorkflow`** : injection d'`IConfiguration` (constructeur), lecture unique de
   `Agent:SpecialistRoleKeys` (défaut `["BusinessAnalyst"]`), passée en `Input` aux deux réunions.
   La variable `BaReport` devient `SpecialistReports` (dictionnaire) ; `RunPreTaskMeeting.ExtraContext`
   agrège `SpecialistReports.Values` (au lieu du seul `BaReport`) avec les comptes-rendus
   Qa/Technical/escalades/réponse humaine.

## Consequences

### Positif
- Ajouter un rôle spécialiste = une entrée dans `SpecialistRoleCatalog` + une clé de config —
  aucun changement à `MeetingActivityBase`/`AlveusTaskWorkflow`.
- Plusieurs spécialistes actifs simultanément (ex. `["BusinessAnalyst", "UxDesigner"]`) :
  `MeetingActivityBase` généralisé n'a pas de limite à 3 participants.
- Comportement par défaut (`["BusinessAnalyst"]`) strictement identique à avant ce changement —
  pas de migration nécessaire pour les déploiements existants.

### Négatif
- Chaque spécialiste actif ajoute un tour de débat par round de réunion — coût/latence
  approximativement linéaire en nombre de spécialistes.
- Les personas restent en C# (pas de "persona swap" sans rebuild/redeploy) — décision de scope
  actée avant ce changement, pas une régression.
- `SpecialistRoleCatalog.Roles[clé]` lève `KeyNotFoundException` si `Program.cs`/`MeetingActivityBase`
  reçoivent une clé absente du catalogue alors qu'elle est présente dans
  `Agent:SpecialistRoleKeys` ; `Program.cs` valide explicitement via `InvalidOperationException`
  à l'enregistrement DI (fail-fast au démarrage), mais `MeetingActivityBase`/`GetRoleTask`
  utilisent `TryGetValue` + `ArgumentOutOfRangeException` — cohérence à vérifier si de nouvelles
  clés sont ajoutées à la config sans entrée catalogue correspondante.

## Alternatives considérées

- **Persona 100% config-driven (texte JSON)** — écarté : les personas et `RoleTask` sont des
  paragraphes multi-lignes en français, fragiles à maintenir en JSON (échappement, pas de
  formatage), et ne bénéficient pas de la vérification du compilateur. Le catalogue C# offre la
  généricité recherchée (liste de rôles activables) sans ce coût.
- **Garder Alveus-BusinessAnalyst hardcodé et dupliquer le code pour Alveus-UxDesigner** — écarté :
  viole DRY sur `MeetingActivityBase`/`Program.cs`/`RunPreTaskMeeting`/`RunFinalReviewMeeting`
  (chaque nouveau rôle dupliquerait ~80 lignes de wiring DI et la logique de réunion), et ne permet
  pas le multi-spécialiste sans dupliquer encore `AgentRoles`.

## Révisions

- 2026-06-15 — création.
- 2026-06-16 — [ADR 0031](0031-config-driven-teams.md) étend ce catalogue en ajoutant le
  périmètre "équipe" (multi-team, multi-endpoint, `MissionPrompt`, `AdditionalInstructions`).
