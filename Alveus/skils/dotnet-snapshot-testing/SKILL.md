---
name: dotnet-snapshot-testing
description: Use this skill whenever setting up, writing, or reviewing automated regression tests in .NET that compare an actual result (API response, JSON payload, screen render) against a previously human-validated reference ("approval testing" / "golden master" / "snapshot testing"), especially when only a SUBSET of the result should be compared — specific JSON fields/paths for APIs, specific zones/elements for screens — while other parts (timestamps, GUIDs, generated IDs, dynamic UI content) must be ignored. Covers Verify (snapshot testing for .NET, with scrubbers for noisy fields) for APIs/JSON/objects, and Playwright for .NET for UI visual regression with masking of specific zones or screenshots of single elements. Trigger this for requests about "tests de non-régression", "comparaison avec une baseline validée", "snapshot testing", "approval testing", "tests visuels", "tests API automatisés", or when integrating these into a CI pipeline / traceability dossier (e.g. IEC 62304).
---

# Snapshot / Approval Testing en .NET — Verify (API/JSON) + Playwright (UI)

## Le pattern général

Tous les cas couverts par ce skill suivent le même schéma en 3 étapes :

1. **Premier run** : le test produit un résultat "received" (JSON, capture d'écran, objet sérialisé).
2. **Validation humaine unique** : un humain relit ce "received", le juge correct, et le promeut en fichier "verified" (la baseline). C'est l'équivalent d'une revue de code — le diff apparaît dans la PR.
3. **Runs suivants** : le test compare automatiquement le nouveau "received" au "verified". Tout écart fait échouer le test, sauf sur les zones explicitement exclues (scrubbers pour Verify, masks pour Playwright).

C'est l'inverse d'une assertion classique (`Assert.Equal(expected, actual)` où `expected` est écrit à la main) : ici la baseline est **générée puis validée**, pas écrite manuellement. C'est ce qui rend le pattern praticable pour des objets complexes (réponses API entières, écrans complets) tout en gardant une trace de revue exploitable pour un audit IEC 62304.

## Choisir le bon outil

| Besoin | Outil | Référence |
|---|---|---|
| Comparer une réponse API / un objet sérialisable / du JSON, en ignorant certains champs (timestamps, IDs, GUIDs...) | **Verify** (`Verify.Xunit`, `Verify.NUnit`, ou `Verify.MSTest`) | `references/verify-api.md` |
| Comparer un écran (capture pleine page ou zone précise), en ignorant certaines zones dynamiques | **Playwright** for .NET (`Microsoft.Playwright`, `Microsoft.Playwright.NUnit` / `.MSTest`) | `references/playwright-ui.md` |
| Les deux dans le même projet de test | Les deux packages cohabitent sans conflit dans un même projet xUnit/NUnit/MSTest | voir les deux fichiers de référence |

Les deux outils partagent la même philosophie (fichiers `.verified.*` versionnés dans le repo, fichiers `.received.*` ignorés via `.gitignore`), donc l'organisation du dépôt de tests peut être commune.

## Mise en place rapide (commun aux deux)

```bash
dotnet new xunit -n MyProject.Tests
cd MyProject.Tests
dotnet add package Verify.Xunit
dotnet add package Microsoft.Playwright
dotnet add package Microsoft.Playwright.Xunit
```

~ Les noms exacts des packages d'intégration (`Verify.Xunit` vs `Verify.NUnit` vs `Verify.MSTest`, et leurs équivalents Playwright) dépendent du framework de test choisi — vérifier sur NuGet.org au moment de l'implémentation, les conventions de nommage de Simon Cropp évoluent peu mais autant confirmer.

`.gitignore` à ajouter dans le dossier de tests :

```
*.received.json
*.received.png
*.received.txt
```

## Initialisation globale de Verify (une seule fois)

Verify nécessite un "module initializer" exécuté avant les tests. Pattern habituel :

```csharp
// ModuleInitializer.cs
using System.Runtime.CompilerServices;
using VerifyTests;

public static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        VerifierSettings.DerivePathInfo((sourceFile, projectDirectory, type, method) =>
            new(directory: Path.Combine(projectDirectory, "Snapshots"),
                typeName: type.Name,
                methodName: method.Name));
    }
}
```

~ `DerivePathInfo` est l'API que je connais pour personnaliser l'emplacement des fichiers `.verified.*` — la signature exacte peut varier selon la version, à confirmer dans `references/verify-api.md` / la doc officielle avant de coder.

## Workflow de revue (la partie "validation manuelle")

1. Lancer les tests : les fichiers `*.received.*` apparaissent à côté des `*.verified.*` (ou n'apparaissent pas si tout correspond).
2. Outil de diff : `dotnet tool install -g Verify.Terminal` propose une CLI pour accepter/rejeter les diffs (`accept` renomme `.received` en `.verified`). Des extensions VS Code / Rider existent aussi pour Verify (diff intégré à l'IDE).
3. ⚠ Je n'ai pas une connaissance fiable de l'état actuel de `Verify.Terminal` (commandes exactes, packaging) — à vérifier dans le repo `VerifyTests/Verify` avant de bâtir un workflow d'équipe autour.
4. Pour Playwright, la mise à jour de baseline se fait généralement via un flag CLI (`--update-snapshots`), voir `references/playwright-ui.md`.

## Intégration CI et traçabilité IEC 62304

- Les fichiers `.verified.*` sont **versionnés** dans le repo : tout changement de baseline passe par une PR, donc par une revue — c'est la trace d'approbation exigée par un dossier de vérification.
- Convention de nommage suggérée pour relier un snapshot à une exigence : `MethodName_REQ-1234.verified.json` ou un sous-dossier par exigence, si ta structure de traçabilité repose sur des identifiants d'exigence dans les noms de tests.
- En CI, les tests échouent normalement (pas de mode "update" en pipeline) ; seul un humain en local peut promouvoir un `.received` en `.verified`, ce qui garantit qu'aucune baseline n'est modifiée sans revue.
- Pour Playwright en CI : utiliser l'image Docker officielle Playwright (contient déjà les navigateurs) pour éviter les écarts de rendu entre poste local et CI — voir `references/playwright-ui.md` pour le détail.

## Pour aller plus loin

- `references/verify-api.md` : scrubbers, sélection de sous-ensembles de JSON, exemples de tests d'API ASP.NET Core.
- `references/playwright-ui.md` : masking de zones, capture d'un élément précis, configuration CI/Docker.
