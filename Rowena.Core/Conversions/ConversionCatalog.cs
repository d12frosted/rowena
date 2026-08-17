using System.Text.Json;

namespace Rowena.Core.Conversions;

/// <summary>
/// The conversions worth watching, loaded from JSON.
/// </summary>
/// <remarks>
/// Data rather than code because every rate in it is something a patch can change, and
/// adding a sink should be an edit, not a rebuild. A copy ships embedded so the library is
/// useful with no configuration, and <see cref="Load"/> takes an override from disk.
///
/// Chains are composed at load time from the steps they name, so a composed rate is always
/// derived from the published ones. Writing 100,000 out by hand would be a second place for
/// the same fact to live, and it would be the place that went stale.
/// </remarks>
public sealed class ConversionCatalog
{
    private const string EmbeddedName = "Rowena.Core.Conversions.conversions.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, Resource> resources;
    private readonly Dictionary<string, Conversion> conversions;

    private ConversionCatalog(Dictionary<string, Resource> resources, Dictionary<string, Conversion> conversions)
    {
        this.resources = resources;
        this.conversions = conversions;
    }

    /// <summary>The catalogue shipped with the library.</summary>
    public static ConversionCatalog Default { get; } = LoadEmbedded();

    /// <summary>Resources by their catalogue key.</summary>
    public IReadOnlyDictionary<string, Resource> Resources => resources;

    /// <summary>Every conversion, plain and composed alike.</summary>
    public IReadOnlyList<Conversion> Conversions => [.. conversions.Values];

    /// <summary>A conversion by id.</summary>
    public Conversion this[string id] =>
        conversions.TryGetValue(id, out var conversion)
            ? conversion
            : throw new KeyNotFoundException($"No conversion '{id}' in the catalogue.");

    /// <summary>A resource by catalogue key.</summary>
    public Resource ResourceFor(string key) =>
        resources.TryGetValue(key, out var resource)
            ? resource
            : throw new KeyNotFoundException($"No resource '{key}' in the catalogue.");

    public bool TryGetConversion(string id, out Conversion conversion) =>
        conversions.TryGetValue(id, out conversion!);

    public bool TryGetResource(string key, out Resource resource) =>
        resources.TryGetValue(key, out resource);

