# ADR 0035 — Traduction via NLLB intégré à WLK, pas de LLM

## Status

Accepted

## Context

Le texte transcrit (EN/ZH) doit être traduit en français en flux incrémental, par fragments/groupes de mots, avec un budget de latence serré (le budget bout-en-bout total est ~1600ms, dont la traduction fait partie de l'étage WLK à 1000ms, cf. ADR-0033). WhisperLiveKit propose une intégration NLLB (`--target-language fr`) directement dans son pipeline STT streaming.

## Decision

La traduction est déléguée à NLLB via l'intégration native de WLK (`--target-language fr`). On n'introduit pas de LLM pour la traduction au périmètre initial du POC.

## Consequences

- Latence : un NMT spécialisé (NLLB) est structurellement plus rapide qu'un LLM généraliste pour traduire des fragments courts — pas d'overhead de prompt/génération token-par-token d'un chat model.
- Qualité : NLLB distillé peut décevoir sur les idiomes, le jargon technique ou les négations en contexte de fragments courts et incomplets (nature du streaming) — verdict qualité à établir en lecture critique des transcripts (T1.3).
- Repli documenté et budgété si NLLB échoue le gate qualité : spike timeboxé 1 jour, brancher la sortie STT brute (sans `--target-language`) sur un serveur llama.cpp local avec KV cache (`cache_prompt=true`), modèle 1.5-4B, comparaison qualité ET latence face à NLLB. Ce repli n'est pas implémenté par défaut — uniquement si T1.3 est no-go.
- Si le repli LLM est activé, cela réintroduit une dépendance à l'infrastructure LLM déjà utilisée ailleurs dans Butlr (llama.cpp server, cf. ADR-0006 côté carson) — cohérent avec la stack existante si nécessaire.

## Alternatives considérées

- **LLM (llama.cpp + modèle 1.5-4B) en traduction directe, dès le départ** : rejeté par défaut au profit de NLLB — un LLM généraliste est plus lent et plus coûteux en ressources pour un besoin de traduction pure, sans bénéfice de qualité démontré à ce stade. Reste une option d'itération 2 si NLLB déçoit (cf. Consequences).
- **API de traduction cloud (Google Translate, DeepL, etc.)** : rejeté sans même être évalué — violerait le principe local-first du repo (cf. `CLAUDE.md`, section "Ce qu'il NE faut PAS faire") sans ADR de challenge dédié.

## Révisions

- 2026-07-07 — création
