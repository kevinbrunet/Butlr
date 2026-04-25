# ADR 0014 — Niveaux dynamiques, arbitres et admission par tags

**Date** : 2026-04-25
**Statut** : Accepté — supersede [ADR 0007](0007-virtual-device-arbitration.md)

## Contexte

L'ADR 0007 a posé le modèle d'arbitrage à **trois niveaux fixes** (1 = applications, 2 = override utilisateur, 3 = sécurité système). Trois enums, trois règles spéciales, codées en dur dans le moteur. Ce modèle a fonctionné le temps de cadrer les ADRs 0008-0013, mais il achoppe sur une limite structurelle : **chaque besoin nouveau d'une logique de résolution différente exige un nouveau niveau** — et ces niveaux n'existent pas dans le code.

Cas concret évoqué par Kevin lors de la session du 2026-04-25 : « plus tard, on pourrait avoir plusieurs niveaux pour les apps, chacun calculant sa propre valeur, et c'est l'ordre des niveaux qui dit qui gagne — comme ça chaque niveau a son propre arbitre, optimisable pour l'énergie, le côté smooth, ou autre. Un niveau = un arbitre. »

L'ADR 0007 ne permet pas ça : il y a **un seul** "niveau apps" et **un seul** arbitre attaché. Pour insérer un niveau « apps optimisé énergie » au-dessus d'un niveau « apps confort par défaut », il faudrait éditer le code et la doc.

Trois propriétés du modèle 0007 à préserver, indépendamment de la généralisation :

- L'ordre entre niveaux est **strict winner-takes-all** — pas de blending inter-niveaux.
- La **sémantique d'admission** (qui peut poser un VDevice dans quel niveau) reste explicite et documentée — l'ADR 0013 (agents-utilisateur vs apps autonomes) en dépend.
- La **politique de durée** (TTL obligatoire au niveau utilisateur) reste exprimable.

L'ADR 0014 généralise tout ça : un **niveau = un arbitre + une politique d'admission + une politique de durée + un flag bypass_inertia + un rang**. Les trois niveaux historiques deviennent une **configuration par défaut**, pas une vérité câblée.

## Décision

### Vocabulaire

- **Niveau** (`Tier` côté code) : entité de configuration nommée et ordonnée.
- **Arbitre** (`IArbiter` côté code) : fonction pure attachée à un niveau, qui calcule la valeur que ce niveau veut imposer à un device, à partir des VDevices admis dans ce niveau.
- **Résolution** (sans `r`majuscule) : le résultat de l'orchestrateur — la commande envoyée au driver. C'est la sortie du processus, pas un composant.

Le mot `IResolver` introduit par l'ADR 0007 et utilisé dans `vdevice-architecture.md` est **renommé `IArbiter`**. Tous les patches en aval l'appliquent.

### Anatomie d'un niveau

Un niveau est défini par :

| Champ | Type | Sémantique |
|---|---|---|
| `id` | string | Identifiant stable, ex. `safety`, `user-override`, `apps`, `comfort-blend` |
| `rank` | int | Ordre d'évaluation : **rang plus petit = plus haut**. Strict winner-takes-all : le premier niveau qui rend une valeur non-nulle gagne. |
| `arbiter` | enum/plugin | Identifiant de l'arbitre attaché : `winner-takes-all`, `user-priority-then-timestamp`, `strict-priority`, `weighted-average`, plugin custom… |
| `arbiter_config` | object | Paramètres de l'arbitre (poids, courbes, etc.) |
| `admission` | object | Politique d'admission (cf. ci-dessous) |
| `duration_policy` | object | Politique de durée (cf. ci-dessous) |
| `bypass_inertia` | bool | Si `true`, l'arbitre du driver applique la commande **sans** passer par la rampe d'inertie (cf. ADR 0011) |

### Strict winner-takes-all entre niveaux

L'orchestrateur évalue les niveaux dans l'ordre de `rank` croissant. Pour chaque niveau, il appelle l'arbitre avec les VDevices admis dans ce niveau et le `RealState?` du device. **Premier niveau qui retourne une valeur non-nulle gagne.** L'évaluation s'arrête.

Pas de blending **entre** niveaux. Le blending ne peut exister qu'**à l'intérieur** d'un niveau, via un arbitre comme `weighted-average`.

### Politique d'admission par tags

Chaque niveau déclare :

```yaml
admission:
  tags_required: [user_agent]   # liste de tags qui doivent TOUS être présents
```

Chaque VDevice porte une liste de tags posés à la création par le canal d'entrée :

- `actor_kind=app` → tag `app`.
- `actor_kind=user_agent` → tag `user_agent`.
- `actor_kind=system` → tag `system`.
- (extensible — Phase 3+ : tags fonctionnels comme `comfort`, `energy`, `presence-driven`, à déclarer dans le manifeste de l'app ou le profil de l'agent)

