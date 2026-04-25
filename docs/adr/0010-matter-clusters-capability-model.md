# ADR 0010 — Matter Clusters comme modèle de capacités

**Date** : 2026-04-25
**Statut** : Accepté

## Contexte

L'ADR 0007 introduit les VDevices ; les apps déclarent une **valeur d'intention** typée. Pour que cette valeur soit interprétable par l'orchestrateur et le driver sans hypothèse hardcodée, il faut un **modèle de capacités** standardisé : que peut faire un thermostat, quelles sont ses unités, ses plages, ses modes.

Trois familles de modèles existent :

- ✓ **Domains Home Assistant** (`climate`, `light`, `cover`, `sensor`, `switch`...). Schéma figé par domain. Largement adopté par la communauté HA, mais limité : capacités hors schéma passent en attributs custom non standardisés, sur lesquels les apps ne peuvent pas se reposer.
- ✓ **Matter Clusters** (CSA — Connectivity Standards Alliance, soutenu par Apple, Google, Amazon, Samsung). Schéma strict d'attributs et de commandes par cluster (`OnOff`, `LevelControl`, `Thermostat`, `WindowCovering`, `ColorControl`, `OccupancySensing`...). Ouvert, documenté, versionné, conçu pour interopérabilité multi-fournisseurs. ~ Spec publique sur csa-iot.org.
- ⚠ **Schéma maison** — concevoir une taxonomie propre à Butlr.

Sans modèle de capacités explicite, chaque app hardcode ses hypothèses sur le device — fragilité garantie au moment où on changera de marque de thermostat.

## Décision

Adopter les **Clusters Matter** comme modèle canonique de capacités pour les VDevices, drivers et apps de Butlr.

### Mapping concret

- Chaque **device logique** dans Butlr expose un ou plusieurs clusters Matter, exactement comme un endpoint Matter natif.
- Chaque **VDevice** déclare son intention en termes d'attribut/commande Matter, avec les unités et plages définies par la spec du cluster.
- Le **driver** (cf. ADR 0011) traduit dans les deux sens : du cluster Matter vers la commande MQTT spécifique au device réel (Zigbee2MQTT, ZWaveJS), et de l'état réel remonté vers les attributs Matter normalisés.

Exemples :

```
Thermostat salon (Zigbee Aqara via Z2M)
  └─ Cluster Thermostat (0x0201)
       ├─ Attribute OccupiedHeatingSetpoint (int16, 0.01°C)  ← VDevices écrivent ici
       └─ Attribute LocalTemperature       (read-only)        ← driver remonte la lecture

Ampoule salon (Zigbee Hue via Z2M)
  ├─ Cluster OnOff (0x0006)         ← Attribute OnOff
  ├─ Cluster LevelControl (0x0008)  ← Attribute CurrentLevel
  └─ Cluster ColorControl (0x0300)  ← multi-attributs
```

### Devices non couverts par Matter

Pour les devices sans cluster Matter natif équivalent (rares, mais existent), Butlr définit des **clusters custom** :

- Numérotés dans la **plage manufacturer-specific** réservée par la spec Matter (`~ 0xFC00–0xFFFE`, à confirmer dans la spec).
- Documentés dans `docs/clusters-custom.md` (à créer au moment du premier cluster custom).
- Conformes à la même convention syntaxique (attributs typés, commandes typées, unités, plages).

L'objectif est qu'un cluster custom puisse théoriquement, un jour, être proposé en upstream à la CSA — sans restructuration.

### Pas de double modèle

Butlr **n'expose pas de modèle alternatif** (pas de domain HA, pas de schéma maison à côté). Les drivers qui consomment Zigbee2MQTT (qui parle un dialecte propre proche des domains HA) traduisent **dans le driver** vers Matter — c'est leur rôle.

### Pas de dépendance au runtime Matter

⚠ Important : on adopte le **modèle de capacités** (les clusters) — pas le **runtime Matter**. Butlr **n'est pas** un Matter Controller, **n'expose pas** de fabric Matter au POC. On utilise les clusters comme schéma de référence, c'est tout. Si plus tard on veut intégrer le runtime Matter (Matter bridge, fabric, commissioning), ce sera un ADR séparé — la convergence est facilitée par ce choix de schéma, mais elle n'est pas implicite.

