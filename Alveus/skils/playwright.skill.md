---
name: playwright
description: Use this skill to write UI tests in .NET using Playwright for .NET — navigates pages, fills forms, clicks buttons and asserts content. Trigger for requests about tests d'interface, tests UI, Playwright for .NET, tests fonctionnels navigateur.
---

# Playwright for .NET — tests d'interface

## Mise en place

```bash
# 1. Ajouter le package — toujours pinner la version
dotnet add package Microsoft.Playwright --version 1.60.0

# 2. Builder (génère les binaires Playwright dans bin/Debug/net10.0/.playwright/)
dotnet build

# 3. Installer les navigateurs
#    Sur Linux SANS pwsh (PowerShell non installé) :
#    Le package embarque Node.js dans bin/Debug/net10.0/.playwright/node/linux-x64/node
#    playwright.ps1 est juste un wrapper PowerShell — utiliser Node.js directement :
cd bin/Debug/net10.0
.playwright/node/linux-x64/node .playwright/package/cli.js install chromium
cd ../../..
```

> **Note Linux** : `pwsh bin/Debug/net10.0/playwright.ps1 install` est la commande officielle
> documentée, mais elle requiert PowerShell 7 (`pwsh`). Si `pwsh` n'est pas disponible, la
> commande Node.js ci-dessus fait exactement la même chose — c'est ce que le `.ps1` exécute
> en interne.

> **Note version** : ne pas utiliser le global tool `~/.dotnet/tools/playwright` pour installer —
> il est en v1.50 (rev 1155) et ne correspond pas à Microsoft.Playwright 1.60.0 (rev 1223).
> Toujours utiliser la commande Node.js embarquée dans le build output du projet.

## Configuration xUnit

Pour les tests xUnit, ne pas utiliser `Microsoft.Playwright.Xunit` — utiliser la factory
`IPlaywright` directement :

```csharp
using Microsoft.Playwright;

public sealed class MyUiTests : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly IBrowserContext _context;

    public MyUiTests()
    {
        _playwright = Playwright.CreateAsync().GetAwaiter().GetResult();
        _browser = _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
        }).GetAwaiter().GetResult();
        _context = _browser.NewContextAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }

    [Fact]
    public async Task PageTitle_IsCorrect()
    {
        var page = await _context.NewPageAsync();
        await page.GotoAsync("http://localhost:5000");
        var title = await page.TitleAsync();
        Assert.Equal("My App", title);
    }
}
```

## Actions courantes

```csharp
// Navigation
await page.GotoAsync("http://localhost:5000");

// Remplir un champ
await page.FillAsync("input[name='description']", "Ma tâche");

// Cliquer un bouton
await page.ClickAsync("button[type='submit']");

// Attendre que le contenu soit visible
await page.WaitForSelectorAsync("text=Ma tâche");

// Lire le texte d'un élément
var text = await page.TextContentAsync(".task-list");

// Vérifier qu'un élément existe
var element = await page.QuerySelectorAsync("text=Ma tâche");
Assert.NotNull(element);

// Vérifier qu'un élément n'existe pas
var absent = await page.QuerySelectorAsync("text=Tâche supprimée");
Assert.Null(absent);

// Cocher une case
await page.CheckAsync("input[type='checkbox'][data-id='1']");

// Attendre la navigation après un click
await page.RunAndWaitForNavigationAsync(() => page.ClickAsync("a.some-link"));
```

## Lancer l'application avant les tests

L'application Web doit être démarrée AVANT d'exécuter les tests Playwright. Depuis le
workspace de l'Evaluator, démarrer l'application Worker en arrière-plan :

```bash
# Démarrer l'application en arrière-plan (port 5000 par défaut)
nohup dotnet run --project /chemin/vers/app/ --urls http://localhost:5000 \
  > /tmp/app.log 2>&1 &
APP_PID=$!

# Attendre que l'application soit prête
sleep 5

# Vérifier qu'elle répond
curl -s http://localhost:5000 | head -5

# Exécuter les tests
dotnet test

# Arrêter l'application
kill $APP_PID
```

⚠ L'application Worker tourne dans un autre workspace — l'Evaluator n'a pas accès à ses
fichiers. Utiliser le port et l'URL fournis par Alveus-EnvironmentManager dans son résumé.

## Structure de test recommandée

```csharp
[Fact]
public async Task AddTask_AppearsInList()
{
    var page = await _context.NewPageAsync();
    await page.GotoAsync("http://localhost:5000");

    // Ajouter une tâche
    await page.FillAsync("input[name='description']", "Acheter du lait");
    await page.ClickAsync("button[type='submit']");

    // Vérifier qu'elle apparaît dans la liste
    await page.WaitForSelectorAsync("text=Acheter du lait");
    var item = await page.QuerySelectorAsync("text=Acheter du lait");
    Assert.NotNull(item);
}
```

## Points de vigilance

- Fixer le viewport si nécessaire : `new BrowserContextOptions { ViewportSize = new ViewportSize { Width = 1280, Height = 720 } }`
- Attendre la stabilité du DOM avant d'asserter : `WaitForSelectorAsync` ou `WaitForLoadStateAsync(LoadState.NetworkIdle)`
- Si le port 5000 est pris, utiliser un port libre (5001, 5002, etc.)
- Désactiver HTTPS redirect si l'appli force HTTPS — préférer `http://` pour les tests
