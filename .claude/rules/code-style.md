# Règle : style de code

Conventions à respecter dans tout le repo. Objectif : homogénéité et lisibilité, pas dogmatisme.

## C# (mcp-home)

- **Target** : `net10.0` (LTS). Pas de multi-targeting au POC.
- **Namespace racine** : `Butlr.McpHome`. Un fichier par type public. Nom de fichier = nom de type.
- **Flags csproj obligatoires** :
  ```xml
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  ```
- **Pas de `Newtonsoft.Json`** — `System.Text.Json` uniquement.
- **Async** : suffixe `Async` sur toute méthode async publique. Accepte `CancellationToken` (avec défaut) sur les APIs bloquantes potentielles.
- **Guard clauses** : `ArgumentNullException.ThrowIfNull(...)`, `ArgumentException.ThrowIfNullOrWhiteSpace(...)` plutôt que des `if` manuels.
- **Logs** : `ILogger<T>` injecté, structured logging (`log.LogInformation("Light {Room} -> on", room)`) — **jamais** d'interpolation string dans le message.
- **DI** : enregistrement dans `Program.cs`. Interfaces pour tout ce qui a un état externe ou peut être mocké (backends, clients HTTP).
- **Tests** : xUnit, un fichier de tests par classe testée, nom `<Classe>Tests.cs`. Nom de test en anglais, format `Method_Condition_Expectation`.

## Python (carlson)

- **Python 3.11+** minimum. Union types `X | Y`, `from __future__ import annotations` en tête de chaque module.
- **Type hints** sur tout ce qui est public (fonctions, méthodes, attributs de dataclass).
- **Lint/format** : `ruff` uniquement (config dans `pyproject.toml` — `line-length = 100`).
- **Imports** : triés par ruff (isort-compat). Pas de `from x import *`.
- **Async** : `async def` seulement quand la chaîne d'appel l'exige (Pipecat, httpx, mcp SDK). Helpers purs = sync.
- **Dataclasses vs pydantic** : dataclass frozen pour config interne immutable ; pydantic v2 pour parsing depuis l'extérieur (env, YAML, JSON API).
- **Pas de magic numbers** : toute constante numérique significative a un nom (ex. `FILLER_DELAY_MS`, pas `500`).
- **Tests** : `pytest`. Nom `test_<module>.py`. Pas de classes de test sauf si grouping sémantique fort.

## Bash (scripts/)

- **`set -euo pipefail`** en tête de chaque script.
- **Nommage kebab-case minuscule** (`build-llama.sh`, `get-llama-model.sh`).
- **Source `_lib.sh`** en tête via `. "$(dirname "$(realpath "$0")")/_lib.sh"`, puis `import_btlr_env`.
- **Exit codes** : `set -e` attrape les échecs ; pas de check `$?` manuel sauf dans les blocs `if`.
- **`curl` avec `--fail`** pour les downloads — exit != 0 sur HTTP 4xx/5xx.
- **Chemins** : variables avec `${}`, pas de concaténation nue. `realpath` pour les chemins absolus.
- **Tableaux bash** : `declare -a` ou assignation directe `arr=(...)` ; `"${arr[@]}"` pour l'expansion sûre.

## Markdown (docs/)

- Ton : technique, direct. Pas de marketing ("next-generation", "powerful", "seamless"). Pas d'emoji sauf ADR Status.
- Tableaux > bullets quand il y a 3+ colonnes d'info corrélée.
- Code block avec langage (` ```python`, ` ```csharp`, ` ```powershell`).
- Liens relatifs entre docs du repo (`../adr/0003-...md`), pas d'URL absolue vers GitHub.
- Marqueurs de confiance ✓ ~ ⚠ obligatoires sur les claims externes (cf. `confidence-markers.md`).

## Tout le repo

- **Pas de commentaires évidents** (`// increment i` au-dessus de `i++` = bruit). Commente le *pourquoi*, pas le *quoi*.
- **Pas de TODO sans propriétaire** : `# TODO(Phase 3): câbler le SSE client` OK, `# TODO: fix` pas OK.
- **Nommage** : anglais pour le code, français pour les commentaires longs / doc / ADR. Messages de commit en français OK.
