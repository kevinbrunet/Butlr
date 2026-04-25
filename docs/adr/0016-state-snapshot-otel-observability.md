# ADR 0016 — État éphémère in-memory + snapshot JSONL ; observabilité et audit via OpenTelemetry

**Date** : 2026-04-25
**Statut** : Accepté — supersede partiellement [ADR 0012](0012-state-persistence-audit-fallback.md) (parties state et audit)

## Contexte

L'ADR 0012 plaçait dans une SQLite unique : la config (déjà sortie vers git/yaml par ADR 0015), **les VDevices actifs**, **l'audit log**, **l'état réel des devices**. Reste à traiter les trois derniers, qui ont des natures opérationnelles très différentes :

- **VDevices actifs** : structure stateful, ~ 50-200 entrées en régime, mutation toutes les 30 s (heartbeat/renew), perte tolérable au crash récent (les apps re-déclareront).
- **Audit log** : append-only haute fréquence (chaque arbitrage = un event), volumétrie **élevée** sur la durée (✓ ordre de 10⁵ événements/jour pour un foyer actif ⚠ — à mesurer).
- **État réel des devices** : reconstruit depuis MQTT (Z2M annonce les états), pas un stockage de vérité.

Les forcer dans une SQLite "à la main" est faisable mais peu rentable :

- VDevices actifs : un snapshot fichier suffit (re-déclarations attendues au boot de toute façon).
- Audit log : les outils dédiés à l'observabilité (Tempo, Loki, Grafana, OpenSearch…) le font infiniment mieux que SQLite, avec recherche, agrégation, dashboards, rétention configurable.
- État réel : la source de vérité est MQTT — pas besoin de persister.

Décision tranchée par Kevin le 2026-04-25 : **observabilité et audit passent par OpenTelemetry**. Le collector et son pipeline aval (export Loki/Tempo/Prometheus, stockage long-terme, dashboards) ne sont **pas dans le scope mcp-home** — c'est l'affaire de l'opérateur.

L'ADR 0016 acte les trois sujets : état éphémère, audit/logs/metrics via OTel, et la conséquence sur l'ADR 0012.

## Décision

### VDevices actifs : in-memory primaire + snapshot JSONL

Source de vérité au runtime : **dictionnaire concurrent en mémoire** dans l'orchestrateur (`Butlr.VDevice.Orchestrator`).

Persistance pour survivre aux redémarrages : **snapshot JSONL append-only** dans `~/.butlr/state/vdevices.jsonl`.

- Chaque mutation (`create`, `renew`, `update`, `release`, `expire`) ajoute une **ligne JSON** à la fin du fichier.
- Format ligne : `{ "ts": "...", "op": "create|renew|update|release|expire", "vdevice": {...} }`.
- Append synchrone (cohérence > perf à ce volume — `~ 30 s` entre renews).
- **Compaction périodique** : un job, déclenché au démarrage et toutes les `~ 24 h`, reconstruit un fichier compact `vdevices.jsonl.compacted` ne contenant que les entrées encore vivantes, puis swap atomique.

### Politique de rejeu au boot

Au démarrage de l'orchestrateur :

1. Lire `vdevices.jsonl` (ou sa version compactée).
2. Reconstruire l'état mémoire en rejouant les lignes dans l'ordre, en ignorant celles invalidées par un `release` ou `expire` ultérieur.
3. **Purger les VDevices dont la `duration_ms` (selon ADR 0014, `duration_policy` du niveau) est écoulée pendant le downtime.**
4. Pour les VDevices `persistent` : marquer "en attente de renew" avec une grâce élargie (`~ 2 × heartbeat_interval_ms`). Si l'app correspondante ne se réabonne pas, le VDevice expire.
5. **Aucune commande device émise pendant `~ 5 s`** au boot — fenêtre où l'orchestrateur écoute le state réel reconstruit par les drivers (cf. ADR 0011).

### État réel des devices : pas persisté

Le `RealState` des devices est reconstruit en mémoire à partir des annonces MQTT post-boot. Pas de snapshot. Si un device est offline au boot, il est marqué `degraded` jusqu'à sa première annonce.

Cohérent avec la règle "pas de valeur neutre rejouée au boot" (cf. ADR 0012 §"Pas de fallback automatique").

### Observabilité et audit via OpenTelemetry

Tout ce qui était "audit log" dans l'ADR 0012 + les logs applicatifs + les metrics passent par **OpenTelemetry** :

