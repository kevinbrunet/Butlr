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

## PowerShell (scripts/)

- **PS 7+** (`#Requires -Version 7` en tête).
- **Strict mode** : `Set-StrictMode -Version Latest` + `$ErrorActionPreference = 'Stop'`.
- **Nommage Verb-Noun** pour les fichiers de script (`Build-Llama.ps1`, `Get-LlamaModel.ps1`).
- **Dot-source `_Lib.ps1`** en tête, puis `Import-BtlrEnv`.
- **Exit codes natifs** : après toute commande native (`curl.exe`, `cmake`, `git`), check explicite de `$LASTEXITCODE` — `$ErrorActionPreference=Stop` ne l'attrape pas.
- **`curl.exe` explicite** pour les gros downloads (pas l'alias PS de `Invoke-WebRequest`).
- **Chemins** : `Join-Path` plutôt que concaténation de strings. `Test-Path -LiteralPath`.

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
