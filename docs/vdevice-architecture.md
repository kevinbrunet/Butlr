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
                                                  ├─ Orchestrateur (résolution + audit)
                                                  ├─ Registre permissions
                                                  ├─ Registre devices
                                                  └─ Drivers MQTT
                                                        │
                                                        └─► Bus MQTT (Mosquitto)
                                                               │
                                                               ├─ Zigbee2MQTT
                                                               └─ ZWaveJS
```

Carlson (et tout autre client MCP) **ne parle pas directement au modèle VDevice**. Il parle aux tools MCP, qui sont eux-mêmes des **apps internes** (du point de vue VDevice) — ils déclarent des intentions via la même API que les apps tierces.

Cela évite un cas spécial pour Carlson : du point de vue de l'orchestrateur, "Carlson allume la lumière" est une intention applicative comme une autre, soumise aux mêmes règles d'arbitrage.

---

## 2. Composants de la couche VDevice

### 2.1 Orchestrator (cœur)

Service .NET hébergé dans `mcp-home`. Responsabilités :

- Maintenir la liste des **VDevices actifs** (ADR 0008).
- Recevoir les déclarations d'intention (`create / update / renew / release`).
- **Résoudre la commande device** par device, à chaque changement pertinent (nouvelle intention, expiration, changement de priorité).
- Émettre les commandes vers les drivers.
- Émettre les événements vers les apps (`vdevice.preempted`, etc.).
- Persister VDevices, audit, état réel (ADR 0012).

C'est le composant **stateful par excellence** du système. Tout le reste gravite autour.

### 2.2 Resolver (politique d'arbitrage par device)

Composant interne à l'orchestrateur, paramétrable par device :

- **Niveau 3** présent → winner-takes-all absolu, bypass inertie.
- **Niveau 2** présent et non expiré → winner-takes-all, départage timestamp serveur.
- **Niveau 1** : politique configurable :
  - Par défaut : **priorité stricte**.
  - Optionnel : **plugin de résolution** (pondération, courbe, blending) — implémenté en .NET, chargé par config par device.

Le resolver est une fonction pure : `(vdevices[], real_state) → command`. Cette pureté permet des tests unitaires exhaustifs sans tirer le reste du système.

### 2.3 Permission Registry

Stockage SQLite + API :

- Octroi à la première déclaration (cf. ADR 0009).
- Lecture rapide par couple `(app_id, device_id)`.
- Notifications utilisateur (UI / vocal) à l'octroi.
- Révocation propage immédiatement aux VDevices actifs.

### 2.4 Device Registry

Stockage SQLite + API :

- Devices logiques avec leurs clusters Matter supportés (ADR 0010).
- Mappage vers `external_id` MQTT (Z2M / ZWaveJS).
- État de santé courant (online/offline/degraded).
- Friendly name et pièce assignés par l'utilisateur.

Peuplé automatiquement par les drivers via les annonces MQTT.

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

### 2.6 Audit Log

Journal append-only persisté (SQLite ou fichier rotaté, cf. ADR 0012). Toutes les opérations significatives y passent : création/mort de VDevice, commande envoyée, ack reçu, erreur, octroi/révocation de permission.

L'UI dashboard et la vue "qui propose quoi" en sont la lecture.

### 2.7 Surface API

L'orchestrateur expose son API via :

- **Tools MCP** pour les apps internes (Carlson, Claude Desktop, CLI admin) — créer/renew/release VDevice, lire l'état.
- **Endpoint HTTP/SSE dédié** (à finaliser) pour les apps tierces qui ne sont pas des clients MCP — typiquement les apps domotiques externes (apps Home Assistant si on en garde quelques-unes en lecture, scripts de domotique perso).

⚠ Le double canal (MCP + HTTP direct) est à confirmer au moment du wiring. Une option simple est de **tout faire passer par MCP**, en faisant écrire les apps domotiques tierces comme des clients MCP. À arbitrer.

### 2.8 UI Dashboard (web)

Page web servie par `mcp-home` (cf. `architecture.md` §7.4 — la page existante est étendue) :

- Liste des devices, leur état réel, leur santé.
- Pour chaque device : VDevices actifs, niveaux, qui gagne, audit récent.
- Gestion des permissions (octroyer, modifier, révoquer).
- Configuration des fallbacks par device.
- Configuration de la politique de résolution niveau 1 par device.

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
│   │   ├── Levels.cs                    # enum + invariants
│   │   ├── Resolver/                    # IResolver + DefaultPriorityResolver
│   │   ├── Capabilities/                # types Matter clusters utilisés
│   │   └── Events/                      # records d'événements (preempted, expired...)
│   │
│   ├── Butlr.VDevice.Orchestrator/      # NOUVEAU — service stateful
│   │   ├── OrchestratorService.cs
│   │   ├── PermissionService.cs
│   │   ├── DeviceRegistryService.cs
│   │   ├── AuditService.cs
│   │   ├── Persistence/                 # SQLite (Microsoft.Data.Sqlite)
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
    ├── Butlr.VDevice.Core.Tests/        # NOUVEAU — Resolver, lifecycle
    ├── Butlr.VDevice.Orchestrator.Tests/# NOUVEAU — intégration en mémoire
    └── Butlr.VDevice.Drivers.Tests/     # NOUVEAU — avec broker MQTT en testcontainer
```