Règle d'admission au POC : **un VDevice peut être posé dans un niveau si le VDevice porte tous les tags listés dans `admission.tags_required`.** Si plusieurs niveaux acceptent les tags d'un VDevice, le **niveau de plus haut rang** (= plus petit `rank`) qui matche est choisi. Règle simple, suffisante pour le POC. Évolutive vers des prédicats plus riches si besoin (cf. Conséquences §"Ouvert").

### Politique de durée par niveau

```yaml
duration_policy:
  persistent_allowed: false      # un VDevice posé ici peut-il être persistent ?
  ttl_required: true             # la déclaration doit-elle porter un duration_ms ?
  ttl_max_ms: 86400000           # null = pas de cap ; sinon cap (ex. 24h)
```

Au moment de la création d'un VDevice, l'orchestrateur valide :

- Si `duration_policy.persistent_allowed=false` et la requête est `persistent=true` → **refus**.
- Si `duration_policy.ttl_required=true` et la requête n'a pas de `duration_ms` → **refus**.
- Si `duration_policy.ttl_max_ms` est défini et `duration_ms > ttl_max_ms` → **refus** (avec message explicite).

Cette politique remplace la règle hardcodée "niveau 2 = TTL obligatoire" de l'ADR 0008. Le contrat ne change pas en surface — il devient simplement configurable par niveau.

### Bypass d'inertie au niveau du niveau

Le flag `bypass_inertia: true | false` est porté par le **niveau**, pas par le VDevice. Un VDevice n'a aucun moyen d'altérer l'inertie du driver — c'est une propriété structurelle du niveau dans lequel il est admis. Cohérent avec la règle de l'ADR 0007 : seul le niveau de sécurité bypass l'inertie ; ici on rend le mécanisme configurable mais on garde la sémantique défensive.

### Configuration par défaut (preset "trois niveaux")

Le preset par défaut, à l'install, est exactement ce que décrivait l'ADR 0007 :

```yaml
tiers:
  - id: safety
    rank: 1
    arbiter: winner-takes-all
    admission:
      tags_required: [system]
    duration_policy:
      persistent_allowed: true
      ttl_required: false
    bypass_inertia: true

  - id: user-override
    rank: 2
    arbiter: user-priority-then-timestamp
    admission:
      tags_required: [user_agent]
    duration_policy:
      persistent_allowed: false
      ttl_required: true
      ttl_max_ms: 86400000      # 24h ⚠ — borne arbitraire de sécurité, à challenger
    bypass_inertia: false

  - id: apps
    rank: 3
    arbiter: strict-priority
    admission:
      tags_required: [app]
    duration_policy:
      persistent_allowed: true
      ttl_required: false
    bypass_inertia: false
```

Tout déploiement démarre avec ce preset. L'utilisateur (ou un installeur) peut ensuite éditer la config (cf. ADR 0015 sur le stockage en git) pour insérer des niveaux supplémentaires. Exemple typique d'évolution :

```yaml
  - id: comfort-blend
    rank: 4                       # entre user-override et apps si on shift apps en 5
    arbiter: weighted-average
    admission:
      tags_required: [app, comfort]
    duration_policy:
      persistent_allowed: true
      ttl_required: false
    bypass_inertia: false
```

### Reconfiguration

**Au POC : statique au démarrage**. Toute modification de la config des niveaux exige :

1. Arrêt de l'orchestrateur.
2. **Purge de tous les VDevices actifs** (ils peuvent référencer un `tier_id` qui a disparu, ou des `tags_required` qui ne matchent plus).
3. Redémarrage avec la nouvelle config.

Reconfiguration à chaud = Phase 3+ (à challenger : la migration des VDevices actifs sous changement de niveau est un sujet non-trivial).

### Admission et identification du `tier_id`

À la création d'un VDevice, le client fournit ou non un `tier_id` :

- **Si fourni** : l'orchestrateur vérifie que les tags du VDevice satisfont l'admission de ce niveau. Sinon refus.
- **Si non fourni** : l'orchestrateur **résout automatiquement** le niveau de plus haut rang dont l'admission matche les tags du VDevice. Si aucun niveau ne matche → refus.

La résolution automatique simplifie la vie des clients (un agent-utilisateur n'a pas besoin de connaître le nom du niveau `user-override`, il pose un VDevice avec `actor_kind=user_agent` et l'orchestrateur le route).

### Plugins d'arbitre

