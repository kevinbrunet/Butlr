# ADR 0007 — Virtual Device et arbitrage à trois niveaux

**Date** : 2026-04-25
**Statut** : Accepté

## Contexte

L'architecture actuelle de `mcp-home` (cf. `architecture.md` §3.2 et §11) prévoit, à terme, des backends concrets (`MqttBackend`, `HomeAssistantBackend`) implémentant `IDeviceBackend` derrière un mock console au POC. Ce design est suffisant pour valider la chaîne vocale + tool calling, mais il **ne résout pas le problème central** d'un système domotique multi-app : **plusieurs sources d'intention pilotent le même device, et leurs conflits sont silencieux**.

C'est le défaut structurel partagé par la quasi-totalité des plates-formes existantes :

- ~ Home Assistant : les automations s'écrivent contre le device, pas contre une intention. Quand deux automations veulent le même device, c'est la dernière qui s'exécute qui gagne, sans trace lisible pour l'utilisateur.
- ~ OpenHAB, Jeedom : même pattern.
- ✓ AWS IoT Device Shadow propose un modèle `desired/reported` propre, mais sans arbitrage **multi-sources** — il y a un seul desired par device.
- ⚠ Google Weave / Android Things avait commencé à formaliser une notion de traits + résolution de conflits, abandonné depuis.

Aucun système open source mature ne traite l'**arbitrage explicite** comme citoyen de première classe. C'est l'angle d'attaque de Butlr.

Côté protocole bas-niveau, la couche radio est résolue : ✓ Zigbee2MQTT et ✓ ZWaveJS exposent les devices sur un bus MQTT avec normalisation par fabricant. On n'a aucune raison d'écrire de Zigbee/Z-Wave nous-mêmes (cf. ADR 0011 sur les drivers).

## Décision

Introduire un modèle **Virtual Device (VDevice)** comme abstraction centrale entre les apps/plug-ins/automations et le device physique :

```
[App A]  → VDevice(intention A) ──┐
[App B]  → VDevice(intention B) ──┼──► [Orchestrateur] ──► [Driver] ──► [Device réel]
[User]   → VDevice(intention U) ──┘                ▲
                                                   │
                                          état réel & santé device
```

### Règles structurantes

1. **Une app ne voit jamais l'état réel.** Elle déclare une intention via son VDevice. L'orchestrateur seul lit l'état réel et la liste des VDevices actifs.
2. **Un VDevice est créé quand une app commence à piloter** et libéré quand elle ne pilote plus (cf. ADR 0008 pour la mécanique de renew/lifecycle).
3. **Les VDevices sont organisés en trois niveaux de priorité**, avec des sémantiques de résolution distinctes :

| Niveau | Sémantique | Résolution | Durée | Émetteurs autorisés |
|---|---|---|---|---|
| 3 — Sécurité / Urgence | Winner-takes-all absolu, **bypass l'inertie** du driver | Système uniquement | Jusqu'à libération système | Système (CO, incendie, coupure de sécurité) |
| 2 — Utilisateur / Manuel | Winner-takes-all, **durée obligatoire** (pas de défaut) | Priorité utilisateur, départage par timestamp serveur | Durée explicite obligatoire | User (UI, vocal, interrupteur configuré en niveau 2) |
| 1 — Apps automatiques | Configurable par device : priorité stricte par défaut, pondération possible via orchestrateur-plugin | Continu, toujours calculé | Persistant ou TTL explicite | Apps tierces, orchestrateurs-plugins |

4. **Le niveau 1 tourne en permanence en arrière-plan**, même quand un niveau 2 ou 3 le masque. C'est un flux continu, pas un état mis en pause.
5. **Le niveau 2 est un masque temporaire** posé par-dessus le calcul niveau 1. À l'expiration du masque, la valeur niveau 1 courante (déjà calculée) s'applique immédiatement. Pas de recalcul, pas de notion de "reprise".
6. **Niveau 3 bypass l'inertie** du driver — c'est une règle dure dans le contrat driver (cf. ADR 0011).
7. **Override manuel = VDevice niveau 2** avec priorité utilisateur et durée. Pas un cas spécial dans le code : juste une intention prioritaire qui expire.
8. **L'orchestrateur "pondération énergie" évoqué par Kevin n'est pas un arbitre global** : c'est un **plugin de résolution** qui remplace la règle par défaut au niveau 1, uniquement. Il ne touche jamais aux niveaux 2 et 3.

### État réel commandé

