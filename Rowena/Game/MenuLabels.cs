using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Rowena.Game;

/// <summary>
/// The game's own words for the menu entries this plugin clicks.
/// </summary>
/// <remarks>
/// A context menu is clicked by index, and an index is a guess: the entries an item offers
/// depend on where it is sitting and what can be done with it, so the third entry is not
/// reliably the same thing twice. Guessing wrong on an inventory menu is not a harmless miss,
/// because "Discard" is on that menu too.
///
/// So nothing here is clicked by position. The entry is found by matching its text against the
/// game's own string for the action, read out of the sheet the game draws the menu from, which
/// means it is in the player's language rather than in mine. No match is a stop, not a guess.
///
/// The row numbers are read off the sheet rather than remembered: more than one row carries the
/// same words, so every candidate is collected and any of them counts as a match. A patch that
/// moves them makes the match fail, which stops the run rather than clicking something else.
/// </remarks>
internal sealed class MenuLabels(IDataManager data)
{
    /// <summary>Rows of the addon sheet that read "Put Up for Sale" in English.</summary>
    private static readonly uint[] PutUpForSaleRows = [99, 956];

    /// <summary>Rows that read "Discard", which nothing here ever clicks.</summary>
    private static readonly uint[] DiscardRows = [91];

    private IReadOnlySet<string>? sale;
    private IReadOnlySet<string>? discard;

    /// <summary>What the menu calls putting something on the market, in the player's language.</summary>
    public IReadOnlySet<string> PutUpForSale => sale ??= Read(PutUpForSaleRows);

    /// <summary>What it calls throwing something away, so that entry can be refused by name.</summary>
    public IReadOnlySet<string> Discard => discard ??= Read(DiscardRows);

    private IReadOnlySet<string> Read(uint[] rows)
    {
        var sheet = data.GetExcelSheet<Addon>();

        return rows
            .Select(row => sheet.GetRowOrDefault(row)?.Text.ExtractText())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!.Trim())
            .ToHashSet(StringComparer.Ordinal);
    }
}
