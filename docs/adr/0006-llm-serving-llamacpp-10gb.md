# ADR 0006 — LLM serving via llama.cpp server sur GPU 10 GB

**Date** : 2026-04-23
**Statut** : Accepté

## Contexte

Kevin a 10 GB de VRAM NVIDIA. Il faut servir un LLM local, OpenAI-compatible, qui supporte le tool calling et tient largement dans cette enveloppe avec un context window utilisable (viser 8k au POC).

Initialement j'avais proposé vLLM. Son PagedAttention pré-alloue la VRAM agressivement et laisse peu de marge sur 10 GB — surdimensionné pour un seul utilisateur.

## Décision

Servir le LLM avec **llama.cpp server** (`llama-server`) compilé avec le backend CUDA. Modèle : **Qwen 2.5 7B Instruct** au format **GGUF Q5_K_M**.

Budget mémoire estimé (~ à mesurer) :

| Poste | Taille |
|---|---|
| Poids Q5_K_M | ~5,4 GB |
| KV cache @ context 8k | ~2 GB |
| Overhead CUDA | ~1 GB |
| **Total** | **~8,4 GB** sur 10 GB |

Fallback Q4_K_M si tension (~4,7 GB poids), perte de qualité mesurable mais acceptable sur du dialogue.

## Alternatives considérées

- **vLLM** — excellent pour débit multi-requêtes, mais overhead VRAM important et pas d'intérêt avec un seul utilisateur. Écarté pour 10 GB.
- **Ollama** — wrap autour de llama.cpp, pratique mais couche d'abstraction en plus. Écarté pour garder le contrôle direct sur les flags (`-ngl`, `-c`, `--parallel`, tool calling).
- **Text Generation Inference (TGI) HuggingFace** — solide mais overhead supérieur à llama.cpp. Écarté même raison que vLLM.
- **Modèle plus gros (14B)** — ne rentre pas confortablement en 10 GB avec un context utilisable. Écarté.
- **Modèle plus petit (Qwen 2.5 3B)** — rentre très largement mais qualité de tool calling ~ insuffisante sur de l'argumentation complexe. À reconsidérer seulement si le 7B ne tient pas dans les contraintes de latence.

## Conséquences

### Positif
- Tout rentre confortablement dans 10 GB, marge pour monter à 16k context si besoin en Q4.
- Endpoint OpenAI-compatible ✓, Pipecat s'y branche sans adaptateur custom.
- llama.cpp supporte nativement le tool calling au format OpenAI ~ (vérifier la version à l'install, le support est relativement récent).
- Un seul binaire CUDA à gérer, update du modèle = remplacement d'un fichier GGUF.

### Négatif
- Débit inférieur à vLLM sur du multi-requête. Non bloquant avec 1 utilisateur.
- Moins d'observabilité out-of-the-box que vLLM (metrics Prometheus, traces). Acceptable au POC.
- Si un jour on passe à plusieurs utilisateurs simultanés ou à des modèles plus gros, il faudra réévaluer (vLLM, TGI, ou un GPU plus costaud).

## Révisions
- **2026-04-23** : création. Choix motivé par la contrainte 10 GB VRAM.
