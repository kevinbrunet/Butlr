# ADR 0017 — Outils agent Alveus : shell persistant et éditeur de fichiers, restreints à un workspace

## Status

Accepted

## Context

L'agent `ChatClientAgent` "AlveusWorker" introduit dans `Alveus.Web` (cf. `Program.cs`) n'avait jusqu'ici aucun `AITool`. On veut lui donner deux capacités agentiques classiques :

- exécuter des commandes shell et lire leur sortie ;
- lire/lister/créer/modifier des fichiers (équivalent d'un éditeur de type `str_replace_editor`, pattern répandu dans les agents de code ~).

Le LLM derrière l'agent est servi en local via llama.cpp (Qwen 2.5 7B Instruct, cf. [ADR 0006](0006-llm-serving-llamacpp-10gb.md)). ⚠ Le tool-calling d'un modèle 7B est nettement moins fiable qu'un modèle frontier — un appel de commande hallucinant un argument, ou une boucle de l'agent répétant un appel destructeur, est un scénario réaliste, pas théorique.

Donner un accès shell + édition de fichiers **sans aucune restriction** sur la machine qui héberge `Alveus.Web` revient à donner au LLM les droits du process ASP.NET Core sur tout le système de fichiers accessible. C'est un changement de surface d'attaque structurant, pas un détail d'implémentation — d'où cet ADR.

## Decision

Les deux tools (`CmdRunTool`, `StrReplaceEditorTool`) sont **scoping-restreints à un répertoire de travail unique**, configuré via `Agent:WorkspaceRoot` (résolu en chemin absolu au démarrage, créé s'il n'existe pas) :

- `StrReplaceEditorTool` résout tout `path` (relatif ou absolu) puis vérifie que le chemin final reste sous `WorkspaceRoot` ; sinon il renvoie une erreur à l'agent sans toucher au disque. **Cette vérification est une garantie effective** — chaque opération fichier passe par elle.
- `CmdRunTool` lance un shell persistant (`bash`) avec `WorkingDirectory = WorkspaceRoot`. **Ce n'est pas une garantie** : une commande peut faire `cd /`, utiliser des chemins absolus, ou appeler des binaires qui touchent le reste du système. Le scoping ne fait que fixer le répertoire de départ et limiter le cas d'usage "involontaire" (l'agent qui liste/édite par erreur en dehors du workspace).

Le `WorkspaceRoot` par défaut (dev) est `Alveus/src/Alveus.Web/workspace/`, un répertoire dédié et vide, distinct du reste du repo Butlr.

## Consequences

### Positif
- L'agent peut explorer, créer, modifier des fichiers et exécuter des commandes dans un espace de travail dédié — utile pour des tâches agentiques (génération de code, scripts, tests) sans toucher au repo principal.
- `StrReplaceEditorTool` empêche par construction toute lecture/écriture hors du workspace, même en cas d'hallucination de chemin (`../../etc/passwd` etc.).
- Historique d'édition en mémoire par fichier (`undo_edit`) limite l'impact d'un edit erroné dans la session courante.

### Négatif
- `CmdRunTool` reste un accès shell quasi complet à la machine hôte du process (même utilisateur que `Alveus.Web`). Le scoping du `WorkingDirectory` est une commodité, pas une sandbox. **Ne pas faire tourner `Alveus.Web` avec un utilisateur privilégié.**
- Pas d'allowlist/denylist de commandes au POC : `rm -rf`, `curl`, accès réseau, etc. sont possibles depuis `CmdRunTool` si l'agent les invoque.
- L'historique d'undo est en mémoire (`Dictionary` dans le tool, enregistré en singleton) : perdu au redémarrage du process, et peut grossir sans borne sur une session longue.
- Le shell persistant n'a pas de timeout réseau/process : une commande bloquante (`tail -f`, prompt interactif) bloque la prochaine invocation jusqu'au timeout de lecture configuré.

## Alternatives considérées

- **Aucune restriction (accès complet au système)** — écarté : surface d'attaque maximale pour un gain nul au POC, alors que le besoin réel (générer/tester du code, manipuler des fichiers de travail) tient dans un répertoire dédié.
- **Sandbox process (conteneur, `firejail`, namespaces Linux)** — solution la plus robuste pour `CmdRunTool`, mais ajoute une dépendance d'infra (Docker ou équivalent) non triviale pour un POC, et complique le déploiement décrit dans `scripts/`. À reconsidérer si l'agent doit un jour exécuter du code non `trusted` (ex. généré par un utilisateur externe).
- **Allowlist de commandes pour `CmdRunTool`** — plus sûr mais rigide : une allowlist suffisamment large pour des tâches de dev (compilateurs, gestionnaires de paquets, git…) couvre de fait `rm`, `curl`, etc. Écarté pour le POC, à revisiter si l'usage se précise.
- **Pas de tool shell du tout, uniquement l'éditeur de fichiers** — écarté : une bonne partie de la valeur d'un agent de code vient de pouvoir lancer build/tests, ce qui nécessite un shell.

## Révisions

- 2026-06-13 — création.
