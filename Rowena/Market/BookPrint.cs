using System.Security.Cryptography;
using System.Text;
using Rowena.Core.Market;

namespace Rowena.Market;

/// <summary>
/// A short, exact print of a book, so a checker can tell "moved" from "wrong".
/// </summary>
/// <remarks>
/// The floor and the total units listed looked like enough to say whether a book was the one
/// a number was worked out against, and they are not. The board's cut is floored per listing,
/// so what an order costs depends on how the units are split across listings and not only on
/// how many there are: a hundred Mount Tokens taken from a run of thirteen listings that all
/// ask 55,525 gil comes to a different total by a gil or two depending where the boundaries
/// fall, and both totals are right.
///
/// So the print covers the split rather than the summary. Two books with the same print buy
/// identically; two with different prints are different questions, and comparing their
/// answers proves nothing about either.
/// </remarks>
internal static class BookPrint
{
    /// <summary>The listings, in order, as a short hex digest.</summary>
    public static string Of(OrderBook? book)
    {
        if (book is null)
            return "";

        var listings = string.Join(
            "|",
            book.Listings
                .OrderBy(listing => listing.UnitPrice)
                .ThenBy(listing => listing.Quantity)
                .Select(listing => $"{listing.UnitPrice}:{listing.Quantity}"));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(listings)))[..12].ToLowerInvariant();
    }
}
