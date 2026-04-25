# ADR 0011 — Drivers : adaptateurs MQTT entre VDevices et devices physiques

**Date** : 2026-04-25
**Statut** : Accepté

## Contexte

L'ADR 0007 introduit les VDevices et l'orchestrateur. L'ADR 0010 fixe les clusters Matter comme modèle de capacités. Reste à câbler tout ça au monde physique.

La couche radio domotique est un problème **résolu** :

- ✓ **Zigbee2MQTT** (Z2M) — pont Zigbee ↔ MQTT mature, normalise tous les devices Zigbee de centaines de fabricants en un dialecte MQTT cohérent. Apache 2.0. Très largement déployé.
- ✓ **ZWaveJS** — équivalent pour Z-Wave, expose aussi sur MQTT via `zwave-js-ui` ou `zwave-js-server`. Communauté plus restreinte mais mature.
- ⚠ **Matter** — runtime Matter natif (sans bridge MQTT) demande un commissioning et une fabric — coût d'entrée non négligeable. Reportable à Phase 2+.

`architecture.md` §3.2 mentionnait un `MqttBackend` derrière `IDeviceBackend`, et `MQTTnet` comme client MQTT. C'était la trajectoire Phase 2 — il faut maintenant la **structurer** autour de la couche VDevice et du modèle Matter.

## Décision

### Driver = adaptateur Matter cluster ↔ MQTT

Un **driver** dans Butlr est un composant qui :

1. **Lit le bus MQTT** (Zigbee2MQTT, ZWaveJS) pour un ou plusieurs devices physiques.
2. **Normalise les valeurs brutes** en attributs Matter selon le mapping de l'ADR 0010.
3. **Reçoit la commande logique** de l'orchestrateur (en cluster Matter).
4. **Traduit cette commande en messages MQTT** spécifiques au device réel et les publie.
5. **Gère l'inertie, l'arrondi, et l'état de santé** du device.

```
[Device Zigbee Aqara T1]
        ↑ Zigbee
[zigbee2mqtt service]
        ↑ MQTT topics zigbee2mqtt/<device>/...
[Bus MQTT Mosquitto]
        ↑ subscribe / publish
[Driver ButlrThermostatZigbee]
        - normalise: payload Z2M → Thermostat cluster
        - traduit:    Thermostat cluster → payload Z2M
        - inertie, arrondi, health
        ↑ Matter cluster API
[Orchestrateur VDevice]
```

### Granularité driver = par classe de device, pas par marque

Un driver gère **une classe de device** (`ThermostatDriver`, `LightDriver`, `CoverDriver`). C'est Zigbee2MQTT qui absorbe les différences fabricant en exposant un dialecte MQTT unifié. ✓ Z2M publie des metadata par device qui décrivent ses caractéristiques en JSON — le driver de classe consomme ces metadata pour s'adapter au device individuel.

Si une particularité fabricant fuit hors du dialecte Z2M (rare), elle est isolée dans une **stratégie spécifique** au sein du driver de classe, pas dans un driver dédié.

### Inertie paramétrable au driver

L'inertie répond au cas limite identifié dans la session de conception : un override niveau 2 expire pendant une transition, le device fait un retournement brutal vers la valeur niveau 1. La parade :

- Chaque driver expose un paramètre `inertia_ms` (ou plus structuré selon le device : rampe linéaire, pas minimum entre commandes, etc.).
- L'orchestrateur émet la commande logique ; le driver la **lisse** vers le device physique selon son inertie.
- **Niveau 3 bypass l'inertie**. Règle dure du contrat driver : un VDevice niveau 3 produit une commande **immédiate**, sans rampe, sans pas. Sécurité avant confort.

### Valeur logique normalisée dans le feedback loop

Quand le driver remonte l'état réel à l'orchestrateur (que ce soit via abonnement MQTT ou polling pour les devices passifs), il remonte la **valeur normalisée Matter** — pas la valeur brute du capteur.

Exemple : un thermostat MQTT publie `21.5` (lu sur Z2M en degrés Celsius). Le cluster Thermostat Matter exprime `LocalTemperature` en `int16, 0.01 °C` — donc la valeur normalisée est `2150`. C'est `2150` que l'orchestrateur reçoit, pas `21.5`. Sans cette normalisation, l'orchestrateur calcule des deltas erronés et le feedback loop est cassé.

### État de santé device first-class

Le driver remonte un **état de santé** explicite à chaque tick (ou à chaque changement) :

```
status: online | offline | degraded | unreachable
last_seen_at: timestamp
last_command_at: timestamp
last_command_acked: bool
error_count: int
```

L'orchestrateur **ne suppose jamais** qu'une commande envoyée a été appliquée — il attend un ack via le feedback loop ou marque le device degraded. Sans cette donnée, les scénarios "device offline" deviennent silencieusement cassés (cf. cas limite 4 de la session de conception).

### Politique d'erreur de commande

À documenter par driver, mais grandes lignes communes :

- **Retry** : N tentatives avec backoff exponentiel `~ 1 s, 2 s, 5 s` ⚠ — paramètres exacts à finaliser.
- **Notification orchestrateur** après échec définitif : événement `device.command_failed(device_id, command, error)` — propagé aux apps via `vdevice.preempted(reason=command_failed)` pour celles concernées.
- **Pas de rejeu silencieux** : une commande échouée n'est pas réessayée 24h plus tard parce que le device est revenu. L'orchestrateur recalcule à partir de l'état des VDevices courants — c'est le contrat (cf. ADR 0007).

