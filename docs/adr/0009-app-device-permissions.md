# ADR 0009 — Modèle de permissions app/device au premier accès

**Date** : 2026-04-25
**Statut** : Accepté

## Contexte

L'ADR 0007 introduit les VDevices et les niveaux de priorité ; l'ADR 0008 leur lifecycle. Reste un sujet structurant : **qui a le droit de piloter quoi, à quel niveau, et comment l'utilisateur reste maître du système** sans être noyé sous les pop-ups.

Sans modèle de permissions explicite :

- N'importe quelle app peut se déclarer prioritaire 99 sur n'importe quel device. Aucun bénéfice de la couche d'arbitrage.
- L'utilisateur n'a aucune lisibilité sur "qui peut faire quoi". Lifecycle des permissions inexistant — une app désinstallée peut avoir laissé des permissions orphelines.
- L'authentification de l'app elle-même (vs un process malveillant qui imite App Cocooning) est indistincte de l'autorisation d'usage.

Le **modèle Android** (permissions par catégorie, demandées au premier accès, révocables) est une référence reconnue, qui s'aligne naturellement avec la couche VDevice.

## Décision

### Permission posée à la première déclaration d'intention

Quand une app déclare un VDevice sur un device pour la **première fois**, l'orchestrateur :

1. Bloque la création du VDevice tant que la permission n'est pas accordée.
2. Notifie l'utilisateur (UI ou vocal) avec : nom de l'app, nom du device, niveau demandé, priorité demandée.
3. Attend la réponse utilisateur : **autorisé** (avec niveau et priorité finaux), **refusé**, **différé** (rappel ultérieur).
4. Persiste le résultat dans le registre des permissions (cf. ADR 0012 sur la persistance).

Exemple d'invite (UI ou vocal) :

> « App Cocooning veut piloter votre thermostat du salon.
> Niveau demandé : Automatique (niveau 1).
> Priorité demandée : 60.
> Autoriser ? »

### Règles de downgrade et de niveau

- L'utilisateur peut **downgrader** la priorité demandée (ex. 60 → 30), pas l'upgrader. Une app qui a demandé 60 ne peut pas se retrouver promue à 80 sans s'auto-déclarer.
- L'utilisateur peut **descendre l'app d'un niveau** (passer une demande niveau 1 priorité 80 vers un niveau 1 priorité 30 ; ou refuser). Il ne peut pas la monter d'un niveau.
- **Une app ne peut pas s'auto-déclarer niveau 2.** Le niveau 2 est réservé à des intentions émises par l'utilisateur (UI, vocal, interrupteur configuré niveau 2) — cf. ADR 0007.
- **Le niveau 3 est réservé au système** et n'est jamais demandé par une app tierce. Il est posé par les détecteurs intégrés (CO, fumée, sécurité hardware), via des composants signés du système.

### Permissions persistantes mais révocables

Une fois accordée, la permission est mémorisée par le couple `(app_id, device_id)`. Elle inclut :

- Le **niveau autorisé** (1 ou 2).
- La **priorité maximum autorisée** dans ce niveau.
- Les **clusters Matter autorisés** (cf. ADR 0010) — une app peut avoir le droit de lire `Thermostat.OccupiedHeatingSetpoint` sans avoir le droit d'écrire `OnOff`.
- La **date d'octroi** et le statut courant.

L'utilisateur peut à tout moment, depuis l'UI ou par vocal :

- Révoquer la permission. Le VDevice actif est immédiatement libéré (préemption — cf. ADR 0008), un événement `vdevice.preempted(reason=permission_revoked)` est émis.
- Modifier la priorité maximum. Le **changement est immédiat** : l'orchestrateur recalcule la résolution à chaud. Une app qui était en train de gagner peut perdre le contrôle sans nouvelle déclaration de sa part — c'est un comportement **assumé**, signalé par `vdevice.preempted(reason=priority_changed)`.

### Lifecycle d'app

- **Installation** : pas de permission posée à l'installation. La première intention sur chaque device déclenche son propre prompt.
- **Mise à jour** : si une mise à jour étend le scope d'une app (nouveaux clusters, nouveaux devices ciblés), un nouveau prompt est requis pour les nouveaux scopes. Les permissions déjà accordées restent valides tant qu'elles n'élargissent pas.
- **Désinstallation** : nettoyage automatique de toutes les permissions de l'app et libération des VDevices actifs. Pas d'orphelins.

