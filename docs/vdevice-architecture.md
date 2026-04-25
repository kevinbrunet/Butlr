# Couche VDevice — structuration du projet

> Document de structuration pour la **Phase 2 de mcp-home** : introduction de la couche d'arbitrage Virtual Device (VDevice). Ne remplace pas `architecture.md` (qui décrit le POC vocal global) — le complète sur la dimension domotique.

**Marqueurs de confiance** : ✓ fiable / ~ approximatif / ⚠ extrapolé.

---

## 1. Position dans l'archi globale

`architecture.md` §2 décrit deux artefacts : `carlson/` (pipeline vocal Python) et `mcp-home/` (serveur MCP .NET 10). La couche VDevice est **interne à `mcp-home`** : elle vit derrière la surface MCP exposée à Carlson.

```
                 [Carlson] ──MCP/SSE──┐
                 [Claude Desktop] ────┤
                 [CLI admin] ─────────┴─► [mcp-home]
                                            │
                                            ├─ Surface MCP (tools)
                                            ├─ UI web (dashboard arbitrage)
                                            └─ Couche VDevice ← ce doc
                                                  │
                                                  ├─ Orchestrateur (arbitrage + state JSONL)
                                                  ├─ Config git/yaml (hiérarchie maison/étage/pièce/device)
                                                  ├─ Permissions (yaml en git)
                                                  ├─ Registre devices (yaml en git + état runtime in-memory)
                                                  ├─ ObservabilityService → OTel (out-of-scope)
                                                  └─ Drivers MQTT
                                                        │
                                                        └─► Bus MQTT (Mosquitto)
                                                               │
                                                               ├─ Zigbee2MQTT
                                                               └─ ZWaveJS
```

Carlson (et tout autre client MCP) **ne parle pas directement au modèle VDevice**. Il parle aux tools MCP, qui transforment l'appel en déclaration d'intention vers l'orchestrateur.

**Distinction structurante (cf. ADR 0013)** : Carlson n'est **pas** une app comme les autres. C'est un **agent-utilisateur** — il transmet l'intention de l'utilisateur, il ne porte pas d'intention propre. Quand l'utilisateur dit « allume la lumière », l'auteur de l'intention c'est l'utilisateur ; Carlson est le **canal**.

Concrètement, deux catégories de citoyens cohabitent dans le modèle :

| Catégorie | Exemples | Tag VDevice | `actor_kind` |
|---|---|---|---|
| **Application autonome** | App Cocooning, App ChauffageEco, arbitres custom | `app` | `app` |
| **Agent-utilisateur** | Carlson, UI web, app mobile, interrupteur de pièce niveau 2 | `user_agent` | `user_agent` |
| **Système** | Détecteurs intégrés (CO, fumée), composants signés du système | `system` | `system` |

Le payload d'intention porte donc `actor_kind`, `actor_user_id` (pour les agents-utilisateur), `via_agent_id` (le canal : `carlson`, `ui-web`, `switch:salon`...), `app_id` (pour les apps autonomes), et un `tier_id` optionnel. Le **niveau cible** est résolu automatiquement par admission de tags si non spécifié — cf. [ADR 0014](adr/0014-dynamic-tiers-arbiters.md). Détails dans ADR 0013, ADR 0014 et §4 ci-dessous.

---

## 2. Composants de la couche VDevice

### 2.1 Orchestrator (cœur)

Service .NET hébergé dans `mcp-home`. Responsabilités :

- Maintenir la liste des **VDevices actifs en mémoire** (ADR 0008, persistés via snapshot JSONL — cf. ADR 0016).
- Recevoir les déclarations d'intention (`create / update / renew / release`).
- **Résoudre la commande device** par device : pour chaque changement pertinent, itérer les niveaux configurés dans l'ordre de `rank` croissant, demander à l'arbitre de chaque niveau s'il a une valeur, premier non-null gagne (cf. ADR 0014 — strict winner-takes-all entre niveaux).
- Émettre les commandes vers les drivers.
- Émettre les événements vers les apps (`vdevice.preempted`, etc.).
- Émettre spans / logs / metrics OTel pour toute opération significative (cf. ADR 0016).