## Conséquences

### Positif

- **Pas de taxonomie à inventer.** Le travail de standardisation est déjà fait par la CSA et plusieurs centaines d'industriels.
- **Convergence avec l'écosystème industriel.** Apple, Google, Amazon, Samsung convergent vers Matter — ✓ tendance lourde 2024-2026 ~. Butlr est aligné sur l'avenir du secteur.
- **Future intégration native.** Le jour où on veut consommer un device Matter directement (sans Zigbee2MQTT), le mapping est trivial — c'est déjà notre format interne.
- **Documentation gratuite.** Chaque cluster est documenté sur csa-iot.org avec sa sémantique exacte. Les développeurs d'apps n'ont pas à attendre une doc Butlr.
- **Découplage app / fabricant.** Une app Cocooning écrit `Thermostat.OccupiedHeatingSetpoint = 21°C` et n'a aucune idée de la marque du thermostat, du protocole radio, ni du dialecte MQTT. C'est le driver qui absorbe les différences.

### Négatif

- **Verbosité.** Les noms de clusters Matter sont longs et techniques (`OccupiedHeatingSetpoint` au lieu de `target_temp`). À encapsuler dans le SDK client par langage pour ergonomie développeur.
- **Apprentissage initial.** Un développeur Butlr doit lire une partie de la spec Matter avant d'écrire sa première app. Mitigation : doc Butlr présente les clusters utilisés avec exemples et lie aux specs CSA.
- **Couverture incomplète.** Certains usages domestiques exotiques (machine à café, robot tondeuse, sauna) n'ont pas de cluster Matter. Nécessite des clusters custom — coût documentaire mais maîtrisable.
- **Versioning Matter.** ⚠ La spec Matter évolue (versions 1.0, 1.2, 1.4...). Butlr doit pinner une version de référence et migrer explicitement, pas suivre passivement.
- **Pas de ROI immédiat au POC.** Le POC actuel a deux tools `turn_on_light` / `turn_off_light` (cf. `architecture.md` §7.1) — Matter est massivement surdimensionné pour ça. La décision est prise pour la **Phase 2** (intégration MQTT), pas pour le POC. Le POC continue avec sa surface minimale ; on bascule sur Matter quand on introduit la couche VDevice + drivers.

## Alternatives considérées

### A. Adopter les domains Home Assistant

Plus connu de la communauté DIY, plus facile à introspecter (HA est ouvert et largement documenté). Rejeté :

- Couverture **incomplète et inconsistante** des capacités hors schéma standard (les attributs custom sont l'outil par défaut, ce qui défait l'intérêt).
- Lié à un produit (HA) qu'on ne veut pas placer comme dépendance amont — Butlr doit pouvoir vivre indépendamment.
- Aucun horizon de standardisation industrielle.

### B. Concevoir un schéma maison "Butlr Capabilities"

Maximum de contrôle. Rejeté :

- Coût massif de maintenance et de documentation pour réinventer ce que la CSA fait déjà.
- Aucun bénéfice tangible vs Matter pour notre usage.
- Aliène Butlr de l'écosystème — un ADR de regret quasi garanti à 2-3 ans.

### C. Hybride (Matter pour les capacités courantes, schéma maison pour le reste)

Tentation. Rejeté : deux modèles à apprendre, deux modèles à versioner, deux modèles dans les drivers. Les **clusters custom dans la plage manufacturer-specific** sont la voie propre : un seul modèle, extensible.

### D. Adopter le modèle Zigbee Cluster Library directement

C'est en fait la base historique de Matter (qui réutilise une grande partie de la sémantique ZCL). Rejeté : ZCL est plus bas-niveau (Zigbee-spécifique), Matter en est l'héritier multi-protocole. Autant prendre directement Matter.

## Révisions

- **2026-04-25** — Création. Adoption pour Phase 2 (couche VDevice). Le POC `architecture.md` §7 reste sur sa surface minimale et basculera vers le modèle clusters au moment du wiring du premier driver MQTT (cf. ADR 0011).
