# Playwright for .NET — tests visuels avec comparaison sélective de zones

## Principe

`Expect(page).ToHaveScreenshotAsync(...)` capture l'écran (ou un élément) et le compare pixel-à-pixel à une image de référence stockée dans `__screenshots__/` (ou un dossier configuré). Le premier run échoue et écrit `-actual.png` ; tu valides visuellement, puis tu relances avec un flag de mise à jour pour créer la baseline `.png` versionnée.

## Mise en place

```bash
dotnet add package Microsoft.Playwright
dotnet add package Microsoft.Playwright.Xunit
dotnet build
pwsh bin/Debug/net8.0/playwright.ps1 install
```

~ La commande d'installation des navigateurs (`playwright.ps1 install`, parfois via `Microsoft.Playwright.CLI` en `dotnet tool`) et le chemin exact du script généré dépendent de la version et de l'OS — sur Linux, c'est souvent `pwsh bin/Debug/<tfm>/playwright.ps1 install` ou un script `.sh` équivalent. À confirmer au moment de l'implémentation.

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
        MaskColor = "#FF00FF" // optionnel, couleur de remplissage du masque
    });
}
```

Les zones désignées par `Mask` sont remplies d'un aplat de couleur **avant** la comparaison pixel : leur contenu (changeant) n'influence jamais le résultat du test, mais leur *présence/position* est tout de même vérifiée implicitement (si l'élément disparaît ou change de taille, le masque change de forme et le test échoue quand même — ce qui peut être souhaité ou non selon le cas).

~ `MaskColor` et la forme exacte de l'objet d'options (`PageAssertionsToHaveScreenshotOptions` ou équivalent en C#) — le nom de la classe d'options peut différer du binding JS, à vérifier dans IntelliSense / la doc `microsoft/playwright-dotnet`.

## Capturer / comparer uniquement une zone précise (ta demande "certaines zones d'écran")

Au lieu de capturer toute la page et de masquer le reste, tu peux cibler directement un élément :

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

→ Seule la zone de l'élément `order-summary-card` est capturée et comparée ; le reste de la page n'intervient pas du tout. C'est l'approche la plus stable pour des composants réutilisés à plusieurs endroits (un même composant testé une fois, indépendamment de la page qui l'héberge).

### Comparaison A — pleine page + masques vs B — élément ciblé

| | Pleine page + masques | Élément ciblé |
|---|---|---|
| Détecte les régressions de layout global | Oui | Non |
| Sensible aux changements ailleurs sur la page | Oui (bruit potentiel) | Non |
| Adapté aux composants réutilisables | Moyen | Oui |
| Recommandé pour | Pages d'accueil, dashboards | Cartes, widgets, formulaires isolés |

Tu peux combiner les deux dans la même suite : un test "page" pour la disposition générale (avec masques sur le contenu dynamique), et des tests "composant" pour les zones critiques en détail.

## Tolérance aux différences mineures (anti-aliasing, rendu de polices)

```csharp
await Expect(Page).ToHaveScreenshotAsync("dashboard.png", new()
{
    MaxDiffPixelRatio = 0.01 // 1% de pixels différents tolérés
});
```

~ Le nom du paramètre (`MaxDiffPixelRatio` vs `Threshold` vs `MaxDiffPixels`) a varié selon les versions de Playwright (côté JS, plusieurs options coexistent : `maxDiffPixels`, `maxDiffPixelRatio`, `threshold`). En .NET, vérifier laquelle est exposée dans la version installée — IntelliSense sur `PageAssertionsToHaveScreenshotOptions` donnera la liste exacte.

## Mise à jour de la baseline après validation manuelle

```bash
dotnet test -- Playwright.BrowserName=chromium --update-snapshots
```

~ La syntaxe exacte pour passer `--update-snapshots` via `dotnet test` (qui n'est pas nativement un runner Playwright comme `npx playwright test`) dépend de la façon dont `Microsoft.Playwright.Xunit`/`.MSTest` expose cette option — possiblement via une variable d'environnement plutôt qu'un argument CLI. À tester en environnement réel ; je n'ai pas une certitude suffisante pour donner la commande définitive.

## CI : reproductibilité du rendu

Le rendu (anti-aliasing, polices système, sous-pixels) **diffère entre OS**. Une baseline générée sur Windows/macOS ne matchera pas en CI Linux, et inversement.

✓ Recommandation standard : générer et faire tourner les tests visuels **dans le même environnement** que la CI — typiquement via l'image Docker officielle Playwright (`mcr.microsoft.com/playwright/dotnet`), qui fixe la version des navigateurs et l'environnement de rendu.

~ Le tag exact de l'image (`mcr.microsoft.com/playwright/dotnet:v1.4x.0-noble` ou similaire, aligné sur la version du package NuGet) — à faire correspondre précisément à la version de `Microsoft.Playwright` utilisée, sous peine de mismatch de navigateur.

## Organisation suggérée

```
MyProject.Tests/
├── Snapshots/                    ← fichiers Verify (.verified.json)
├── __screenshots__/              ← baselines Playwright (.png), versionnées
│   ├── chromium-linux/
│   └── ...
├── ApiTests/
│   └── OrdersApiTests.cs
└── UiTests/
    └── DashboardUiTests.cs
```

Playwright range souvent les baselines par plateforme/navigateur (sous-dossiers `chromium-linux`, etc.) — fige le navigateur unique (ex: chromium uniquement) si tu veux éviter de multiplier les baselines à maintenir, sauf besoin explicite de couverture multi-navigateur.

## Points de vigilance

- Désactiver les animations CSS (`Page.AddStyleTagAsync` ou option Playwright dédiée) avant capture — sinon flakiness garantie.
- Fixer la taille du viewport (`ViewportSize` dans les options de contexte) pour des captures reproductibles.
- Attendre explicitement la stabilité du contenu (`WaitForLoadStateAsync`, ou attente sur un sélecteur précis) avant `ToHaveScreenshotAsync` — éviter les captures "trop tôt".
