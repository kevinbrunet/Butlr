# Backlog d'implémentation — Couche VDevice (Phase 2 mcp-home)

> Liste de tâches concrètes, ordonnées par phase, donnable à un développeur. Chaque tâche a un **objectif**, un **critère d'acceptation**, et signale ses **prérequis**.
>
> Référence à lire avant : [`vdevice-architecture.md`](vdevice-architecture.md), ADRs 0007 à 0012.

**Marqueurs de confiance** : ✓ fiable / ~ approximatif / ⚠ extrapolé.

---

## Notes préalables

### Conflit de numérotation ADR à résoudre

`docs/architecture.md` §5 mentionne un futur `0007-wake-word-training.md` (entraînement du wake word "Hey Carlson"). Le numéro **0007 est désormais pris** par l'ADR Virtual Device. Action :

- [ ] Mettre à jour la référence dans `architecture.md` §5 vers le prochain numéro libre (0013 au plus tôt, ou bien lui réserver explicitement le numéro qui correspond au moment où on rédigera cet ADR wake word).

### Branche dédiée

Tout le chantier Phase 2 vit sur une branche `feat/vdevice-layer` — il n'altère pas le POC vocal en cours. Merge à la fin de chaque phase, après revue.

### Définition de "fait"

Une tâche est `✓ done` quand :

1. Le code est mergé sur `feat/vdevice-layer`.
2. Les tests sont verts en CI.
3. Le critère d'acceptation est observable (test, démo, ou mesure).
4. La doc qui en dépend est mise à jour (au moins README du projet concerné).

---

## Phase 2.0 — Fondations (Core sans I/O)

**Objectif** : modèle pur testable, sans aucune dépendance d'infra.

### Tâche 2.0.1 — Scaffolder `Butlr.VDevice.Core`

**Prérequis** : aucun.

- [ ] Créer le projet `src/Butlr.VDevice.Core/Butlr.VDevice.Core.csproj` (.NET 10, namespace `Butlr.VDevice.Core`).
- [ ] L'ajouter à `mcp-home/McpHome.sln`.
- [ ] Configurer `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` (cf. `.claude/rules/code-style.md`).
- [ ] Aucune dépendance NuGet à ce stade.

**Acceptation** : le projet build. Pas de code métier encore.

### Tâche 2.0.2 — Modèle Matter cluster minimal

**Prérequis** : 2.0.1.

- [ ] Définir un type `ClusterId`, `AttributeId`, `CommandId` (records) — alignés sur les conventions Matter (ADR 0010).
- [ ] Modéliser au minimum `OnOff`, `LevelControl`, `Thermostat` (juste les attributs nécessaires aux premiers VDevices).
- [ ] Documenter chaque cluster avec lien vers la spec Matter ✓ (csa-iot.org).

**Acceptation** : un test peut créer un attribut typé et accéder à sa plage et son unité.

### Tâche 2.0.3 — Entité VDevice et invariants

**Prérequis** : 2.0.2.

