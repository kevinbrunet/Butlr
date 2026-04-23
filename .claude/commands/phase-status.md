---
description: Résumer l'état des phases d'implémentation Butlr
---

Donne à Kevin un état synthétique de l'avancement, pour qu'il sache où il en est sans avoir à relire l'archi.

## Étapes

1. **Lire `docs/architecture.md`** — spécifiquement §12 (plan d'implémentation en étapes).

2. **Pour chaque étape** (1 à 10) :
   - Identifier les artefacts attendus (fichiers, services, endpoints).
   - Vérifier leur existence / leur état :
     - Étape 1 : `mcp-home/src/Butlr.McpHome/*.cs`, tests xUnit — **coquille .NET**.
     - Étape 2 : SDK MCP C# câblé, outils exposés sur `/mcp` SSE.
     - Étape 3 : `carlson/src/carlson/mcp_client.py` — client SSE réel.
     - Étape 4 : `carlson/src/carlson/services/llm_local.py` — OpenAILLMService câblé.
     - Étape 5+ : pipeline Pipecat, wake word, TTS, end-to-end.
   - Statut possible : `✓ fait` / `~ partiel` / `⚠ scaffold seulement` / `✗ pas commencé`.

3. **Cross-check avec `scripts/`** pour la Phase 1 (setup stack locale) :
   - `scripts/*.ps1` existent ? → Phase 1 prête à exécuter.
   - llama.cpp buildé / GGUF téléchargé / voix Piper : on ne peut pas le savoir depuis le repo — demande à Kevin.

4. **Produire un tableau** format :
   ```
   | # | Étape | Statut | Blockers / Notes |
   |---|---|---|---|
   ```

5. **Conclure** avec les 2-3 prochaines actions concrètes, ordonnées par dépendance.

## Règles

- Pas de spéculation : si tu ne peux pas vérifier un statut depuis le code, dis-le (marqueur ⚠ ou question directe).
- Pas de flatterie ("bel avancement !"). Ton factuel.
- Marqueurs ✓ ~ ⚠ sur les claims non-triviaux.
