using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rowena.Core.Lists;

/// <param name="RecipeId">Artisan's list entries are keyed by recipe, where Teamcraft uses items.</param>
public readonly record struct ArtisanEntry(uint RecipeId, int Quantity);

/// <summary>
/// Builds the JSON Artisan's own clipboard importer accepts.
/// </summary>
/// <remarks>
/// Kept alongside the Teamcraft link rather than replaced by it, because the two cost very different
/// amounts of effort and only one of them is portable.
///
/// This one is a single paste: copy, then press "Import List From Clipboard (Artisan Export)" in
/// Artisan. The Teamcraft route reaches more tools and resolves sub-crafts, but getting it back into
/// Artisan means opening the site, copying its pre-crafts as text, pasting, copying its final items
/// as text, pasting again, and naming the list. Five steps against one.
///
/// So neither is simply better, and dropping this one in favour of Teamcraft was a mistake: it made
/// the common case five times harder to reach the tool that actually crafts.
/// </remarks>
public static class ArtisanList
{
    private static readonly JsonSerializerOptions Options = new()
    {
        // Artisan reads back its own exports, which carry the plain fields as well as properties.
        IncludeFields = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Build(string name, IEnumerable<ArtisanEntry> entries) =>
        JsonSerializer.Serialize(
            new CraftingList
            {
                // Zero because the importer calls SetID and assigns its own.
                ID = 0,
                Name = name,
                Recipes =
                [
                    .. entries
                        .Where(entry => entry.RecipeId != 0 && entry.Quantity > 0)
                        .Select(entry => new ListItem
                        {
                            ID = entry.RecipeId,
                            Quantity = entry.Quantity,
                            ListItemOptions = new ListItemOptions(),
                        }),
                ],
            },
            Options);

    // Names and casing mirror Artisan's own classes exactly, so these are shaped for it rather than
    // for this codebase.
    private sealed class CraftingList
    {
        public int ID { get; set; }

        public string? Name { get; set; }

        public List<ListItem> Recipes { get; set; } = [];

        public List<uint> ExpandedList { get; set; } = [];

        public bool SkipIfEnough { get; set; }

        public bool SkipLiteral;

        public bool Materia { get; set; }

        public bool Repair { get; set; }

        public int RepairPercent = 50;

        public bool AddAsQuickSynth;

        public bool TidyAfter = true;

        public bool OnlyRestockNonCrafted;
    }

    private sealed class ListItem
    {
        public uint ID { get; set; }

        public int Quantity { get; set; }

        public ListItemOptions? ListItemOptions { get; set; }
    }

    private sealed class ListItemOptions
    {
        public bool NQOnly { get; set; }

        public bool Skipping { get; set; }
    }
}
