# Butlr — Architecture

> Majordome vocal local, open source, piloté à la voix, qui contrôle la maison via un serveur MCP.

**Marqueurs de confiance** utilisés dans ce doc :
- ✓ connaissance fiable
- ~ approximatif, à vérifier avant d'en dépendre
- ⚠ extrapolé, ne pas utiliser sans vérification

---

## 1. Objectifs et non-objectifs

### Objectifs
- Conversation vocale naturelle en FR et EN, latence perçue la plus faible possible.
- 100 % local, 100 % open source (code et poids des modèles), pas d'appel cloud sur le chemin chaud.
- Le LLM peut appeler des outils exposés par un serveur MCP dédié au pilotage de la maison.
- Démarrer mono-machine, prévoir la montée à des satellites par pièce sans refonte.
- Verbalisation d'attente pendant les opérations longues — l'utilisateur n'entend jamais de silence anormal.

### Non-objectifs (volontaires, cette phase)
- Pas de chiffrement end-to-end entre satellites (LAN de confiance au début).
- Pas de multi-utilisateurs identifiés par voix (single-household).
- Pas de persistance mémoire longue terme entre sessions au MVP.
- Pas de mode hors-ligne total pour le fetch d'infos externes (météo, etc.) — les outils qui tapent le web tapent le web.

---

## 2. Topologie globale

Deux projets dans le même repo, **monorepo polyglotte** (cf. ADR 0001) :

```
Butlr/
├── carlson/          # Python — agent vocal Pipecat : wake word → STT → LLM → TTS, client MCP
└── mcp-home/         # .NET 10 — serveur MCP : outils de pilotage de la maison
```

Carlson est client MCP. mcp-home est serveur MCP **long-running**, lancé indépendamment (service systemd / Windows Service / `dotnet run` en dev). Au démarrage, Carlson ouvre une connexion **SSE/HTTP** vers mcp-home (`MCP_HOME_URL`, défaut `http://localhost:5090/mcp` ~), s'authentifie par bearer token (`MCP_HOME_TOKEN`), liste les tools exposés et les traduit en function schemas OpenAI-compatible pour le LLM local. Un tool_call émis par le LLM est proxifié vers mcp-home via ce même canal HTTP (cf. ADR 0003).

La topologie cible est **Carlson satellite ↔ mcp-home central**. On assume cette topologie dès le POC pour éviter une migration transport ultérieure, même si les deux processus tournent souvent sur la même machine au démarrage.

