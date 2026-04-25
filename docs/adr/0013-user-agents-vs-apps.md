# ADR 0013 — Agents-utilisateur vs applications autonomes

**Date** : 2026-04-25
**Statut** : Accepté

## Contexte

Les ADRs 0007 à 0012 modélisent un système d'arbitrage où des **apps** déclarent des **VDevices** sur des **devices**, avec un modèle de permissions à la Android. Cette modélisation marche pour des apps autonomes (App Cocooning, App ChauffageEco) qui ont une logique propre, un état interne, des intentions persistantes ou périodiques.

Elle **ne marche pas telle quelle** pour Carlson, l'UI web, l'app mobile, ou un interrupteur physique configuré niveau 2. Ces composants ne sont **pas** des sources d'intention autonome — ils **transmettent l'intention de l'utilisateur**. Quand Kevin dit « Hey Carlson, allume la lumière », l'auteur de l'intention c'est **Kevin**, pas Carlson.

Cette confusion a une conséquence directe :

- Si Carlson est traité comme une app, il faudrait lui accorder une permission par device, et il accumulerait des VDevices niveau 1. Ce n'est ni le sens fonctionnel ni la sémantique souhaitée par Kevin (cf. session de conception 2026-04-25).
- L'ADR 0007 pose que **le niveau 2 est réservé aux intentions de l'utilisateur** ; l'ADR 0009 pose qu'**une app ne peut pas s'auto-déclarer niveau 2**. Ces deux règles se contredisent si Carlson est une app **et** émet des niveau 2.
- La traçabilité d'audit est dégradée : on perd le **canal** par lequel l'utilisateur s'est exprimé (vocal ? web ? mobile ? interrupteur ?), or c'est précisément l'information utile pour debugger un comportement inattendu.

Il faut donc une **deuxième catégorie** de citoyen dans le modèle : les **agents-utilisateur**.

## Décision

### Distinguer deux catégories de citoyens

