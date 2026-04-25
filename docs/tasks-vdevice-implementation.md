# Backlog d'implémentation — Couche VDevice (Phase 2 mcp-home)

> Liste de tâches concrètes, ordonnées par phase, donnable à un développeur. Chaque tâche a un **objectif**, un **critère d'acceptation**, et signale ses **prérequis**.
>
> Référence à lire avant : [`vdevice-architecture.md`](vdevice-architecture.md), ADRs 0008-0011, 0013, **0014, 0015, 0016** (qui supersedent 0007 et 0012). Les ADRs 0007 et 0012 sont conservés pour la motivation historique.

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

**Objectif** : modèle pur testable, sans aucune dépendance d'infra. **Niveaux dynamiques par config dès cette phase** (cf. ADR 0014) — pas de niveau hardcodé `1/2/3`.

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

### Tâche 2.0.3 — Entité `Tier` et `TierRegistry`

**Prérequis** : 2.0.1.

- [ ] Record `Tier { Id, Rank, ArbiterRef, ArbiterConfig, Admission, DurationPolicy, BypassInertia }` (cf. ADR 0014 §"Anatomie d'un niveau").
- [ ] Type `Admission { TagsRequired: IReadOnlySet<string>, TagsForbidden: IReadOnlySet<string> }`.
- [ ] Type `DurationPolicy { PersistentAllowed: bool, TtlRequired: bool, TtlMaxMs: int? }`.
- [ ] `TierRegistry` chargé au démarrage : ids uniques, ranks uniques, validation cohérence (`ArbiterRef` connu, `TagsRequired` non vide ou explicitement `[]`).
- [ ] **Pas d'enum `Level`**. Les niveaux sont nommés et identifiés par chaîne (`"safety"`, `"user-override"`, `"apps"`).
- [ ] Tests : registre valide, registre avec rank dupliqué (refus), registre avec arbitre inconnu (refus), tri par rank.

**Acceptation** : on charge le preset par défaut (3 niveaux : `safety` rank 1, `user-override` rank 2, `apps` rank 3) sans erreur. Les invariants sont vérifiés au load-time, pas au runtime.

### Tâche 2.0.4 — Entité VDevice et invariants

**Prérequis** : 2.0.2, 2.0.3.

