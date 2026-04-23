---
description: Créer un nouvel ADR avec le format maison
---

Tu vas créer un nouvel Architecture Decision Record dans `docs/adr/`.

## Étapes

1. **Déterminer le numéro** : lister les ADR existants (`ls docs/adr/`), prendre le numéro max + 1, sur 4 chiffres (`0007`, `0008`, ...).

2. **Demander à Kevin** (si pas déjà fourni via `$ARGUMENTS`) :
   - Le titre court de la décision (ex. "LLM context window à 8k tokens").
   - Le contexte en une ou deux phrases.
   - Les alternatives qu'il a déjà en tête (au moins 2).
   Si `$ARGUMENTS` contient déjà le sujet, enchaîne sans re-demander.

3. **Créer le fichier** `docs/adr/NNNN-slug-en-anglais-court.md` avec le format défini dans `.claude/rules/adr-writing.md`. Status par défaut : `Accepted`.

4. **Remplir chaque section** :
   - **Context** : pourquoi maintenant, contraintes. Marqueurs ✓ ~ ⚠ sur les faits externes.
   - **Decision** : 1-3 phrases, claires, actives.
   - **Consequences** : positif ET négatif. Au moins 2 conséquences négatives explicites.
   - **Alternatives considérées** : une sous-section par alternative rejetée, avec raison.
   - **Révisions** : date du jour, mention "création".

5. **Si la décision impacte l'architecture** (topologie, pipeline, transport, composant nouveau) : mets aussi à jour `docs/architecture.md` — trouve la section concernée, ajoute la référence au nouvel ADR.

6. **Challenger Kevin une fois** avant de finaliser : y a-t-il une alternative plus simple ? La décision est-elle réversible ? Le prix de se tromper est-il élevé ?

## Règles dures

- Numérotation monotone — jamais de trou.
- Un ADR ne supprime pas un ADR précédent. S'il le remplace, mets l'ancien en `Superseded by ADR NNNN` et cite-le dans Context.
- Pas d'ADR sans au moins 2 alternatives sérieuses rejetées.

## Argument

`$ARGUMENTS` — si fourni, contient le sujet / titre de l'ADR à créer.
