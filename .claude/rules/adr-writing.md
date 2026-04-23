# Règle : rédaction des ADR

Toute décision d'architecture non-triviale doit être tracée dans un Architecture Decision Record (`docs/adr/NNNN-slug.md`). Un ADR = une décision. Pas de méga-ADR.

## Quand créer un ADR

- Choix entre plusieurs technos (lib, framework, protocole).
- Décision de design structurante (pattern d'orchestration, topologie, transport).
- Changement de trajectoire par rapport à un ADR existant (alors : ADR *supersedes* l'ancien).
- Ajout d'une dépendance externe non-triviale (cloud, service payant, lib avec licence sensible).

**Pas besoin d'ADR** pour : un refactor interne, un renommage, un correctif de bug, un choix de variable.

## Format

Inspire-toi des ADR existants (`0001` à `0006`). Structure canonique :

```markdown
# ADR NNNN — <Titre court et actif>

## Status

Accepted | Proposed | Superseded by ADR XXXX | Deprecated

## Context

Le problème concret. Pourquoi une décision est nécessaire *maintenant*. Les contraintes externes (perf, licence, compétences, calendrier).
Utilise les marqueurs ✓ ~ ⚠ pour les faits externes.

## Decision

La décision retenue, formulée clairement en une à trois phrases. Pas de "on envisage" — une ADR acte.

## Consequences

Ce qui devient possible, ce qui devient bloqué, ce qui devient cher, ce qui devient simple. Positif ET négatif.

## Alternatives considérées

Une section par alternative rejetée, avec la raison du rejet. C'est la partie qui rend l'ADR utile 6 mois plus tard — si tu n'as pas creusé d'alternative, c'est que la décision est triviale et ne mérite pas un ADR.

## Révisions

- YYYY-MM-DD — création
- YYYY-MM-DD — note sur X suite à Y
```

## Règles dures

- **Numérotation monotone croissante**. Prochain ADR = `0007-...`. Jamais de trou, jamais de doublon.
- **Slug en anglais, court, lisible**. `0007-llm-context-window-8k` > `0007-taille-fenetre-contexte`.
- **Un ADR n'est jamais supprimé.** S'il devient obsolète, on met son Status à "Superseded by ADR XXXX" et on laisse le fichier. La traçabilité historique est la raison d'être de l'outil.
- **Quand un ADR est superseded**, le nouveau doit citer explicitement l'ancien dans sa section Context et expliquer ce qui a changé.
- **Marqueurs de confiance obligatoires** (cf. `confidence-markers.md`) pour tous les claims externes.

## Checklist avant de merger un ADR

- [ ] Le titre décrit la décision, pas le problème.
- [ ] Status est cohérent (Accepted par défaut pour un nouveau).
- [ ] Au moins deux alternatives sérieuses rejetées avec raison.
- [ ] Les conséquences négatives sont explicites (pas un ADR marketing).
- [ ] `architecture.md` est mis à jour si l'ADR change la topologie ou le pipeline.