C'est le composant **stateful par excellence** du système. Tout le reste gravite autour.

### 2.2 TierRegistry et arbitres (par niveau, configurable)

L'arbitrage n'est plus une fonction monolithique : c'est un **registre de niveaux** chargé depuis la config (cf. ADR 0014, ADR 0015), où chaque niveau porte son propre arbitre, ses tags d'admission, sa politique de durée, son flag `bypass_inertia`.

- **`TierRegistry`** : structure read-only chargée au démarrage depuis `config/home.yaml` (et les overrides de pièce / device). Indexe les niveaux par `id` et par `rank`. Valide la cohérence au load-time : rangs uniques, ids uniques, arbitres référencés existent, tags valides.
- **`IArbiter`** (anciennement `IResolver`) : interface pure
  ```csharp
  interface IArbiter
  {
      Value? Arbitrate(IReadOnlyCollection<VDevice> admitted, RealState? state);
  }
  ```
- Implémentations de référence dans `Butlr.VDevice.Core` :
  - `WinnerTakesAllArbiter` — premier admis gagne (utile pour `safety` ou tout niveau mono-émetteur).
  - `StrictPriorityArbiter` — plus haute priorité gagne, départage timestamp serveur.
  - `UserPriorityThenTimestampArbiter` — départage par priorité utilisateur d'abord, timestamp serveur ensuite (cf. ADR 0008).
  - `WeightedAverageArbiter` — moyenne pondérée des valeurs des VDevices admis (uniquement pour les attributs numériques continus ; refus typé sinon).
- **Arbitres custom** chargés depuis des assemblies .NET listées dans la config (cf. ADR 0014 §"Plugins d'arbitre").

Les arbitres sont des **fonctions pures**. Tests unitaires exhaustifs sans tirer le reste du système.

### 2.3 Permission Registry (en git/yaml)

Lecture sur disque + cache mémoire (cf. ADR 0009 patché par ADR 0014, persistance via ADR 0015) :

- Source de vérité : `~/.butlr/config/permissions/<app_id>__<device_id>.yaml`.
- Octroi à la première déclaration (UI ou vocal) — création + commit git.
- Lecture rapide par couple `(app_id, device_id)`.
- Révocation : update du fichier (`status: revoked`) + commit, propage immédiatement aux VDevices actifs (préemption).
- Les agents-utilisateur ne consultent pas ce registre (ils ne sont pas soumis au modèle de permission — cf. ADR 0013).

### 2.4 Device Registry et hiérarchie maison/étage/pièce/device (en git/yaml)

Source de vérité : arborescence `~/.butlr/config/<étage>/<pièce>/<device>.yaml` (cf. ADR 0015) :

- Devices logiques avec leurs clusters Matter supportés (ADR 0010).
- Mappage vers `external_id` MQTT (Z2M / ZWaveJS).
- Friendly name dérivé du nom de fichier ou explicite.
- Pièce et étage dérivés de la position dans l'arborescence — pas de duplication.
- `tier_overrides` par device : delta vs config héritée de la pièce.
- `fallback` opt-in par device (cf. ADR 0012 §"Pas de fallback automatique" — règle préservée).

L'**état de santé** courant (online/offline/degraded) est **en mémoire**, alimenté par les drivers, **pas persisté** (cf. ADR 0016 §"État réel des devices").

Découverte automatique Z2M : si une annonce MQTT mentionne un `external_id` non listé, le device est créé en yaml dans un répertoire `unsorted/` à la racine ; l'utilisateur le déplace ensuite dans la bonne pièce via l'UI ou un `git mv`.

### 2.5 Drivers

Un driver = adaptateur Matter cluster ↔ MQTT pour une **classe de device** (ADR 0011) :

- `LightDriver` : `OnOff`, `LevelControl`, `ColorControl`.
- `ThermostatDriver` : `Thermostat`.
- `CoverDriver` : `WindowCovering`.
- `SwitchDriver` : `OnOff`.
- `OccupancySensorDriver` : `OccupancySensing`.
- `ContactSensorDriver` : `BooleanState`.
- ... (à étendre).

