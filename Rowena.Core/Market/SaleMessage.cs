using System.Globalization;
using System.Text.RegularExpressions;

namespace Rowena.Core.Market;

/// <summary>
/// What the game says when a retainer sells something, in numbers.
/// </summary>
/// <remarks>
/// The only free record of one's own sales. Universalis knows what the market did; nothing
/// knows what I did, which is the difference between "this sells for about a thousand" and
/// "mine sold for nine hundred after sitting for a week".
///
/// Read from the message text rather than from a packet because the text is what Dalamud
/// hands over, and the item itself arrives beside it as a link rather than as a name, so the
/// one thing that would have been fragile to parse is the one thing that does not need
/// parsing.
///
/// The wording is English, which is a real limit and is stated rather than hidden: a client
/// in another language records nothing rather than recording something wrong.
/// </remarks>
public static partial class SaleMessage
{
    /// <summary>A sale as the message reports it, before anything is known about the item.</summary>
    public readonly record struct Sold(int Quantity, long Gil);

    /// <summary>
    /// Reads a quantity and a price out of a sale message, or nothing when it is not one.
    /// </summary>
    /// <remarks>
    /// The price is taken from beside the word rather than by looking for the largest number
    /// in the line, which would be wrong exactly when it mattered: ninety-nine water crystals
    /// sell for less gil than there are crystals.
    /// </remarks>
    public static Sold? Read(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || Price().Match(text) is not { Success: true } price)
            return null;

        // A leading count, and only a leading one. Names carry digits of their own, and a
        // grade in the middle of one is not a quantity.
        var quantity = Count().Match(text) is { Success: true } count
            ? int.Parse(count.Groups[1].Value, CultureInfo.InvariantCulture)
            : 1;

        return new Sold(
            quantity,
            long.Parse(price.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"sold for ([\d,]+) gil", RegexOptions.IgnoreCase)]
    private static partial Regex Price();

    [GeneratedRegex(@"^The (\d+) ", RegexOptions.IgnoreCase)]
    private static partial Regex Count();
}
