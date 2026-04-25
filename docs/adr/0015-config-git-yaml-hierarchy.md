# ADR 0015 — Configuration hiérarchique en git, fichiers yaml, héritage par delta

**Date** : 2026-04-25
**Statut** : Accepté — supersede partiellement [ADR 0012](0012-state-persistence-audit-fallback.md) (partie config)

## Contexte

L'ADR 0012 posait **SQLite** comme stockage unique de tout ce qui doit survivre au redémarrage : permissions, registre des devices, VDevices actifs, audit log, fallbacks. Modèle simple mais qui fusionne quatre préoccupations distinctes :

1. **Configuration** (rare en écriture, lisible humainement, versionnable) — apps installées, permissions octroyées, niveaux d'arbitrage configurés (cf. ADR 0014), fallbacks par device, hiérarchie maison/étage/pièce/device.
2. **État éphémère** (mutation toutes les 30 s, volume modeste, perte tolérable au crash récent) — VDevices actifs, last_renew, real_state.
3. **Audit / observabilité** (append-only haute fréquence, volumétrie élevée, consommé par des outils dédiés) — décisions d'arbitrage, commandes envoyées, erreurs.
4. **Métadonnées de devices** (rare en écriture, alimenté par découverte Z2M) — `external_id`, `class`, capacités.

Mettre tout ça dans une seule SQLite mélange ces préoccupations. Lors de la session du 2026-04-25, Kevin a poussé une autre direction pour la **config** : un repo git de fichiers yaml organisés en arborescence de répertoires, qui matérialise la topologie de la maison et donne un historique versionné gratuit.

L'ADR 0015 acte ce choix pour la **configuration uniquement**. L'état éphémère et l'audit relèvent de l'ADR 0016.

## Décision

### Une arborescence git = une maison

La configuration vit dans un repo git local, par défaut à `~/.butlr/config/`. La structure de répertoires **est** la topologie de la maison :

```
~/.butlr/config/
├── home.yaml                        # template maison
├── etage-rdc/
│   ├── etage.yaml                   # delta étage
│   ├── salon/
│   │   ├── piece.yaml               # delta pièce
│   │   ├── thermostat-salon.yaml    # delta device
│   │   ├── lampe-salon-1.yaml
│   │   └── lampe-salon-2.yaml
│   └── cuisine/
│       └── ...
├── etage-1/
│   └── ...
├── apps/
│   ├── cocooning.yaml               # manifeste app installée
│   └── chauffage-eco.yaml
├── permissions/
│   └── cocooning__thermostat-salon.yaml
└── arbiters/
    └── (assemblies + manifests d'arbitres custom)
```

Chaque répertoire intermédiaire (`home.yaml`, `etage.yaml`, `piece.yaml`) porte les **deltas de config qui s'appliquent à tous les descendants**. Chaque fichier device (`thermostat-salon.yaml`) porte les **deltas spécifiques à ce device**.

`cd config/etage-rdc/salon/` : tu es dans le salon ; ce que tu vois là est la config qui s'applique au salon et à ses devices.

### Format yaml uniquement, pas de markdown

Décision tranchée par Kevin le 2026-04-25 : **yaml pur, pas de markdown avec frontmatter**. Le yaml porte la donnée structurée. Si une pièce ou un device demande de la documentation humaine, un `README.md` séparé peut être ajouté à côté — pas la responsabilité du parser de config.

Bénéfices :

