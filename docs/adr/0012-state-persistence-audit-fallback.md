# ADR 0012 — Persistance d'état, audit log, bootstrap et fallback

**Date** : 2026-04-25
**Statut** : Accepté

## Contexte

Le modèle VDevice (ADR 0007), son lifecycle (ADR 0008), les permissions (ADR 0009) et les drivers (ADR 0011) supposent **un état persistant**. Tel quel, l'orchestrateur est entièrement en mémoire. Si le service redémarre :

- Tous les VDevices actifs sont perdus.
- Les permissions accordées sont perdues.
- L'historique de qui a piloté quoi est perdu.
- L'état réel des devices n'est connu qu'au prochain message MQTT, ce qui peut prendre minutes ou heures pour des devices passifs.

La transparence promise par l'ADR 0007 (l'UI montre qui a fait quoi) n'existe **que si l'audit est persisté** — sinon elle est limitée à l'instant présent.

Enfin, deux cas limites de la session de conception restent ouverts :

- **Bootstrap sans VDevice** : que commande-t-on à un device au démarrage si aucune app n'a encore déclaré d'intention ?
- **Fallback** : que faire si un device perd toute couverture (toutes les apps qui le ciblaient ont disparu) ?

## Décision

### Persistance d'état — quatre stores distincts

Quatre familles d'état à persister, avec des contraintes différentes :

| Store | Contenu | Contrainte | Tech ⚠ |
|---|---|---|---|
| **Permissions** | Couples `(app_id, device_id, niveau, priorité_max, clusters_autorisés, statut)` | Lecture fréquente, écriture rare. Cohérence forte. | SQLite ✓ |
| **VDevices actifs** | Liste des VDevices vivants (id, app, device, niveau, priorité, valeur, TTL/persistance, dernier renew) | Lecture/écriture très fréquentes. Tolérant à la perte courte. | SQLite ✓ avec WAL, ou store en mémoire snapshotté périodiquement |
| **Audit log** | Append-only : toutes les commandes, résolutions, préemptions, erreurs, octrois/révocations de permission | Append-only, lecture rare (UI/debug). Volume potentiellement élevé. | SQLite append-only ou journal fichier rotaté |
| **État réel des devices** | Dernière valeur normalisée connue par device, état de santé | Cache : reconstruit depuis MQTT au boot. | Mémoire + snapshot SQLite optionnel |

✓ SQLite est le choix par défaut pour l'ensemble : zéro setup, embedded dans `mcp-home`, parfaitement suffisant pour un foyer ⚠. Si plus tard la volumétrie d'audit explose, l'audit pourra être basculé vers un journal séparé sans impacter le reste.

### Politique de rejeu au boot

Au démarrage de l'orchestrateur :

1. **Charger les permissions** depuis SQLite. C'est la source de vérité, immédiate.
2. **Charger les VDevices actifs** depuis SQLite. Pour chaque VDevice :
   - Si `niveau=2` et durée écoulée pendant le downtime → **purgé** (l'override n'est plus pertinent, l'utilisateur n'attend plus son effet).
   - Si `niveau=1` persistant → **ressuscité actif**, en attente du prochain renew dans une fenêtre de grâce élargie au boot (`~ 2 × heartbeat_interval_ms` ⚠).
   - Si `niveau=1` avec TTL → idem, purgé si le TTL est écoulé pendant le downtime.
3. **Reconstruire l'état réel** par abonnement MQTT — les drivers se reconnectent et republient ce qu'ils savent. Pendant la fenêtre de reconstruction (quelques secondes), l'orchestrateur n'envoie aucune commande, juste écoute.
4. **Recalculer la résolution** une fois l'état réel reconstruit.
5. **Appliquer la commande** seulement après que la reconstruction est jugée complète (timeout configurable, par défaut `~ 5 s` après le démarrage des drivers).

Cette politique évite deux pièges :