### Authentification de l'app elle-même

L'authentification (savoir que c'est bien App Cocooning qui parle) est un sujet distinct de l'autorisation. Au POC sur LAN privé, l'authentification reste basique (cf. ADR 0003 — bearer token partagé pour tout le système). Une **identité d'app** distincte (token signé par app, ou clé publique par app) est **explicitement reportée à un ADR ultérieur** quand le système sortira du LAN privé. ⚠ Ne pas confondre les deux : ce qui est tranché ici, c'est l'autorisation utilisateur ; l'authentification cryptographique des apps reste à concevoir.

## Conséquences

### Positif

- **L'utilisateur reste maître**, sans être noyé : un prompt par couple `(app, device)`, pas un par intention.
- **Lifecycle propre** : install / update / uninstall ont des règles claires, pas de permissions orphelines.
- **Lisibilité totale** : l'UI peut afficher la matrice `(apps × devices × niveau × priorité × clusters)` à tout moment.
- **Ne pas bloquer les power users** : une fois la permission accordée, plus de friction.
- **Cohérence avec le niveau de l'ADR 0007** : impossibilité d'auto-déclaration niveau 2 par construction, pas par convention.

### Négatif

- **Première utilisation chargée** : N apps × M devices = N × M prompts potentiels. Mitigation : grouper les prompts en batch quand une app déclare des intentions sur plusieurs devices à la fois (ex. "Cocooning veut piloter : thermostat salon, thermostat chambre, ventilation. Tout autoriser ?").
- **Complexité UI**. La matrice de permissions doit être présentée intelligemment — par device et par app, avec recherche. Sans ça l'utilisateur s'y perd.
- **Préemption non bloquante mais surprenante** pour les développeurs d'apps. Le canal `vdevice.preempted` doit être documenté de façon proéminente dans le SDK client, sinon "mon app ne marche plus" sera un bug récurrent.
- **Identité d'app non résolue.** Le modèle suppose qu'on sait quelle app parle ; en LAN privé avec bearer token unique, c'est un alignement contractuel, pas cryptographique. Acceptable au POC, à durcir Phase 2+.

## Alternatives considérées

### A. Pas de permission, confiance totale en le LAN privé

Cohérent avec l'ADR 0003 (bearer token partagé sur LAN). Rejeté : le problème ici n'est pas la sécurité périmétrique (le LAN est privé), c'est la **lisibilité fonctionnelle** pour l'utilisateur. Sans permissions, l'UI "qui propose quoi" ne peut pas dire qui a le droit de proposer.

### B. Permissions configurables uniquement via fichier de config (pas de prompt)

Modèle "ops first". Rejeté : Butlr cible un usage domestique, pas un cluster industriel. Le prompt à la Android est une exigence d'expérience utilisateur, pas une option.

### C. Permission par catégorie de device et non par device individuel

"Cocooning a accès à tous les thermostats." Plus rapide à configurer. Partiellement rejeté : c'est utilisable comme **shortcut UI** ("autoriser sur tous les devices similaires"), mais le **registre canonique** reste par device individuel pour pouvoir révoquer finement.

### D. Promotion de niveau possible par l'utilisateur

"L'utilisateur peut promouvoir App Cocooning au niveau 2." Rejeté : ça brouille la sémantique de l'ADR 0007. Le niveau 2 est par définition un masque temporaire émis par l'utilisateur ; promouvoir une app à ce niveau c'est créer une 4ème catégorie. Si une app doit toujours gagner, c'est qu'elle a une priorité haute au niveau 1, pas qu'elle change de niveau.

### E. Authentification cryptographique d'app dès le POC

Plus propre. Rejeté pour le POC : surdimensionné dans un contexte LAN privé mono-utilisateur. ADR séparé à ouvrir le jour où le système sort du LAN ou accepte des apps tierces non auditées.

## Révisions

- **2026-04-25** — Création. Issue de la session de conception du 2026-04-24. Ne traite **que** l'autorisation utilisateur ; l'authentification cryptographique des apps reste à un ADR ultérieur.
