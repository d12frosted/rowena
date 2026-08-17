using System.Text;

namespace Rowena.Core.Lists;

/// <param name="ItemId">
/// The item, not the recipe. Teamcraft works out for itself which recipe makes it.
/// </param>
public readonly record struct TeamcraftEntry(uint ItemId, int Quantity);

/// <summary>
/// Builds a Teamcraft import link.
/// </summary>
/// <remarks>
/// Chosen over writing any one plugin's own format because it is the only thing the ecosystem shares.
/// Artisan imports and exports it, GatherBuddyReborn's Vulcan window has a tab for it, and Teamcraft
/// itself is the website everyone already uses. One export reaches all of them; a plugin-specific
/// format reaches one and rots when that plugin changes.
///
/// It also settles the sub-craft problem without any work here. Teamcraft resolves the tree from the
/// finished items, which is why Artisan's own exporter deliberately strips intermediates before
/// building the link rather than listing them.
///
/// The payload is "itemId,null,quantity" per entry, semicolon separated, then base64. The null is a
/// slot Teamcraft uses for a recipe id when the caller has a preference; leaving it empty lets
/// Teamcraft choose, which is what we want when several jobs can make the same thing.
/// </remarks>
public static class TeamcraftList
{
    public const string ImportBase = "https://ffxivteamcraft.com/import/";

    /// <summary>The base64 payload, without the URL around it.</summary>
    public static string Encode(IEnumerable<TeamcraftEntry> entries)
    {
        var payload = string.Join(
            ';',
            entries
                .Where(entry => entry.ItemId != 0 && entry.Quantity > 0)
                .Select(entry => $"{entry.ItemId},null,{entry.Quantity}"));

        return payload.Length == 0 ? "" : Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>The full link, or empty when there is nothing to send.</summary>
    public static string Url(IEnumerable<TeamcraftEntry> entries)
    {
        var encoded = Encode(entries);
        return encoded.Length == 0 ? "" : ImportBase + encoded;
    }
}
