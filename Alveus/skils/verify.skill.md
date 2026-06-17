---
name: verify
description: Use this skill to write API/JSON snapshot tests in .NET using Verify (VerifyTests/Verify) — serializes objects or HTTP responses and compares them to a versioned .verified.json baseline, with scrubbing of volatile fields (timestamps, GUIDs, generated IDs) and selective projection of only the fields that matter. Trigger for requests about tests d'API, comparaison JSON, snapshot testing, approval testing, golden master, réponses HTTP, Verify.Xunit, traçabilité IEC 62304.
---

# Verify — snapshot testing d'API et JSON en .NET

## Principe

`Verify(...)` sérialise l'objet passé (DTO, `HttpResponseMessage`, `string` JSON…) et le compare au fichier `NomDeClasse.NomDeMethode.verified.json`. Le premier run génère un `.received.json` ; tu le relis, tu le valides, tu le renommes en `.verified.json` — c'est la baseline versionnée. Les runs suivants comparent automatiquement.

C'est l'inverse d'une assertion manuelle (`Assert.Equal`) : la baseline est **générée puis validée**, pas écrite à la main. Praticable pour des réponses API complètes, utile pour la traçabilité IEC 62304 (le `.verified.json` est lisible comme une spec).

## Mise en place

```bash
dotnet add package Verify.Xunit   # ou Verify.NUnit / Verify.MSTest selon le framework
```

~ Noms de packages : `Verify.Xunit`, `Verify.NUnit`, `Verify.MSTest` — vérifier sur NuGet.org pour la version courante.

`.gitignore` à ajouter dans le dossier de tests :

```
*.received.json
*.received.txt
```

## Initialisation globale (une seule fois par projet)

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

~ `DerivePathInfo` — API connue pour personnaliser l'emplacement des `.verified.*` ; confirmer la signature dans la doc officielle.

## Exemple minimal — réponse API

```csharp
public class OrdersApiTests(WebApplicationFactory<Program> factory)
{
    [Fact]
    public async Task GetOrder_ReturnsExpectedShape()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/orders/123");
        var json = await response.Content.ReadAsStringAsync();

        await Verify(json).UseExtension("json");
    }
}
```

~ `.UseExtension("json")` produit un `.verified.json` plutôt que `.verified.txt` — vérifier le nom exact dans la version installée.

## Exclure les champs volatils : scrubbers

Les **scrubbers** remplacent une valeur par un placeholder stable (`Guid_1`, `DateTime_1`…) avant comparaison.

### Par type — global (recommandé)

```csharp
[ModuleInitializer]
public static void Init()
{
    VerifierSettings.ScrubMembersWithType<DateTime>();
    VerifierSettings.ScrubMembersWithType<Guid>();
}
```

→ Tout champ `DateTime` ou `Guid`, quel que soit son nom, devient `DateTime_1`, `Guid_1`, etc.

### Par membre — ciblé

```csharp
VerifierSettings.ScrubMembers<OrderDto>(o => o.CreatedAt, o => o.Id);
```

~ `ScrubMembers<T>` est l'API connue ; il existe plusieurs surcharges (`ScrubMember`, `ScrubMembersWithType`…) — confirmer dans la doc section "Scrubbers".

### Par regex sur JSON brut

```csharp
await Verify(json)
    .ScrubLinesWithReplace(line =>
        Regex.Replace(line, "\"correlationId\":\\s*\"[^\"]+\"", "\"correlationId\": \"SCRUBBED\""));
```

## Ne vérifier qu'un sous-ensemble de champs

### Approche A — projection (recommandée)

```csharp
[Fact]
public async Task GetOrder_BusinessFieldsAreCorrect()
{
    var order = await client.GetFromJsonAsync<OrderDto>("/api/orders/123");

    await Verify(new
    {
        order!.Status,
        order.CustomerId,
        order.TotalAmount,
        Items = order.Items.Select(i => new { i.Sku, i.Quantity })
    });
}
```

Le `.verified.json` ne contient que les champs contractuels — lisible comme une spec, stable face aux ajouts futurs.

### Approche B — `IgnoreMember` (ponctuel)

```csharp
await Verify(order)
    .IgnoreMember<OrderDto>(o => o.TraceId)
    .IgnoreMember<OrderDto>(o => o.ProcessedAt);
```

~ `IgnoreMember` exclut les propriétés du JSON sérialisé (elles n'apparaissent pas du tout, contrairement aux scrubbers qui les remplacent par un placeholder).

## Tests paramétrés (Theory)

```csharp
[Theory]
[InlineData("123")]
[InlineData("456")]
public async Task GetOrder_ForVariousIds(string orderId)
{
    var response = await client.GetAsync($"/api/orders/{orderId}");
    await Verify(await response.Content.ReadAsStringAsync())
        .UseParameters(orderId)
        .UseExtension("json");
}
```

→ Génère `GetOrder_ForVariousIds.123.verified.json`, `GetOrder_ForVariousIds.456.verified.json`, etc.

## Workflow de validation manuelle

1. Lancer les tests → les fichiers `.received.json` apparaissent.
2. Relire le contenu, vérifier qu'il correspond au résultat attendu.
3. Renommer `.received.json` → `.verified.json` (ou utiliser `Verify.Terminal` / extensions IDE).
4. Committer le `.verified.json` — c'est la trace d'approbation (utile pour IEC 62304).

~ `Verify.Terminal` (CLI `dotnet tool`) — état exact à confirmer dans le repo `VerifyTests/Verify` avant de bâtir un workflow d'équipe.

## Intégration CI

- Les `.verified.json` sont versionnés dans Git ; les `.received.json` sont ignorés.
- En CI les tests échouent normalement si la baseline diffère — seul un humain en local peut promouvoir un `.received` en `.verified`.
- Trier les collections avant `Verify` si l'ordre n'est pas garanti (évite les faux positifs).
- Scrubber systématiquement les dates relatives et les IDs générés — sinon flakiness avec le temps.
