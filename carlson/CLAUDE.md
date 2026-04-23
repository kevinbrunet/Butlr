# CLAUDE.md — carlson

Contexte local quand tu travailles dans `carlson/`. Le `CLAUDE.md` racine est aussi chargé — celui-ci ne le répète pas.

## Rôle

Majordome vocal local. Pipecat orchestre un pipeline de frames audio → texte → LLM (+ tools MCP) → audio.

Client MCP pur : Carlson n'implémente **aucun** outil de domotique. Il consomme ceux exposés par `mcp-home` via SSE/HTTP.

## Structure

```
carlson/
├── pyproject.toml             # hatchling, extras [stt, vad, wake, tts-piper, tts-xtts, llm, dev, all]
├── src/carlson/
│   ├── main.py                # entrée `carlson`
│   ├── config.py              # Config.from_env()
│   ├── mcp_client.py          # McpHomeClient (SSE)
│   ├── pipeline.py            # build_pipeline(config, mcp)
│   ├── filler.py              # sidecar latence perçue (cf. ADR 0004)
│   ├── persona.py             # system prompt / persona Carlson
│   └── services/
│       ├── llm_local.py       # OpenAILLMService vers llama.cpp server
│       ├── stt_whisper.py     # faster-whisper
│       ├── tts_piper.py       # Piper (TTS par défaut)
│       └── wake_word.py       # openWakeWord
├── tests/test_filler.py
└── assets/wakeword/           # modèle "Hey Carlson" (à entraîner)
```

## Conventions Python

- Python 3.11+ (union types `X | Y`, `from __future__ import annotations` partout).
- Type hints sur tout ce qui est public.
- `ruff` pour lint/format (`line-length = 100`, `target-version = "py311"`).
- Classes `Config` / DTOs = `pydantic` v2 ou `dataclass(frozen=True)` — cohérence dans un même module.
- **Pas d'async partout sans raison** — Pipecat est async, donc le pipeline l'est ; les helpers purs restent sync.
- Imports triés par ruff (isort-compat). Pas de `from x import *`.

## Variables d'environnement clés

Cf. `README.md` pour la liste complète. Les 4 critiques :

| Var | Défaut | Rôle |
|---|---|---|
| `LLM_BASE_URL` | `http://localhost:8080/v1` | llama.cpp server local |
| `MCP_HOME_URL` | `http://localhost:5090/mcp` | endpoint SSE mcp-home |
| `MCP_HOME_TOKEN` | *(vide)* | bearer token ; vide = LAN dev seulement |
| `WAKEWORD_MODEL` | `assets/wakeword/hey_carlson.tflite` | modèle openWakeWord custom |

## Tests

```bash
pytest
```

Peu de tests au POC : la logique métier est dans les services externes (LLM, STT, TTS). Ce qui se teste utilement = adaptateurs, logique de filler, parsing config. Pas de mock de Pipecat — tests d'intégration du pipeline complet viendront en Phase 4.

## Phase actuelle

Phase 0 terminée (nettoyage scaffold). Phase 1 en cours (setup stack locale via `scripts/` racine).
Phases 2-5 détaillées dans le plan remis précédemment (MCP client réel, LLM, pipeline Pipecat, wake word + end-to-end).

### Phase 0 — [terminée] Nettoyage du scaffold
### Phase 1 — [terminée] Environnement local prêt (hors code Carlson) 
Ce sont des étapes infra, pas du code Carlson, mais elles sont bloquantes pour la suite. À faire avant la Phase 2, sinon tu itères à vide.
#### Étape 1.1 — [terminée] llama-server en standalone. Compiler/installer llama.cpp CUDA (⚠ -DGGML_CUDA=ON au cmake). Télécharger le GGUF Qwen 2.5 7B Instruct Q5_K_M. Lancer llama-server -m <file.gguf> -ngl 99 -c 8192 --host 0.0.0.0 --port 8080 --jinja (⚠ flag --jinja nécessaire pour tool calling correct ~, à confirmer avec la version installée).
Critère : curl http://localhost:8080/v1/models retourne le modèle, et un POST /v1/chat/completions avec tools=[...] renvoie bien un tool_calls quand pertinent. Mesure aussi TTFT et throughput token/s à ce stade — ça te donne une baseline.
#### Étape 1.2 — [terminée] Whisper prêt. Installer faster-whisper + ctranslate2 avec le provider CUDA. Télécharger le modèle large-v3. Faire un test standalone : 5 s d'audio → texte en < 500 ms. Si > 1 s, passer à medium ou large-v3-turbo ~.
Critère : script one-shot qui transcrit un wav en temps acceptable sur ta GPU.
#### Étape 1.3 — [terminée] Piper TTS prêt. Télécharger les voix fr_FR-siwis-medium + en_US-amy-medium. Test : une phrase française et une anglaise → wav correct.
Critère : phrase générée en < 300 ms, son propre.

