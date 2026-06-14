# ADR 0026 — Agent Alveus-UserDoc, réunion finale et boucle de retour sur verdict KO

## Status

Accepted

## Context

[ADR 0023](0023-worker-environment-evaluator-workflow.md) termine le workflow sur le verdict
`pass` d'Alveus-Evaluator. [ADR 0024](0024-hand-rolled-multi-agent-meetings.md) introduit le
mécanisme de réunion multi-agents (BA/QA/Tech, `Raise`/`Vote`/tally). Il reste à :
1. ajouter une étape de documentation utilisateur après le verdict `pass` ;
2. faire évaluer le résultat global par BA/QA/Tech via une réunion finale ;
3. décider quoi faire d'un verdict global négatif (KO).

Point d'attention soulevé en amont : pendant la réunion finale, BA/QA/Tech sont enracinés sur leurs
sous-répertoires respectifs (`tech-docs/`, `test-plan/`, `business-rules/` — ADR 0025), pas sur le
workspace parent (Worker/Evaluator/UserDoc). Avec leurs outils actuels, ils ne peuvent donc pas lire
l'intégralité de `workspace/`/`workspace-evaluator/`/`workspace-userdoc/`. Deux options : (a) leur
donner un second jeu d'outils en lecture enraciné sur le workspace parent, actif uniquement pendant
la réunion finale ; (b) faire travailler la réunion finale sur les **résumés** (`Finish.summary`)
produits par Worker/EnvironmentManager/Evaluator/UserDoc, complétés par la documentation propre de
chaque agent (qu'il peut relire normalement dans son propre sous-répertoire).

## Decision

1. **Nouvel agent Alveus-UserDoc** (`Agent:UserDocName`, défaut `AlveusUserDoc`), nouvelle activité
   `RunUserDocPrompt : AgentPromptActivityBase`. Workspace dédié `Agent:UserDocWorkspaceRoot`
   (défaut dev `workspace-userdoc/`), incluant en sous-répertoire le workspace d'Alveus-BusinessAnalyst
   (`business-rules/`, ADR 0025) — Alveus-UserDoc peut donc lire les règles métier à jour pour
   rédiger sa documentation. Agent volontairement **minimal** : `HandleDoneAsync` complète
   directement par `"Done"`, sans appel à `IAgentWorkVerificationService` (ADR 0020) — même schéma
   que `RunEvaluatorPrompt` sans verdict (ADR 0023). S'exécute après `RunEvaluator --Passed-->`,
   avec `Prompt = TaskPrompt + UserDocInstructions` (instructions d'Alveus-Technical issues de la
   réunion de pré-tâche, ADR 0025).

2. **`RunFinalReviewMeeting : MeetingActivityBase`** (ADR 0024) : `Topic` = ticket original +
   résumés concaténés de Worker/EnvironmentManager/Evaluator/UserDoc (`Finish.summary` de chacun).
   Topic implicite `"task-fulfilled"` seedé au round 1 via `SeedOpenTopics()`. `GetRoleTask`
   demande à chaque agent de relire sa propre documentation (`business-rules/`/`test-plan/`/
   `tech-docs/`) au vu des résumés fournis, puis de voter sur `"task-fulfilled"`
   (`agree` = la tâche est correctement remplie de son point de vue). `FinalVerdict` = `"ok"` si le
   tally de `"task-fulfilled"` est en faveur de `agree` (3-0 ou 2-1 après correction, ADR 0024),
   `"ko"` sinon.

   **Décision sur l'accès à l'"espace étendu"** : option (b) retenue — la réunion finale travaille
   sur les **résumés** propagés via `Topic`, complétés par la documentation que chaque agent peut
   relire dans son propre sous-répertoire (déjà accessible sans outillage supplémentaire). Pas de
   second jeu d'outils en lecture pour BA/QA/Tech.

3. **Verdict KO → comptes-rendus + boucle de retour** : si `FinalVerdict == "ko"`, chaque agent
   ayant voté `disagree` a, selon sa consigne (`GetRoleTask`), écrit un compte-rendu markdown dans
   son propre sous-répertoire expliquant ce qui ne correspond pas ; le contenu (ou un résumé) est
   récupéré via `Finish.summary` et exposé dans `BaReport`/`QaReport`/`TechReport`
   (`Output<string?>`, `null` si l'agent était `agree` et n'a donc rien écrit). `RunFinalReviewMeeting`
   sort par `"KO"` (vs `"OK"` si `FinalVerdict == "ok"`, vs `"NeedsHelp"` si `MeetingOutcome.NeedsHelp`
   — ADR 0024).

   Dans `AlveusTaskWorkflow`, `"KO"` route vers `OuterLoopGuard --Continue--> RunPreTaskMeeting`,
   dont `ExtraContext` est recalculé à chaque passage comme la concaténation non vide de
   `BaReport`/`QaReport`/`TechReport` (variables alimentées par les `Output` de
   `RunFinalReviewMeeting` — pas de variable `PreviousReports` séparée, cf. Alternatives).

4. **`OuterLoopIterationGuard : CodeActivity`** (`Workflows/OuterLoopIterationGuard.cs`), copie du
   pattern `LoopIterationGuard` (ADR 0023 §6) avec sa propre variable `OuterLoopCount` et sa propre
   constante `MaxIterations = 3` (plus basse que `LoopIterationGuard.MaxIterations = 5` : un cycle
   complet réunion de pré-tâche → Worker/EnvironmentManager/Evaluator → UserDoc → réunion finale est
   nettement plus coûteux qu'un cycle interne Worker/EnvironmentManager/Evaluator). Expose
   `Output<int> Iteration`, utilisé par `OuterLoopIterationGuardTests` (test déterministe, sans
   LLM, sur le modèle de `LoopIterationGuardTests`).

5. **Graphe `AlveusTaskWorkflow` mis à jour** (cf. ADR 0024/0025 pour les briques sous-jacentes) :
   ```
   RunPreTaskMeeting (Start)
     --Done--> RunWorker --Done--> RunEnvironmentManager --Done--> RunEvaluator
     --NeedsHelp--> (fin, Blocked)

   (boucle interne Worker/EnvironmentManager/Evaluator inchangée — LoopGuard, ADR 0023)

   RunEvaluator --Passed--> RunUserDoc
     --Done--> RunFinalReviewMeeting
     --NeedsMoreInfo/Blocked--> (fin)

   RunFinalReviewMeeting
     --OK--> (fin, succès — pas de connexion, comme RunEvaluator --Passed--> aujourd'hui)
     --KO--> OuterLoopGuard --Continue--> RunPreTaskMeeting
                            --LimitReached--> (fin, Blocked)
     --NeedsHelp--> (fin, Blocked)
   ```
   Nouvelles variables de workflow : `WorkerInstructions`, `EvaluatorInstructions`,
   `UserDocInstructions` (string, déf. vide — sorties de `RunPreTaskMeeting`, ADR 0025),
   `BaReport`/`QaReport`/`TechReport` (string?, déf. null — sorties de `RunFinalReviewMeeting`),
   `OuterLoopCount` (int, déf. 0). `Flowchart.Activities` liste toutes les nouvelles activités
   (`runPreTaskMeeting`, `runUserDoc`, `runFinalReviewMeeting`, `outerLoopGuard` — leçon ADR 0023
   §5, sinon `InvalidOperationException` au runtime).

## Consequences

### Positif
- Alveus-UserDoc réutilise `AgentPromptActivityBase` sans modification — seul `HandleDoneAsync` est
  spécialisé, comme `RunEvaluatorPrompt`.
- Option (b) ("résumés uniquement") évite un second jeu d'outils par agent BA/QA/Tech : pas de
  duplication d'instances `CmdRunTool`/`StrReplaceEditorTool`, pas de questions de cycle de vie
  ("actif uniquement pendant la réunion finale").
- La boucle KO → pré-tâche réutilise `RunPreTaskMeeting` tel quel : les comptes-rendus arrivent via
  `ExtraContext`, un mécanisme déjà prévu par ADR 0024 (pas de nouvelle activité pour "rejouer" la
  pré-tâche).
- Deux gardes de boucle distincts (`LoopGuard` interne, `OuterLoopGuard` externe) avec des limites
  différentes adaptées au coût réel de chaque cycle.

### Négatif
- ⚠ Option (b) signifie que la réunion finale ne voit **que** ce que Worker/EnvironmentManager/
  Evaluator/UserDoc ont choisi de mettre dans leur `summary` — si un résumé est trop bref, BA/QA/Tech
  voteront sur une information incomplète. Si cela s'avère insuffisant en pratique, l'option (a)
  (second jeu d'outils en lecture) reste une extension possible sans remettre en cause le reste
  du graphe — **à observer** (cf. Révisions).
- Un cycle KO complet (pré-tâche → Worker/EnvManager/Evaluator → UserDoc → finale) peut appeler
  jusqu'à ~3 + 3 + 1 + 3 = 10 agents LLM par itération, × `OuterLoopIterationGuard.MaxIterations = 3`
  — coût cumulé important si le verdict reste KO. `MaxIterations = 3` est volontairement bas pour
  borner ce coût, au prix d'un abandon ("LimitReached") plus rapide qu'avec `LoopGuard` (5).
- `RunFinalReviewMeeting.OnAgentFinishAsync` est un no-op : si BA/QA/Tech émettent
  `downstreamInstructions` pendant la réunion finale (ADR 0025), elles sont ignorées — accepté, pas
  de cas d'usage identifié (cf. ADR 0025 Négatif).
- Pas de validation que les comptes-rendus KO (`BaReport`/`QaReport`/`TechReport`) sont effectivement
  écrits sur disque par l'agent dissident — seul `Finish.summary` est capté ; un agent qui vote
  `disagree` sans rédiger de fichier produira un `ExtraContext` basé uniquement sur son résumé.

## Alternatives considérées

- **Variable `PreviousReports` dédiée** (mentionnée dans le plan initial), alimentée explicitement
  à la sortie de `OuterLoopGuard --Continue-->` par concaténation des 3 rapports — remplacée par un
  calcul inline de `RunPreTaskMeeting.ExtraContext` à partir de `BaReport`/`QaReport`/`TechReport`
  directement (sorties de `RunFinalReviewMeeting`). Fonctionnellement équivalent (vide au premier
  passage puisque ces variables valent `null`, concaténation des rapports non vides aux passages
  suivants), mais évite une variable intermédiaire redondante.
- **Option (a) — second jeu d'outils en lecture pour BA/QA/Tech pendant la réunion finale** —
  envisagée puis écartée pour la version initiale au profit de (b), cf. Context. Reste l'option de
  repli documentée si (b) se révèle insuffisante.
- **Faire écrire le compte-rendu KO par un 4ᵉ agent dédié** (plutôt que par l'agent dissident
  lui-même dans `GetRoleTask`) — écarté : l'agent dissident est le mieux placé pour expliciter son
  propre désaccord (il vient de voter `disagree` avec un `comment`, ADR 0024) ; un agent séparé
  devrait reformuler ce désaccord de seconde main, perte d'information et coût LLM supplémentaire.
- **`OuterLoopIterationGuard.MaxIterations` égal à `LoopIterationGuard.MaxIterations` (5)** — écarté :
  un cycle externe est ~3 à 4× plus coûteux qu'un cycle interne (point Négatif ci-dessus) ; une
  limite identique aurait pu multiplier le coût pire-cas par 5 sans bénéfice proportionnel
  (au-delà de quelques tentatives, un désaccord persistant signale probablement un besoin
  d'arbitrage humain plutôt qu'un problème que plus d'itérations résoudrait).

## Révisions

- 2026-06-14 — création.