L'arbitre par défaut d'un niveau (`winner-takes-all`, `user-priority-then-timestamp`, `strict-priority`, `weighted-average`) est implémenté en `Butlr.VDevice.Core`. Les **arbitres custom** sont chargés depuis des assemblies .NET listées dans la config (cf. ADR 0015 — le manifeste d'assembly est un fichier yaml référencé).

C'est l'évolution de l'ancien "plugin de résolution niveau 1" de l'ADR 0007 : désormais **chaque niveau peut avoir son arbitre**, et il n'y a plus rien de spécial au niveau 1.

## Conséquences

### Positif

- **Plus de cas spéciaux** dans le moteur : niveau 3 = bypass_inertia true, niveau 2 = TTL obligatoire, niveau 1 = priorité stricte deviennent **trois lignes de config**, pas trois branches `if`.
- **Extensibilité réelle** : insérer un niveau "comfort-blend" entre `user-override` et `apps` est un edit de config + redémarrage, pas un patch de code.
- **Sémantique préservée** : la table de l'ADR 0007 §"État réel commandé" reste vraie pour le preset par défaut.
- **Cohérence avec ADR 0013** : la règle "agents-utilisateur seuls peuvent émettre niveau 2" devient "le niveau `user-override` n'admet que des VDevices avec tag `user_agent`" — exprimée par config, plus par hardcoding.
- **Tests plus simples** : chaque arbitre est une fonction pure testable en isolation. Le modèle "registre de niveaux" est trivial à fixturer.

### Négatif

- **Surface de configuration plus large** : un mauvais ordonnancement de `rank`, ou un `admission.tags_required` mal posé, peut produire un système incohérent (ex. niveau de sécurité après le niveau apps → catastrophique). Mitigation : validation au load-time, refus de démarrer si la config sent mauvais (rang non-unique, tag inconnu, arbitre inconnu, etc.).
- **Doc à maintenir** : la sémantique d'arbitrage n'est plus "lue dans le code" mais "lue dans la config". L'UI dashboard doit afficher la config des niveaux pour que ce soit visible (cf. patch `vdevice-architecture.md` §6).
- **Reconfiguration à chaud non triviale** : la migration des VDevices sous changement de niveau est ouverte. Pour le POC, on s'en sort en exigeant un redémarrage — assumé, mais frustrant si la maison tourne H24.
- **Compatibilité ADR 0008/0009/0013** : tous trois patchés par notes de révision. Pas de réécriture, mais le lecteur doit savoir lire `level=2` comme `tier=user-override`. Note explicite ajoutée dans chaque ADR.

### Ouvert (Phase 3+)

- Prédicats d'admission plus riches que des tags (ex. plage horaire, état présence, source réseau). À l'arrivée, garder la rétro-compatibilité avec la liste de tags simple.
- Reconfiguration à chaud avec migration des VDevices actifs (politique : purge ? remap ? rejeu ?).
- Arbitres dynamiques (chargement à chaud d'assemblies plugin).

## Alternatives considérées

### A. Garder les 3 niveaux fixes (ADR 0007 inchangé)

Le statu quo. Rejeté : la limite structurelle est claire (cas évoqué par Kevin), et la généralisation est peu chère **si** on la pose dès le départ. La rétrofitter sera douloureux : tout le code orchestrateur, les payloads, l'audit, l'UI sont posés sur "niveau ∈ {1,2,3}".

### B. Niveaux dynamiques mais arbitres globaux (un seul arbitre pour tout)

Plus simple. Rejeté : on perd le bénéfice principal — pouvoir attacher un arbitre **différent** par niveau (`weighted-average` à un niveau, `strict-priority` à un autre).

### C. Blending inter-niveaux

Un méta-arbitre qui combinerait les valeurs de plusieurs niveaux. Rejeté explicitement par Kevin : "strict winner-takes-all entre niveaux". Le blending reste une affaire **intra**-niveau, et la sémantique "qui gagne" reste lisible.

### D. Bypass d'inertie portable par le VDevice

Tentation : un VDevice "urgent" qui demanderait à bypasser l'inertie. Rejeté : n'importe quelle app malicieuse pourrait s'auto-déclarer urgente. Le bypass est une propriété **structurelle du niveau** — seul le niveau de sécurité bypass, par construction.

### E. Reconfiguration à chaud dès le POC

Plus impressionnant. Rejeté : la migration des VDevices actifs sous changement de structure de niveaux est un problème ouvert (que devient un VDevice posé sur un niveau qui vient d'être supprimé ?). Acceptable de l'éviter au POC en exigeant un redémarrage. Phase 3+ pour faire ça proprement.

### F. Hardcoder les arbitres en C#, pas de plugins

Plus simple. Rejeté : on est déjà à 4 arbitres de base, et le besoin Phase 2+ d'arbitres custom (énergie, confort, courbes utilisateur) est documenté dans la conversation source. Le mécanisme "plugin = assembly chargée par config" est trivial en .NET (déjà prévu pour les drivers et les arbitres dans l'ADR 0007).

## Révisions

- **2026-04-25** — Création. Issue d'une demande de Kevin lors de la session du 2026-04-25 : « la notion de niveau doit être dynamique. Un niveau = un arbitre. ». Supersede ADR 0007 — la table à 3 niveaux devient une config par défaut. Patche en aval ADRs 0008/0009/0013, `vdevice-architecture.md`, `tasks-vdevice-implementation.md`.