| Catégorie | Émet | Niveau autorisé | Permission par device | Identité |
|---|---|---|---|---|
| **Application autonome** | VDevices niveau 1 | 1 uniquement | Oui (modèle Android, ADR 0009) | `app_id` (ex. `cocooning`) |
| **Agent-utilisateur** | VDevices niveau 2 (et 1, voir ci-dessous) | 1 et 2 | Non (au sens de l'ADR 0009) | `actor_user_id` + `via_agent_id` |

### Composants qui sont des agents-utilisateur

À jour 2026-04-25 :

- **Carlson** (assistant vocal). L'utilisateur parle, Carlson identifie le `actor_user_id` (au POC : utilisateur unique du foyer ; Phase 3 : speaker diarization, cf. `architecture.md §11`).
- **UI web** servie par `mcp-home`. Identification utilisateur par session (login local au POC).
- **App mobile** (Phase 3+). Identification par appairage.
- **Interrupteurs physiques configurés en niveau 2** (cf. ADR 0007 : un interrupteur peut être configuré soit niveau 1 persistent, soit niveau 2 à durée fixe). Quand niveau 2, il agit comme agent-utilisateur ; `actor_user_id` est anonyme ou attribué par contexte (pièce, présence détectée).

### Composants qui sont des apps autonomes

- App Cocooning, App ChauffageEco, ou toute logique d'automatisation tierce installée par l'utilisateur.
- Au sein de mcp-home, les **plugins de résolution niveau 1** sont aussi modélisés comme des apps spéciales (avec une identité `app:plugin:<nom>`).

### Modèle de payload d'intention

Toute déclaration d'intention envoyée à l'orchestrateur porte les champs suivants :

```
{
  "actor_kind": "app" | "user_agent",
  "actor_user_id": "kevin" | null,           // requis si actor_kind=user_agent
  "via_agent_id":  "carlson" | "ui-web" | "ui-mobile" | "switch:salon",
  "app_id":        "cocooning" | null,       // requis si actor_kind=app
  "device_id":     "...",
  "level":         1 | 2,
  ...
}
```

Conséquences au niveau de l'orchestrateur :

- Si `actor_kind=app` et `level=2` → **refus** (cf. ADR 0009).
- Si `actor_kind=user_agent` et `level=2` → **autorisé**, départage par `actor_user_id` priorité utilisateur puis timestamp serveur (ADR 0008).
- L'audit log persiste **les deux** : `actor_user_id` et `via_agent_id`. La vue "qui a fait quoi" peut filtrer par utilisateur, par canal, ou par les deux.

### Niveau 1 par un agent-utilisateur — cas particulier

Un agent-utilisateur peut **aussi** poser des VDevices niveau 1 — par exemple, l'UI web propose à Kevin de **changer durablement** la consigne d'un thermostat (pas un override temporaire, une nouvelle valeur de référence). Dans ce cas :

- L'intention reste `actor_kind=user_agent`, `actor_user_id=kevin`.
- L'orchestrateur la traite **comme un VDevice niveau 1** classique (cf. ADR 0007), avec `app_id` synthétique du genre `app:user-direct:kevin` pour la traçabilité dans le registre des apps.
- Pas de prompt de permission (l'utilisateur ne se demande pas l'autorisation à lui-même).
- Persistance ou TTL explicite, comme tout VDevice niveau 1 (ADR 0008).

Ce cas est l'équivalent VDevice du "set point manuel persistant" connu dans Home Assistant — sauf qu'il est explicitement traçable et révocable.

### Résolution de la durée pour les niveau 2 émis par agent

L'ADR 0008 maintient sa règle dure : **l'orchestrateur refuse un niveau 2 sans `duration_ms`**. Ce qui change ici, c'est que **la résolution de la durée est la responsabilité de l'agent**, pas de l'orchestrateur :

- **Carlson** : si l'utilisateur dit « allume la lumière » sans préciser la durée :
  - Soit Carlson **calcule** une durée selon une heuristique configurée (par device et par contexte temporel : « lumière du salon le soir » = jusqu'à minuit ; « radiateur en hiver » = 1 h).
  - Soit Carlson **demande explicitement** à l'utilisateur (« Pour combien de temps ? »).
  - **Jamais** Carlson n'émet un niveau 2 sans durée — il est rejeté par l'orchestrateur.
- **UI web / mobile** : un slider de durée est **obligatoirement présent** dans le contrôle d'override niveau 2. Pas de défaut implicite.
- **Interrupteur physique configuré niveau 2** : la durée est configurée **à l'enregistrement de l'interrupteur** (cf. ADR 0007 — décision Kevin : « pour les interrupteurs c'est configuré dans l'interrupteur »). À l'appui, le device crée un niveau 2 avec la durée pré-enregistrée.

Heuristique de calcul Carlson (hors scope de cet ADR — détaillée dans la spec de Carlson) : possible de pondérer par contexte de présence, plage horaire, type de device.

### Authentification des agents-utilisateur

L'ADR 0009 reportait l'authentification cryptographique d'**app**. Le sujet est **plus critique** pour les agents-utilisateur, car eux peuvent émettre du niveau 2 — il faut s'assurer qu'un process malveillant ne peut pas se faire passer pour Carlson et émettre des niveau 2 arbitraires.

Au POC sur LAN privé (cf. ADR 0003), même bearer token partagé que le reste — acceptable.

Phase 2+ : ADR séparé à ouvrir pour donner à chaque agent-utilisateur une **identité signée** distincte (clé asymétrique par instance d'agent, attestation au démarrage, révocable). C'est plus exigeant que pour les apps tierces, qui sont forcées niveau 1.

## Conséquences

### Positif

- **Cohérence sémantique restaurée.** La règle "niveau 2 réservé à l'utilisateur" est respectée par construction, plus de cas spécial dans le code.
- **Audit lisible.** Une question "pourquoi le thermostat est à 22 ?" trouve une réponse en deux temps : "à 18:33 Kevin a posé un override 22°C pour 2h — via Carlson en vocal".
- **Pas de pollution de permissions.** Carlson ne demande pas la permission de piloter chacun des 50 devices du foyer un par un. Il **est** l'utilisateur.
- **Symétrie naturelle entre canaux.** Carlson, UI web, UI mobile, interrupteur niveau 2 → même rôle, même payload, même trace.
- **Décharge l'orchestrateur** : la résolution de durée (UX) reste côté agent, le contrat orchestrateur reste minimaliste.

### Négatif

- **Identité d'agent à concevoir** plus tôt qu'un simple `app_id` cryptographique. Sujet à ouvrir Phase 2+.
- **Multi-utilisateur reste un sujet ouvert.** Au POC, `actor_user_id` est unique. Quand on ajoutera la diarization (Phase 3, cf. `architecture.md §11`), la priorité utilisateur intra-niveau 2 (ex. "Kevin maître" > "invité") prendra son sens — **prévoir le champ priorité dans le payload dès maintenant** pour éviter une migration de schéma plus tard.
- **Charge UX pour Carlson.** L'heuristique de calcul de durée et le pattern "demander quand on ne sait pas" sont à concevoir et tester avec soin — c'est là que se joue la qualité perçue de l'assistant vocal.
- **Cas limite : automation déclenchée par voix.** Si un jour Carlson permet à l'utilisateur de **créer des automations** vocalement (« quand je rentre, mets la lumière à 50% »), il faudra distinguer le cas "Carlson exécute pour Kevin" (niveau 2 ponctuel) du cas "Carlson installe une app au nom de Kevin" (création d'une app autonome niveau 1 dans le système). Hors scope POC, mais à garder en tête.

## Alternatives considérées

### A. Carlson est une app interne avec permission auto-octroyée niveau 2 sur tous les devices

Tentation initiale (mon premier jet, cf. tasks-vdevice-implementation.md §2.7.1 avant correction). Rejeté : viole l'ADR 0009 ("une app ne peut pas s'auto-déclarer niveau 2") et brouille la sémantique. Le niveau 2 cesse d'être "ce que l'utilisateur a fait" pour devenir "ce que Carlson a fait au nom de l'utilisateur, peut-être" — perte de lisibilité.

### B. Carlson en niveau 1 avec très haute priorité

Plus simple, contourne le problème de niveau. Rejeté : un override vocal n'est pas une intention persistante, c'est un acte ponctuel. Le mettre niveau 1 le ferait persister jusqu'à ce qu'une autre app le préempte — exactement ce que Kevin ne veut pas (« si je dis carlson allume la lumière, cela va passer comme un override temporaire »).

### C. Autoriser n'importe quelle app à émettre niveau 2 (suppression de la règle)

Plus libre. Rejeté : on perd la garantie sémantique du niveau 2, et n'importe quelle app malicieuse pourrait s'auto-déclarer prioritaire — exactement ce que la séparation par niveaux de l'ADR 0007 cherche à éviter.

### D. Modèle "OAuth scope" : Carlson tient un token utilisateur signé qui prouve son authority

Plus rigoureux, plus complexe. Rejeté pour le POC (LAN privé, single-household). À reconsidérer en Phase 2+ comme ADR séparé sur l'authentification.

### E. Pas de `via_agent_id`, juste `actor_user_id`

Plus simple. Rejeté : on perd la traçabilité du canal, qui est précisément l'information utile pour comprendre des comportements anormaux (« mon thermostat a été overridé — par Carlson ou par mon UI ? »).

## Révisions

- **2026-04-25** — Création. Issue d'une correction de Kevin lors de la session du 2026-04-25 : "carlson n'est pas une app comme les autres, il agit au nom d'un utilisateur". Patche en aval les ADRs 0008 et 0009 (notes de révision ajoutées dans ces fichiers) ainsi que `vdevice-architecture.md` et `tasks-vdevice-implementation.md`.
