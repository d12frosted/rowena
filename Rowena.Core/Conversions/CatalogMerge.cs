namespace Rowena.Core.Conversions;

/// <summary>
/// Joins hand-written conversions with generated ones, hand-written first.
/// </summary>
/// <remarks>
/// A generated catalogue and the file can describe the same trade, and when they do the
/// file's copy wins: it carries the venue and handoff someone bothered to write, where the
/// generated one carries whatever a sheet happened to say. Sameness is the trade itself,
/// what goes in and what comes out at what rate, not the id or the name; two NPCs offering
/// the same exchange are one opportunity, so duplicates among the generated collapse too.
/// </remarks>
public static class CatalogMerge
{
    public static IReadOnlyList<Conversion> Merge(
        IReadOnlyList<Conversion> hand,
        IReadOnlyList<Conversion> generated)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<Conversion>(hand.Count + generated.Count);

        foreach (var conversion in hand.Concat(generated))
        {
            if (seen.Add(Signature(conversion)))
                merged.Add(conversion);
        }

        return merged;
    }

    /// <summary>The trade as a rate, insensitive to the order its sides were written in.</summary>
    private static string Signature(Conversion conversion) =>
        $"{Side(conversion.Inputs)}>{Side(conversion.Outputs)}";

    private static string Side(IReadOnlyList<ResourceAmount> amounts) =>
        string.Join(
            "+",
            amounts
                .Select(amount => $"{amount.Resource.Kind}:{amount.Resource.Id}x{amount.Quantity}")
                .OrderBy(part => part, StringComparer.Ordinal));
}