Chaque driver écoute les topics MQTT pertinents, normalise vers Matter, applique l'inertie, et publie les commandes.

### 2.6 Observabilité (OpenTelemetry)

Toutes les opérations significatives produisent des **spans, logs structurés et metrics OTel** (cf. [ADR 0016](adr/0016-state-snapshot-otel-observability.md)). Le **collector OTel et le backend de stockage** (Loki, Tempo, Grafana, OpenSearch…) sont **out-of-scope `mcp-home`** — c'est l'opérateur qui les configure.

- **Service** `ObservabilityService` : façade interne qui encapsule l'instrumentation. Émet :
  - Span `arbitration` par décision (attributs : `device_id`, `evaluated_tiers`, `winning_tier`, `winning_vdevice_id`, `arbiter`, `inputs_count`).
  - Span enfant `driver.command` à l'envoi MQTT.
  - Logs structurés `vdevice.created/renewed/released/expired/preempted`, `permission.granted/revoked`, `device.command_failed`.
  - Metrics : `butlr.vdevice.active`, `butlr.arbitration.duration`, `butlr.driver.command.total`, `butlr.mqtt.connected`, `butlr.permission.pending`.
- **Endpoint local "recent activity"** (Phase 2.6) : cache mémoire des `~ 1000 dernières` décisions par device, lu par l'UI dashboard. Pas un audit légal, juste une vue rapide pour l'utilisateur sans dépendance backend.

### 2.7 Surface API

L'orchestrateur expose son API via :

- **Tools MCP** pour les agents-utilisateur côté MCP (Carlson, Claude Desktop, CLI admin) et les apps autonomes implémentées comme clients MCP. Le tool reçoit du client une intention et la traduit en `POST /vdevice` interne en remplissant le bon `actor_kind` (cf. ADR 0013).
- **Endpoint HTTP/SSE dédié** pour les apps autonomes tierces qui ne sont pas des clients MCP (apps domotiques externes, scripts perso) et pour l'**UI web/mobile** (agents-utilisateur côté UI).