### Pourquoi ce découpage

- **`Core` sans I/O** : tests unitaires purs sur le resolver et le lifecycle, sans broker MQTT, sans SQLite.
- **`Orchestrator` séparé des drivers** : on peut tester l'orchestrateur avec des fake drivers en mémoire, et tester les drivers avec un fake orchestrateur.
- **`Drivers` regroupé** : un seul service hôte qui spawn les drivers selon la config — pas un binaire par driver.
- **`Api` séparé** : si on décide finalement de tout faire passer par MCP (cf. §2.7), ce projet disparaît proprement.

---

## 4. Modèle d'API (esquisse)

> ⚠ Esquisse pour ancrer la conversation. Spec détaillée à écrire au moment du wiring.

### 4.1 Déclaration d'intention (côté app)

```
POST /vdevice
{
  "app_id": "cocooning",
  "device_id": "thermostat-salon",
  "level": 1,
  "priority": 60,
  "cluster": "Thermostat",
  "attribute": "OccupiedHeatingSetpoint",
  "value": 2100,                    // 21.00 °C en int16, 0.01 °C
  "duration": "persistent"          // ou "ttl_ms": 600000
}
→ 201 { "vdevice_id": "vd-abc123", "heartbeat_interval_ms": 30000, "grace_ms": 5000 }
```

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

### 4.5 Override utilisateur (niveau 2)

```
POST /vdevice
{
  "app_id": "user:kevin",
  "device_id": "thermostat-salon",
  "level": 2,
  "priority": 100,                  // priorité utilisateur
  "cluster": "Thermostat",
  "attribute": "OccupiedHeatingSetpoint",
  "value": 2200,
  "duration_ms": 7200000            // OBLIGATOIRE au niveau 2
}
```

### 4.6 Stream d'événements (côté app)

Canal SSE `GET /vdevice/events?app_id=cocooning` qui pousse `vdevice.preempted`, `vdevice.expired`, `device.command_failed`, `permission.revoked`.

---

## 5. Choix techniques par défaut

| Domaine | Choix par défaut | Raison | Alt. à évaluer |
|---|---|---|---|
| Persistance | ✓ SQLite (`Microsoft.Data.Sqlite`) | Embedded, zéro setup, suffisant ⚠ | LiteDB (simpler), PostgreSQL (Phase 3+) |
| Client MQTT | ✓ MQTTnet (déjà dans `architecture.md` §3.2) | MIT, mature, async | — |
| Broker MQTT | ✓ Mosquitto (déjà mentionné) | Standard de facto, stable | EMQX (cluster-able, surdimensionné ici) |
| Bus radio | ✓ Zigbee2MQTT | ADR 0011 | — |
| Plugins de résolution | Assemblies .NET chargés au démarrage selon config | Simplicité, type-safe | WASM plugins (Phase 3+) |
| Audit storage | SQLite append-only avec rotation | Cohérent avec le reste | Journal fichier rotaté si volumétrie explose |

---

## 6. Mode de progression conseillé

Le chantier est volumineux. Pour éviter le big-bang, voici l'ordre recommandé (détails dans `tasks-vdevice-implementation.md`) :

1. **Phase 2.0 — Fondations.** `Butlr.VDevice.Core` complet (entités, resolver, lifecycle), tests unitaires. Aucune intégration. **Critère** : tous les cas limites de l'audit conversation passent en test unitaire.
2. **Phase 2.1 — Orchestrateur en mémoire.** `Butlr.VDevice.Orchestrator` sans persistance. Surface API minimale (create/renew/release). Fake driver en mémoire. Tests d'intégration.
3. **Phase 2.2 — Persistance.** SQLite branché, rejeu au boot, audit log.
4. **Phase 2.3 — Permissions.** Modèle Android, prompt UI, registry persisté.
5. **Phase 2.4 — Premier driver MQTT.** `LightDriver` Zigbee2MQTT. Bout-en-bout sur de vraies ampoules.
6. **Phase 2.5 — Drivers étendus.** Thermostat, cover, switch, capteurs.
7. **Phase 2.6 — UI dashboard.** Vue "qui propose quoi", gestion permissions.
8. **Phase 2.7 — Migration outils MCP.** Les tools MCP existants (`turn_on_light`, etc.) deviennent des clients VDevice (apps internes).

À chaque phase, **un état démo-able** : on peut couper la suite à n'importe laquelle des phases sans laisser le système dans un état hybride dégénéré.

---

## 7. Liens

- [ADR 0007 — Virtual Device et arbitrage](adr/0007-virtual-device-arbitration.md)
- [ADR 0008 — Lifecycle des VDevices](adr/0008-vdevice-lifecycle-renew.md)
- [ADR 0009 — Permissions](adr/0009-app-device-permissions.md)
- [ADR 0010 — Matter Clusters](adr/0010-matter-clusters-capability-model.md)
- [ADR 0011 — Drivers MQTT](adr/0011-driver-mqtt-adapter.md)
- [ADR 0012 — Persistance, audit, fallback](adr/0012-state-persistence-audit-fallback.md)
- [Backlog d'implémentation](tasks-vdevice-implementation.md)
- [Architecture globale Butlr](architecture.md)