- **Lib** : ✓ `OpenTelemetry.Api` + `OpenTelemetry.Extensions.Hosting` + `OpenTelemetry.Instrumentation.AspNetCore` (.NET, Apache 2.0, support natif).
- **Exporter** : OTLP/gRPC vers `localhost:4317` par défaut, configurable (`Otel:Endpoint` dans `appsettings.json`).
- **Service name** : `butlr-mcp-home`, version semver de l'assembly.
- **Le collector** (OTel Collector, ou un alternatif) **est out-of-scope mcp-home**. C'est l'opérateur qui le configure pour exporter vers son backend (Loki, Tempo, Grafana Cloud, OpenSearch, etc.).

#### Mapping audit / OTel

| Évènement audit (ancien 0012) | Représentation OTel |
|---|---|
| Création / renew / release / expiration de VDevice | **Log structuré** (`ILogger.LogInformation("vdevice.created", { vdevice_id, tier_id, app_id, actor_user_id, via_agent_id, device_id, duration_ms, value })`) |
| **Décision d'arbitrage** sur un device | **Span** `arbitration` avec attributs `{ device_id, evaluated_tiers, winning_tier, winning_vdevice_id, winning_value, arbiter, inputs_count }` |
| Commande envoyée au driver | **Span enfant** `driver.command` avec attributs `{ device_id, cluster, attribute, value, bypass_inertia }` |
| Échec de commande device | Log error + metric `device_command_failed_total` (counter par device_class) |
| Permission octroyée / révoquée | Log `permission.granted` / `permission.revoked` avec attributs `{ app_id, device_id, tier_max, priority_max, granted_by }` |
| Préemption d'un VDevice | Log + span event `vdevice.preempted(reason)` |

#### Metrics exposées

Au minimum :

- `butlr.vdevice.active` (gauge, par tier_id et device_class)
- `butlr.arbitration.duration` (histogram, par device_class)
- `butlr.driver.command.total` (counter, par device_class et result=success|failure)
- `butlr.mqtt.connected` (gauge, 0/1, par broker)
- `butlr.permission.pending` (gauge — nombre de demandes en attente)

Conventions : préfixe `butlr.`, snake_case, attributs minuscules.

#### Traces

Une **déclaration d'intention** = un span racine `vdevice.create` (ou `update`/`renew`/`release`). Sous ce span, l'arbitrage produit un span enfant `arbitration`, qui peut produire un span enfant `driver.command`. Le `traceparent` est propagé dans les headers HTTP entrants — un client (Carlson, UI) qui passe un `traceparent` voit ses appels chaînés au backend dans Tempo.

#### Volume

Hypothèse ⚠ : un foyer actif produit `~ 10⁵ évènements/jour` (50 devices × ~ 200 mutations/jour × overhead spans). Aucun de ces évènements n'a de raison de transiter par mcp-home pour stockage long-terme — c'est le rôle du backend OTel aval. mcp-home garde uniquement le minimum opérationnel en mémoire (last 1000 décisions par device pour debug) si on veut un endpoint de "recent activity" ; à arbitrer en Phase 2.6 (UI dashboard).

### Pas d'audit log dans mcp-home

Conséquence directe : **pas de table `audit_log` SQLite, pas de fichier JSONL d'audit dédié**. Tout passe par OTel. Si le collector est down et que les exporters bufferisent, on perd les évènements au-delà du buffer — accepté comme contrainte (au pire, on rejoue avec un span manqué, mais pas de "trou de mémoire" structurel côté mcp-home).

### Suite ADR 0012

L'ADR 0012 est superseded **par deux successeurs** :