- **Burst de commandes au boot** (l'orchestrateur croit que tout est à recommander car son état réel est vide → surcharge MQTT, oscillation).
- **Override fantôme** (un niveau 2 expiré pendant le downtime se rejoue par erreur).

### Audit log — qui, quoi, quand, pourquoi

Chaque entrée d'audit contient au minimum :

```
timestamp_server    : datetime
event_type         : enum (vdevice_created, vdevice_renewed, vdevice_released,
                          vdevice_expired, vdevice_preempted,
                          command_sent, command_acked, command_failed,
                          permission_granted, permission_revoked, permission_modified,
                          resolution_recomputed)
app_id             : string | null
device_id          : string
vdevice_id         : string | null
level              : 1 | 2 | 3 | null
value_or_command   : json
winning_vdevice    : string | null
reason             : string | null
```

L'UI "qui propose quoi" est une vue construite à partir de cet audit + des VDevices courants. Sans audit persisté, l'UI ne peut pas répondre à "pourquoi mon chauffage a-t-il chauffé hier soir à 22°C ?".

Politique de rétention : ⚠ à finaliser, base à `90 jours` rolling avec compaction, à valider selon volume observé sur un foyer réel.

### Découverte de devices — annonces MQTT

L'ADR 0011 fixe que les drivers consomment les annonces MQTT (Z2M / ZWaveJS) pour peupler le registre de devices. Le **registre de devices** est lui aussi persisté (table SQLite), avec :

- `device_id` (logique, stable, attribué par Butlr)
- `external_id` (ce que Z2M expose)
- `clusters_supportés`
- `friendly_name` (modifiable par l'utilisateur)
- `pièce` (assignée par l'utilisateur, optionnelle)
- `status` (online/offline/unreachable)

Les clusters supportés sont déduits par le driver à partir des metadata Z2M (cf. ADR 0011).

### Bootstrap sans VDevice : aucun comportement par défaut

Au boot, si aucune app n'a déclaré d'intention sur un device et qu'aucun VDevice de fallback n'est configuré :

- L'orchestrateur **ne commande rien**. Le device reste dans son dernier état physique connu (par lecture MQTT ou son état hardware par défaut).
- C'est un **choix volontaire** : envoyer une "valeur neutre" risquerait d'allumer/éteindre/régler à 19°C des devices que l'utilisateur n'a pas explicitement configurés.

### Fallback : VDevice de niveau 1 priorité minimale

L'utilisateur peut **optionnellement** configurer une **app de fallback** par device (ou par classe de device) :

- Implémentée comme un VDevice **niveau 1** de **priorité minimale** (par convention : priorité 0 ou 1 ⚠ — à finaliser).
- Persistant (pas de TTL).
- Apparaît dans l'UI comme une app spéciale "Fallback Butlr".

Aucun cas spécial dans le code de l'orchestrateur : c'est un VDevice comme les autres, juste avec un statut spécial pour l'UI. Cohérent avec la décision ADR 0007 "tout est VDevice".

Si l'utilisateur ne configure pas de fallback, le comportement reste : aucune commande tant qu'aucune app n'écrit. Pas de magie.

## Conséquences

### Positif

- **Survie au redémarrage** sans surprise : VDevices niveau 1 persistants sont ressuscités, niveau 2 expiré purgé, état réel reconstruit avant toute commande.
- **Audit persisté = transparence réelle**, pas juste un dashboard de l'instant présent. Une question "pourquoi cette commande à 22h13 hier ?" trouve une réponse.
- **Fallback comme VDevice = pas de cas spécial dans le moteur.** Cohérence interne du modèle.
- **Découverte automatique des devices** via Z2M, peu de saisie manuelle.
- **SQLite suffit** au POC et probablement bien au-delà ✓ — pas de PostgreSQL/serveur DB à opérer.

### Négatif

- **Volumétrie audit non maîtrisée a priori.** Un foyer avec 50 devices, 10 apps, des renews fréquents peut produire `~ 100k–500k entries/jour` ⚠. Compaction et rotation à concevoir tôt, sinon SQLite va gonfler.
- **Cohérence transactionnelle SQLite vs MQTT.** L'arrivée d'un message MQTT et l'écriture en base ne sont pas atomiques. Acceptable au POC (state cache, on retombe sur ses pieds après reconstruction). À surveiller en cas de crash hard.
- **Politique de rejeu au boot complexe** à tester. Beaucoup de cas (heartbeat manqué juste avant boot, niveau 2 qui expire pendant le boot, device offline pendant tout le boot...). Tests d'intégration dédiés indispensables.
- **Pas de comportement de défaut** au boot si aucune app n'écrit — l'utilisateur doit configurer un fallback s'il veut un comportement de base. Acceptable mais doit être documenté dans l'onboarding.

## Alternatives considérées

### A. Tout en mémoire, pas de persistance

POC pur. Rejeté : un redémarrage = système amnésique, et c'est la situation que tous les utilisateurs ont vécue avec HA et qu'on combat. Persistance dès le jour 1 du wiring, pas en bonus.

### B. PostgreSQL embarqué

Plus scalable. Rejeté : surdimensionné pour un foyer, complexité opérationnelle injustifiée. Migration possible plus tard sans regret.

### C. EventStore / Kafka pour l'audit

Tentation pour traiter l'audit comme un event stream first-class. Rejeté : surdimensionné, coût opérationnel non justifié, pas de besoin de replay event-sourced au POC.

### D. Bootstrap avec valeur neutre (ex. luminosité 50%, thermostat 19°C)

Valeur de défaut "raisonnable" au boot. Rejeté : pas raisonnable du tout dans une salle de bain de personne âgée à 4h du matin. Le silence (rien commandé) est le défaut sûr.

### E. Fallback comme cas spécial moteur (pas un VDevice)

Plus rapide à implémenter. Rejeté : double sémantique dans le moteur, contraire au principe ADR 0007 "tout est VDevice". Le coût marginal de le traiter comme un VDevice est nul.

### F. Audit en mémoire avec dump périodique

Plus rapide en runtime. Rejeté : un crash = perte des dernières minutes d'audit, exactement les minutes intéressantes pour le debug. Append synchrone vaut le coût.

## Révisions

- **2026-04-25** — Création. Six décisions consolidées (persistance, rejeu, audit, découverte, bootstrap, fallback) parce qu'elles sont étroitement couplées et qu'un ADR par sujet aurait fait six fichiers redondants. Si l'un de ces sujets devient assez complexe pour mériter son propre ADR, il sera scindé en supersession partielle.
