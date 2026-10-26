# ADR 0051 — Pocket TTS sur GPU par défaut

## Status

Accepted — supersedes partiellement [ADR 0036](0036-pocket-tts-cloned-voices-safetensors.md) sur le seul point « Pocket TTS tourne sur CPU » ; le reste d'ADR-0036 (Pocket TTS, voix en `.safetensors`) est inchangé.

## Context

ADR-0036 plaçait Pocket TTS sur CPU, comme le recommande son auteur (petit modèle, batch de 1). Les runs du 2026-10-26 (ADR-0048, 0050) montrent que c'est la limite structurelle de Loom : le TTS produit l'audio français à peine plus vite que le temps réel (facteur 0,7 à 1,3 selon les runs). Dès que la traduction est plus longue que la source, ou qu'une génération dérape, le retard s'accumule dans le consommateur unique, qui sérialise traduction et synthèse.

✓ La documentation de Pocket TTS 4.0.1 (section « Running on GPU ») indique : pas d'argument `device` dans `load_model()`, non supporté officiellement, mais `TTSModel` est un `nn.Module` qu'on peut déplacer avec `.to("cuda")` ; ~2,6 fois plus rapide qu'en CPU sur une Tesla T4 selon leurs mesures ; les morceaux générés restent sur le GPU et doivent être ramenés sur le CPU avant `.numpy()` ; `quantize=True` est incompatible avec CUDA.

✓ Mesuré sur fedora3 (RTX 5090), voix `estelle`, 5 phrases françaises, second tour (le premier chauffe) :

| | Premier morceau p50 | Facteur temps réel p50 |
|---|---|---|
| CPU | 289 ms | 1,32 |
| GPU | 47 ms | 3,96 |

## Decision

`PocketTtsSynthesizer` déplace le modèle sur le GPU par défaut (`device="cuda"`, repli CPU automatique si CUDA est absent), dans mono comme dans multi. L'option `--tts-device cpu` rétablit le comportement d'ADR-0036.

## Consequences

- Le budget TTS d'ADR-0036 (premier morceau p95 < 400 ms) devient tenable avec de la marge, et la synthèse va environ 4 fois plus vite que la parole : le consommateur unique peut rattraper un retard au lieu de l'accumuler.
- Le TTS partage la carte avec le STT, la traduction (llama.cpp) et, en multi, la séparation et l'empreinte vocale. Les appels restent sérialisés par le consommateur unique (ADR-0044/0046) ; WhisperLiveKit et le serveur vLLM tournent en parallèle sur la même carte. ⚠ Contention non mesurée en détail ; la VRAM supplémentaire est faible (petit modèle).
- `quantize=True` n'est plus une piste tant que le TTS est sur GPU.
- Chemin non supporté officiellement par Pocket TTS : une version future pourrait le casser sans prévenir.
- Cible embarquée (Jetson, cf. `Loom/CLAUDE.md`) : CUDA y est disponible, le choix reste cohérent ; un Raspberry Pi resterait sur CPU.

## Alternatives considérées

### Rester sur CPU avec `quantize=True`

✓ Gain annoncé de ~27 % sur x86 (doc 4.0.1), soit un facteur temps réel de ~1,7 au mieux, contre ~4 sur GPU. Insuffisant pour absorber les écarts.

### Un second processus TTS sur CPU en parallèle

Contourne la sérialisation sans changer de matériel, mais double la mémoire, complique l'ordre de sortie, et contredit le consommateur unique introduit après les plantages GPU (ADR-0044/0046).

## Révisions

- 2026-10-26 — création, après la sonde CPU/GPU ci-dessus.
