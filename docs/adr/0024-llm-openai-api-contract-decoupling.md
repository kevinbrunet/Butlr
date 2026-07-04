# ADR 0024 — Découplage du LLM via contrat API OpenAI-compatible

## Status

Accepted — supersedes ADR 0006

## Context

ADR 0006 ancrait le LLM dans la machine qui fait tourner Carlson : llama.cpp compilé localement, GGUF téléchargé localement, flags de runtime (`-ngl`, `-c`, `--jinja`) gérés par les scripts du repo. Ce couplage avait du sens au POC — on maîtrisait la totalité de la stack sur un seul poste.

Deux problèmes sont apparus depuis :

1. **Friction opérationnelle.** Construire llama.cpp avec CUDA, télécharger un GGUF de ~5 GB, ajuster les flags à chaque changement de version ou de modèle, c'est du travail répété à chaque machine ou reset d'environnement.

2. **Couplage artificiel modèle / application.** La seule chose dont Carlson a besoin du LLM, c'est une API `/v1/chat/completions` conforme à la spec OpenAI. Le moteur de serving, la quantization, la VRAM, le matériel — tout ça est orthogonal à la logique applicative.

Par ailleurs, un serveur Qwen 3.6 tourne désormais en permanence sur le LAN (`192.168.1.85:8083`). Le maintenir dans l'application serait une duplication.

## Decision

Carlson consomme le LLM via **un unique point de configuration : `LLM_BASE_URL`**. Cette variable pointe vers n'importe quel backend exposant une API OpenAI-compatible (`/v1/chat/completions`, `/v1/models`).

Le repo ne contient plus ni script de build de llama.cpp, ni script de téléchargement de GGUF, ni logique de démarrage de serveur local. L'identité du backend (moteur, modèle, machine) est une préoccupation d'infrastructure, pas d'application.

Valeur par défaut : `http://192.168.1.85:8083/v1` (Qwen 3.6 sur le LAN local).

## Consequences

### Positif
- Changement de modèle = changement d'une variable d'environnement. Aucune modification du code applicatif.
- Compatible avec n'importe quel backend du marché : llama.cpp, Ollama, vLLM, LM Studio, OpenAI, Groq, un autre serveur LAN, etc.
- Les scripts de setup sont plus courts et plus rapides — plus de build C++ ni de téléchargement GGUF.
- La séparation infrastructure / application devient explicite et testable : le smoke test (`test-llama-server.sh`) valide uniquement le contrat API, pas l'implémentation interne du serveur.
- Les autres composants locaux (STT faster-whisper, TTS Piper, wake word) ne sont pas affectés — ils restent on-device.

### Négatif
- Carlson dépend d'un service réseau LAN pour le LLM. Si le serveur est éteint ou injoignable, la fonctionnalité LLM est indisponible. C'était déjà le cas en pratique avec un serveur local qu'il fallait lancer manuellement.
- Le principe *local-first* s'applique maintenant par couche : STT/TTS/wake word restent locaux ; le LLM peut être local ou LAN selon la machine qui l'héberge.
- ~ Les capacités exactes du modèle distant (context window, tool calling, vitesse) ne sont pas contrôlées par ce repo. À documenter dans `env.sh` ou un README d'infrastructure séparé.

## Alternatives considérées

- **Garder llama.cpp local, juste mettre à jour le modèle** — règle la question du modèle mais pas la friction de build et de maintenance des scripts. Écarté : le serveur LAN est déjà en place et plus pratique.
- **Abstraire via une interface Python** (LLMBackend protocol, factories) — sur-ingénierie au stade actuel. La spec OpenAI est déjà l'interface. Écarté.
- **Passer à un client OpenAI officiel (cloud)** — contraire au principe local-first. Écarté sauf évolution explicite vers un usage cloud.

## Révisions

- 2026-07-04 — création. Motivé par la mise en service d'un serveur Qwen 3.6 LAN permanent et la suppression des scripts build/download llama.cpp.
