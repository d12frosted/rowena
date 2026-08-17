using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rowena.Game;

/// <summary>
/// Builds the JSON Artisan accepts from its clipboard importer.
/// </summary>
/// <remarks>
/// Artisan has no IPC for building a list. It has GetLists and StartListById for ones that already
/// exist, and CraftItem to start crafting immediately, and nothing in between. What it does have is
/// a button reading "Import List From Clipboard (Artisan Export)", which parses the clipboard as one
/// of its own exported lists and saves it.
///
/// So this writes that shape. It is Artisan's own supported import path rather than a poke at its
/// configuration file, which it holds in memory and would overwrite on its next save.
///
/// One consequence is worth being plain about: the importer always creates a new list. Appending to
/// an existing one is not possible by any route Artisan exposes, which is why items accumulate on
/// this side and go over in a single export.
/// </remarks>
internal static class ArtisanList
{
    /// <param name="RecipeId">Artisan's list items are keyed by recipe, not by item.</param>
    internal readonly record struct Entry(uint RecipeId, int Quantity);

    private static readonly JsonSerializerOptions Options = new()
    {
        // Artisan reads its own exports, which include the fields as well as the properties.
        IncludeFields = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Build(string name, IEnumerable<Entry> entries) =>
        JsonSerializer.Serialize(
            new ArtisanCraftingList
            {
                // Left at zero because the importer calls SetID and assigns its own.
                ID = 0,
                Name = name,
                Recipes =
                [
                    .. entries
                        .Where(entry => entry.RecipeId != 0 && entry.Quantity > 0)
                        .Select(entry => new ArtisanListItem
                        {
                            ID = entry.RecipeId,
                            Quantity = entry.Quantity,
                            ListItemOptions = new ArtisanListItemOptions(),
                        }),
                ],
            },
            Options);

    // Field names and casing have to match Artisan's own classes, so these mirror them exactly
    // rather than being shaped for this codebase.
    private sealed class ArtisanCraftingList
    {
        public int ID { get; set; }

        public string? Name { get; set; }

        public List<ArtisanListItem> Recipes { get; set; } = [];

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

    private sealed class ArtisanListItem
    {
        public uint ID { get; set; }

        public int Quantity { get; set; }

        public ArtisanListItemOptions? ListItemOptions { get; set; }
    }

    private sealed class ArtisanListItemOptions
    {
        public bool NQOnly { get; set; }

        public bool Skipping { get; set; }
    }
}