    /// <summary>Parses a catalogue.</summary>
    /// <exception cref="InvalidDataException">
    /// When the document is malformed, names a resource or step that is not defined, or
    /// reuses an id. All of these are worth failing loudly over: a catalogue that silently
    /// drops half its entries would read as "nothing is worth doing".
    /// </exception>
    public static ConversionCatalog Load(string json)
    {
        CatalogDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<CatalogDocument>(json, Options);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"Catalogue is not valid JSON: {error.Message}", error);
        }

        if (document is null)
            throw new InvalidDataException("Catalogue is empty.");

        var resources = ReadResources(document);
        var conversions = ReadConversions(document, resources);
        ReadChains(document, conversions);

        return new ConversionCatalog(resources, conversions);
    }

    private static Dictionary<string, Resource> ReadResources(CatalogDocument document)
    {
        var resources = new Dictionary<string, Resource>(StringComparer.Ordinal);

        foreach (var (key, entry) in document.Resources ?? new())
        {
            var kind = entry.Kind?.ToLowerInvariant() switch
            {
                "item" => ResourceKind.Item,
                "currency" => ResourceKind.Currency,
                _ => throw new InvalidDataException(
                    $"Resource '{key}' has kind '{entry.Kind}'; expected 'item' or 'currency'."),
            };

            if (entry.Id == 0)
                throw new InvalidDataException($"Resource '{key}' has no id.");

            resources[key] = new Resource(kind, entry.Id, entry.Name ?? key);
        }

        return resources;
    }

    private static Dictionary<string, Conversion> ReadConversions(
        CatalogDocument document,
        Dictionary<string, Resource> resources)
    {
        var conversions = new Dictionary<string, Conversion>(StringComparer.Ordinal);

        foreach (var entry in document.Conversions ?? [])
        {
            var id = entry.Id ?? throw new InvalidDataException("A conversion is missing its id.");

            if (conversions.ContainsKey(id))
                throw new InvalidDataException($"Conversion '{id}' is defined more than once.");

            conversions[id] = new Conversion(
                id,
                entry.Name ?? id,
                ReadAmounts(entry.Inputs, resources, id, "input"),
                ReadAmounts(entry.Outputs, resources, id, "output"),
                entry.Venue ?? "");
        }

        return conversions;
    }

    private static void ReadChains(CatalogDocument document, Dictionary<string, Conversion> conversions)
    {
        foreach (var entry in document.Chains ?? [])
        {
            var id = entry.Id ?? throw new InvalidDataException("A chain is missing its id.");

            if (conversions.ContainsKey(id))
                throw new InvalidDataException($"Chain '{id}' reuses an existing conversion id.");

            var names = entry.Steps ?? [];
            if (names.Count == 0)
                throw new InvalidDataException($"Chain '{id}' names no steps.");

            var steps = names
                .Select(step => conversions.TryGetValue(step, out var found)
                    ? found
                    : throw new InvalidDataException($"Chain '{id}' names unknown step '{step}'."))
                .ToArray();

            Conversion composed;
            try
            {
                composed = ConversionChain.Compose(steps);
            }
            catch (ArgumentException error)
            {
                throw new InvalidDataException($"Chain '{id}' does not compose: {error.Message}", error);
            }

            conversions[id] = composed with { Id = id, Name = entry.Name ?? composed.Name };
        }
    }

    private static IReadOnlyList<ResourceAmount> ReadAmounts(
        List<AmountEntry>? amounts,
        Dictionary<string, Resource> resources,
        string conversionId,
        string role)
    {
        if (amounts is null || amounts.Count == 0)
            throw new InvalidDataException($"Conversion '{conversionId}' has no {role}s.");

        return
        [
            .. amounts.Select(amount =>
            {
                var key = amount.Resource
                    ?? throw new InvalidDataException($"An {role} of '{conversionId}' names no resource.");

                if (!resources.TryGetValue(key, out var resource))
                    throw new InvalidDataException($"Conversion '{conversionId}' names unknown resource '{key}'.");

                if (amount.Quantity <= 0)
                    throw new InvalidDataException(
                        $"The {role} '{key}' of '{conversionId}' has quantity {amount.Quantity}.");

                return new ResourceAmount(resource, amount.Quantity);
            }),
        ];
    }

    /// <summary>
    /// The catalogue text as shipped, for seeding an editable copy somewhere a user can
    /// reach it. Handing them a real file to edit beats documenting the schema.
    /// </summary>
    public static string EmbeddedJson()
    {
        using var stream = typeof(ConversionCatalog).Assembly.GetManifestResourceStream(EmbeddedName)
            ?? throw new InvalidOperationException(
                $"Embedded catalogue '{EmbeddedName}' is missing. Available: "
                + string.Join(", ", typeof(ConversionCatalog).Assembly.GetManifestResourceNames()));

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static ConversionCatalog LoadEmbedded() => Load(EmbeddedJson());

    // Wire shapes. Everything nullable so a malformed catalogue produces one of the
    // messages above rather than a NullReferenceException from somewhere deeper.
    private sealed record CatalogDocument(
        Dictionary<string, ResourceEntry>? Resources,
        List<ConversionEntry>? Conversions,
        List<ChainEntry>? Chains);

    private sealed record ResourceEntry(string? Kind, uint Id, string? Name);

    private sealed record ConversionEntry(
        string? Id,
        string? Name,
        string? Venue,
        List<AmountEntry>? Inputs,
        List<AmountEntry>? Outputs);

    private sealed record AmountEntry(string? Resource, int Quantity);

    private sealed record ChainEntry(string? Id, string? Name, List<string>? Steps);
}
