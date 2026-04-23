# Règle : marqueurs de confiance

**Toute** donnée factuelle non-triviale produite dans ce repo — code, doc, ADR, commit message, réponse en chat — doit porter un marqueur de confiance explicite :

- **✓** : présent dans ma connaissance avec confiance raisonnable. Référence stable et bien établie.
- **~** : approximatif, partiellement incertain, ou susceptible d'avoir évolué. À vérifier avant usage critique.
- **⚠** : incertain, extrapolé, ou inféré. **Ne pas utiliser sans vérification.**

## Où s'applique la règle

Tout ce qui rentre dans l'une de ces catégories :

- Chiffres précis (tailles de modèles, latences, VRAM, performance).
- Références normatives ou réglementaires (ISO, ANSSI, MDR, RGPD, WCAG, etc.).
- Citations de specs ou de standards (OpenAI API, MCP, OAuth, etc.).
- Benchmarks techniques ("X est plus rapide que Y de Z%").
- Affirmations sur le comportement d'une lib / d'une API quand tu n'as pas la source sous les yeux.
- Dates, versions précises, noms de flags / options.

**Ne s'applique pas** aux faits triviaux et vérifiables au pas suivant ("Python est typé dynamiquement", "HTTP utilise TCP"), ni aux décisions de design qui sont des choix explicites (pas une donnée externe).

## Comment faire

### Dans le code et la doc

```python
# ~ faster-whisper large-v3 : ~1.5 GB RAM en float16, à confirmer sur la machine cible.
# ✓ CUDA Compute Capability 7.5 minimum pour float16 natif.
# ⚠ Flag --jinja obligatoire pour tool calling — comportement pas testé avec la version pinnée.
```

### Dans les ADR

Dans la section "Context" ou "Consequences", préfixe les claims externes :

> ~ llama.cpp tourne Qwen 2.5 7B Q5_K_M à ~30 tok/s sur une RTX 4090 (benchmarks communautaires mi-2025, pas re-mesurés).

### Dans les réponses en chat à Kevin

Obligatoire devant tout chiffre précis, version, référence. Si tu ne trouves pas une donnée avec assez de certitude, dis-le explicitement **et** indique où la vérifier — plutôt qu'un nombre plausible-mais-faux.

## Pourquoi

Mieux vaut un "je ne sais pas, vérifie ici" utile qu'une réponse fausse bien formulée. Les marqueurs permettent à Kevin de trier visuellement ce qui est sûr de ce qui ne l'est pas, sans avoir à interroger chaque phrase.

C'est une règle **dure** : un document sans marqueurs où il en faudrait = défaut, pas une simple préférence stylistique.
