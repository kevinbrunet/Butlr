# ADR 0008 — Lifecycle des VDevices : renew, TTL, fenêtre de grâce

**Date** : 2026-04-25
**Statut** : Accepté

## Contexte

L'ADR 0007 introduit les VDevices comme déclarations d'intention émises par les apps, niveau par niveau. Restent à préciser :

1. **Comment un VDevice naît, vit et meurt.** Sans règle stricte, une app qui plante laisse derrière elle un VDevice fantôme qui pèse indéfiniment dans la résolution.
2. **Comment garantir que les overrides utilisateurs (niveau 2) n'oublient pas l'utilisateur.** Un override à 24°C posé "pour quelques minutes" et oublié pendant trois jours est un scénario observé sur tous les systèmes domotiques existants.
3. **Comment préempter une app sans la laisser piloter à l'aveugle**, par exemple quand l'utilisateur change la priorité d'une autre app et que celle-ci prend le contrôle.

L'analogue que la communauté connaît bien est le pattern **JWT refresh token** : ✓ un token a un TTL court, le client le renouvelle activement, et un manquement de renew = expiration silencieuse mais déterministe.

## Décision

### Naissance d'un VDevice

Un VDevice est créé lors de la première déclaration d'intention d'une app sur un device. Cette première déclaration est un événement **autorisé par l'utilisateur** (cf. ADR 0009 sur les permissions). À la création, l'app fournit obligatoirement :

