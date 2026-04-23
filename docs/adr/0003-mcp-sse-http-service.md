# ADR 0003 — Transport MCP : SSE/HTTP d'emblée, mcp-home en service long-running

**Date** : 2026-04-23
**Statut** : Accepté — supersede la version initiale "stdio au MVP puis SSE en phase satellites"

## Contexte

La topologie cible de Butlr est : **mcp-home sur un serveur central** (pas nécessairement GPU, lightweight), **Carlson sur une machine satellite** (GPU NVIDIA pour LLM + Whisper). Le satellite peut bouger (PC fixe → mini-PC dans le salon plus tard, éventuellement plusieurs satellites dans différentes pièces).

La version initiale de cet ADR proposait stdio au MVP puis SSE/HTTP quand on passerait au multi-machine. Kevin a décidé (2026-04-23) de **court-circuiter l'étape stdio** : la topologie cible est assumée dès le POC, donc le transport aussi.

Les transports disponibles dans les SDK MCP officiels :
- **stdio** : Carlson spawne mcp-home en sous-processus, même machine, zéro réseau. Écarté.
- **SSE** (Server-Sent Events sur HTTP/1.1) : flux unidirectionnel serveur→client + POSTs client→serveur. Transport HTTP historique de MCP. ✓ supporté par les SDK Python et C#.
- **Streamable HTTP** : ~ évolution récente de la spec MCP qui unifie les flux HTTP. À vérifier le support SDK-par-SDK au moment du wiring — si les deux SDKs le supportent, on l'utilise, sinon on reste sur SSE.

## Décision

