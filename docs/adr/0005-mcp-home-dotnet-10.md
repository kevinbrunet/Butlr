# ADR 0005 — mcp-home en .NET 10 avec Generic Host et C#

**Date** : 2026-04-23
**Statut** : Accepté

## Contexte

Le serveur MCP `mcp-home` expose les tools de pilotage de la maison. Initialement pensé en Python (par cohérence avec Carlson), mais Kevin code en .NET au quotidien et veut mettre sa productivité à profit. MCP étant un protocole cross-langage via JSON-RPC (transport SSE/HTTP, cf. ADR 0003), le choix de langage de serveur n'a aucun impact sur le client Python.

## Décision

- **Runtime** : .NET 10 LTS. ~ Sortie novembre 2025, 3 ans de support au titre du pattern Microsoft "versions paires = LTS". Cohérent avec un projet perso qu'on veut laisser tourner plusieurs années sans upgrade forcée.
- **Langage** : C#. Plus d'écosystème et de tooling que F#, plus simple à déboguer sur les problèmes de sérialisation MCP.
- **Hosting** : ASP.NET Core + `Microsoft.Extensions.Hosting` (Generic Host + Kestrel). DI, configuration, logging et options pattern fournis nativement. Kestrel sert le transport SSE/HTTP que MCP utilise d'emblée (cf. ADR 0003 — pas de stdio intermédiaire).
- **Namespace racine** : `Butlr.McpHome`.
- **Packaging** : `dotnet publish -c Release --self-contained -p:PublishSingleFile=true` → un binaire autonome déployé en **service long-running** (unité `systemd` sous Linux, Windows Service sous Windows, ou simple `dotnet run` en dev). Plus jamais invoqué en sous-processus par Carlson. Taille estimée ~30–50 Mo ⚠ à mesurer.
- **Dépendances principales** : `ModelContextProtocol` + `ModelContextProtocol.AspNetCore` ~ (SDK C# officiel Anthropic, à pinner à l'install), `MQTTnet` ✓, `YamlDotNet` ✓, `xUnit` ✓.

## Alternatives considérées

- **Console app sans ASP.NET Core** (équivalent d'une appli minimale qui ouvrirait son propre listener HTTP ou utiliserait stdio) — moins de deps, binaire plus léger. Écarté : transport SSE/HTTP retenu (cf. ADR 0003), et Kestrel + Generic Host est l'outil idiomatique en .NET pour ça. On paie ~20 Mo ⚠ mais on gagne DI/config/logging/HTTP stack prête.
- **F# plutôt que C#** — séduisant pour un serveur fonctionnel sans état. Écarté : écosystème plus mince, moins de ressources communautaires quand on rencontre un bug du SDK MCP.
- **.NET 8 LTS** — plus mature que .NET 10 en avril 2026. Écarté : fin de support en ~novembre 2026 ⚠, on se retrouverait rapidement à migrer. .NET 10 donne 3 ans tranquille.
- **Native AOT dès le départ** — binaire encore plus petit, cold-start quasi nul. Écarté pour le MVP : compatibilité AOT à vérifier par dépendance (MCP SDK ~, MQTTnet ✓ récent). À reconsidérer en Phase 2 comme optimisation.

## Conséquences

### Positif
- Productivité Kevin maximisée.
- Type safety complet sur la surface d'outils MCP, source generators pour `System.Text.Json` → sérialisation rapide sans reflection.
- Binaire self-contained déployable sans runtime pré-installé — idéal pour un serveur central léger (mini-PC, Raspberry Pi, conteneur).
- DI propre pour injecter le backend selon la configuration, testabilité accrue.
- ASP.NET Core expose naturellement à la fois la surface MCP (SSE) et l'UI web optionnelle (§7.4 de `architecture.md`) sur le même host.

### Négatif
- Deux toolchains à maintenir dans le repo (cf. ADR 0001).
- SDK MCP C# moins mature que son homologue Python ~ — on risque quelques allers-retours sur les versions, bugs de jeunesse. Mitigation : rester sur une version stable, remonter les bugs upstream.
- Pas de partage de code trivial entre Carlson et mcp-home — acceptable, la frontière est déjà le protocole MCP.

## Révisions
- **2026-04-23** : création.
- **2026-04-23** : mise à jour suite à révision ADR 0003 — hosting ASP.NET Core + Kestrel pour le transport SSE/HTTP d'emblée ; packaging en service long-running et non plus en sous-processus.