- Parser unique (`~ YamlDotNet`, MIT — à valider versions précises au moment du wiring).
- Pas de mix data/prose dans un même fichier — moins de risque d'erreur de parsing.
- Schéma validable (json-schema généré depuis les types C#, applicable au yaml).

### Héritage par delta, pas merge sémantique

Chaque fichier yaml (sauf `home.yaml`) ne contient **que ce qui change par rapport au parent**. Le parent est implicite par la position dans l'arborescence :

- `home.yaml` est la racine — il porte la config par défaut complète.
- `etage-rdc/etage.yaml` porte les deltas vs `home.yaml`.
- `etage-rdc/salon/piece.yaml` porte les deltas vs (`home.yaml` ⊕ `etage-rdc/etage.yaml`).
- `etage-rdc/salon/thermostat-salon.yaml` porte les deltas vs la config résolue de la pièce.

Au démarrage, l'orchestrateur calcule, pour chaque device, sa **config résolue** = `home ⊕ étage ⊕ pièce ⊕ device`. L'opérateur `⊕` est une fusion **clef par clef** :

- Si une clef est présente dans le delta, elle remplace la valeur du parent.
- Si une clef est absente, la valeur du parent est conservée.
- Pour les listes (ex. liste des `tiers` configurés), pas de fusion automatique : on remplace toute la liste si elle est redéfinie. Si un override veut juste "ajouter un niveau", il doit redéfinir la liste complète (acceptable au POC, peut être amélioré par une syntaxe explicite `extends:` Phase 3+).

**Avantages d'un héritage explicite par delta** :

- Modifier `home.yaml` propage **automatiquement** aux devices au prochain reload — pas de merge à faire.
- Pas de conflit "git merge sémantique" entre template et override : git fait son merge **textuel** sur les fichiers de delta, ce qui reste trivial dans la quasi-totalité des cas.
- Détection de conflits **sémantiques** (un override pointe une clef disparue du parent) au load-time, avec warning explicite. Politique POC : `warning + ignore` (l'override orphelin est laissé inactif, l'utilisateur est notifié dans l'UI).

**Pourquoi pas un git-merge sémantique custom ?** Implémentable, mais bien plus complexe (3-way merge sur structures yaml typées). Le modèle delta atteint le même résultat fonctionnel avec une mécanique purement déterministe.

### Modèle de fichier device — exemple

```yaml
# config/etage-rdc/salon/thermostat-salon.yaml
device_id: thermostat-salon
external_id: zigbee2mqtt/0x00158d00012c4321
class: thermostat                          # référence ADR 0010 / 0011
class_clusters: [Thermostat]

# Delta vs config résolue de la pièce
tier_overrides:
  apps:
    arbiter: weighted-average              # remplace l'arbitre strict-priority par défaut
    arbiter_config:
      weights:
        cocooning: 0.6
        chauffage-eco: 0.4

fallback:
  enabled: true
  cluster: Thermostat
  attribute: OccupiedHeatingSetpoint
  value: 1900                              # 19,00 °C en int16
```

### Modèle de fichier maison — exemple

```yaml
# config/home.yaml
schema_version: 1

# Niveaux par défaut (preset 3-niveaux d'ADR 0014)
tiers:
  - id: safety
    rank: 1
    arbiter: winner-takes-all
    admission: { tags_required: [system] }
    duration_policy: { persistent_allowed: true,  ttl_required: false }
    bypass_inertia: true
  - id: user-override
    rank: 2
    arbiter: user-priority-then-timestamp
    admission: { tags_required: [user_agent] }
    duration_policy: { persistent_allowed: false, ttl_required: true, ttl_max_ms: 86400000 }
    bypass_inertia: false
  - id: apps
    rank: 3
    arbiter: strict-priority
    admission: { tags_required: [app] }
    duration_policy: { persistent_allowed: true,  ttl_required: false }
    bypass_inertia: false

fallback:
  enabled: false                            # par défaut, pas de fallback global
```

### Apps et permissions — fichiers yaml

Une app installée = un fichier yaml dans `config/apps/<app_id>.yaml` :

```yaml
app_id: cocooning
name: "App Cocooning"
version: "1.0.3"
manifest_url: "https://..."                 # optionnel
default_tags: [app, comfort]                # tags portés par les VDevices émis par cette app
```

Une permission octroyée = un fichier yaml dans `config/permissions/<app_id>__<device_id>.yaml` :

```yaml
app_id: cocooning
device_id: thermostat-salon
tier_max: apps                              # niveau maximum admissible (cf. ADR 0014)
priority_max: 60
clusters_allowed: [Thermostat]
status: granted
granted_at: 2026-05-01T10:32:18Z
granted_by: kevin                           # Phase 3+ : multi-utilisateur
```

Octroyer une permission = créer ce fichier + commit. Révoquer = mettre à jour `status: revoked` ou supprimer le fichier (à arbitrer dans la Phase 2.3 — préférence : update + commit pour garder la trace ; on lit `status` à chaque load).

### Outillage côté code .NET

- **API git** : `~ LibGit2Sharp` (MIT, mature). Init au premier démarrage si le dossier n'existe pas, commit après chaque mutation, pas de push automatique (l'utilisateur ou l'opérateur configure un remote s'il veut un backup).
- **Parsing yaml** : `~ YamlDotNet` (MIT). Désérialisation typée vers les records du `Butlr.VDevice.Core`.
- **Validation** : json-schema généré depuis les types C# au build time, appliqué au load-time. Refus de démarrer sur config invalide, message explicite avec chemin du fichier en faute.

### Commits

Chaque mutation de config produit un commit :

- Octroi de permission : `commit -m "permission(cocooning,thermostat-salon): grant tier_max=apps priority_max=60"`.
- Modification de fallback : `commit -m "fallback(thermostat-salon): set 19°C"`.
- Ajout de niveau : `commit -m "tiers: insert comfort-blend at rank 4"`.

Auteur des commits : `butlr-orchestrator <orchestrator@butlr.local>` au POC. Phase 3+ : usurper l'identité de l'utilisateur déclencheur (`actor_user_id`), avec signature.

### Reload

Au POC, **pas de hot-reload**. Toute modification de config exige un redémarrage de l'orchestrateur. Cohérent avec ADR 0014 (reconfig des niveaux = redémarrage et purge des VDevices).

Phase 3+ : un watcher filesystem peut déclencher un reload partiel (changement d'un fichier device → recompute la config résolue de ce device sans toucher au reste). À cadrer.

## Conséquences

### Positif

- **Lisibilité de la maison** : `tree config/` dessine la topologie. `cat etage-rdc/salon/thermostat-salon.yaml` donne tous les overrides. Pas besoin d'UI pour comprendre l'état.
- **Versioning natif** : `git log -- etage-rdc/salon/thermostat-salon.yaml` dit qui a changé quoi quand. Bonus inattendu : audit de configuration **gratuit**.
- **Backup natif** : `git push origin main` vers un remote (NAS, GitHub privé) = backup complet de la config. Pas un système de backup à concevoir.
- **Diff et review** : un changement de config peut être proposé via PR, revu par l'utilisateur ou un installeur, mergé. Hors scope POC, ouvre des perspectives.
- **Pas de schéma SQL à migrer** : ajouter un champ à un device = ajouter une clef yaml. Migrations gérées par la `schema_version` au top du yaml et un mapper au load-time.

### Négatif

- **Pas adapté au volume** : git ne fait pas l'audit haute fréquence (cf. ADR 0016 — audit en OTel, pas en git). La config est un usage **rare en écriture** (≤ qq commits/jour au pire) ; au-delà, le repo gonfle et `git log` devient lent.
- **Concurrence d'écriture** : si l'UI dashboard et un script CLI éditent en même temps, lock applicatif nécessaire. Au POC, single-process — pas un problème immédiat.
- **Coût de démarrage** : parsing yaml + git open au boot (~ < 1 s pour ~ 50 devices, à mesurer). Acceptable.
- **Conflits sémantiques de delta** non détectables sans validation explicite : un override pointant un champ disparu du parent passe à travers d'un git merge mais doit être détecté à load-time. Validation obligatoire — un test de health "config orphan keys" dans la suite.
- **Migration ADR 0012** : toute la partie "schéma SQLite" de 0012 disparaît côté config. ADR 0012 est superseded par 0015 (config) et 0016 (state + audit).

## Alternatives considérées

### A. Tout en SQLite (ADR 0012 inchangé)

Le statu quo. Rejeté : (1) on perd le versioning, (2) on perd la lisibilité filesystem, (3) on traite la config (rare) et l'audit (haute fréquence) avec le même outil sans bénéfice. La séparation "config en git, audit en OTel, state en mémoire+JSONL" est plus alignée sur les natures de chaque donnée.

### B. JSON au lieu de yaml

Plus rigoureux (pas d'ambiguïté yaml). Rejeté : moins lisible humainement, pas de commentaires inline. La config doit pouvoir être éditée à la main occasionnellement — yaml gagne sur l'ergonomie. Validation par json-schema reste possible (yaml = json structurellement).

### C. TOML

Lisible, sans les pièges yaml. Rejeté : écosystème moins fourni en .NET, moins idiomatique. Pas de gain décisif vs yaml dans ce contexte.

### D. Markdown avec frontmatter yaml

Mon premier jet. **Rejeté explicitement par Kevin** le 2026-04-25 : "on opte pour le yaml et pas du md". Si une pièce demande de la doc humaine, un `README.md` à côté suffit — pas besoin de mélanger.

### E. Git merge sémantique custom (template ⟷ override en 3-way)

Plus puissant. Rejeté : complexité d'implémentation (parsing, diff structurel, résolution de conflit). Le modèle d'héritage par delta atteint le même résultat avec un déterminisme bien plus fort.

### F. Un fichier yaml unique pour toute la maison

Plus simple à parser. Rejeté : on perd la cartographie filesystem, qui est une partie clé de la valeur. Avec 50 devices, le fichier deviendrait illisible.

### G. Stockage relationnel hybride (config en SQLite avec export yaml)

Tentation : SQLite pour l'I/O, yaml pour la lecture humaine. Rejeté : complexité, double source de vérité, risque de divergence. Quand la lisibilité est l'objectif, `yaml + git` est plus direct.

## Révisions

- **2026-04-25** — Création. Issue de la session du 2026-04-25 : Kevin pousse pour un repo git de yaml hiérarchiques au lieu de SQLite pour la config. Supersede partiellement ADR 0012 (sa partie "schéma SQLite config" disparaît). Cosigne ADR 0014 sur le format de la config des niveaux (yaml `tiers:`).