- **mcp-home** : service long-running .NET 10 basé sur ASP.NET Core (Generic Host + Kestrel). Plus jamais invoqué en sous-processus. Démarrage indépendant de Carlson.
- **Route MCP** : `POST /mcp` pour les requêtes JSON-RPC, `GET /mcp/events` pour le flux SSE (exact sous réserve de la convention retenue par `ModelContextProtocol` csharp-sdk ~ — à aligner à l'install).
- **Port par défaut** : `5090` ~, overridable via `appsettings.json` (`Kestrel:Endpoints`) ou env var.
- **Carlson** : client MCP ouvre la connexion SSE vers `http://<mcp_home_host>:5090/mcp` au démarrage. URL configurable (env var `MCP_HOME_URL`). Reconnexion automatique avec backoff exponentiel sur drop.
- **Auth MVP** : bearer token partagé. Env var `MCP_HOME_TOKEN` côté serveur (génération au premier run, persisté dans un fichier local). Côté Carlson, même token en env var, envoyé en header `Authorization: Bearer <token>`. Rotation manuelle au besoin, pas de rotation automatique au POC.
- **Périmètre réseau POC** : LAN privé (filaire ou Wi-Fi domestique). Pas d'exposition Internet. Pas de TLS au POC — le bearer token suffit sur un LAN de confiance. mTLS / reverse proxy TLS / overlay VPN (Tailscale, WireGuard) = Phase 2 quand on voudra accéder depuis l'extérieur du réseau.
- **Déploiement mcp-home** : unité `systemd` Linux ou Service Windows selon la machine cible. Au POC, un simple `dotnet run` en terminal séparé suffit pour itérer.

## Alternatives considérées

- **stdio au MVP puis SSE plus tard** (version initiale de cet ADR) — zéro config, zéro port, zéro auth au POC. Écarté : la migration stdio → SSE imposerait de refaire le wiring client (changement d'API MCP client côté Carlson), la gestion de cycle de vie (plus de spawn/kill), le packaging (plus de single-file invoqué, maintenant un service), et réécrire cet ADR. Coût de la migration > coût d'assumer le réseau d'emblée.
- **WebSocket** — bidi-réel, latence potentiellement meilleure que SSE sur la boucle serveur→client. Écarté : pas l'usage standard du SDK MCP ⚠, et la spec MCP s'oriente vers Streamable HTTP plutôt que WS.
- **gRPC** — performant, typage fort, mais pas supporté par le SDK MCP officiel ⚠. Écarté.
- **mTLS dès le POC** — cryptographiquement robuste, élimine le risque de token compromis. Écarté : setup PKI (CA, certs client/serveur, rotation) coûteux pour un seul utilisateur sur LAN privé. Bearer token + garder le LAN fermé = ratio sécurité/coût meilleur au POC.
- **Reverse proxy (Caddy, nginx) devant mcp-home dès le POC** pour TLS + auth — surdimensionné en POC monomachine/LAN. À envisager Phase 2 si on sort du LAN.

## Conséquences

### Positif
- **Topologie POC = topologie cible.** Pas de refactor transport à prévoir quand un vrai satellite arrive.
- **mcp-home se teste indépendamment de Carlson.** `curl` sur `/mcp`, Claude Desktop en mode HTTP MCP ~, ou un client MCP CLI suffisent pour valider les tools avant de brancher le pipeline vocal. Boucle de dev mcp-home raccourcie.
- **Redémarrer Carlson ne redémarre pas mcp-home.** L'état en mémoire (lumières allumées/éteintes) persiste au travers des redémarrages du client.
- **Phase 2 satellite** = brancher un Raspberry Pi ou un mini-PC avec Carlson dessus, ajuster `MCP_HOME_URL`, et c'est parti. Zéro changement archi.
- **Multi-client gratuit.** Si un jour on ajoute un agent textuel ou une CLI d'admin à côté de Carlson, ils se branchent sur le même mcp-home sans duplication.

### Négatif
- **Surface d'auth dès le jour 1.** Token partagé à générer, stocker, distribuer aux deux côtés. Simple mais non nul. Oubli d'un token côté client = erreur 401 silencieuse à déboguer.
- **Dépendance réseau dès le dev.** Si mcp-home est down pendant qu'on code Carlson, rien ne tourne. Mitigation : un `FakeMcpClient` ou un mode offline côté Carlson pour pouvoir itérer sur le pipeline vocal sans serveur MCP vivant.
- **Ordre de démarrage à gérer.** Carlson doit retry avec backoff jusqu'à ce que mcp-home soit prêt, et échouer proprement après N tentatives. Non trivial à bien faire (éviter les boucles infinies, logger clairement, distinguer "mcp-home pas démarré" de "mauvais token").
- **Observabilité réseau requise plus tôt.** Logs HTTP côté serveur, timeouts/retries/traces côté client. Acceptable mais c'est du travail qu'on aurait différé avec stdio.
- **Maturité des transports HTTP des SDK MCP** ~ — le MCP spec a bougé récemment (SSE historique → Streamable HTTP). Vérifier au moment du wiring que les versions Python (`mcp`) et C# (`ModelContextProtocol.AspNetCore`) se comprennent sur le transport choisi. Risque de bugs de jeunesse, upstream à surveiller.
- **Plus de "zéro conf local"** — on perd la propriété "je clone le repo, `dotnet run` + `uv run carlson`, ça marche sans config". Il faut exporter `MCP_HOME_URL` et `MCP_HOME_TOKEN` des deux côtés, même en dev mono-machine.

### À cadrer (pas maintenant)
- Politique précise de backoff Carlson (intervalle initial, facteur, max attempts).
- Exposition de metrics mcp-home (endpoint `/metrics` Prometheus-style ? ~).
- Passage à mTLS ou overlay VPN le jour où la topologie sort du LAN privé — ADR dédié à ce moment-là.
- Rotation de token si on veut plus qu'un secret statique.

## Révisions

- **2026-04-23 (création initiale)** : stdio au MVP, SSE/HTTP quand satellites multi-machines.
- **2026-04-23 (révision, supersede)** : SSE/HTTP d'emblée, mcp-home en service long-running. Décidé par Kevin pour aligner la topologie POC sur la topologie cible et éviter une migration transport ultérieure.
