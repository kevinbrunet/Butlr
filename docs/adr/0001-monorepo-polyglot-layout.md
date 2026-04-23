# ADR 0001 — Monorepo polyglotte : Python (Carlson) + .NET (mcp-home)

**Date** : 2026-04-23
**Statut** : Accepté (révisé le 2026-04-23 après choix de .NET pour mcp-home, cf. ADR 0005)

## Contexte

Le projet Butlr contient deux artefacts avec des contraintes et des idiomes différents :

- `carlson/` — pipeline audio temps réel, lourd en deps GPU (Whisper, vLLM, Pipecat). Python est imposé : Pipecat n'a pas d'équivalent .NET mûr.
- `mcp-home/` — serveur MCP stateless, deps légères, potentiellement déployable ailleurs que Carlson. Écrit en .NET 10 pour la productivité de Kevin et le packaging self-contained (cf. ADR 0005).

Il faut choisir l'organisation de repo qui laisse chaque projet dans son idiome natif tout en gardant un versionnement et une doc communs.

## Options considérées

1. **Un seul langage** — forcer Python pour mcp-home ou chercher un équivalent .NET de Pipecat. Écarté : Pipecat sans substitut acceptable ; mcp-home en Python ne joue pas sur les forces de Kevin.
2. **Deux repos distincts** — couplés par des versions. Coût de coordination élevé pour un projet perso avec un seul développeur.
3. **Monorepo polyglotte** — deux projets, chacun avec son build system, versionnement unique.

## Décision

Option 3. Structure :

```
Butlr/
├── docs/                          # architecture + ADRs (partagé)
├── carlson/                       # Python
│   ├── pyproject.toml
│   └── src/carlson/...
└── mcp-home/                      # .NET 10
    ├── mcp-home.sln
    ├── Directory.Build.props
    ├── src/Butlr.McpHome/
    └── tests/Butlr.McpHome.Tests/
```

Chaque sous-projet est autonome : son CI, ses tests, son packaging. La racine n'a pas de build system unifié — pas de Bazel, pas de Nx. Simple, lisible.

## Conséquences

### Positif
- Chaque projet tourne dans son idiome natif, sans compromis.
- PR atomiques possibles quand une évolution touche les deux côtés (ex. ajout d'un tool côté serveur + mise à jour du prompt côté client).
- La doc et les ADRs sont partagés dans `docs/`, cohérence maintenue sans synchro multi-repos.
- Un futur split en deux repos reste accessible via `git subtree split` sans perte d'historique.

### Négatif
- Deux toolchains à installer en dev (CPython 3.11+ avec GPU deps, et .NET 10 SDK). Coût unique, documenté dans les READMEs.
- Un développeur qui ne connaît qu'un des deux mondes est gêné — acceptable ici, Kevin fait les deux.
- Les outils d'analyse statique mono-langage (ruff, dotnet format) vivent dans leur silo — pas de couche unifiée. Acceptable.

## Révisions
- **2026-04-23** : création (tout Python).
- **2026-04-23** : pivot — mcp-home devient .NET 10 (cf. ADR 0005). ADR réécrit en polyglotte.
