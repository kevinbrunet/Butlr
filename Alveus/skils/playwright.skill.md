---
name: playwright
description: Use this skill to write UI visual regression tests in .NET using Playwright for .NET — captures screenshots of pages or individual components and compares them pixel-by-pixel to a versioned baseline PNG, with masking of dynamic zones. Trigger for requests about tests visuels, tests d'interface, captures d'écran, régressions UI, comparaison pixel, Playwright for .NET.
---

# Playwright for .NET — visual regression testing

## Principe

`Expect(page).ToHaveScreenshotAsync(...)` capture l'écran (ou un élément) et le compare pixel-à-pixel à une image de référence versionnée dans `__screenshots__/`. Le premier run échoue et écrit un fichier `-actual.png` ; tu valides visuellement, puis tu mets à jour la baseline pour qu'elle devienne la référence commitable.

## Mise en place

```bash
dotnet add package Microsoft.Playwright
dotnet add package Microsoft.Playwright.Xunit
dotnet build
pwsh bin/Debug/net10.0/playwright.ps1 install
```

~ La commande d'installation des navigateurs (`playwright.ps1 install`, parfois via `Microsoft.Playwright.CLI`) et le chemin exact du script généré dépendent de la version et de l'OS — sur Linux, vérifier si c'est `.ps1` ou `.sh`. À confirmer au moment de l'implémentation.

`.gitignore` à ajouter :

```
*-actual.png
*-diff.png
```

Les baselines (`__screenshots__/**/*.png`) sont versionnées ; les captures d'échec ne le sont pas.

## Exemple minimal — capture pleine page avec zones masquées

```csharp
[Fact]
public async Task Dashboard_LooksCorrect()
{
    await Page.GotoAsync("https://localhost:5001/dashboard");

    await Expect(Page).ToHaveScreenshotAsync("dashboard.png", new()
    {
        Mask = new[]
        {
            Page.Locator("[data-testid='last-login']"),
            Page.Locator("[data-testid='user-avatar']"),
            Page.Locator(".live-clock")
        },
        MaskColor = "#FF00FF"
    });
}
```

Les zones `Mask` sont remplies d'un aplat avant comparaison — leur contenu changeant n'influence pas le résultat, mais leur présence/position est tout de même vérifiée implicitement.

## Cibler un composant précis plutôt que la page entière

```csharp
[Fact]
public async Task OrderSummaryCard_LooksCorrect()
{
    await Page.GotoAsync("https://localhost:5001/orders/123");

    var card = Page.Locator("[data-testid='order-summary-card']");

    await Expect(card).ToHaveScreenshotAsync("order-summary-card.png", new()
    {
        Mask = new[] { card.Locator(".generated-reference-number") }
    });
}
```

Seule la zone de l'élément est capturée — le reste de la page n'intervient pas.

### Pleine page + masques vs élément ciblé

| | Pleine page + masques | Élément ciblé |
|---|---|---|
| Détecte les régressions de layout global | Oui | Non |
| Sensible aux changements ailleurs sur la page | Oui (bruit potentiel) | Non |
| Adapté aux composants réutilisables | Moyen | Oui |
| Recommandé pour | Dashboards, pages d'accueil | Cartes, widgets, formulaires |

## Tolérance aux différences mineures (anti-aliasing, rendu de polices)

```csharp
await Expect(Page).ToHaveScreenshotAsync("dashboard.png", new()
{
    MaxDiffPixelRatio = 0.01 // 1% de pixels différents tolérés
});
```

~ Le nom exact du paramètre (`MaxDiffPixelRatio`, `MaxDiffPixels`, `Threshold`) varie selon les versions — vérifier dans IntelliSense sur `PageAssertionsToHaveScreenshotOptions`.

## Workflow de validation manuelle

1. Lancer les tests → un fichier `-actual.png` apparaît pour chaque baseline manquante.
2. Relire l'image `-actual.png` visuellement pour vérifier qu'elle correspond au résultat attendu.
3. Mettre à jour la baseline :

```bash
dotnet test -- Playwright.BrowserName=chromium --update-snapshots
```

~ La syntaxe exacte pour `--update-snapshots` via `dotnet test` peut différer selon la version de `Microsoft.Playwright.Xunit` — possiblement via variable d'environnement (`PLAYWRIGHT_UPDATE_SNAPSHOTS=1`). À confirmer.

4. Committer les nouvelles baselines dans `__screenshots__/`.

## CI : reproductibilité du rendu

Le rendu (anti-aliasing, polices, sous-pixels) **diffère entre OS**. Une baseline Windows ne matchera pas en CI Linux.

✓ Recommandation standard : générer et exécuter les tests dans le même environnement que la CI — l'image Docker officielle `mcr.microsoft.com/playwright/dotnet` fixe la version des navigateurs et l'environnement de rendu.

~ Le tag exact de l'image (`v1.4x.0-noble` ou similaire) doit correspondre précisément à la version du package NuGet installé, sous peine de mismatch de navigateur.

## Points de vigilance

- Désactiver les animations CSS avant capture — sinon flakiness garantie.
- Fixer la taille du viewport (`ViewportSize` dans les options de contexte) pour des captures reproductibles.
- Attendre explicitement la stabilité du contenu (`WaitForLoadStateAsync`, ou attente sur un sélecteur précis) avant `ToHaveScreenshotAsync`.
- Fixer un navigateur unique (ex: chromium) pour éviter de multiplier les baselines à maintenir, sauf besoin de couverture multi-navigateur.

## Organisation suggérée

```
MyProject.Tests/
├── __screenshots__/          ← baselines Playwright (.png), versionnées
│   └── chromium-linux/
└── UiTests/
    └── DashboardUiTests.cs
```