Phase 2 — Slice 1 : STT → LLM → TTS push-to-talk, pas de tool
Objectif de slice : dire "quelle heure est-il ?" et entendre une réponse cohérente, sans wake word ni tool calling.
Étape 2.1 — Skeleton main.py + pipeline.py. Wiring Pipecat minimal : mic input → STT → LLM (OpenAI client pointé sur llama-server) → TTS Piper → speaker. Pas de VAD ni de wake word pour l'instant — push-to-talk (une touche pour parler, relâche pour envoyer à Whisper).
Critère : l'app démarre, affiche la liste des devices audio, accepte un push-to-talk.
Étape 2.2 — STT câblé. Connecter stt_whisper.py à Pipecat. Faire parler le frame InputAudioRawFrame → TranscriptionFrame.
Critère : je parle, la console logue la transcription.
Étape 2.3 — LLM câblé. llm_local.py : client OpenAI-compatible vers llama-server, SYSTEM_PROMPT depuis persona.py. Streaming activé (important pour TTFT perçu).
Critère : texte transcrit → réponse LLM streamée dans les logs.
Étape 2.4 — TTS câblé + sortie audio. tts_piper.py branche le flux texte → Piper → sounddevice.
Critère : je parle, Carlson répond à voix haute. Première démo-able.
Étape 2.5 — VAD (Silero). Remplacer le push-to-talk par VAD pour détection automatique de fin d'énoncé. Calibrer le seuil.
Critère : conversation sans toucher le clavier, latence endpoint < 400 ms ⚠.

Phase 3 — Slice 2 : premier tool call end-to-end via MCP/SSE
Prérequis : mcp-home doit avoir été wiré en MCP SSE (étape 2 de architecture.md §12 côté serveur).
Étape 3.1 — Implémenter McpHomeClient.start() en SSE. Utiliser le SDK mcp Python, transport SSE, URL + bearer token depuis Config. list_tools() au démarrage, stocker les schemas.
Critère : au démarrage, la console de Carlson affiche les 2 tools turn_on_light / turn_off_light listés depuis mcp-home.
Étape 3.2 — Exposer les tools au LLM. Modifier l'appel LLM pour passer tools=mcp.tools_as_openai() dans le body. Vérifier que Qwen + llama-server renvoient bien des tool_calls au bon format (⚠ point à valider, c'est là que --jinja compte).
Critère : "allume le salon" → le LLM émet un tool_call(turn_on_light, {room: "salon"}), visible dans les logs.
Étape 3.3 — Implémenter McpHomeClient.call(). Forward du tool_call vers mcp-home, récupérer le tool_result, re-injecter dans la boucle LLM.
Critère : "allume le salon" → la console de mcp-home logue turn_on_light room=salon et Carlson dit une phrase de confirmation. Deuxième démo-able.
Étape 3.4 — Gestion d'erreur et reconnexion SSE. Backoff exponentiel si mcp-home est down au démarrage (max 5 tentatives ~), reconnexion propre sur drop. Logger clair entre "pas démarré" et "401 mauvais token".
Critère : je coupe/relance mcp-home, Carlson reprend sans crasher.

Phase 4 — Slice 3 : wake word "Hey Carlson"
Étape 4.1 — Entraînement du modèle. Installer openWakeWord avec les deps d'entraînement, générer les données synthétiques via Piper TTS (⚠ plusieurs voix, du bruit de fond simulé), lancer l'entraînement. Durée ~ 30 min à 2 h selon data+epochs.
Critère : hey_carlson.tflite produit, false positive rate mesuré sur un enregistrement de 30 min de conversation quotidienne.
Étape 4.2 — Câblage Pipecat. wake_word.py branche le modèle en amont de la VAD. Seuil 0.5 par défaut, ajusté après mesure.
Critère : silence jusqu'à "Hey Carlson" → Carlson répond. Sur 10 min de bruit ambiant hors phrase magique, ≤ 1 déclenchement intempestif ⚠.
Étape 4.3 — Confirmation douce (garde-fou). Seconde passe sur 1 s d'audio après déclencheur pour filtrer les faux positifs (pattern openWakeWord standard).
Critère : faux positifs réduits sans augmentation perceptible de latence.

Phase 5 — Slice 4 : fillers (pré-narration + sidecar)
Étape 5.1 — Pré-narration via system prompt. Enrichir persona.py avec les instructions filler décrites dans §6.2 mécanisme 1 de l'archi. Tester sur des tool calls lents.
Critère : sur un tool artificiellement ralenti (Task.Delay(1500) côté mcp-home), Carlson dit "un instant" avant d'exécuter, pas après.
Étape 5.2 — Sidecar FrameProcessor. Implémenter la classe FillerSidecar qui observe FunctionCallInProgressFrame avec deadline 500 ms, émet un TTSSpeakFrame depuis FILLERS (déjà dans filler.py) si le LLM n'a pas déjà parlé.
Critère : même test lent, mais sans la pré-narration du system prompt (retirer temporairement) — le sidecar compense.
Étape 5.3 — Test intégration d'ordre des frames. Valider §6.4 — pré-narration → filler sidecar (si déclenché) → réponse post-tool. Idéalement un test enregistré qui vérifie l'ordre des TextFrame/TTSSpeakFrame émis.
Critère : test d'intégration vert, perception conversationnelle naturelle.
## Points d'attention

- ⚠ `pipecat-ai` bouge vite : **pin la version exacte** dès que le pipeline tourne (v0.0.x pre-1.0, breaking changes fréquents ~).
- ~ Flag `--jinja` côté llama.cpp nécessaire pour que le tool calling soit bien formatté. À confirmer avec la version installée.
- ⚠ TTS XTTS (`coqui-tts`) a une licence non-commerciale restrictive — Piper par défaut, XTTS seulement en opt-in pour tests perso.