### Pourquoi deux projets séparés (et pas un seul)
- **Découplage matériel** : Carlson est GPU-bound (LLM + Whisper), mcp-home est lightweight. On peut héberger mcp-home sur un mini-PC / Raspberry Pi central sans GPU et bouger Carlson sur la machine GPU, sans couplage.
- **Responsabilité unique** : mcp-home est un service stateless (ou presque) qui répond à des appels MCP ; Carlson est un pipeline audio temps réel. Contraintes d'ops et cadences de release différentes.
- **Réutilisation** : mcp-home peut être branché sur un autre client MCP (Claude Desktop en mode HTTP ~, un agent textuel, une CLI d'admin) sans toucher au pipeline vocal.
- **Découplage langage** : Pipecat n'existe qu'en Python ; le reste du savoir-faire de Kevin est en .NET. La frontière MCP isole ces deux mondes — JSON-RPC sur HTTP, zéro couplage de runtime (cf. ADR 0005).

---

## 3. Stack technique

### 3.1 Carlson (pipeline vocal)

| Brique | Choix | Justification courte | Licence |
|---|---|---|---|
| Orchestration | Pipecat | Frame-based, conçu pour temps réel audio, gère VAD + interruption + function calls. | BSD-2 ✓ |
| Wake word | openWakeWord | Open source intégral, modèles custom entraînables à partir de synthèse TTS. | Apache 2.0 ✓ |
| VAD | Silero VAD | Intégré à Pipecat, très faible empreinte. | MIT ✓ |
| STT | faster-whisper (CTranslate2) | Whisper large-v3 sur GPU NVIDIA, streaming. | MIT ✓ |
| LLM | Qwen 2.5 7B Instruct | Voir §3.3. Très bon tool calling, bilingue FR/EN, rentre confortablement en 10 GB VRAM. | Apache 2.0 ✓ |
| Serveur LLM | llama.cpp `llama-server` (CUDA) | Overhead mémoire minimal, GGUF très efficace, endpoint OpenAI-compatible. Mieux adapté que vLLM à 10 GB VRAM + un seul utilisateur (cf. ADR 0006). | MIT ✓ |
| TTS MVP | Piper | Latence basse, FR corrects, packaging trivial. | MIT ✓ |
| TTS upgrade | Coqui XTTS v2 ou Kokoro | Qualité supérieure, voice cloning. Vérifier licence avant commercialisation. | XTTS : Coqui Public Model License ~ ; Kokoro : Apache 2.0 ✓ |

### 3.2 mcp-home (serveur outils maison, .NET 10)

| Brique | Choix | Licence / confiance |
|---|---|---|
| Runtime | .NET 10 LTS | ~ sortie nov 2025, pattern MS versions paires = LTS 3 ans |
| Langage | C# | ✓ idiome dominant de l'écosystème |
| SDK MCP | `ModelContextProtocol` (csharp-sdk officiel) | ~ nom et maturité à pinner à l'install |
| Hosting | ASP.NET Core + Generic Host (Kestrel pour le HTTP) | ✓ MIT |
| Transport MCP | **SSE/HTTP d'emblée**, service long-running — pas de stdio (cf. ADR 0003) | ✓ |
| Auth MVP | Bearer token partagé (`MCP_HOME_TOKEN`), LAN privé, pas de TLS au POC (cf. ADR 0003) | ~ |
| DI / Config / Logging | Natifs via Generic Host (`IServiceCollection`, `appsettings.json`, `ILogger`) | ✓ |
| Bus domotique | `MQTTnet` sur broker Mosquitto | ✓ MIT |
| Home Assistant (phase 3) | `HttpClient` sur l'API REST/WebSocket HA ; NetDaemon comme alternative opinionated | ✓ ; NetDaemon ~ |
| Registry YAML | `YamlDotNet` | ✓ MIT |
| Tests | xUnit + `Microsoft.NET.Test.Sdk` | ✓ |
| Packaging | `dotnet publish -c Release --self-contained -p:PublishSingleFile=true`, déployé en service systemd / Windows Service | ⚠ taille ~30–50 Mo à mesurer |
| AOT (opti future) | `-p:PublishAot=true` pour binaire compact + cold-start quasi nul | ~ compatibilité à vérifier par dépendance |
| Abstraction | `IDeviceBackend` pluggable : `MockBackend` → `MqttBackend` → `HomeAssistantBackend` |

### 3.3 Choix LLM pour 10 GB VRAM

VRAM cible : 10 GB. Ça exclut les modèles 14B et plus en quantisation raisonnable. On reste en famille 7–8B paramètres.

**Choix retenu** : Qwen 2.5 7B Instruct, quantisation **GGUF Q5_K_M** via llama.cpp server.

Budget mémoire estimé (⚠ à mesurer sur ta config) :

| Poste | Taille |
|---|---|
| Poids Q5_K_M (7,6 B params) | ~5,4 GB ~ |
| KV cache @ context 8k | ~2 GB ~ |
| Overhead CUDA / buffers | ~1 GB ⚠ |
| **Total** | **~8,4 GB**, marge ~1,6 GB sur 10 GB |

Fallback si tension : descendre à **Q4_K_M** (~4,7 GB poids ~), ce qui libère ~0,7 GB. La perte de qualité Q5 → Q4 est mesurable mais pas catastrophique sur du dialogue conversationnel ~.

**Pourquoi Qwen 2.5 7B et pas autre chose**
- **Bilingue FR/EN** solide dès la 7B. ✓ FR courant ; ~ idiomes rares.
- **Tool calling** natif, format bien parsé par llama.cpp, compatible schéma OpenAI. ~ qualité comparable à Llama 3.1 8B sur les benchs publics.
- **Licence Apache 2.0** ✓.

**Alternative à garder en tête** : **Hermes 3 Llama 3.1 8B** (NousResearch), fine-tuné spécifiquement pour le function calling. Empreinte mémoire quasi identique. Licence Llama 3.1 Community (~ OK usage perso, à revérifier si autre usage). À tester en A/B si le tool calling de Qwen déçoit en pratique.

**Context window** : on cible 8k au POC. Au-delà (16k, 32k) le KV cache explose — à évaluer seulement si une conversation longue devient un vrai cas d'usage.

---

## 4. Flux de données

### 4.1 Happy path — question sans outil

```
Mic ──► WakeWord ──► VAD ──► STT ──────────► LLM ──► TTS ──► Speaker
                                                streaming
```

Latence cible (ordres de grandeur, à valider sur ton GPU) :
- Wake word : ~100 ms ✓
- VAD endpoint (fin d'énoncé) : 200–400 ms ⚠ dépend du réglage
- STT Whisper large-v3 streaming GPU : 300–500 ms après endpoint ~
- LLM TTFT (Qwen 7B Q5 @ llama.cpp CUDA) : ⚠ 150–400 ms extrapolé, à mesurer sur la vraie config 10 GB
- TTS Piper TTFT : ~100 ms ~

Total perçu pour une réponse courte : ~1–1,5 s ⚠ — à mesurer sur ta config avant de s'engager.

### 4.2 Chemin avec tool call

```
... STT ──► LLM ──► tool_call(turn_on_light, {"room": "salon"})
                       │
                       ├──► MCP client ──► MCP server ──► Backend ──► Device
                       │                                       │
                       │                                       └──► result
                       │
                       └──► si délai > 500 ms, sidecar joue un filler
                       
            ◄── tool_result ──► LLM reprend ──► TTS ──► Speaker
```

Voir §6 pour le design des fillers.

---

## 5. Wake word « Hey Carlson »

openWakeWord ne fournit pas un modèle pré-entraîné pour cette phrase. Deux options :

**Option A — Entraîner un modèle custom** (recommandée)
- openWakeWord inclut un pipeline de génération de données synthétiques via Piper TTS (des milliers de variantes de l'énoncé générées automatiquement).
- Entraînement : ~ 30 min à 2 h sur CPU selon la quantité de data et le nombre d'epochs.
- Sortie : un fichier `.tflite` dropable dans `carlson/assets/wakeword/`.

**Option B — Utiliser un wake word existant**
- Moins bon pour l'identité "Carlson" mais zéro setup. À écarter si tu tiens au nom.

**Décision (2026-04-23) : option A validée par Kevin.** Piste concrète dans `docs/adr/0007-wake-word-training.md` (à rédiger au moment d'entraîner).

Garde-fou contre les déclenchements : confirmation douce par deuxième passe (validation sur 1 s d'audio après le déclencheur) — ~ pattern standard, implémenté dans openWakeWord.

---

## 6. Stratégie de verbalisation (fillers)

### 6.1 Problème
Quand le LLM émet un tool_call, il y a une fenêtre morte : exécution de l'outil + allers-retours réseau + reprise du LLM. Pour des outils rapides (lumière locale, <200 ms), pas besoin de filler — ça ajouterait de la latence pour rien. Pour des outils lents (appel météo externe 800 ms, scraping, etc.), le silence casse l'immersion.

### 6.2 Deux mécanismes complémentaires

**Mécanisme 1 — Pré-narration pilotée par le system prompt**
Le system prompt de Carlson contient :

> Avant tout tool call qui peut prendre plus d'une demi-seconde (requête réseau, scraping, contrôle de plusieurs appareils), émets d'abord UNE courte phrase en français ou anglais selon la langue de la conversation, qui annonce ce que tu fais. Ex : « Je regarde la météo », « Un instant, je m'en occupe ». Pour les actions instantanées (allumer une lampe locale), pas besoin d'annoncer.

Le LLM fait le travail, le texte pré-tool est streamé vers TTS pendant que le tool s'exécute. Pas de code spécifique — c'est de l'orchestration par prompt.

Limite : la fiabilité dépend du modèle. ~ Qwen 2.5 respecte bien ce genre d'instruction, mais il faut un eval.

**Mécanisme 2 — Sidecar filler (garde-fou)**
Un FrameProcessor Pipecat custom observe les `FunctionCallInProgressFrame`. Logique :

```
on FunctionCallInProgressFrame(tool_name):
    start_timer(tool_name, deadline=500ms)

on FunctionCallResultFrame(tool_name):
    cancel_timer(tool_name)

on timer_expired(tool_name):
    filler = pick_filler(tool_name, language=current_language)
    emit TTSSpeakFrame(filler)
```

Le catalogue de fillers est indexé par catégorie d'outil :

```python
FILLERS = {
    "search":   {"fr": ["Je cherche ça…", "Un instant, je regarde…"],
                 "en": ["Let me look that up…", "One moment…"]},
    "control":  {"fr": ["Je m'en occupe.", "C'est parti."],
                 "en": ["On it.", "Right away."]},
    "weather":  {"fr": ["Je consulte la météo.", "Je regarde le temps."],
                 "en": ["Checking the weather.", "Looking at the forecast."]},
    "_default": {"fr": ["Un instant…", "Laissez-moi vérifier."],
                 "en": ["One moment…", "Let me check."]},
}
```

Règle anti-répétition : ne pas jouer deux fois la même phrase consécutivement, mémoire glissante sur N=5.

### 6.3 Pourquoi les deux, pas juste un
- Le prompt (méca 1) gère bien le cas nominal mais n'offre aucune garantie — un modèle plus petit peut oublier.
- Le sidecar (méca 2) garantit qu'aucun silence > 500 ms ne passe, même si le modèle déraille.
- Ensemble, ils évitent la double verbalisation : si le LLM a déjà parlé, le sidecar ne se déclenche pas (on compte le temps depuis la fin du dernier `TextFrame` émis, pas depuis le tool_call).

### 6.4 Ordre des frames — point critique
Pipecat sérialise : le LLM peut émettre du texte ET un tool_call dans le même tour. L'implémentation doit garantir que :
1. Le texte pré-tool passe au TTS avant que le tool ne soit exécuté.
2. Le filler sidecar, s'il se déclenche, est inséré APRÈS le texte pré-tool et AVANT le texte post-tool.
3. Le texte post-tool (la réponse finale) part au TTS après réception du `tool_result`.

⚠ Ce point est à valider par un test d'intégration dès la première version fonctionnelle.

---

## 7. Design du serveur MCP (mcp-home)

### 7.1 Surface d'outils du POC — volontairement minimale

L'objectif du POC est de **valider la chaîne vocale + tool calling**, pas de piloter une vraie maison. La surface est réduite à deux tools :

```
turn_on_light(room: string)   → Ack ("Light on in <room>")
turn_off_light(room: string)  → Ack ("Light off in <room>")
```

Le backend ne touche à aucun équipement physique : il **affiche l'action dans la console** (via `ILogger` structuré) et met à jour un état en mémoire. On saura que ça marche en :
1. Disant « Hey Carlson, allume le salon » → Carlson répond, la console de mcp-home logge `turn_on_light room=salon`.
2. Optionnellement : une petite page web (voir §7.4) montre l'état des pièces en temps réel pour la démo.

Tout le reste (températures, scènes, média, timers, météo) est **hors scope POC**. Voir §11 pour les extensions envisagées.

### 7.2 Abstraction backend (POC)

```csharp
public interface IDeviceBackend
{
    Task TurnOnLightAsync(string room, CancellationToken ct);
    Task TurnOffLightAsync(string room, CancellationToken ct);
    IReadOnlyDictionary<string, bool> GetLightStates();  // pour la web UI optionnelle
}
```

Une seule implémentation au POC : `ConsoleMockBackend`. Log structuré via `ILogger`, état en mémoire, aucun effet physique.

L'interface reste volontairement mince — on l'étendra quand un vrai backend (MQTT, HA, autre agent) arrivera. Sélection du backend via `appsettings.json` (`Home:Backend = "console"`) pour préparer la DI dès maintenant, même avec une seule impl.

### 7.3 Pourquoi MCP et pas du REST direct
- **Tool discovery automatique** : Carlson liste les tools au démarrage, le LLM reçoit un function schema à jour sans redéploiement couplé.
- **Même surface pour Claude Desktop** ou d'autres agents MCP — on teste les outils avec un client interactif avant de les brancher au vocal.
- **Sérialisation stricte** : les schémas MCP sont JSON Schema, pas d'ambiguïté sur les types.
- **Piste multi-agents** (cf. §11) : si Carlson devient orchestrateur de plusieurs agents spécialisés, chaque agent est un serveur MCP parmi d'autres — la mécanique de tool discovery scale naturellement.
- Coût : une couche de plus. Valeur > coût ici.

### 7.4 Web UI optionnelle pour la démo

ASP.NET Core Minimal API greffé sur le même Generic Host : une route `/` qui renvoie une page HTML simple, et une route `/events` en Server-Sent Events qui pousse chaque changement d'état. Quelques dizaines de lignes.

Activation par config : `Home:WebUi:Enabled = true` (désactivé par défaut). Port : 5080 ~ par défaut, configurable.

À ne pas considérer comme un vrai dashboard — c'est un feedback visuel pour la démo, pas un produit.

---

## 8. Trade-offs explicites

| Décision | Gagne | Perd |
|---|---|---|
| Pipecat plutôt que LiveKit Agents | Simplicité locale, pas de dépendance à un serveur LiveKit | Communauté plus petite ~ |
| llama.cpp server plutôt que vLLM | Empreinte VRAM minimale sur 10 GB, GGUF très flexible, setup simple | Débit inférieur en multi-requête ~ — non bloquant à 1 utilisateur |
| Piper en MVP TTS | Latence, packaging trivial | Voix moins expressive — Carlson aura une voix "fonctionnelle" pas "majordome" avant l'upgrade |
| Qwen 2.5 plutôt que Llama 3.1 | Bilinguisme supérieur, tool calling équivalent ~ | Modèle chinois — si c'est un critère pour toi, à arbitrer |
| Transport MCP SSE/HTTP d'emblée (pas de phase stdio) | Topologie POC = topologie cible, mcp-home testable seul, pas de migration transport à venir | Auth + reconnexion + ordre de démarrage à gérer dès le jour 1, plus de "zéro conf local" (cf. ADR 0003) |
| Backend console-only au POC | Aucun matériel requis, démo visuelle facile via logs ou page web légère | Pas de valeur d'usage tant qu'on ne branche pas de vrais devices — accepté, c'est un POC |
| Filler sidecar ET pré-narration | Robustesse double | Complexité d'ordre de frames |
| Deux projets dans un monorepo polyglotte (Python + .NET) | Versionnement cohérent, PR atomiques, chaque projet dans son idiome | Deux toolchains à maintenir en dev ; un seul historique, split-history nécessaire pour open-sourcer mcp-home séparément |
| mcp-home en .NET 10 plutôt qu'en Python | Idiome de Kevin, type safety, binaire self-contained | Toolchain de plus ; SDK MCP C# moins mature que le Python ~ |
| ASP.NET Core + Generic Host plutôt que console nue | DI, config, logging, Kestrel HTTP prêt à l'emploi pour le transport SSE | ~20 Mo de plus dans le publish ⚠, dépendances runtime en plus |

---

## 9. Portée du POC

POC = valider la **chaîne vocale avec tool calling** sur du hardware perso, pas bâtir un système domotique. Concrètement :

- **In scope** : wake word, VAD, STT FR/EN, LLM Qwen 7B avec tool calling, TTS, fillers, client MCP, serveur MCP .NET, backend console, deux tools `turn_on_light` / `turn_off_light`, page web optionnelle pour feedback visuel démo.
- **Out of scope** : MQTT, Home Assistant, vrais équipements, multi-utilisateurs, persistance, satellites multi-pièces, météo, média, scènes, timers.

Les questions de stratégie domotique réelle (HA ou pas, quels protocoles, quels drivers) sont **hors du périmètre de cette archi**. Elles feront l'objet d'un ADR séparé le jour où on les ouvrira. Pour l'instant, on sort d'abord un POC qui fonctionne.

---

## 10. Sécurité et robustesse

- **Réseau POC** : mcp-home écoute sur un port TCP (défaut `5090` ~) du serveur central, accessible depuis le LAN privé. Auth par bearer token partagé (`MCP_HOME_TOKEN`) — pas de TLS au POC, le LAN privé est la barrière (cf. ADR 0003). Ne **pas** exposer mcp-home sur Internet dans cet état.
- **Réseau Phase 2 (sortie du LAN)** : mTLS, reverse proxy TLS (Caddy/nginx) ou overlay VPN (Tailscale / WireGuard). ⚠ ADR dédié à ouvrir à ce moment-là.
- **Tools destructeurs** : pas de tool capable d'éteindre le chauffage à 2°C. Garde-fous au niveau du serveur MCP (plage de valeurs par outil), pas au niveau du prompt.
- **Commande manuelle d'urgence** : les devices doivent rester contrôlables sans Carlson (interrupteur physique, app HA). Non négociable.
- **Privacy** : aucune audio ne quitte la machine. Whisper en local, TTS en local, LLM en local. Les seuls appels externes sont les tools qui le nécessitent (météo, recherche web), et ils sont explicites dans la surface MCP.

---

## 11. Ce que je revisiterais quand ça grandit

- **Orchestrateur multi-agents** : Carlson devient un chef d'orchestre qui route les demandes vers plusieurs agents spécialisés (agent domotique, agent cuisine, agent agenda, agent recherche…). Chaque agent = un serveur MCP distinct. `mcp-home` n'est alors qu'un parmi d'autres — la mécanique de tool discovery scale naturellement. Côté LLM, deux options à arbitrer le moment venu : (a) exposer au LLM principal tous les tools agrégés, (b) mettre en place un router/planner qui choisit d'abord l'agent, puis délègue. (b) scale mieux mais ajoute de la latence.
- **Mémoire longue durée** : Carlson a besoin de se souvenir de préférences ("mets toujours 21°C quand je dis froid"). Solution probable : un petit store clé-valeur, exposé comme tool MCP (`remember`, `recall`). Phase 2+.
- **Multi-utilisateur** : speaker diarization (pyannote ✓ open source) pour savoir qui parle. Impact sur la mémoire par personne. Phase 3.
- **Satellites multi-pièces** : wake word sur chaque satellite, STT et TTS en local sur le satellite OU déportés vers le serveur central selon le hardware. Arbitrage latence/coût matériel. Phase 3.
- **Évaluation continue** : un harness qui rejoue des audios enregistrés contre le pipeline pour détecter les régressions de WER/latence/qualité TTS. À monter quand on aura plus de 20 intents.
- **Interruption** : si l'utilisateur coupe Carlson au milieu d'une phrase, Pipecat gère l'interruption au niveau frame ✓ mais le comportement doit être testé sur les cas de tool calls en cours (annulation propre).
- **Personnalité** : un majordome a un ton. Prompt engineering au début ; fine-tune léger si nécessaire (LoRA sur Qwen) — pas avant d'avoir validé que le LLM brut ne suffit pas.
- **Vrais devices / domotique** : MQTT, Home Assistant, Zigbee/Matter. Hors scope POC, ADR dédié à ouvrir le jour où on attaque.

---

## 12. Prochaines étapes concrètes

1. **Scaffold .NET de mcp-home** — structure `.sln` + `csproj` ASP.NET Core, Generic Host + Kestrel, `IDeviceBackend` + `ConsoleMockBackend`, les deux tools `turn_on_light` / `turn_off_light`. Test xUnit du mock. Pas encore de wiring MCP SDK, juste le squelette HTTP.
2. **Wiring MCP C# SDK en SSE/HTTP** — exposer mcp-home sur `POST /mcp` + `GET /mcp/events` (noms à ajuster selon `ModelContextProtocol.AspNetCore` ~), auth bearer token basique, et valider avec un client MCP CLI ou `curl` que les deux tools sont exposés et appelables. Pas de stdio.
3. **Slice Carlson 1** — pipeline Python : STT → LLM (llama.cpp server Qwen 7B) → TTS. Push-to-talk en dev, pas encore de wake word. Vérifier que "quelle heure est-il ?" marche bout en bout, sans tool.
4. **Slice Carlson 2** — brancher le client MCP de Carlson sur mcp-home, exposer les tools au LLM. Dire "allume le salon" → l'instruction s'affiche dans la console de mcp-home. Premier vrai tool calling end-to-end.
5. **Slice Carlson 3** — wake word custom "Hey Carlson" entraîné avec openWakeWord. Remplace le push-to-talk.
6. **Slice Carlson 4** — sidecar filler + pré-narration. Tester la perception sur des tool calls artificiellement ralentis (ex. `Task.Delay(1500)` côté mcp-home).
7. **Web UI optionnelle** (facultatif) — page HTML servie par mcp-home qui affiche l'état des lumières en temps réel pour la démo.

Chaque slice est pensée pour être démo-able seule. On ne passe à la suivante qu'une fois la précédente validée sur hardware réel.