- Le device cible (par identifiant logique normalisé, cf. ADR 0010 sur les capacités Matter).
- Le **niveau** (1 ou 2 ; le niveau 3 est réservé système et n'est pas créable par une app tierce).
- La **valeur d'intention** (typée selon le cluster Matter du device).
- La **politique de durée**, obligatoire et explicite :
  - Au **niveau 2** : `duration_ms` obligatoire. Aucun défaut. Le système refuse une déclaration niveau 2 sans durée.
  - Au **niveau 1** : soit `persistent=true` (le VDevice ne meurt qu'à libération explicite, modulo le heartbeat), soit `ttl_ms` explicite. Aucun défaut.
- La **priorité demandée** (l'utilisateur peut downgrader, pas upgrader — cf. ADR 0009).

### Vie d'un VDevice : heartbeat / renew

Inspiration JWT refresh token :

- Chaque VDevice porte un `heartbeat_interval_ms` négocié à la création (par défaut court — `~ 30 s`, à finaliser au moment du wiring).
- L'app doit **renouveler activement** le VDevice avant que la fenêtre n'expire en envoyant un `renew(vdevice_id)`. Le renew remet à zéro le compteur sans modifier la valeur d'intention.
- Si l'app veut modifier sa valeur, elle envoie une `update(vdevice_id, new_value)` qui vaut implicitement renew.
- **Fenêtre de grâce** : le système accepte un renew arrivant jusqu'à `grace_ms` (`~ 5 s`, à finaliser) après l'expiration officielle. Au-delà, le VDevice est considéré expiré et libéré. Sans cette fenêtre, une app dont le timer dérive d'une milliseconde perd son VDevice de façon intermittente.

### Mort d'un VDevice

Un VDevice meurt dans les cas suivants :

- L'app le libère explicitement (`release(vdevice_id)`).
- Au **niveau 2**, `duration_ms` s'écoule sans renew (le niveau 2 a une durée bornée par construction — un renew au niveau 2 prolonge la fenêtre dans la limite du `duration_ms` posé à la création ⚠ — règle exacte à préciser dans la spec API).
- Au **niveau 1** non persistant, `ttl_ms` s'écoule sans renew.
- Au **niveau 1** persistant, le heartbeat manque pendant `heartbeat_interval_ms + grace_ms`.
- Le système préempte le VDevice (par décision utilisateur, par exemple révocation de permission ou changement de priorité — cf. ADR 0009).

À la mort d'un VDevice, l'orchestrateur recalcule immédiatement la commande device. Si l'app était en train de "gagner", la nouvelle commande peut différer brutalement (atténuation : inertie au driver, cf. ADR 0011).

### Notification de préemption

Quand un VDevice perd le contrôle sans être tué (ex. une autre app prend la priorité après changement de config utilisateur), l'orchestrateur émet un événement `vdevice.preempted(vdevice_id, reason)` à destination de l'app. L'app reste vivante (son VDevice est toujours là), mais elle sait qu'elle ne pilote plus le device.

C'est purement informatif — non bloquant pour l'orchestrateur, mais utile pour les apps qui veulent réagir (logger, désactiver leur logique, basculer en mode passif, etc.).

### Cas du niveau 2 : conflit simultané

Deux overrides niveau 2 quasi simultanés (vocal + UI mobile) sur le même device :

- **Départage par priorité utilisateur d'abord** (le niveau 2 garde une notion de priorité intra-niveau, ex. "user maître" > "user invité").
- **À priorité égale, départage par timestamp serveur** (pas timestamp client — drift d'horloge des clients = race conditions reproductibles).
- Le perdant reçoit une réponse explicite (`409 Conflict` ou équivalent dans l'API d'intention), pas un silence.

## Conséquences

### Positif

- **Pas d'états fantômes durables.** Une app qui plante perd son VDevice au plus tard `heartbeat_interval_ms + grace_ms` après le crash.
- **Pas d'overrides oubliés.** Le niveau 2 a une fin programmée par construction.
- **Renew = stratégie d'engagement.** Une app passive et oubliée se purge toute seule. Une app vivante qui veut maintenir son intention paie un coût explicite (renew réguliers).
- **Préemption observable.** Les apps savent quand elles perdent le contrôle, sans devoir poller.
- **Race conditions sur conflits niveau 2 résolues** par timestamp serveur, déterministe.

### Négatif

- **Coût de chatter MQTT/HTTP** côté apps niveau 1 persistantes — un renew toutes les `heartbeat_interval_ms`. À mesurer dans le contexte d'un foyer (50 devices × 10 apps × 1 renew/30s = 1000 messages/min ⚠, à valider). La fréquence devra peut-être être adaptative (`heartbeat_interval_ms` plus long pour les apps stables).
- **Sensibilité au drift d'horloge serveur.** Les TTL niveau 2 dépendent du temps serveur — pas un problème en single-server, plus délicat en HA multi-nœuds (Phase 3+ ⚠).
- **API d'intention plus chargée que `set/get`.** Une app doit savoir gérer create/renew/release et écouter `preempted`. Mitigation : SDK client par langage qui encapsule le boilerplate.

## Alternatives considérées

### A. Pas de heartbeat, libération uniquement explicite

Plus simple. Rejeté : un seul crash d'app pollue l'orchestrateur durablement, et l'utilisateur n'a aucun moyen de "réparer" sans redémarrer le système.

### B. Heartbeat passif par poll côté orchestrateur (ping app → app répond ou meurt)

Symétrique au renew actif. Rejeté : ça oblige l'orchestrateur à connaître l'adresse réseau de chaque app et à faire des appels sortants sur N apps. Le renew actif est unidirectionnel (apps → orchestrateur), beaucoup plus simple à scaler.

### C. TTL implicite par défaut au niveau 2 (ex. "2h si rien dit")

Tentation à pousser. **Rejeté explicitement par Kevin** lors de la session de conception : un défaut implicite est exactement ce qui crée des overrides oubliés. La règle dure est : niveau 2 sans `duration_ms` = la commande est refusée. Pas de magie, pas d'oubli silencieux.

### D. Pas de fenêtre de grâce

Plus pur. Rejeté : drift de millisecondes des timers d'apps = perte intermittente de VDevices, debugging cauchemardesque pour les développeurs d'apps tierces. La grâce est le minimum vital.

### E. Timestamp client pour départage des conflits niveau 2

Plus simple si on a confiance dans les clients. Rejeté : drift d'horloge non négligeable, et pire, un client malicieux peut antidater pour gagner. Le timestamp serveur est un choix défensif évident.

## Révisions

- **2026-04-25** — Création. Découle de l'ADR 0007. Les valeurs précises de `heartbeat_interval_ms` et `grace_ms` sont à finaliser au moment du wiring — listées comme paramètres `~` ici.
- **2026-04-25** — Patch suite à ADR 0013 (agents-utilisateur vs apps). Le contrat orchestrateur reste **inchangé** : niveau 2 sans `duration_ms` → refus dur. Précision : la **résolution de la durée** (calcul heuristique ou prompt utilisateur quand l'utilisateur n'a pas explicité de durée) est de la **responsabilité de l'agent-utilisateur** (Carlson, UI web/mobile, interrupteur niveau 2 cf. ADR 0013), pas de l'orchestrateur. Les apps autonomes ne peuvent pas émettre niveau 2 (cf. ADR 0009), donc cette question ne les concerne pas. Le **timestamp serveur** pour départage des conflits niveau 2 simultanés s'applique aussi aux niveaux 2 émis par agents-utilisateur ; le champ `actor_user_id` du payload (ADR 0013) sert au départage par priorité utilisateur **avant** le timestamp.
