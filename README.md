# Butlr 🏛️

> *Appliquer les standards de sécurité du médical et des infrastructures critiques à l'espace le plus intime qui soit : la maison.*

Je travaille sur des dispositifs médicaux (SaMD classe IIb, IEC 62304, ISO 14971) et des infrastructures soumises à NIS2. Dans ces contextes, la sécurité n'est pas une option - c'est une exigence de conception. Modèle de menace explicite, souveraineté des données, défense en profondeur, infrastructure auditée et reproductible.

La maison sait tout. Présence, habitudes, rythmes de vie, vulnérabilités. Un système domotique intelligent collecte en permanence ce que vous ne confieriez pas à n'importe qui. Pourquoi mériterait-il moins de rigueur qu'un dispositif médical ?

Butlr est la réponse à cette question - une infrastructure locale et souveraine pour la domotique, construite avec les mêmes exigences que les systèmes critiques que je conçois professionnellement.

---

**Deux couches d'IA cohabitent dans ce projet.**

La première est **dans** le projet - un majordome vocal qui orchestre la maison, raisonne en mode agentique, appelle les capacités domotiques via un serveur MCP, sans aucune donnée qui quitte le périmètre domestique.

La seconde est **autour** du projet - un workflow agentique LLM qui assiste le développement lui-même : génération de code, revue d'architecture, documentation, tests. Butlr se construit avec les outils qu'il explore.

---

C'est aussi l'occasion de faire les choses de A à Z. Provisionner l'infrastructure avec Ansible. Orchestrer avec Kubernetes. Déployer avec Helm. Concevoir l'architecture, écrire le code, définir les ADRs, construire la CI/CD, gérer les secrets, monitorer. Tout - sur un projet où je maîtrise le contexte métier.

**C'est un projet prospectif et exploratoire. Il n'est pas destiné à la production pour le moment.**

---

## Ce que Butlr est

- Un **POC en cours de construction** - l'honnêteté d'abord
- Un **laboratoire d'architecture** - chaque décision non-triviale est documentée dans un ADR
- Un **terrain d'expérimentation** - infrastructure as code (Ansible → Kubernetes → Helm), IA vocale temps réel, server MCP, workflow agentique LLM
- Un **projet souverain by design** - aucune dépendance cloud n'entre sans justification explicite dans un ADR

## Ce que Butlr n'est pas

- Un produit fini ou proche de l'être
- Un projet optimisé pour les contributions externes
- Une démonstration de maturité - c'est une démonstration de réflexion

---

## Architecture

Voir [`docs/architecture.md`](docs/architecture.md) pour la vue complète.

---

## Documentation

- [`docs/adr/`](docs/adr/) - Architecture Decision Records, un par décision non-triviale

Les ADRs sont la mémoire du projet. Lire avant de modifier l'architecture.



## Sécurité by design - parce que la maison est intime

La maison est l'espace le plus intime qui soit. Présence, habitudes, vulnérabilités, rythmes de vie - un système domotique intelligent sait tout ça. C'est précisément pour ça qu'il ne peut pas se permettre d'être négligent sur la sécurité.

Ce n'est pas une posture. C'est une conséquence logique.

Je travaille sur des dispositifs médicaux (SaMD classe IIb, IEC 62304, ISO 14971) et des infrastructures critiques soumises à NIS2. La mitigation du risque, l'analyse de menaces, la défense en profondeur - c'est mon quotidien professionnel. Butlr applique les mêmes principes à un contexte domestique.

Concrètement :

- **Souveraineté des données** - aucune donnée comportementale ne quitte la maison. Pas de cloud, pas de vendor lock-in, pas de surface d'attaque externe inutile.
- **Infrastructure auditée** - Ansible pour la reproductibilité, Kubernetes pour l'isolation des workloads, mTLS pour les communications inter-composants. L'environnement est versionnable et auditable.
- **Modèle de menace explicite** - chaque décision d'architecture (ADR) intègre les implications sécurité. Le filler sidecar pattern (ADR-0004) n'est pas qu'une décision UX - c'est aussi une décision sur ce qui transite sur le réseau local et quand.
- **Principe de moindre privilège** - mcp-home expose uniquement les capacités domotiques nécessaires. Le serveur MCP est la seule surface d'interaction entre l'agent LLM et les systèmes physiques de la maison.

NIS2 élargit le périmètre de la sécurité des systèmes d'information aux infrastructures du quotidien. Butlr explore ce que ça signifie appliqué à l'échelle domestique - un laboratoire pour des questions qui vont devenir mainstream.

---

## Pourquoi ce projet existe

Je suis architecte logiciel. Mes missions se passent dans des contextes réglementés - santé, infrastructure critique - où l'expérimentation est contrainte et les décisions techniques ont du poids.

Butlr est l'espace où j'expérimente librement. Où j'experimente une stack d'infrastructure souveraine. Où je teste un pattern de workflow agentique. Où j'applique à la domotique les standards de sécurité que l'on impose au médical. Où je me trompe sans conséquence.

Les sujets que j'explore ici : infrastructure as code (Ansible → Kubernetes → Helm), IA locale souveraine, pipelines agentiques temps réel, serveurs MCP comme couche d'abstraction entre agents et systèmes, sécurité by design à l'échelle d'un environnement contraint.

Si un jour le projet mature suffisamment pour devenir autre chose, tant mieux. En attendant, c'est mon laboratoire.

---



## Licence

MIT - fais-en ce que tu veux.