```
si un VDevice niveau 3 actif ─► commande = sa valeur (bypass inertie)
sinon si un VDevice niveau 2 actif et non expiré ─► commande = sa valeur
sinon ─► commande = résolution niveau 1 (priorité ou pondération selon la politique du device)
sinon (aucun VDevice actif) ─► aucune commande, sauf si un VDevice de fallback est configuré (cf. ADR 0012)
```

### Sémantique état vs commande

Le modèle s'applique aux **états continus** (luminosité, température, position de volet) et aux **switches binaires** lus comme états (on/off persistant). Les **commandes ponctuelles** (déclencher une alarme, jouer un son, envoyer une notification) **ne passent pas par ce modèle** — elles ont leur propre canal d'événements. Confondre les deux est la principale source de bugs documentée dans les systèmes similaires.

## Conséquences

### Positif

- **Arbitrage explicite et auditable.** L'UI peut afficher en temps réel : qui propose quoi, qui gagne, pourquoi. C'est le différenciateur fort vs Home Assistant.
- **Découplage app ↔ app.** Une app n'a pas connaissance des autres. Composabilité réelle.
- **Testabilité native.** Un VDevice est une déclaration d'intention pure ; on peut simuler des scénarios sans driver ni hardware.
- **Override manuel sans cas spécial.** Le niveau 2 est juste une intention prioritaire avec TTL — la même mécanique que les apps.
- **Plugin de résolution remplaçable.** L'orchestrateur "pondération énergie" devient un plugin de premier niveau, pas une refonte du noyau.

### Négatif

- **Complexité de moteur de règles.** L'orchestrateur devient un composant à concevoir avec soin (ordonnancement, race conditions, observabilité). Plus complexe que `IDeviceBackend.TurnOnLightAsync`.
- **Charge de doc et de gouvernance.** Chaque type de device doit avoir une politique de résolution documentée (priorité ou pondération + paramètres).
- **Apprentissage utilisateur.** Le concept de niveau et de priorité est plus exigeant qu'une automation "if X then Y". L'UI doit absorber cette complexité (cf. ADR 0009).
- **Pas de déprécation immédiate de `IDeviceBackend`.** Le mock console du POC reste valide ; l'introduction de la couche VDevice se fait en Phase 2 (cf. doc structuration et task list).
- **Décision sur le timestamp de départage**. À priorité égale au niveau 2, le départage est par **timestamp serveur** (pas client) pour éviter le drift d'horloge des clients.

## Alternatives considérées

### A. Garder `IDeviceBackend` simple, ajouter la résolution conflit ad hoc dans chaque tool

C'est l'évolution "naturelle" depuis le POC. Rejetée : on retombe exactement dans le défaut d'Home Assistant. Aucun gain de découplage, aucune transparence, on déplace juste le problème dans le code des tools MCP.

### B. Modèle AWS IoT Device Shadow (desired/reported) sans arbitrage multi-sources

Élégant pour un seul producteur d'intention. Rejeté : Butlr est explicitement multi-app par design ; un seul desired ne couvre pas le cas d'usage.

### C. Arbitre stateful avec apprentissage / pondération dynamique implicite

Approche "smart home avec ML qui apprend qui gagne". Rejeté : opacité totale pour l'utilisateur (l'inverse de l'objectif), debugging cauchemardesque, non-déterminisme. La pondération est possible **mais reste explicite et configurée** par l'utilisateur via un orchestrateur-plugin, pas apprise en silence.

### D. Single-level priority queue (un seul niveau, juste des numéros)

Plus simple à implémenter. Rejeté : on perd la garantie absolue du niveau 3 (sécurité), et on ne peut pas distinguer "override user temporaire" d'une "intention applicative permanente". Mélange dangereux.

### E. Mélanger priorité et pondération dans la même résolution

Tentation initiale. Rejeté car incohérent : si User à priorité 100 et apps à 40/60 avec pondération, doit-il court-circuiter ou entrer dans la pondération avec son poids ? Aucune réponse n'est intuitive. La séparation par niveau (winner-takes-all aux niveaux 2/3, pondération possible au niveau 1 uniquement) résout proprement.

## Révisions

- **2026-04-25** — Création. Issue d'une session de conception (Kevin × Claude) le 2026-04-24 sur un système domotique composable. Ouvre la voie aux ADRs 0008 (lifecycle), 0009 (permissions), 0010 (capacités Matter), 0011 (drivers MQTT), 0012 (persistance/audit/fallback).
