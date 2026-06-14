# ADR 0029 — Sandbox effective de CmdRunTool via bubblewrap

## Status

Accepted

## Context

[ADR 0017](0017-alveus-agent-shell-editor-tools.md) (Accepted) introduit `CmdRunTool`, un shell
`bash` persistant dont le `WorkingDirectory` est initialisé à `WorkspaceRoot`, mais documente
explicitement que ce scoping "n'est pas une garantie" : une commande peut faire `cd /`, utiliser
des chemins absolus, ou lancer des process détachés (`nohup ... & disown`) qui survivent à
`Dispose()` (`Kill(entireProcessTree: true)` ne tue pas les descendants désolidarisés).

Cette limite, jusque-là théorique, s'est concrétisée pendant les tests e2e
`AlveusTaskWorkflow` : Alveus-Worker a créé une application ASP.NET dans
`/home/kevin-brunet/TodoApp` — hors de tout `WorkspaceRoot` — et l'a lancée en arrière-plan
(`nohup`). Le process est resté actif (port 5142) après la fin du test, donnant à un agent piloté
par un modèle 7B-35B (~ tool-calling moins fiable qu'un modèle frontier, cf. ADR 0017) un moyen
d'écrire et d'exécuter du code n'importe où sur la machine hôte de `Alveus.Web`.

ADR 0017 listait déjà "Sandbox process (conteneur, `firejail`, namespaces Linux)" comme
alternative rejetée "pour un POC", "à reconsidérer si l'agent doit un jour exécuter du code non
`trusted`". L'incident ci-dessus est cette reconsidération. `bubblewrap` (`bwrap` 0.11.0 ✓,
confirmé installé sur la machine de dev) est l'implémentation choisie : c'est l'outil sandbox
sans privilège utilisé par Flatpak, déjà présent sur le système, sans dépendance d'infra
supplémentaire (pas de Docker/podman).

## Decision

`CmdRunTool` lance désormais son shell persistant via `bwrap` (si disponible dans `PATH`,
détecté une fois via `CmdRunTool.IsBwrapAvailable`) avec la politique suivante :

- `--ro-bind / /` : tout le système de fichiers hôte est monté en lecture seule par défaut.
- `--dev /dev`, `--proc /proc` : nécessaires au fonctionnement normal d'un shell/`dotnet`,
  `--proc` génère un procfs propre au nouveau namespace PID.
- `--tmpfs /tmp` : `/tmp` isolé, sans fuite vers le `/tmp` de l'hôte.
- `--bind <chemin> <chemin>` en lecture-écriture, par-dessus le read-only global, pour :
  `~/.nuget`, `~/.dotnet` (caches/sentinels requis par `dotnet build/run/test`) et
  `WorkspaceRoot` (seul répertoire de travail de l'agent, lecture-écriture complète).
- `--chdir WorkspaceRoot` : équivalent du `WorkingDirectory` précédent, mais à l'intérieur du
  sandbox.
- `--unshare-pid --die-with-parent` : le shell devient l'init d'un namespace PID dédié. Quand
  `Dispose()` tue ce process, le noyau détruit le namespace et **tue tous ses descendants**, y
  compris les process `nohup`/`disown`-és.

Si `bwrap` est absent du `PATH`, `CmdRunTool` retombe sur le comportement antérieur (`bash`
direct, `WorkingDirectory = WorkspaceRoot`, scoping non garanti d'ADR 0017) et logue un
avertissement via `ILogger<CmdRunTool>` — pas de régression dure en l'absence de `bwrap`, mais
perte de la garantie.

## Consequences

### Positif
- Écriture confinée à `WorkspaceRoot` (+ caches `~/.nuget`/`~/.dotnet`) : un chemin absolu hors de
  ces répertoires échoue avec "Read-only file system" au lieu de réussir silencieusement.
  `StrReplaceEditorTool` avait déjà cette garantie (ADR 0017) ; `CmdRunTool` l'obtient désormais
  pour le même périmètre.
- Tout process lancé par l'agent — y compris `nohup ... & disown` — est tué quand `Dispose()`
  détruit le namespace PID. Plus de serveur orphelin survivant à la fin d'un workflow/test.
- `/tmp` isolé par agent : effet de bord possible (~ à confirmer) sur les crashs `MSB4166
  Child node exited prematurely` observés quand plusieurs `dotnet build`/`dotnet test`
  concurrents (process hôte + outils agent) partageaient le même `/tmp`.
- Aucune nouvelle dépendance d'infra : `bwrap` est déjà installé (Flatpak), pas de Docker/podman.

### Négatif
- Risques déjà acceptés par ADR 0017 et **inchangés** par cet ADR : réseau non restreint
  (`curl`, etc. toujours possibles), lecture de tout le système de fichiers hôte (juste plus
  l'écriture).
- Dépendance optionnelle à `bwrap` : si absent (ex. environnement de déploiement minimal), fallback
  silencieux vers le comportement non sandboxé d'ADR 0017 — log d'avertissement seulement, pas
  d'échec dur. À surveiller si `Alveus.Web` est déployé hors d'un poste de dev Linux avec
  bubblewrap.
- `~/.nuget`/`~/.dotnet` restent accessibles en écriture à tous les agents (partagés entre
  workspaces) : un agent pourrait corrompre le cache NuGet partagé. Risque jugé faible (mêmes
  caches que ceux utilisés par l'utilisateur sur sa machine de dev) et hors scope ici.
- Légère complexité supplémentaire dans `CmdRunTool` (construction des arguments `bwrap`,
  détection de disponibilité).

## Alternatives considérées

- **`firejail`** — solution similaire (sandbox sans privilège), mais non installée sur la machine
  de dev (`bwrap` l'est, via Flatpak) ; pas d'avantage clair pour ce besoin. Écarté.
- **Conteneur Docker/podman** — plus robuste (isolation réseau/utilisateur plus poussée) mais
  ajoute une dépendance d'infra lourde, contredisant la motivation initiale d'ADR 0017 de rester
  simple pour un POC. Écarté, peut être reconsidéré si `Alveus.Web` est un jour déployé en
  multi-tenant.
- **Allowlist de commandes pour `CmdRunTool`** — déjà écartée par ADR 0017 (trop rigide pour des
  tâches de dev). `bwrap` rend cette question largement non pertinente : même une commande
  arbitraire ne peut plus écrire hors de `WorkspaceRoot`.
- **Reset du `cd` avant chaque commande (sans sandbox OS)** — envisagé en première analyse, mais
  ne corrige pas le cas réel observé (un seul appel avec un chemin absolu, `dotnet new -o
  /home/.../TodoApp`, suffit) ni les process détachés survivants. Écarté en faveur de `bwrap`.
- **Isoler le réseau (`--unshare-net`)** — non retenu pour cet ADR : casserait `dotnet restore`
  pour des paquets absents du cache `~/.nuget`, et le risque réseau était déjà documenté/accepté
  par ADR 0017. À traiter séparément si besoin.

## Révisions

- 2026-06-14 — création, suite à l'incident `/home/kevin-brunet/TodoApp` (process orphelin créé
  hors workspace par Alveus-Worker).
