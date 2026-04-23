# ADR 0004 — Verbalisation d'attente : pré-narration LLM + sidecar filler

**Date** : 2026-04-23
**Statut** : Accepté

## Contexte

Durant un tool call lent (> 500 ms ~), le silence perçu casse la fluidité conversationnelle. Trois approches possibles, on arbitre.

## Options

1. **Pré-narration uniquement via prompt** : le system prompt instruit le LLM d'annoncer son intention avant un tool call. Simple. Fiabilité dépend du modèle.
2. **Sidecar uniquement** : un processor Pipecat injecte automatiquement une phrase d'attente si un tool call dépasse un seuil de temps. Robuste mais décorrélé du contenu.
3. **Les deux combinés** : prompt pour le cas nominal (phrase contextuelle), sidecar comme garde-fou (phrase générique).

## Décision

Option 3. Le prompt gère 80 % des cas avec une phrase qui a du sens ; le sidecar garantit qu'aucun silence anormal ne passe si le LLM oublie.

Règles :
- Seuil du sidecar : 500 ms après l'émission de `FunctionCallInProgressFrame`. ⚠ À ajuster par mesure.
- Le sidecar ne se déclenche pas si du texte a été émis par le LLM dans les 500 ms précédentes (compteur partagé entre pré-narration et sidecar).
- Catalogue de fillers indexé par catégorie d'outil + langue courante. Anti-répétition sur N=5 dernières phrases.
- Ordre des frames : pré-narration → tool call → filler (si nécessaire) → tool result → réponse finale.

## Conséquences

- Deux surfaces de comportement à tester (prompt-driven et time-driven). Les tests d'intégration doivent couvrir les deux.
- Tuning du seuil : à éval sur le terrain, pas à deviner. Métrique cible : silence perçu < 1 s dans 95 % des cas ⚠.
- Si le modèle LLM change, réévaluer la fiabilité de la pré-narration — risque de surdéclenchement du sidecar ou au contraire de silences qui passent entre les mailles.