L'**actor_kind n'est pas choisi par le client** : il est posé par la couche d'entrée selon le canal. Un tool MCP `set_thermostat` exposé à Carlson force `actor_kind=user_agent` ; un endpoint `/vdevice` consommé par App Cocooning force `actor_kind=app`. Cette séparation par canal d'entrée empêche un client malveillant de se déclarer agent-utilisateur (au POC bearer token unique, plus strict avec l'identité signée Phase 2+ — cf. ADR 0013 §"Authentification").

⚠ Le double canal (MCP + HTTP direct) est à confirmer au moment du wiring. Tout faire passer par MCP reste possible si on préfère un seul transport — à arbitrer une fois la première app tierce non-MCP rencontrée.

### 2.8 UI Dashboard (web)

Page web servie par `mcp-home` (cf. `architecture.md` §7.4 — la page existante est étendue) :

- Vue navigable de la **hiérarchie maison/étage/pièce/device** (reflète l'arborescence `config/`).
- Liste des devices, leur état réel, leur santé.
- Pour chaque device : VDevices actifs, niveau qui gagne et pourquoi, "recent activity" depuis le cache mémoire (cf. §2.6).
- Gestion des permissions (octroyer, modifier, révoquer) — édite les fichiers yaml + commit git.
- Configuration des fallbacks par device — édite le yaml device.
- Configuration des **niveaux et de leur arbitre** par device (override) ou par pièce / étage / maison.
- Indicateur "config orpheline" : un override yaml qui ne matche plus rien dans son parent (cf. ADR 0015 §"Héritage par delta").

C'est l'interface qui matérialise la transparence promise par le modèle.

---

## 3. Mapping vers le code

> ⚠ Structure proposée, à valider au démarrage du chantier. Conforme aux conventions de `.claude/rules/code-style.md` (C# / .NET 10, namespace racine `Butlr.McpHome`).

```
mcp-home/
├── src/
│   ├── Butlr.McpHome/                   # binaire principal (existant)
│   │   ├── Program.cs
│   │   ├── McpServer/                   # surface MCP (existant)
│   │   ├── WebUi/                       # dashboard (existant, à étendre)
│   │   └── ...
│   │
│   ├── Butlr.VDevice.Core/              # NOUVEAU — modèle pur, sans I/O
│   │   ├── VDevice.cs                   # entité
│   │   ├── Tier.cs                      # niveau (id, rank, admission, duration_policy, bypass_inertia)
│   │   ├── TierRegistry.cs              # registre read-only chargé au démarrage
│   │   ├── Arbiters/                    # IArbiter + implémentations de référence
│   │   │   ├── IArbiter.cs
│   │   │   ├── WinnerTakesAllArbiter.cs
│   │   │   ├── StrictPriorityArbiter.cs
│   │   │   ├── UserPriorityThenTimestampArbiter.cs
│   │   │   └── WeightedAverageArbiter.cs
│   │   ├── Capabilities/                # types Matter clusters utilisés
│   │   └── Events/                      # records d'événements (preempted, expired...)
│   │
│   ├── Butlr.VDevice.Config/            # NOUVEAU — chargement config git/yaml + héritage delta
│   │   ├── ConfigRepository.cs          # API git (LibGit2Sharp), commit/load
│   │   ├── YamlSerializer.cs            # YamlDotNet wrapper
│   │   ├── DeltaResolver.cs             # héritage maison ⊕ étage ⊕ pièce ⊕ device
│   │   ├── SchemaValidator.cs           # validation au load-time
│   │   └── Models/                      # records typés (HomeConfig, RoomConfig, DeviceConfig…)
│   │
│   ├── Butlr.VDevice.Orchestrator/      # NOUVEAU — service stateful
│   │   ├── OrchestratorService.cs
│   │   ├── PermissionService.cs
│   │   ├── DeviceRegistryService.cs
│   │   ├── ObservabilityService.cs      # façade OpenTelemetry (spans, logs, metrics)
│   │   ├── State/                       # snapshot JSONL (vdevices.jsonl) + rejeu boot
│   │   └── Wiring/                      # extensions DI
│   │
│   ├── Butlr.VDevice.Drivers/           # NOUVEAU — drivers MQTT
│   │   ├── DriverHostService.cs
│   │   ├── MqttClient.cs                # MQTTnet wrapper
│   │   ├── Zigbee2Mqtt/
│   │   │   ├── Z2mDiscovery.cs
│   │   │   ├── LightDriver.cs
│   │   │   ├── ThermostatDriver.cs
│   │   │   ├── CoverDriver.cs
│   │   │   └── ...
│   │   └── ZWaveJs/                     # à initialiser plus tard
│   │
│   └── Butlr.VDevice.Api/               # NOUVEAU — surface HTTP/SSE pour apps tierces
│       ├── VDeviceController.cs
│       └── ...
│
└── tests/
    ├── Butlr.McpHome.Tests/             # existant
    ├── Butlr.VDevice.Core.Tests/        # NOUVEAU — arbitres, lifecycle, admission par tags
    ├── Butlr.VDevice.Orchestrator.Tests/# NOUVEAU — intégration en mémoire
    └── Butlr.VDevice.Drivers.Tests/     # NOUVEAU — avec broker MQTT en testcontainer
```

### Pourquoi ce découpage

- **`Core` sans I/O** : tests unitaires purs sur les arbitres et le lifecycle, sans broker MQTT, sans disque, sans git.
- **`Orchestrator` séparé des drivers** : on peut tester l'orchestrateur avec des fake drivers en mémoire, et tester les drivers avec un fake orchestrateur.
- **`Drivers` regroupé** : un seul service hôte qui spawn les drivers selon la config — pas un binaire par driver.
- **`Api` séparé** : si on décide finalement de tout faire passer par MCP (cf. §2.7), ce projet disparaît proprement.

---

## 4. Modèle d'API (esquisse)

> ⚠ Esquisse pour ancrer la conversation. Spec détaillée à écrire au moment du wiring.

### 4.1 Déclaration d'intention par une app autonome

```
POST /vdevice
{
  "actor_kind": "app",
  "app_id": "cocooning",
  "device_id": "thermostat-salon",
  "tier_id": "apps",                // optionnel : si absent, résolution auto par admission de tags
  "priority": 60,
  "cluster": "Thermostat",
  "attribute": "OccupiedHeatingSetpoint",
  "value": 2100,                    // 21.00 °C en int16, 0.01 °C
  "duration": "persistent"          // ou "ttl_ms": 600000
}
→ 201 { "vdevice_id": "vd-abc123", "tier_id": "apps", "heartbeat_interval_ms": 30000, "grace_ms": 5000 }
```

Notes :
- `tier_id` est **optionnel** ; s'il est absent, l'orchestrateur sélectionne le niveau de plus haut `rank` dont les `tags_required` sont satisfaits par les tags du VDevice (`app` ici, dérivé de `actor_kind`). Cf. ADR 0014 §"Résolution automatique du niveau".
- Une demande `tier_id="user-override"` avec `actor_kind=app` → **refus dur** : le tag `app` ne matche pas les `tags_required: [user_agent]` du niveau. Cf. ADR 0009 patché par ADR 0014.

### 4.2 Renew

```
POST /vdevice/{id}/renew
→ 200 { "next_renew_before": "2026-04-25T10:00:30Z" }
```

### 4.3 Update (renew implicite + nouvelle valeur)

```
PATCH /vdevice/{id}
{ "value": 2050 }
→ 200 { "next_renew_before": "..." }
```

### 4.4 Release

```
DELETE /vdevice/{id}
→ 204
```

### 4.5 Override utilisateur (`user-override`) par un agent-utilisateur

```
POST /vdevice
{
  "actor_kind": "user_agent",
  "actor_user_id": "kevin",         // requis si actor_kind=user_agent
  "via_agent_id": "carlson",        // canal : carlson | ui-web | ui-mobile | switch:salon ...
  "device_id": "thermostat-salon",
  "tier_id": "user-override",       // optionnel : auto-résolu via tag user_agent
  "priority": 100,                  // priorité utilisateur intra-niveau (ADR 0008)
  "cluster": "Thermostat",
  "attribute": "OccupiedHeatingSetpoint",
  "value": 2200,
  "duration_ms": 7200000            // OBLIGATOIRE — duration_policy.ttl_required du niveau (ADR 0014)
}
```

Le niveau `user-override` du preset par défaut a `ttl_required: true` (héritage direct de "niveau 2 = TTL obligatoire" de l'ADR 0008). Une déclaration sans `duration_ms` → refus dur.

La résolution de `duration_ms` (calcul heuristique ou prompt utilisateur) est de la **responsabilité de l'agent**, pas de l'orchestrateur — cf. ADR 0013 §"Résolution de la durée".

### 4.6 Réglage durable par un agent-utilisateur (niveau `apps`, cas particulier)

Un agent-utilisateur peut aussi poser un VDevice sur le niveau `apps` (concurrent des apps autonomes) — par exemple, l'UI web propose à l'utilisateur de **changer durablement** la consigne d'un thermostat (pas un override temporaire, une nouvelle valeur de référence) :

```
POST /vdevice
{
  "actor_kind": "user_agent",
  "actor_user_id": "kevin",
  "via_agent_id": "ui-web",
  "device_id": "thermostat-salon",
  "tier_id": "apps",                // explicite : on veut le niveau persistant, pas user-override
  "priority": 100,                  // priorité utilisateur directe, surclasse les apps tierces
  "cluster": "Thermostat",
  "attribute": "OccupiedHeatingSetpoint",
  "value": 2050,
  "duration": "persistent"          // pas de TTL : c'est une consigne durable
}
```

⚠ Le niveau `apps` du preset par défaut a `tags_required: [app]`. Pour qu'un agent-utilisateur puisse y émettre, soit (a) le niveau accepte aussi `user_agent` dans ses tags d'admission (à configurer), soit (b) le payload porte un tag explicite `app` en plus de `user_agent` (la couche d'entrée en décide selon le canal). Choix de policy à figer au wiring — cf. ADR 0014 §"Tags multiples sur un VDevice".

L'orchestrateur le traite comme un VDevice classique avec un `app_id` synthétique `app:user-direct:kevin` pour la traçabilité. Pas de prompt de permission (cf. ADR 0013).

### 4.7 Stream d'événements (côté app)

Canal SSE `GET /vdevice/events?app_id=cocooning` qui pousse `vdevice.preempted`, `vdevice.expired`, `device.command_failed`, `permission.revoked`.

---

## 5. Configuration hiérarchique git/yaml

Cf. [ADR 0015](adr/0015-config-git-yaml-hierarchy.md) pour la décision et le format complet. Synthèse opérationnelle pour cette couche :

### 5.1 Arborescence

La config vit dans un repo git local : `~/.butlr/config/`. Structure :

```
~/.butlr/config/
├── home.yaml                  # racine : preset de niveaux par défaut, métadonnées maison
├── etage-rdc/
│   ├── etage.yaml             # delta vs home (ex. plus de niveaux ajoutés à l'étage)
│   ├── salon/
│   │   ├── piece.yaml         # delta vs étage
│   │   ├── thermostat-salon.yaml
│   │   ├── plafonnier.yaml
│   │   └── volet-baie.yaml
│   └── cuisine/...
├── etage-1/...
├── unsorted/                  # devices découverts par Z2M, à ranger
│   └── 0x84fd27...yaml
├── apps/                      # déclaration des apps autonomes connues
│   └── cocooning.yaml
├── permissions/               # une permission = un fichier yaml
│   └── cocooning__thermostat-salon.yaml
└── arbiters/                  # plugins .NET référencés
    └── cocooning-energy.yaml
```

### 5.2 Héritage par delta (pas par git-merge sémantique)

Chaque fichier enfant ne contient **que les deltas** vs la config héritée du parent. Au boot, le `DeltaResolver` empile les overlays dans l'ordre maison → étage → pièce → device et produit la config effective par device en mémoire.

Le merge est **explicite et typé** (pas un git-merge ligne à ligne). Conséquence : un `git pull` ramène les fichiers à jour et le boot recalcule tout. Aucun conflit de résolution sémantique au runtime.

⚠ Une clé qui n'existe pas dans la config parente est traitée comme une **config orpheline** : log warning au boot, ignorée à l'arbitrage, exposée dans l'UI dashboard (cf. §2.8). Pas de fail-fast — la config orpheline n'empêche pas le système de tourner.

### 5.3 Reconfiguration = restart (POC)

Au POC, modifier la config (édition manuelle yaml ou via UI qui commit) **n'est pas pris en compte à chaud** : il faut redémarrer `mcp-home`. Au redémarrage :

1. Tous les VDevices actifs sont **purgés** (cf. §6 et ADR 0016 §"Politique de rejeu au boot").
2. Le `TierRegistry` est rechargé.
3. Les apps doivent recréer leurs VDevices (les apps niveau 1 persistant le font de toute façon par renew).

Le hot-reload est listé Phase 3+ — pas un objectif POC (cf. ADR 0014 §"Reconfiguration").

### 5.4 Outils

| Besoin | Outil |
|---|---|
| Manipuler le repo git | ✓ LibGit2Sharp (MIT) |
| Parser/sérialiser yaml | ✓ YamlDotNet (MIT) |
| Valider le schéma au load | json-schema généré depuis les records C# de `Butlr.VDevice.Config/Models/` |

---

## 6. Observabilité (OpenTelemetry)

Cf. [ADR 0016](adr/0016-state-snapshot-otel-observability.md). Synthèse opérationnelle :

### 6.1 État VDevices

- **Source de vérité runtime** : en mémoire (collection thread-safe dans l'orchestrateur).
- **Snapshot** : fichier append-only `~/.butlr/state/vdevices.jsonl`. Chaque mutation (create/renew/update/release/expired/preempted) émet une ligne.
- **Compaction** : périodique (~ 24 h ⚠) via swap atomique vers `vdevices.jsonl.new` puis `rename`. Au boot, on lit le fichier compacté pour reconstruire l'état.
- **Pas de SQLite, pas de DB**. Le state est mutation-heavy mais les requêtes sur l'état sont triviales (par device_id) — l'in-memory + JSONL append est strictement suffisant.

### 6.2 Audit, logs et metrics → OpenTelemetry

Tout ce qui était "audit log" dans l'ancien ADR 0012 passe par OTel. Mapping :

| Événement | Vecteur OTel | Attributs clefs |
|---|---|---|
| Création / mutation VDevice | log structuré niveau Info | `vdevice_id`, `device_id`, `tier_id`, `actor_kind`, `priority` |
| Arbitrage | span `arbitration` | `device_id`, `evaluated_tiers`, `winning_tier`, `winning_vdevice_id`, `arbiter`, `inputs_count`, `duration_ms` |
| Commande device | span enfant `driver.command` | `device_id`, `cluster`, `attribute`, `value`, `mqtt_topic`, `ack_received` |
| Préemption | log structuré niveau Info | `vdevice_id`, `reason` (`priority_changed`, `permission_revoked`, `expired`...) |
| Octroi/révocation permission | log structuré niveau Info | `app_id`, `device_id`, `tier_max`, `priority_max`, `granted_by` |
| Échec commande | log structuré niveau Warning ou Error | `device_id`, `error_kind`, `mqtt_response` |

Metrics standard exposées :

- `butlr.vdevice.active` (gauge, par tier)
- `butlr.arbitration.duration` (histogram, par device)
- `butlr.driver.command.total` (counter, par status `ack`/`nack`/`timeout`)
- `butlr.mqtt.connected` (gauge 0/1 par broker)
- `butlr.permission.pending` (gauge — nombre de prompts en attente)

### 6.3 Stack OTel — out-of-scope mcp-home

`mcp-home` instrumente avec `OpenTelemetry.Api` + `OpenTelemetry.Extensions.Hosting` et exporte en **OTLP/gRPC** vers `localhost:4317` par défaut (configurable). Le **collector OTel et le backend** (Loki, Tempo, Grafana, OpenSearch, Honeycomb…) **ne sont pas livrés par `mcp-home`**. C'est un choix d'opérateur — au POC, un docker-compose externe suffit, voire pas de collector du tout (les exports échouent silencieusement en dev local sans casser `mcp-home`).

### 6.4 "Recent activity" UI sans backend OTel

Pour que l'UI dashboard reste utilisable même sans collector OTel branché, l'`ObservabilityService` maintient un **cache mémoire** des `~ 1 000` dernières décisions par device, exposé via un endpoint local. Pas un audit légal — une vue rapide pour répondre à "pourquoi mon thermostat a chauffé à 22h13 ?" sans dépendance externe.

---

## 7. Choix techniques par défaut

| Domaine | Choix par défaut | Raison | Alt. à évaluer |
|---|---|---|---|
| State VDevices | ✓ in-memory + snapshot JSONL | Mutations-heavy, pas de SQL nécessaire (cf. ADR 0016) | LiteDB si introspection plus riche un jour |
| Config | ✓ git + yaml hiérarchique (cf. ADR 0015) | Versionnable, lisible, diffable, pas de DB pour de la config rare-écriture | SQLite si écriture devient fréquente (improbable) |
| Audit / logs / metrics | ✓ OpenTelemetry (OTLP/gRPC) | Standard, outils gratuits, collector out-of-scope (cf. ADR 0016) | journal fichier rotaté si OTel pose souci en dev |
| Lib git | ✓ LibGit2Sharp (MIT) | Mature, embedded, pas de dépendance à `git` CLI | — |
| Lib yaml | ✓ YamlDotNet (MIT) | Standard .NET, support des comments à l'écriture | — |
| Client MQTT | ✓ MQTTnet (déjà dans `architecture.md` §3.2) | MIT, mature, async | — |
| Broker MQTT | ✓ Mosquitto (déjà mentionné) | Standard de facto, stable | EMQX (cluster-able, surdimensionné ici) |
| Bus radio | ✓ Zigbee2MQTT | ADR 0011 | — |
| Plugins d'arbitre | ✓ Assemblies .NET chargées au démarrage selon `arbiters/*.yaml` | Simplicité, type-safe | WASM plugins (Phase 3+) |

---

## 8. Mode de progression conseillé

Le chantier est volumineux. Pour éviter le big-bang, voici l'ordre recommandé (détails dans `tasks-vdevice-implementation.md`) :

1. **Phase 2.0 — Fondations.** `Butlr.VDevice.Core` complet (entités, `Tier`, `TierRegistry`, `IArbiter` + arbitres de référence, lifecycle), tests unitaires. Aucune intégration. **Critère** : tous les cas limites passent en test unitaire (admission par tags, strict winner-takes-all entre niveaux, départage intra-niveau, lifecycle renew/expire/preempt).
2. **Phase 2.1 — Orchestrateur en mémoire.** `Butlr.VDevice.Orchestrator` sans persistance disque. Surface API minimale (create/renew/release). Fake driver en mémoire. Tests d'intégration. `ObservabilityService` instrumente déjà (spans, logs, metrics) — l'export OTel peut être désactivé en test.
3. **Phase 2.2 — Config git/yaml et state snapshot.** `Butlr.VDevice.Config` (LibGit2Sharp + YamlDotNet, `DeltaResolver`, validation schéma). `State/` JSONL snapshot + rejeu au boot. Plus d'audit SQLite — l'instrumentation OTel posée en 2.1 est la seule trace.
4. **Phase 2.3 — Permissions.** Modèle Android, prompt UI, registry yaml en `config/permissions/`.
5. **Phase 2.4 — Premier driver MQTT.** `LightDriver` Zigbee2MQTT. Bout-en-bout sur de vraies ampoules.
6. **Phase 2.5 — Drivers étendus.** Thermostat, cover, switch, capteurs.
7. **Phase 2.6 — UI dashboard.** Hiérarchie maison/étage/pièce/device, vue "qui propose quoi", gestion permissions, configuration de niveaux par scope, indicateur "config orpheline".
8. **Phase 2.7 — Migration outils MCP.** Les tools MCP existants (`turn_on_light`, etc.) deviennent des clients VDevice (apps internes ou agents-utilisateur selon le canal — cf. §2.7).

À chaque phase, **un état démo-able** : on peut couper la suite à n'importe laquelle des phases sans laisser le système dans un état hybride dégénéré.

---

## 9. Liens

- [ADR 0007 — Virtual Device et arbitrage](adr/0007-virtual-device-arbitration.md) *(superseded by 0014)*
- [ADR 0008 — Lifecycle des VDevices](adr/0008-vdevice-lifecycle-renew.md)
- [ADR 0009 — Permissions](adr/0009-app-device-permissions.md)
- [ADR 0010 — Matter Clusters](adr/0010-matter-clusters-capability-model.md)
- [ADR 0011 — Drivers MQTT](adr/0011-driver-mqtt-adapter.md)
- [ADR 0012 — Persistance, audit, fallback](adr/0012-state-persistence-audit-fallback.md) *(superseded by 0015 + 0016)*
- [ADR 0013 — Agents-utilisateur vs apps autonomes](adr/0013-user-agents-vs-apps.md)
- [ADR 0014 — Niveaux dynamiques et arbitres](adr/0014-dynamic-tiers-arbiters.md)
- [ADR 0015 — Config hiérarchique git/yaml](adr/0015-config-git-yaml-hierarchy.md)
- [ADR 0016 — State snapshot et observabilité OTel](adr/0016-state-snapshot-otel-observability.md)
- [Backlog d'implémentation](tasks-vdevice-implementation.md)
- [Architecture globale Butlr](architecture.md)