- [ ] Record `VDevice { Id, AppId?, ActorUserId?, ViaAgentId?, DeviceId, TierId, Tags, Priority, Cluster, Attribute, Value, DurationPolicy, CreatedAt, LastRenewAt }`.
- [ ] Tags dérivés du `actor_kind` : `app | user_agent | system` (cf. ADR 0014 §"Tags d'admission").
- [ ] Invariants en constructeur :
  - Le `tier_id` doit exister dans le `TierRegistry`.
  - Les `Tags` doivent satisfaire `Admission.TagsRequired` du niveau (sinon `ArgumentException`).
  - Si `DurationPolicy.TtlRequired` du niveau ⇒ `VDevice.DurationPolicy = Ttl(...)` obligatoire ; `Persistent` refusé.
  - Si `!DurationPolicy.PersistentAllowed` du niveau ⇒ `Persistent` refusé.
  - Si `TtlMaxMs` posé ⇒ `Ttl(ms) ≤ TtlMaxMs` sinon refus.
  - Priority dans `[0, 100]` ⚠ (plage à valider en revue d'API).
- [ ] Helper `ResolveTier(payload, registry) → tier_id` : si payload sans `tier_id`, renvoie le niveau de plus haut `rank` dont `TagsRequired ⊆ tags(payload)` (cf. ADR 0014).

**Acceptation** : tests couvrent — création valide ; refus tag manquant ; refus TTL manquant quand `ttl_required: true` ; refus persistent quand `persistent_allowed: false` ; auto-résolution du tier_id.

### Tâche 2.0.5 — Interface `IArbiter` et arbitres de référence

**Prérequis** : 2.0.4.

- [ ] Interface `IArbiter { Value? Arbitrate(IReadOnlyCollection<VDevice> admitted, RealState? state); }` — fonction pure.
- [ ] Implémentations dans `Butlr.VDevice.Core/Arbiters/` :
  - `WinnerTakesAllArbiter` — premier admis gagne (utile pour `safety`, niveau mono-émetteur).
  - `StrictPriorityArbiter` — plus haute priorité gagne ; départage timestamp serveur.
  - `UserPriorityThenTimestampArbiter` — priorité utilisateur d'abord (`actor_user_id` mappé sur la priorité utilisateur), timestamp serveur ensuite (cf. ADR 0008).
  - `WeightedAverageArbiter` — moyenne pondérée des valeurs (uniquement attributs numériques continus ; refus typé sinon).
- [ ] Tests unitaires exhaustifs par arbitre (entrée vide, un seul candidat, ex aequo, ex aequo départagé).

**Acceptation** : chaque arbitre est testé en isolation sans dépendance au reste du système.

### Tâche 2.0.6 — Pipeline d'arbitrage strict winner-takes-all

**Prérequis** : 2.0.3, 2.0.5.

- [ ] Service `Arbitration` (pure) :
  - Entrée : `IReadOnlyCollection<VDevice>` actifs sur le device, `TierRegistry`, `RealState?`.
  - Pour chaque niveau dans l'ordre de `rank` croissant : filtrer les VDevices admis (par `tier_id` + `Admission.TagsRequired/TagsForbidden`), appeler l'arbitre, premier non-null **wins** (cf. ADR 0014 §"Strict winner-takes-all entre niveaux").
  - Renvoie `{ Value, WinningTierId, WinningVDeviceId, BypassInertia }` ou `null` (aucune commande).
- [ ] Tests des cas limites de l'ancien ADR 0007 §"État réel commandé" mais exprimés via le preset 3 niveaux par défaut (preuve de non-régression sémantique).

**Acceptation** : on peut substituer le preset 3 niveaux par un preset 5 niveaux et l'arbitrage continue à fonctionner sans recompilation.

### Tâche 2.0.7 — Lifecycle (renew, expiration, fenêtre de grâce)

**Prérequis** : 2.0.4.

- [ ] Service `VDeviceLifecycle` (pure, en mémoire, sans persistance) :
  - `Create(...) → VDevice` avec horodatage serveur.
  - `Renew(id, now) → bool` — accepte si dans la fenêtre `[lastRenew, lastRenew + heartbeat + grace]`.
  - `IsExpired(vdevice, now)` selon `DurationPolicy` et `LastRenewAt`.
  - `Tick(now) → IReadOnlyCollection<VDeviceId> expired` — renvoie ceux à libérer.
- [ ] Constantes globales `~ HEARTBEAT_INTERVAL_MS = 30_000`, `GRACE_MS = 5_000` configurable. Paramétrables par niveau Phase 3+ si besoin (cf. ADR 0008 patché par 0014).
- [ ] Tests : renew dans fenêtre, hors fenêtre, expire à `Ttl`, persistent expire au heartbeat manqué.

**Acceptation** : le cas limite "renew une milliseconde après expiration" passe grâce à la grâce.

### Tâche 2.0.8 — Conflits intra-niveau simultanés (timestamp serveur)

**Prérequis** : 2.0.5, 2.0.7.

- [ ] Vérifier que `UserPriorityThenTimestampArbiter` départage correctement deux VDevices `user-override` simultanés (différentes priorités utilisateur, mêmes priorités).
- [ ] Tests : timestamp serveur (pas client) — fournir un faux clock contrôlable.
- [ ] Émission d'un événement `VDeviceConflictResolved` pour observabilité (consommé par `ObservabilityService` en Phase 2.1).

**Acceptation** : déterminisme observable en test, pas de race.

### Tâche 2.0.9 — Plugins d'arbitre custom (chargement dynamique)

**Prérequis** : 2.0.5.

- [ ] Mécanisme de chargement par référence : `ArbiterRef` dans le `Tier` peut pointer vers un type d'une assembly listée dans la config (cf. ADR 0014 §"Plugins d'arbitre", ADR 0015 `arbiters/*.yaml`).
- [ ] `ArbiterFactory` qui résout l'instance à partir de la config (DI ou reflection).
- [ ] Tests : un arbitre custom (assembly de test) est chargé et utilisé.

**Acceptation** : on peut configurer un niveau qui utilise un arbitre tiers sans modifier `Butlr.VDevice.Core`.

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

### Tâche 2.1.6 — `ObservabilityService` et instrumentation OTel

**Prérequis** : 2.1.2 (instrumenter dès la fin de la 2.1, sans attendre la persistance).

- [ ] Dépendances NuGet : `OpenTelemetry.Api`, `OpenTelemetry.Extensions.Hosting`, exporter OTLP/gRPC (cf. ADR 0016).
- [ ] Façade `ObservabilityService` qui encapsule `ActivitySource` (spans), `ILogger<T>` (logs structurés), `Meter` (metrics).
- [ ] Instrumentation des points clefs identifiés dans `vdevice-architecture.md §6.2` :
  - Span `arbitration` à chaque décision (attributs : `device_id`, `evaluated_tiers`, `winning_tier`, `winning_vdevice_id`, `arbiter`, `inputs_count`, `duration_ms`).
  - Logs structurés `vdevice.created/renewed/released/expired/preempted`.
  - Metrics `butlr.vdevice.active`, `butlr.arbitration.duration` dès cette phase.
- [ ] Endpoint OTLP configurable via `appsettings.json` (`Otel:Endpoint`, défaut `http://localhost:4317`).
- [ ] Cache mémoire "recent activity" (`~ 1 000 dernières décisions` par device) maintenu par `ObservabilityService`, exposé via endpoint local pour l'UI dashboard (cf. ADR 0016 §"Recent activity UI").
- [ ] Tests : spans et logs émis correctement (in-memory exporter en test) ; export OTel désactivable en test.

**Acceptation** : un lancement avec collector OTel branché → traces et logs visibles côté collector ; sans collector → `mcp-home` continue à tourner sans erreur, le cache "recent activity" reste accessible.

---

## Phase 2.2 — Config git/yaml et state snapshot

**Objectif** : config hiérarchique versionnée en git, state survivant au redémarrage. Pas de SQLite, pas d'audit dédié — l'instrumentation OTel posée en 2.1.6 reste la seule trace observationnelle.

### Tâche 2.2.1 — Scaffolder `Butlr.VDevice.Config`

**Prérequis** : Phase 2.1 finalisée (en particulier 2.1.6).

- [ ] Projet `src/Butlr.VDevice.Config/` ajouté à la solution.
- [ ] Dépendances NuGet : `LibGit2Sharp` ✓ (MIT), `YamlDotNet` ✓ (MIT).
- [ ] Référence depuis `Butlr.VDevice.Orchestrator` (lecture config) et `Butlr.McpHome` (écriture via UI).

**Acceptation** : projet build, importable.

### Tâche 2.2.2 — `ConfigRepository` (git)

**Prérequis** : 2.2.1.

- [ ] Path repo configurable, défaut `~/.butlr/config/`.
- [ ] Init au premier démarrage : si pas de repo, le créer + commit du preset par défaut (3 niveaux `safety`/`user-override`/`apps` cf. ADR 0014 + arborescence vide).
- [ ] API : `Load() → ConfigSnapshot`, `Commit(message, files...)`, `Status()`, `History(limit)`.
- [ ] Pas de gestion de conflits multi-writer au POC : un seul process écrit (mcp-home), un humain peut éditer hors-ligne et `git pull` au prochain redémarrage.

**Acceptation** : le repo est créé idempotent au premier boot ; un commit programmatique apparaît dans `git log` ; un edit manuel (par l'utilisateur) est visible au prochain `Load()`.

### Tâche 2.2.3 — `YamlSerializer` et records typés

**Prérequis** : 2.2.1.

- [ ] Wrapper YamlDotNet avec settings standard (camelCase, indentation, support des comments à l'écriture pour les fichiers édités par l'UI).
- [ ] Records dans `Butlr.VDevice.Config/Models/` : `HomeConfig`, `FloorConfig`, `RoomConfig`, `DeviceConfig`, `TierConfig`, `ArbiterConfig`, `AppConfig`, `PermissionConfig` (cf. ADR 0015 §"Format des fichiers").
- [ ] Tests sérialisation round-trip (yaml → record → yaml identique) sur les exemples de l'ADR 0015.

**Acceptation** : les exemples yaml de l'ADR 0015 (preset 3 niveaux, device thermostat, permission cocooning) parsent sans warning ; round-trip stable.

### Tâche 2.2.4 — `DeltaResolver` (héritage maison ⊕ étage ⊕ pièce ⊕ device)

**Prérequis** : 2.2.3.

- [ ] Algorithme : pour chaque device, empiler `home.yaml`, `<étage>/etage.yaml`, `<étage>/<pièce>/piece.yaml`, `<étage>/<pièce>/<device>.yaml`. Le delta enfant remplace les clefs présentes ; les autres restent du parent.
- [ ] Pas de merge sémantique git ligne-à-ligne — chaque overlay est un record typé fusionné en mémoire (cf. ADR 0015 §"Héritage par delta").
- [ ] **Détection de config orpheline** : une clef de delta qui ne figure pas dans la config résolue parente → log warning + ignorée + remontée à l'UI dashboard (Phase 2.6).
- [ ] Tests : héritage simple, override partiel, override total, clef orpheline.

**Acceptation** : on peut produire la config effective d'un device en moins de `~ 50 ms` ⚠ pour un foyer de 50 devices.

### Tâche 2.2.5 — `SchemaValidator` (json-schema généré depuis records)

**Prérequis** : 2.2.3.

- [ ] Génération automatique d'un json-schema depuis les types C# de `Models/`.
- [ ] Validation au load-time du `ConfigRepository` : tout fichier yaml invalide → log error + fail-fast au démarrage (la config doit être correcte pour démarrer).
- [ ] Tests : config valide, config avec champ inconnu (refus), config avec type incohérent (refus).

**Acceptation** : un yaml malformé empêche le démarrage avec un message clair pointant le fichier et la ligne.

### Tâche 2.2.6 — State snapshot JSONL

**Prérequis** : Phase 2.1 (orchestrateur en mémoire).

- [ ] Path snapshot configurable, défaut `~/.butlr/state/vdevices.jsonl` (cf. ADR 0016 §"État VDevices").
- [ ] À chaque mutation (create/renew/update/release/expire/preempt), append d'une ligne JSON.
- [ ] Compaction périodique (~ 24 h ⚠) : lecture du fichier, calcul de l'état net, écriture vers `vdevices.jsonl.new`, swap atomique (`File.Move` avec replace).
- [ ] Tests : un crash entre deux mutations ne perd que la dernière non-flushée ; compaction préserve l'état net.

**Acceptation** : kill -9 du process pendant un workload → redémarrage retrouve l'état (modulo la dernière ms).

### Tâche 2.2.7 — Politique de rejeu au boot

**Prérequis** : 2.2.6, 2.2.4 (chargement TierRegistry depuis config).

- [ ] À l'init de l'orchestrateur :
  - Charger le `TierRegistry` depuis la config résolue.
  - Charger les VDevices depuis le snapshot JSONL compacté + replay des lignes post-compaction.
  - Pour chaque VDevice : ignorer si déjà `released` ou `expired` ; purger ceux dont la `DurationPolicy` du niveau exige TTL et dont `LastRenewAt + ttl_ms < now`.
  - Marquer les persistent "en attente de renew" avec grâce élargie (`~ 2 × heartbeat`).
  - Ne pas commander avant `~ 5 s` après reconstruction de l'état réel via MQTT (cf. ADR 0016 §"Politique de rejeu au boot").
- [ ] Tests : scenarios "boot après crash long / court / pendant un override `user-override`".

**Acceptation** : pas de burst de commandes au boot, pas d'override expiré rejoué.

### Tâche 2.2.8 — Reconfiguration = restart (POC)

**Prérequis** : 2.2.4.

- [ ] Documenter dans `docs/operations.md` : modifier la config (manuellement ou via UI) ne prend effet qu'au prochain redémarrage.
- [ ] Au redémarrage si la config a changé : log explicite "config changed since last boot, purging all in-flight VDevices" + commit éventuel d'un message git auto.
- [ ] Pas de hot-reload (Phase 3+).

**Acceptation** : un edit du `home.yaml` suivi d'un restart applique le nouveau preset ; les VDevices persistants sont effectivement purgés.

---

## Phase 2.3 — Permissions

### Tâche 2.3.1 — `PermissionRegistry` (yaml en git)

**Prérequis** : Phase 2.2 finalisée.

- [ ] Source de vérité : `~/.butlr/config/permissions/<app_id>__<device_id>.yaml` (cf. ADR 0009 patché par 0014, ADR 0015 §"Apps et permissions").
- [ ] Champs : `app_id`, `device_id`, `tier_max` (id nommé, pas numéro), `priority_max`, `clusters_allowed[]`, `status` (`pending|granted|revoked`), `granted_at`, `granted_by`.
- [ ] API : `RequestPermission(app_id, device_id, tier_id, priority, clusters)` → `Pending`, `Grant`, `Modify`, `Revoke`. Chaque mutation = update yaml + commit git.
- [ ] Cache mémoire indexé par `(app_id, device_id)` rechargé à chaud sur watch FS (POC : reload sur signal explicite ou au prochain boot).
- [ ] Pas de table SQL.

**Acceptation** : deux apps avec des permissions différentes voient leurs intentions traitées correctement ; un `git log` du dossier `permissions/` montre l'historique des octrois/révocations.

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

### Tâche 2.6.1 — Navigation hiérarchique maison/étage/pièce/device

- [ ] Page `/` racine : preset de niveaux maison + lien vers chaque étage.
- [ ] Page `/etages/{etage}` : config héritée + deltas de l'étage + lien vers chaque pièce.
- [ ] Page `/pieces/{piece}` : config héritée + deltas de la pièce + liste des devices.
- [ ] La hiérarchie reflète exactement l'arborescence `~/.butlr/config/` (cf. ADR 0015).
- [ ] Indicateur "config orpheline" visible à chaque niveau si présent (cf. tâche 2.2.4).

**Acceptation** : naviguer de la racine à un device en suivant la hiérarchie ; voir à chaque niveau ce qui est hérité vs surchargé.

### Tâche 2.6.2 — Vue "qui propose quoi" par device

- [ ] Page `/devices/{id}` qui affiche : état réel, VDevices actifs avec **niveau (id nommé)** / priorité / valeur / origine (`actor_kind`, `app_id` ou `via_agent_id`), qui gagne et pourquoi (niveau gagnant + arbitre + raison de départage), recent activity (cache `ObservabilityService`, cf. tâche 2.1.6).
- [ ] Mise à jour temps réel (SSE).

**Acceptation** : Kevin peut voir en moins de 5 s pourquoi son chauffage est à 22°C maintenant.

### Tâche 2.6.3 — Vue matricielle apps × devices

- [ ] Page `/permissions` : liste des permissions par app et par device.
- [ ] Édition rapide : `tier_max`, `priority_max`, révocation. Chaque action écrit le yaml + commit git.

### Tâche 2.6.4 — Configuration des niveaux par scope

- [ ] Page `/etages/{etage}/tiers`, `/pieces/{piece}/tiers`, `/devices/{id}/tiers` : permettre de surcharger la config de niveau (ajouter / modifier / désactiver un niveau au scope).
- [ ] Sauvegarde = écriture du delta dans le yaml du scope + commit git.
- [ ] Avertissement explicite : "la modification ne prend effet qu'au prochain redémarrage de mcp-home" (cf. tâche 2.2.8).

### Tâche 2.6.5 — Configuration fallback

- [ ] Page `/devices/{id}/fallback` : activer/désactiver, choisir la valeur de fallback.
- [ ] Implémenté comme un VDevice spécial (cf. ADR 0012 §"Fallback comme VDevice", règle préservée).
- [ ] Stocké dans le yaml du device (delta).

---

## Phase 2.7 — Migration outils MCP existants

**Objectif** : faire en sorte que les tools `turn_on_light` / `turn_off_light` actuels (cf. `architecture.md` §7.1) deviennent des **clients VDevice** au lieu de piloter directement le mock. Carlson est un **agent-utilisateur** (cf. ADR 0013), pas une app autonome — la modélisation diffère du chemin "App Cocooning".

### Tâche 2.7.1 — Carlson en tant qu'agent-utilisateur

**Prérequis** : ADR 0013 lu et compris ; Phases 2.1 → 2.4 finalisées.

- [ ] Côté **mcp-home**, les tools MCP exposés à Carlson (`turn_on_light`, `set_thermostat`, etc.) construisent un payload `actor_kind=user_agent`, `actor_user_id=<utilisateur courant>`, `via_agent_id="carlson"`. Pas de `app_id` dans ce chemin.
- [ ] L'**actor_kind est posé par le tool MCP**, pas reçu du client : un client MCP malveillant ne peut pas auto-déclarer `actor_kind=user_agent`. Au POC, l'utilisateur courant est l'utilisateur unique du foyer (cf. `architecture.md §11`) ; structure `actor_user_id` prévue dès maintenant pour la diarization Phase 3+.
- [ ] **Pas de prompt de permission** déclenché par ce chemin (les agents-utilisateur ne sont pas soumis au modèle Android — cf. ADR 0009 patché par ADR 0013).
- [ ] Côté **Carlson (Python)**, la résolution de la `duration_ms` pour le niveau 2 se fait **avant** l'appel au tool MCP :
  - Si l'utilisateur a explicité une durée (« pour 30 min ») → utiliser cette durée.
  - Sinon, **heuristique configurable** par device et plage horaire (ex. lumière salon le soir → jusqu'à `~ 23:30` ; radiateur en hiver → `~ 1 h`). Stub d'heuristique au POC, données de configuration dans `carlson/config/intent_heuristics.yaml` (à créer).
  - Sinon (heuristique non applicable), Carlson **prompt vocalement** : « Pour combien de temps ? ». Réponse parsée et appliquée.
  - **Jamais** d'appel niveau 2 sans `duration_ms` — l'orchestrateur le rejette de toute façon (ADR 0008).
- [ ] Côté **mcp-home**, ajouter validation au point d'entrée : `actor_kind=user_agent` + `tier_id=user-override` (ou résolution auto vers ce niveau) + pas de `duration_ms` → `400 Bad Request` avec message explicite, log structuré de la requête malformée via OTel.
- [ ] Tests :
  - Unitaire orchestrateur : refus `actor_kind=app + tier_id=user-override` (tag `app` ne matche pas `tags_required: [user_agent]` — cf. ADR 0009 patché par 0014).
  - Unitaire orchestrateur : accept `actor_kind=user_agent + tier_id=user-override + duration_ms` ; refus si `duration_ms` absent (`duration_policy.ttl_required: true`).
  - Unitaire Carlson (Python) : heuristique de durée pour différents (device, plage horaire).
  - Intégration : l'audit log d'un override vocal porte bien `actor_user_id=kevin`, `via_agent_id=carlson`.

**Acceptation** : « Hey Carlson, allume le salon » → un VDevice sur le niveau `user-override` avec `actor_kind=user_agent`, `actor_user_id=kevin`, `via_agent_id=carlson`, `duration_ms` calculé par Carlson, visible dans la trace OTel ; l'ampoule s'allume ; à expiration, retour à l'intention applicative ou au fallback.

### Tâche 2.7.1bis — UI web comme agent-utilisateur

**Prérequis** : 2.7.1, Phase 2.6 (UI dashboard) en cours ou finalisée.

- [ ] Le contrôle d'override niveau 2 dans l'UI dashboard contient **obligatoirement** un slider de durée (pas de défaut implicite — cf. ADR 0013).
- [ ] L'endpoint qui consomme le formulaire force `actor_kind=user_agent`, `actor_user_id=<utilisateur de la session>`, `via_agent_id="ui-web"`.
- [ ] Le contrôle "réglage durable" (changer la consigne de référence d'un thermostat, par exemple) émet un VDevice sur le niveau `apps` avec `actor_kind=user_agent`, `app_id` synthétique `app:user-direct:<user>` (cf. ADR 0013 §"Niveau 1 par un agent-utilisateur" et §4.6 de `vdevice-architecture.md` pour le caveat sur les tags).
- [ ] Tests : intégration formulaire → orchestrateur → audit log avec les bons champs.

**Acceptation** : depuis l'UI, un override niveau 2 sans slider configuré est **impossible à soumettre** ; le réglage durable depuis l'UI est tracé distinctement de l'override temporaire dans l'audit.

### Tâche 2.7.2 — Déprécation `ConsoleMockBackend`

- [ ] Le mock console reste en option (pour démo sans MQTT) mais devient un **fake driver** à l'intérieur de la nouvelle archi, pas un backend distinct.
- [ ] Mise à jour `architecture.md` §7.2 et §3.2 pour refléter que `IDeviceBackend` est superseded par le modèle driver/orchestrateur.

**Acceptation** : `architecture.md` ne décrit plus `IDeviceBackend` comme l'abstraction principale.

### Tâche 2.7.3 — Mise à jour ADR pertinents

- [ ] ADR 0005 (mcp-home en .NET 10) : ajouter une note de révision listant les nouvelles dépendances (`LibGit2Sharp`, `YamlDotNet`, `OpenTelemetry.*`, `MQTTnet`).
- [ ] ADR 0003 (transport MCP) : note sur les nouveaux tools MCP exposés.
- [ ] Aucun ADR existant à superseder à ce stade ; les notes de révision doivent référencer les ADRs 0008-0011, 0013-0016 (les 0007 et 0012 sont déjà superseded).

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
- ❌ Ne pas mélanger état continu et command/event dans le même VDevice (cf. ADR 0007 préservé en motivation).
- ❌ Ne pas accepter une intention sur un niveau dont `duration_policy.ttl_required: true` sans `duration_ms` explicite, jamais (cf. ADR 0008 patché par 0014).
- ❌ Ne pas hardcoder les niveaux 1/2/3 en enum. Les niveaux sont nommés et chargés depuis la config (cf. ADR 0014).
- ❌ Ne pas court-circuiter le single-writer MQTT, même "juste pour un test" (cf. ADR 0011).
- ❌ Ne pas commander de "valeur neutre" au boot quand aucun VDevice n'écrit (cf. ADR 0012 §"Pas de fallback automatique", règle préservée par ADR 0016).
- ❌ Ne pas modéliser Carlson (ni l'UI web/mobile, ni les interrupteurs niveau 2) comme une app autonome avec permission auto-octroyée niveau 2 — c'est un agent-utilisateur, pas une app (cf. ADR 0013). Renforcé par ADR 0014 : le tag `app` ne matche pas le niveau `user-override` qui exige le tag `user_agent`.
- ❌ Ne pas stocker la config dans SQLite ou tout backend non-versionné. La config vit en git/yaml (cf. ADR 0015) — la traçabilité git est non-négociable.
- ❌ Ne pas écrire d'audit log applicatif maison. Tout passe par OpenTelemetry (cf. ADR 0016). Le collector est out-of-scope `mcp-home`.
- ❌ Ne pas implémenter le hot-reload de config au POC. Reconfiguration = restart + purge des VDevices.

---

## Liens

- [Structuration projet](vdevice-architecture.md)
- ADR : [0007](adr/0007-virtual-device-arbitration.md) *(superseded by 0014)*, [0008](adr/0008-vdevice-lifecycle-renew.md), [0009](adr/0009-app-device-permissions.md), [0010](adr/0010-matter-clusters-capability-model.md), [0011](adr/0011-driver-mqtt-adapter.md), [0012](adr/0012-state-persistence-audit-fallback.md) *(superseded by 0015 + 0016)*, [0013](adr/0013-user-agents-vs-apps.md), [0014](adr/0014-dynamic-tiers-arbiters.md), [0015](adr/0015-config-git-yaml-hierarchy.md), [0016](adr/0016-state-snapshot-otel-observability.md)
- [Architecture globale](architecture.md)