- ADR 0015 — pour la partie config (permissions, devices, fallbacks, hiérarchie, niveaux).
- ADR 0016 — pour la partie state (VDevices actifs : in-memory + JSONL ; pas d'audit en local ; pas de schéma SQLite).

Le statut de 0012 devient `Superseded by ADR 0015 and ADR 0016`. Son contenu reste lisible pour l'historique.

### Arborescence finale (rappel)

```
~/.butlr/
├── config/                              # git, yaml — ADR 0015
│   ├── home.yaml
│   ├── etage-rdc/...
│   ├── apps/...
│   └── permissions/...
└── state/                               # pas en git — ADR 0016
    └── vdevices.jsonl
```

Plus de `audit/`. Plus de `state.db`.

## Conséquences

### Positif

- **Séparation des préoccupations** : config (rare, lisible, versionnée) en git ; state (éphémère) en mémoire + JSONL ; observabilité (haute fréquence) en OTel. Chaque donnée chez l'outil le plus adapté.
- **Standardisation** : OpenTelemetry est le standard d'industrie. Un opérateur sait déjà comment l'exploiter ; on bénéficie de tous les outils existants (Grafana, Tempo, Loki, Jaeger…) sans rien câbler nous-mêmes.
- **Pas de stockage long-terme à concevoir** : la rotation, la rétention, la compaction de l'audit sortent du scope mcp-home. On émet, point.
- **Audit "natif" au sens OTel** : traces avec `traceparent` propagé, on peut suivre une intention de Carlson jusqu'au driver MQTT en un seul `trace_id`.
- **Boot rapide** : reconstruction in-memory depuis JSONL + reconstruction RealState depuis MQTT. Pas de migrations SQL, pas de lock DB.

### Négatif

- **Dépendance opérationnelle** : pour avoir l'historique d'audit, il faut un collector OTel + un backend (Loki par ex.). Pas blocant pour le fonctionnement nominal de mcp-home, mais c'est une stack tierce à exploiter. Au POC, l'utilisateur peut tourner sans collector — il perd alors l'historique mais l'orchestrateur fonctionne.
- **Pas d'audit local "consultable" sans backend** : si l'utilisateur n'a pas de Grafana/Loki en place, il n'a aucune vue historique. Mitigation : endpoint mcp-home `/recent_activity` qui expose les `~ 1000 dernières` décisions en mémoire (à finaliser Phase 2.6 — c'est un cache pour l'UI, pas un audit légal).
- **Perte au crash du collector** : OTLP avec exporter par défaut bufferise en RAM. Un crash du collector = perte du buffer. Acceptable pour de l'audit indicatif ; pas acceptable pour de l'audit légal — pas notre cas au POC.
- **Compaction JSONL à implémenter proprement** : sans compaction, le fichier `vdevices.jsonl` grossit linéairement (chaque renew = une ligne, soit `~ 5000 lignes/jour` ⚠). Compaction périodique nécessaire — tâche à part dans la Phase 2.2.

### Ouvert (Phase 3+)

- Endpoint `/recent_activity` propre côté UI : combien de décisions garder en mémoire, format de sortie, permissions de lecture.
- "Audit légal" si le projet quitte le mono-utilisateur : signature des évènements, persistance locale en plus d'OTel, retention obligatoire — ADR séparé, hors scope POC.
- Reconfiguration à chaud des niveaux (cf. ADR 0014) : impact sur le fichier JSONL — un VDevice posé sur un `tier_id` qui disparaît doit être migré, supprimé, ou marqué orphelin. À cadrer.

## Alternatives considérées

### A. Tout garder en SQLite (ADR 0012 inchangé)

Le statu quo. Rejeté : SQLite n'est pas le bon outil pour 10⁵ évènements/jour avec des besoins de recherche, dashboards, rétention. C'est techniquement faisable mais on réinvente Loki en moins bien.

### B. JSONL pour l'audit, pas OTel

Plus simple, mêmes propriétés append-only que les VDevices. Rejeté : on se retrouverait à écrire un système de rotation, recherche, agrégation maison. OTel + collector externe le fait gratuitement.

### C. SQLite pour les VDevices actifs, OTel pour l'audit

Hybride. Rejeté : SQLite pour ~ 200 lignes maximum est sur-dimensionné. JSONL atteint le même résultat avec moins de surface (pas de schéma à migrer, pas de connexion à manager). La compaction périodique est triviale.

### D. Rejouer les VDevices depuis OTel au boot

Tentation : si tout l'historique est en OTel, pourquoi un fichier local ? Rejeté : (1) ça fait dépendre le boot d'un service externe ; (2) la rétention OTel n'est pas garantie ; (3) latence de boot dégradée. Le snapshot JSONL est un cache local de l'autorité que **est** l'orchestrateur lui-même au runtime.

### E. Persistance Redis ou autre KV embarqué

Tentation : LiteDB, RocksDB. Rejeté : pas de gain par rapport à JSONL pour ce volume, dépendance supplémentaire. Trade-off mauvais.

### F. Audit en SQLite local **et** en OTel

Double-écriture pour redondance. Rejeté pour le POC : complexité, sources de divergence, peu de valeur ajoutée tant qu'on n'est pas dans un contexte légal/médical. Ré-ouvrable Phase 3+ si un déploiement le demande.

## Révisions

- **2026-04-25** — Création. Issue de la session du 2026-04-25 : Kevin tranche pour OpenTelemetry sur l'observabilité et l'audit, collector externe out-of-scope. Supersede partiellement ADR 0012 (state in-memory + JSONL ; audit en OTel ; plus de SQLite). Cosigne ADR 0015 sur la partition config/state.
