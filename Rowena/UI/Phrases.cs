using Rowena.Core.Conversions;

namespace Rowena.UI;

/// <summary>
/// Turning numbers into the words a column can hold.
/// </summary>
/// <remarks>
/// Shared rather than repeated, because two views wording the same measurement differently is how a
/// reader ends up believing they are looking at two measurements.
/// </remarks>
internal static class Phrases
{
    /// <summary>How long the board would take to take the whole order off you.</summary>
    /// <remarks>
    /// Null is "never", not "instantly". Nothing sells is the strongest thing this column has to say
    /// and printing a blank there would have hidden it.
    /// </remarks>
    public static string Absorb(double? days) => days switch
    {
        null => "never",
        < 1d => "<1 day",
        _ => $"{days.Value:F1} days",
    };

    /// <summary>
    /// Gil at a width the server bar can afford.
    /// </summary>
    /// <remarks>
    /// The bar gets a handful of characters, so this trades digits for magnitude. Everywhere
    /// with room for the real number shows the real number.
    /// </remarks>
    public static string CompactGil(long gil) => gil switch
    {
        >= 10_000_000 => $"{gil / 1_000_000d:F0}M",
        >= 1_000_000 => $"{gil / 1_000_000d:F1}M",
        >= 10_000 => $"{gil / 1_000d:F0}k",
        _ => $"{gil:N0}",
    };

    /// <summary>
    /// An age, at the precision the age itself deserves.
    /// </summary>
    /// <remarks>
    /// Days at the top end because a listing can sit for a week without anybody asking the board
    /// about it again, and "163 h" is a number you have to do arithmetic on before it means
    /// anything.
    /// </remarks>
    public static string Ago(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "seconds",
        { TotalHours: < 1 } => $"{span.TotalMinutes:F0} min",
        { TotalDays: < 1 } => $"{span.TotalHours:F0} h",
        _ => $"{span.TotalDays:F0} d",
    };

    /// <summary>
    /// The noun for one unit of a currency, for labelling a rate.
    /// </summary>
    /// <remarks>
    /// "gil each" never said each what, and 71.25 next to a column of millions invites reading it as
    /// millions too. The tables are already grouped per currency, so the header only needs the noun:
    /// gil/scrip.
    /// </remarks>
    public static string UnitOf(Resource currency)
    {
        var words = currency.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? "unit" : words[^1].ToLowerInvariant();
    }
}