- [ ] Record `VDevice { Id, AppId, DeviceId, Level, Priority, Cluster, Attribute, Value, DurationPolicy, CreatedAt, LastRenewAt }`.
- [ ] Enum `Level { System=3, User=2, Application=1 }`.
- [ ] Type `DurationPolicy` = `Persistent | Ttl(int ms)`.
- [ ] Invariants en constructeur (`ArgumentException.ThrowIfNullOrWhiteSpace`, etc.) :
  - Niveau 2 → `DurationPolicy` doit être `Ttl`. Persistent interdit.
  - Niveau 3 → réservé système (un flag `IsSystem` côté création).
  - Priority dans `[0, 100]` ⚠ (plage à valider en revue d'API).

**Acceptation** : tests unitaires couvrent : création valide, refus niveau 2 sans TTL, refus priority hors plage.

### Tâche 2.0.4 — Resolver par défaut (priorité stricte)

**Prérequis** : 2.0.3.

- [ ] Interface `IResolver { ResolveCommand(IReadOnlyCollection<VDevice> active, RealState? state) → Command? }`.
- [ ] Implémentation `DefaultPriorityResolver` :
  - Niveau 3 actif → sa valeur, drapeau `bypass_inertia=true`.
  - Sinon, niveau 2 actif → sa valeur (départage timestamp serveur si plusieurs).
  - Sinon, niveau 1 plus haute priorité → sa valeur.
  - Sinon → `null` (aucune commande).
- [ ] Tests unitaires couvrant : priorité simple, ex aequo niveau 2, niveau 3 prioritaire absolu, vide.

**Acceptation** : tous les cas de la table de l'ADR 0007 §"État réel commandé" passent en test.

### Tâche 2.0.5 — Lifecycle (renew, expiration, fenêtre de grâce)

**Prérequis** : 2.0.3.

- [ ] Service `VDeviceLifecycle` (pure, en mémoire, sans persistance) :
  - `Create(...) → VDevice` avec horodatage serveur.
  - `Renew(id, now) → bool` — accepte si dans la fenêtre `[lastRenew, lastRenew + heartbeat + grace]`.
  - `IsExpired(vdevice, now)` selon `DurationPolicy` et `LastRenewAt`.
  - `Tick(now) → IReadOnlyCollection<VDeviceId> expired` — renvoie ceux à libérer.
- [ ] Constantes `~ HEARTBEAT_INTERVAL_MS = 30_000`, `GRACE_MS = 5_000` configurable.
- [ ] Tests : renew dans fenêtre, hors fenêtre, niveau 2 expire à `Ttl`, niveau 1 persistent expire au heartbeat manqué.

**Acceptation** : le cas limite "renew une milliseconde après expiration" passe grâce à la grâce.

### Tâche 2.0.6 — Conflits niveau 2 simultanés

**Prérequis** : 2.0.4, 2.0.5.

- [ ] Étendre `DefaultPriorityResolver` pour départager niveau 2 :
  - Priorité utilisateur d'abord.
  - À priorité égale, **timestamp serveur** le plus récent gagne (cf. ADR 0008).
- [ ] Émission d'un événement `VDeviceConflictResolved` pour audit.
- [ ] Tests : deux niveau 2 simultanés (différentes priorités, mêmes priorités).

**Acceptation** : déterminisme observable en test, pas de race.

### Tâche 2.0.7 — Plugin de résolution niveau 1 (interface)

**Prérequis** : 2.0.4.

- [ ] Interface `ILevel1ResolutionPlugin { Resolve(IEnumerable<VDevice> level1, RealState?) → Value? }`.
- [ ] Implémentation de référence `WeightedAverageResolutionPlugin` (pondération par priorité — pour valider que l'extensibilité fonctionne).
- [ ] Le plugin est sélectionnable par device dans la config.
- [ ] Tests : plugin par défaut (priorité stricte), plugin pondération.

**Acceptation** : on peut configurer un device avec un plugin de pondération, et la résolution diffère du défaut.

---

## Phase 2.1 — Orchestrateur en mémoire

**Objectif** : un service qui accepte des intentions et émet des commandes. Pas encore de persistance, pas encore de MQTT.

### Tâche 2.1.1 — Scaffolder `Butlr.VDevice.Orchestrator`

**Prérequis** : Phase 2.0 finalisée.

- [ ] Projet `src/Butlr.VDevice.Orchestrator/` ajouté à la solution.
- [ ] Référence `Butlr.VDevice.Core`.
- [ ] DI standard via `Microsoft.Extensions.DependencyInjection`.

**Acceptation** : projet build, intégrable dans un test host.

### Tâche 2.1.2 — `OrchestratorService` minimal

**Prérequis** : 2.1.1.

- [ ] API in-process : `CreateVDevice`, `RenewVDevice`, `UpdateVDevice`, `ReleaseVDevice`, `GetActiveByDevice`.
- [ ] Maintient l'état en mémoire (dictionnaire concurrent par device).
- [ ] Émet des événements (.NET `IObservable` ou channel) à chaque changement de winner par device.
- [ ] Tick interne périodique pour purger les expirés.

**Acceptation** : test d'intégration en mémoire — créer 3 VDevices sur le même device, observer le winner change selon priorités, expiration purge les fantômes.

### Tâche 2.1.3 — Fake Driver pour tests

**Prérequis** : 2.1.2.

- [ ] Interface `IDriver { ApplyCommand(deviceId, command, ct); ObserveState() }`.
- [ ] `InMemoryDriver` qui logge les commandes reçues et expose un état contrôlable par les tests.
- [ ] L'orchestrateur appelle le driver à chaque changement de winner.

**Acceptation** : test bout-en-bout in-memory : intention → orchestrateur → driver → commande loggée.

### Tâche 2.1.4 — Préemption et événements vers apps

**Prérequis** : 2.1.2.

- [ ] Channel d'événements par `app_id` : `VDevicePreempted`, `VDeviceExpired`.
- [ ] Émission quand une app perd le contrôle suite à : nouveau winner avec priorité plus haute, expiration, révocation de permission, changement de priorité par utilisateur.
- [ ] Tests.

**Acceptation** : un consommateur peut s'abonner et observer les préemptions.

### Tâche 2.1.5 — Surface MCP minimale (apps internes)

**Prérequis** : 2.1.2.

- [ ] Tools MCP exposés par `Butlr.McpHome` : `vdevice_create`, `vdevice_renew`, `vdevice_update`, `vdevice_release`, `vdevice_list_active`.
- [ ] Wiring DI dans `Program.cs`.
- [ ] Test manuel via Claude Desktop ou client MCP CLI : créer un VDevice depuis un client externe.

**Acceptation** : Claude Desktop peut créer/lister/libérer un VDevice via MCP.

---

## Phase 2.2 — Persistance

**Objectif** : survie au redémarrage avec rejeu correct.

### Tâche 2.2.1 — SQLite et schéma

**Prérequis** : Phase 2.1 finalisée.

- [ ] Dépendance `Microsoft.Data.Sqlite` ✓.
- [ ] Schéma : tables `vdevices_active`, `permissions`, `devices`, `audit_log`.
- [ ] Script de migration v1 (création initiale).
- [ ] Path DB par config (`appsettings.json`, défaut `~/.butlr/state.db`).

**Acceptation** : DB créée au premier démarrage, idempotent.

### Tâche 2.2.2 — Persistance des VDevices actifs

**Prérequis** : 2.2.1.

- [ ] Snapshot de l'état VDevice après chaque mutation (create/renew/update/release/expire).
- [ ] Format JSON sérialisé dans la table.
- [ ] Tests : un crash entre deux opérations ne perd qu'au plus la dernière non-flushée.

**Acceptation** : redémarrage du process → état restauré.

### Tâche 2.2.3 — Politique de rejeu au boot

**Prérequis** : 2.2.2.

- [ ] À l'init de l'orchestrateur :
  - Charger les VDevices.
  - Purger ceux avec `Level=2` et durée écoulée pendant le downtime.
  - Purger ceux avec `Level=1 + Ttl` écoulé.
  - Marquer les `Level=1 + Persistent` "en attente de renew" avec grâce élargie (`~ 2 × heartbeat`).
  - Ne pas commander avant `~ 5 s` après reconstruction du state réel.
- [ ] Tests : scenarios "boot après crash long / court / pendant override niveau 2".

**Acceptation** : pas de burst de commandes au boot, pas d'override expiré rejoué.

### Tâche 2.2.4 — Audit log persisté

**Prérequis** : 2.2.1.

- [ ] Service `IAuditLog` injecté partout où des événements significatifs surviennent.
- [ ] Append synchrone (cohérence > perf à ce volume).
- [ ] Index sur `device_id` et `timestamp_server`.
- [ ] Endpoint API `GET /audit?device=...&from=...&to=...` pour l'UI.

**Acceptation** : on peut requêter le log a posteriori et reconstruire la timeline d'un device.

### Tâche 2.2.5 — Rotation et compaction audit

**Prérequis** : 2.2.4.

- [ ] Job périodique qui supprime les entrées > rétention (par défaut `90 jours` ⚠).
- [ ] Compaction SQLite (`VACUUM`) après purge.
- [ ] Métrique de volume exposée sur l'endpoint `/health`.

**Acceptation** : volume DB stable dans le temps sur un foyer simulé (test long-running ⚠).

---

## Phase 2.3 — Permissions

### Tâche 2.3.1 — `PermissionRegistry` persisté

**Prérequis** : Phase 2.2 finalisée.

- [ ] Table `permissions(app_id, device_id, level_max, priority_max, clusters[], status, granted_at)`.
- [ ] API : `RequestPermission(app_id, device_id, level, priority, clusters)` → `Pending`, `Grant`, `Modify`, `Revoke`.
- [ ] Lookup rapide par couple `(app_id, device_id)`.

**Acceptation** : deux apps avec des permissions différentes voient leurs intentions traitées correctement.

### Tâche 2.3.2 — Hook orchestrateur : refus si pas de permission

**Prérequis** : 2.3.1.

- [ ] À chaque création de VDevice, vérifier la permission. Si absente → mettre la création en `Pending` et notifier l'utilisateur.
- [ ] Si refusée → erreur explicite à l'app.
- [ ] Tests.

**Acceptation** : impossible de créer un VDevice sans permission accordée.

### Tâche 2.3.3 — Notification utilisateur (UI minimal)

**Prérequis** : 2.3.2.

- [ ] Endpoint web `/permissions/pending` qui liste les demandes en attente.
- [ ] Formulaire de validation (octroyer / refuser / modifier).
- [ ] WebSocket ou SSE pour rafraîchir en temps réel.

**Acceptation** : Kevin peut voir une demande surgie en moins de 1 s ⚠ après l'intention de l'app.

### Tâche 2.3.4 — Révocation et préemption

**Prérequis** : 2.3.2.

- [ ] Révocation depuis l'UI : libère immédiatement les VDevices actifs de l'app sur ce device, émet `vdevice.preempted(reason=permission_revoked)`.
- [ ] Modification de priorité maximum : recalcule la résolution à chaud (cf. ADR 0009).
- [ ] Tests.

**Acceptation** : révoquer une permission interrompt le pilotage en cours.

### Tâche 2.3.5 — Lifecycle d'app (uninstall)

**Prérequis** : 2.3.1.

- [ ] API `UninstallApp(app_id)` : libère tous VDevices, supprime toutes permissions, log dans audit.
- [ ] Tests d'isolation : pas d'orphelin résiduel.

**Acceptation** : après uninstall, `SELECT COUNT(*) FROM vdevices_active WHERE app_id=...` = 0.

---

## Phase 2.4 — Premier driver MQTT (LightDriver)

### Tâche 2.4.1 — Scaffolder `Butlr.VDevice.Drivers`

**Prérequis** : Phase 2.3 finalisée.

- [ ] Projet `src/Butlr.VDevice.Drivers/`.
- [ ] Dépendance `MQTTnet` ✓.
- [ ] Wiring DI : enregistrer `DriverHostService` dans `Butlr.McpHome` selon config.

### Tâche 2.4.2 — Connexion broker Mosquitto

**Prérequis** : 2.4.1.

- [ ] Config broker (`Mqtt:Host`, `Mqtt:Port`, credentials) dans `appsettings.json`.
- [ ] Reconnexion automatique avec backoff.
- [ ] Métrique de santé connexion exposée sur `/health`.

**Acceptation** : kill du broker → reco automatique en `~ 30 s` ⚠.

### Tâche 2.4.3 — Découverte Z2M

**Prérequis** : 2.4.2.

- [ ] Subscribe à `zigbee2mqtt/bridge/devices`.
- [ ] Parser les annonces, créer/maj `Device` dans le registry.
- [ ] Mapper les caractéristiques Z2M aux clusters Matter (au minimum pour les ampoules : `OnOff`, `LevelControl`, éventuellement `ColorControl`).

**Acceptation** : démarrer mcp-home avec Z2M tournant → tous les devices Zigbee apparaissent dans le registry.

### Tâche 2.4.4 — `LightDriver`

**Prérequis** : 2.4.3.

- [ ] Pour chaque device de classe `light` : subscribe à `zigbee2mqtt/<device>` (état) et publish à `zigbee2mqtt/<device>/set` (commande).
- [ ] Normalisation valeurs Z2M ↔ Matter (`brightness 0-254` ↔ `LevelControl.CurrentLevel`).
- [ ] Inertie paramétrable : pas minimum entre commandes (`~ 100 ms`), rampe pour les variations brusques (configurable par device).
- [ ] **Niveau 3 bypass l'inertie** (test obligatoire).
- [ ] État de santé remonté à chaque écho MQTT.

**Acceptation** : démo sur une vraie ampoule — App fictive crée un VDevice → ampoule s'allume. User crée un override niveau 2 → la lumière change. Override expire → retour à l'intention app.

### Tâche 2.4.5 — Politique d'erreur de commande

**Prérequis** : 2.4.4.

- [ ] Retry `~ 1s, 2s, 5s` ⚠ sur échec d'ack MQTT.
- [ ] Après échec final : event `device.command_failed`, audit log, état device → `degraded`.
- [ ] Pas de rejeu silencieux (cf. ADR 0011).

**Acceptation** : couper l'ampoule → l'orchestrateur passe en `degraded` après les retries, log l'erreur, n'essaie pas de re-commander en boucle.

### Tâche 2.4.6 — Single-writer MQTT (vérification)

**Prérequis** : 2.4.4.

- [ ] Documenter dans `docs/operations.md` (à créer) : "rien d'autre n'écrit MQTT" — règle dure ADR 0011.
- [ ] (Optionnel) Sniff `mqtt-explorer` ou test automatique qui détecte des publishes externes sur les topics Z2M et alerte.

**Acceptation** : doc présente, exemple de violation détecté.

---

## Phase 2.5 — Drivers étendus

Tâches symétriques à 2.4, par classe :

- [ ] **2.5.1** — `ThermostatDriver` (cluster Thermostat).
- [ ] **2.5.2** — `CoverDriver` (cluster WindowCovering).
- [ ] **2.5.3** — `SwitchDriver` (cluster OnOff seul, distinct des lights).
- [ ] **2.5.4** — `OccupancySensorDriver` (read-only, cluster OccupancySensing).
- [ ] **2.5.5** — `ContactSensorDriver` (cluster BooleanState).

Chaque driver a son test d'intégration avec un device réel ou un mock Z2M.

---

## Phase 2.6 — UI Dashboard

### Tâche 2.6.1 — Vue "qui propose quoi" par device

- [ ] Page `/devices/{id}` qui affiche : état réel, VDevices actifs avec niveau/priorité/valeur/origine, qui gagne et pourquoi, audit récent.
- [ ] Mise à jour temps réel (SSE).

**Acceptation** : Kevin peut voir en moins de 5 s pourquoi son chauffage est à 22°C maintenant.

### Tâche 2.6.2 — Vue matricielle apps × devices

- [ ] Page `/permissions` : liste des permissions par app et par device.
- [ ] Édition rapide (priorité max, révocation).

### Tâche 2.6.3 — Configuration politique de résolution niveau 1

- [ ] Page `/devices/{id}/policy` : choisir entre priorité stricte et plugin pondération.
- [ ] Paramètres du plugin éditables (poids par défaut, etc.).

### Tâche 2.6.4 — Configuration fallback

- [ ] Page `/devices/{id}/fallback` : activer/désactiver, choisir la valeur de fallback.
- [ ] Implémenté comme un VDevice spécial (cf. ADR 0012).

---

## Phase 2.7 — Migration outils MCP existants

**Objectif** : faire en sorte que les tools `turn_on_light` / `turn_off_light` actuels (cf. `architecture.md` §7.1) deviennent des **clients VDevice** (apps internes) au lieu de piloter directement le mock.

### Tâche 2.7.1 — App interne "carlson-internal"

- [ ] Lors d'un appel `turn_on_light(room)`, mcp-home crée un VDevice niveau 2 (durée par défaut `~ 4 h` ⚠ — à arbitrer) au nom de `app_id=carlson-internal`.
- [ ] Permission auto-octroyée pour Carlson sur tous les devices `light` (Carlson est une app système configurée).
- [ ] Tests.

**Acceptation** : "Hey Carlson, allume le salon" → un VDevice niveau 2 apparaît dans l'audit, l'ampoule s'allume.

### Tâche 2.7.2 — Déprécation `ConsoleMockBackend`

- [ ] Le mock console reste en option (pour démo sans MQTT) mais devient un **fake driver** à l'intérieur de la nouvelle archi, pas un backend distinct.
- [ ] Mise à jour `architecture.md` §7.2 et §3.2 pour refléter que `IDeviceBackend` est superseded par le modèle driver/orchestrateur.

**Acceptation** : `architecture.md` ne décrit plus `IDeviceBackend` comme l'abstraction principale.

### Tâche 2.7.3 — Mise à jour ADR pertinents

- [ ] ADR 0005 (mcp-home en .NET 10) : ajouter une note de révision listant les nouvelles dépendances (`Microsoft.Data.Sqlite`).
- [ ] ADR 0003 (transport MCP) : note sur les nouveaux tools MCP exposés.
- [ ] Aucun ADR existant à superseder, mais les notes de révision doivent référencer les ADRs 0007-0012.

---

## Suivi du chantier

### Récap des phases et leur valeur démo-able

| Phase | Démo de fin | Prérequis hardware |
|---|---|---|
| 2.0 | Tests unitaires verts du moteur | Aucun |
| 2.1 | Orchestrateur in-memory drivable depuis Claude Desktop via MCP | Aucun |
| 2.2 | Survie redémarrage | Aucun |
| 2.3 | Modèle permission complet | Aucun |
| 2.4 | Bout-en-bout sur une vraie ampoule | Coordinateur Zigbee + Z2M + 1 ampoule |
| 2.5 | Idem sur thermostat, volet, capteurs | Devices correspondants |
| 2.6 | UI dashboard utilisable au quotidien | (Phases précédentes) |
| 2.7 | Migration des tools MCP du POC | (Phases précédentes) |

### Estimation grossière

⚠ Estimation à très grande maille, à challenger par le dev qui prendra le ticket :

- Phase 2.0 : `~ 5 j`
- Phase 2.1 : `~ 5 j`
- Phase 2.2 : `~ 5 j`
- Phase 2.3 : `~ 5 j`
- Phase 2.4 : `~ 7 j`
- Phase 2.5 : `~ 10 j`
- Phase 2.6 : `~ 10 j`
- Phase 2.7 : `~ 3 j`

**Total `~ 50 j` ⚠** sur un dev expérimenté .NET. Compter `+30%` pour les imprévus = `~ 65 j`.

### Anti-goals

À garder dans la tête tout au long du chantier :

- ❌ Ne pas réimplémenter Zigbee/Z-Wave en bas niveau. Z2M / ZWaveJS font le travail.
- ❌ Ne pas inventer une taxonomie de capacités maison. Les clusters Matter sont la référence (ADR 0010).
- ❌ Ne pas mélanger état continu et command/event dans le même VDevice (cf. ADR 0007).
- ❌ Ne pas accepter une intention niveau 2 sans `duration_ms` explicite, jamais (cf. ADR 0008).
- ❌ Ne pas court-circuiter le single-writer MQTT, même "juste pour un test" (cf. ADR 0011).
- ❌ Ne pas commander de "valeur neutre" au boot quand aucun VDevice n'écrit (cf. ADR 0012).

---

## Liens

- [Structuration projet](vdevice-architecture.md)
- ADR : [0007](adr/0007-virtual-device-arbitration.md), [0008](adr/0008-vdevice-lifecycle-renew.md), [0009](adr/0009-app-device-permissions.md), [0010](adr/0010-matter-clusters-capability-model.md), [0011](adr/0011-driver-mqtt-adapter.md), [0012](adr/0012-state-persistence-audit-fallback.md)
- [Architecture globale](architecture.md)
