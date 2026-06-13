# Verify — tests d'API / JSON avec comparaison sélective

## Principe

`Verify(...)` sérialise l'objet passé (DTO, `HttpResponseMessage`, `string` JSON...) et le compare au fichier `NomDeClasse.NomDeMethode.verified.json`. Le premier run génère `.received.json` ; tu le relis, tu le renommes (ou tu utilises l'outil d'accept) en `.verified.json`, et il devient la baseline versionnée.

## Exemple minimal — test d'une réponse API

```csharp
public class OrdersApiTests
{
    private readonly HttpClient _client;

    public OrdersApiTests(WebApplicationFactory<Program> factory)
        => _client = factory.CreateClient();

    [Fact]
    public async Task GetOrder_ReturnsExpectedShape()
    {
        var response = await _client.GetAsync("/api/orders/123");
        var json = await response.Content.ReadAsStringAsync();

        await Verify(json)
            .UseExtension("json");
    }
}
```

~ `.UseExtension("json")` permet d'avoir un fichier `.verified.json` plutôt que `.verified.txt` quand on passe une `string`. Vérifier le nom exact de la méthode dans la version installée (`VerifierSettings` propose plusieurs variantes).

## Exclure des champs volatils : les scrubbers

Les **scrubbers** remplacent une valeur par un placeholder stable (`Guid_1`, `DateTime_1`...) avant comparaison. Ils s'appliquent globalement (dans le `ModuleInitializer`) ou par test.

### Scrubbers par type — global

```csharp
[ModuleInitializer]
public static void Init()
{
    VerifierSettings.ScrubMembers<OrderDto>(o => o.CreatedAt, o => o.Id);
}
```

~ `ScrubMembers<T>` (scrub par expression de membre, multi-types) est l'API que je connais pour ce cas, mais il existe plusieurs surcharges (`ScrubMember`, `ScrubMembersWithType<DateTime>`, etc.) selon que tu veuilles scrubber par nom de propriété, par type, ou par expression. À confirmer dans la doc `VerifyTests/Verify` (section "Scrubbers") avant de figer une convention d'équipe.

### Scrubber par type, toutes occurrences

```csharp
VerifierSettings.ScrubMembersWithType<DateTime>();
VerifierSettings.ScrubMembersWithType<Guid>();
```

→ Tout champ de type `DateTime` ou `Guid`, peu importe son nom, devient `DateTime_1`, `Guid_1`, etc. dans le fichier `.verified.json`. Très utile pour les entités avec `CreatedAt`, `UpdatedAt`, `Id` générés.

### Scrubber par regex sur le JSON brut

Si tu compares une `string` JSON brute plutôt qu'un objet typé :

```csharp
await Verify(json)
    .ScrubLinesWithReplace(line =>
        Regex.Replace(line, "\"correlationId\":\\s*\"[^\"]+\"", "\"correlationId\": \"SCRUBBED\""));
```

~ `ScrubLinesWithReplace` existe dans Verify pour des transformations ligne-par-ligne ; la signature précise (delegate `Func<string,string>` vs autre) à vérifier.

## Comparer uniquement un sous-ensemble de champs (ta demande "certains champs JSON")

Deux approches, à choisir selon ton besoin :

### Approche A — Projection avant Verify (recommandée, la plus explicite)

Plutôt que de scrubber ce qu'on veut *ignorer*, on **projette** ce qu'on veut *vérifier* dans un objet anonyme dédié. C'est la méthode la plus lisible et la plus stable dans le temps — le fichier `.verified.json` ne contient que ce qui compte pour ce test.

```csharp
[Fact]
public async Task GetOrder_BusinessFieldsAreCorrect()
{
    var response = await _client.GetAsync("/api/orders/123");
    var order = await response.Content.ReadFromJsonAsync<OrderDto>();

    // On ne vérifie que les champs métier pertinents pour ce test,
    // pas l'enveloppe complète (timestamps, liens HATEOAS, etc.)
    await Verify(new
    {
        order!.Status,
        order.CustomerId,
        order.TotalAmount,
        Items = order.Items.Select(i => new { i.Sku, i.Quantity })
    });
}
```

Avantage : un changement dans un champ non vérifié (ex: ajout d'un nouveau champ technique dans la réponse) ne casse pas le test. Inconvénient : si l'API change de forme sur un champ non projeté, ce test ne le détecte pas — combine avec un test de contrat / schéma à part si nécessaire.

### Approche B — `IgnoreMember` / `IgnoreMembersWithType` (pour ignorer ponctuellement)

```csharp
await Verify(order)
    .IgnoreMember<OrderDto>(o => o.TraceId)
    .IgnoreMember<OrderDto>(o => o.ProcessedAt);
```

~ `IgnoreMember` existe pour exclure des propriétés du JSON sérialisé lui-même (elles n'apparaissent pas du tout dans `.verified.json`, contrairement aux scrubbers qui les remplacent par un placeholder). À vérifier : disponibilité pour des types imbriqués / collections.

### Recommandation

Pour ton cas ("certains champs JSON" sélectionnés) → **Approche A** (projection). C'est explicite, versionnable, et chaque test documente *quels champs métier sont contractuels* — ce qui est un plus pour la traçabilité 62304 (le `.verified.json` devient lisible comme une spec).

## Comparaison de fragments JSON spécifiques (JSONPath)

Si tu dois extraire un sous-arbre JSON précis depuis une réponse volumineuse :

```csharp
using System.Text.Json;

var doc = JsonDocument.Parse(json);
var fragment = doc.RootElement
    .GetProperty("data")
    .GetProperty("order")
    .GetProperty("lineItems");

await Verify(fragment.GetRawText())
    .UseExtension("json");
```

⚠ Pas de support JSONPath natif connu dans Verify lui-même — l'extraction se fait avec `System.Text.Json` (ou `Newtonsoft.Json` / `JsonNode`) avant l'appel à `Verify`. Si un package `Verify.*` propose du JSONPath natif, je ne le connais pas avec certitude — vérifier sur le repo `VerifyTests`.

## Paramétrage par cas de test (theory / paramétré)

```csharp
[Theory]
[InlineData("123")]
[InlineData("456")]
public async Task GetOrder_ForVariousIds(string orderId)
{
    var response = await _client.GetAsync($"/api/orders/{orderId}");
    await Verify(await response.Content.ReadAsStringAsync())
        .UseParameters(orderId)
        .UseExtension("json");
}
```

→ Génère `GetOrder_ForVariousIds.123.verified.json`, `GetOrder_ForVariousIds.456.verified.json`, etc.

## Points de vigilance

- Versionner les `.verified.*` dans Git ; ignorer les `.received.*`.
- Trier les collections avant `Verify` si l'ordre n'est pas garanti par l'API (sinon faux positifs de diff).
- Pour les dates relatives ("il y a 2 jours"), scrubber systématiquement — sinon le test devient flaky avec le temps.