### Règle absolue : MQTT mono-écrivain

**Rien d'autre que les drivers Butlr n'écrit sur MQTT.** Pas d'automation Home Assistant en parallèle qui publie sur les mêmes topics. Pas de Node-RED qui patche en douce. La règle est **architecturalement non négociable** : sinon la couche d'arbitrage est court-circuitée et on retombe dans le défaut qu'on cherche à éviter.

Home Assistant peut **lire** MQTT (mode observateur, dashboard) — c'est sans danger. Il ne doit **jamais publier** sur les topics que les drivers Butlr écoutent ou pilotent.

### Découverte automatique des devices

✓ Zigbee2MQTT publie des annonces de devices à la connexion (topic `zigbee2mqtt/bridge/devices`). Le driver de classe correspondant les détecte, lit les metadata, et déclare le device dans le registre Butlr. Pareil côté ZWaveJS.

L'orchestrateur n'a pas à connaître les devices à l'avance — il les apprend via les drivers.

### Pas de driver direct au runtime Matter au POC

L'ADR 0010 distingue **modèle de capacités** (clusters Matter) du **runtime Matter** (fabric, commissioning). Au POC, **aucun driver direct Matter** : on consomme tout via Zigbee2MQTT / ZWaveJS / autre pont MQTT. Si un cluster Butlr a besoin d'un device Matter natif, ADR séparé à ouvrir le moment venu (Matter Bridge, fabric Butlr, etc.).

## Conséquences

### Positif

- **On n'écrit pas un octet de Zigbee ou Z-Wave.** ✓ Z2M et ZWaveJS sont matures, on hérite de leurs centaines de devices supportés gratuitement.
- **Driver de classe = code minimal.** Un `ThermostatDriver` couvre tous les thermostats supportés par Z2M, sans connaître Aqara, Danfoss, Eve, etc.
- **Inertie sépare confort de sécurité.** Le confort (rampes douces) ne peut pas court-circuiter la sécurité (niveau 3 immédiat).
- **Feedback loop déterministe.** Toutes les valeurs sont normalisées Matter, plus de drift entre commandé et rapporté.
- **Découverte gratuite** via les annonces Z2M.
- **Single-writer MQTT** = arbitrage non court-circuité, garantie du modèle.

### Négatif

- **Discipline architecturale forte.** La règle "rien d'autre n'écrit MQTT" doit être tenue dans la durée, y compris après un "juste un test rapide". Un guide opérateur explicite doit être écrit (`docs/operations.md` à créer).
- **Dépendance à Z2M / ZWaveJS.** Si un de ces projets cesse d'évoluer, on hérite du problème. Mitigation : ce sont les standards de facto avec des communautés actives.
- **Pas de Matter natif au POC.** On consomme Matter-via-Zigbee, pas Matter-natif. Adoption Matter directe = Phase 2+.
- **Coût d'écrire les drivers de classe.** Au minimum : `LightDriver`, `ThermostatDriver`, `CoverDriver`, `SwitchDriver`, `OccupancySensorDriver`. ~ 2-4 semaines-personne ⚠ pour un premier set crédible.
- **Politique de retry à finaliser.** Les paramètres exacts (`~ 1s, 2s, 5s`) sont des extrapolations à valider en vrai.

## Alternatives considérées

### A. Driver direct Zigbee/Z-Wave (sans Z2M)

Théoriquement plus rapide. Rejeté :

- Énorme matrice fabricant à supporter, des années-personne.
- Aucun bénéfice par rapport à Z2M.
- Z2M est déjà sur le marché avec une qualité élevée.

### B. Driver natif Matter au POC

Plus pur architecturalement. Rejeté :

- Commissioning Matter et fabric maison = chantier majeur.
- Au POC, on a d'abord besoin de valider la couche d'arbitrage — pas d'investir sur le runtime Matter.
- Reportable sans regret, le modèle de capacités (clusters) est déjà aligné.

### C. Un driver par fabricant

"DriverAqaraT1, DriverDanfossEco". Rejeté : explosion combinatoire, oppose à l'esprit Z2M qui est précisément de masquer le fabricant.

### D. Lecture MQTT via Home Assistant comme proxy

Faire passer toutes les commandes par HA. Rejeté :

- HA devient un single point of failure.
- HA introduit sa propre couche d'automations qui peut entrer en conflit (exactement le problème qu'on combat).
- Pas de bénéfice opérationnel — Z2M parle MQTT directement.

### E. Pas de single-writer MQTT, on accepte les conflits

Pragmatique pour les utilisateurs venant de HA. Rejeté **fermement** : sape la valeur principale du modèle. Si quelqu'un veut garder son HA en pilote, alors Butlr n'est pas pour lui — il faut accepter le trade-off ou migrer.

### F. Driver passif sans inertie (orchestrateur = source unique de smoothing)

Plus simple côté driver. Rejeté : l'inertie dépend des contraintes hardware (pas minimum entre deux commandes, plage atteignable, mode chauffage/refroidissement) — c'est la responsabilité du driver, pas de l'orchestrateur. Sépare les responsabilités.

## Révisions

- **2026-04-25** — Création. Concrétise la trajectoire évoquée dans `architecture.md` §3.2 (`IDeviceBackend → MqttBackend`) en la remplaçant par le modèle driver/orchestrateur. Le mock console du POC reste valide jusqu'à l'introduction de la couche VDevice (Phase 2).
