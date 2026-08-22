using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Rowena.Core.Market;
using Rowena.Game;
using Rowena.Market;

namespace Rowena.UI;

/// <summary>
/// Drawing an item so it can be looked at and acted on.
/// </summary>
/// <remarks>
/// A table of numbers about things you cannot click is a worse tool than it needs to be. Every
/// row that names an item gets its icon, a tooltip with what the board says about it, and a
/// context menu that hands off to the windows and plugins that already do the job.
///
/// The game's own item tooltip cannot be summoned into an ImGui window, so there are two answers
/// here rather than one: a drawn tooltip carrying the market facts, which is what this window is
/// actually for, and "link in chat" for when the real thing with every stat on it is wanted.
/// </remarks>
internal sealed class ItemCells(
    Items items,
    ITextureProvider textures,
    ItemActions actions,
    MarketCache market,
    PricingScope scope,
    BoardWatcher board)
{
    private const float IconSize = 20f;

    private const uint JobIconBase = 62000;

    /// <summary>An item's name, for rows that are not drawn through <see cref="Draw"/>.</summary>
    public string Name(uint itemId) => items.Name(itemId);

    /// <summary>Draws the icon for an item, or a matching gap when there is none.</summary>
    public void Icon(uint itemId, float size = IconSize) => RawIcon(items.Get(itemId).Icon, size);

    /// <summary>Draws a game icon by its own id, or a matching gap.</summary>
    public void RawIcon(uint iconId, float size = IconSize)
    {
        if (iconId != 0
            && textures.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrDefault() is { } texture)
        {
            ImGui.Image(texture.Handle, new Vector2(size, size));
            return;
        }

        // A gap the size of an icon, so rows without one still line up.
        ImGui.Dummy(new Vector2(size, size));
    }

    /// <summary>
    /// A crafting job as its icon and three letters.
    /// </summary>
    /// <remarks>
    /// The full names do not fit and were being cut to "Woodwo" and "Clothcra", which is worse than
    /// an abbreviation because it looks like a mistake. The icon carries the recognition and the
    /// abbreviation carries the certainty.
    /// </remarks>
    public void Job(uint classJobId, string abbreviation)
    {
        // The class and job icon set runs from 62000, offset by the ClassJob row.
        RawIcon(classJobId == 0 ? 0 : JobIconBase + classJobId, 16f);
        ImGui.SameLine(0f, 4f);
        ImGui.TextColored(Palette.Dim, abbreviation);
    }

    /// <summary>
    /// An item's icon and name, clickable.
    /// </summary>
    /// <param name="recipeId">
    /// When the item is crafted, the recipe behind it. Left-clicking then opens the crafting log,
    /// which is the one action worth having without a menu.
    /// </param>
    /// <param name="materials">Shown in the tooltip, so a craft can be judged without clicking.</param>
    /// <param name="inputsHeading">
    /// What the tooltip calls that list. A craft has materials; a flip has things to buy.
    /// </param>
    /// <param name="refreshTrade">
    /// When the row is a trade, refetches its items' prices. In the menu because it is the
    /// step before committing gil: the numbers are minutes old and the board is not.
    /// </param>
    public void Draw(
        string label,
        uint itemId,
        uint? recipeId = null,
        IReadOnlyList<MaterialLine>? materials = null,
        Action? refreshTrade = null,
        string inputsHeading = "materials")
    {
        ImGui.PushID($"{itemId}-{recipeId ?? 0}");

        Icon(itemId);
        ImGui.SameLine();

        if (ImGui.Selectable(label) && recipeId is { } recipe)
            actions.OpenCraftingLog(recipe);

        if (ImGui.IsItemHovered())
            Tooltip(label, itemId, recipeId, materials, inputsHeading);

        if (ImGui.BeginPopupContextItem("actions"))
        {
            Menu(label, itemId, recipeId, refreshTrade, materials, inputsHeading);
            ImGui.EndPopup();
        }

        ImGui.PopID();
    }

    private void Tooltip(
        string label,
        uint itemId,
        uint? recipeId,
        IReadOnlyList<MaterialLine>? materials,
        string inputsHeading)
    {
        ImGui.BeginTooltip();

        Icon(itemId, 32f);
        ImGui.SameLine();
        ImGui.TextUnformatted(label);

        // Shown for the board you would sell on, since that is what an item is worth to you. The
        // data centre's cheaper listings are what you would pay to buy one, which is a different
        // question and belongs in the material breakdown below.
        var book = scope.Selling is { } selling ? market.Book(selling, itemId) : null;

        if (book?.Floor is { } floor)
        {
            ImGui.TextColored(
                Palette.Dim,
                $"{floor:N0} gil on {scope.Selling}, {book.UnitsListed} listed, "
                + $"{book.SaleVelocityPerDay:F1} sold a day");
            Depth(book);
        }
        else
        {
            ImGui.TextColored(Palette.Bad, $"nothing listed on {scope.Selling ?? "your world"}");
        }

        Mine(itemId, book);

        if (materials is { Count: > 0 })
        {
            ImGui.Separator();
            ImGui.TextColored(Palette.Dim, inputsHeading);

            foreach (var material in materials)
            {
                Icon(material.ItemId, 16f);
                ImGui.SameLine();

                if (material.Sourced)
                    ImGui.TextUnformatted($"{material.Quantity}x {material.Name}   {material.Cost:N0}");
                else
                    ImGui.TextColored(Palette.Bad, $"{material.Quantity}x {material.Name}   not on the board");
            }
        }

        ImGui.Separator();
        ImGui.TextColored(
            Palette.Dim,
            recipeId is null ? "right-click for actions" : "click to open the crafting log, right-click for more");

        ImGui.EndTooltip();
    }

    /// <summary>
    /// What I have out for this item, as the board itself last reported it.
    /// </summary>
    /// <remarks>
    /// Known only for items whose board I have opened, since this comes from the game's own
    /// packets rather than from anywhere that can be asked. Worth saying loudly when it is
    /// known: a row telling me to make more of something I already have three of, or that
    /// somebody now sits under my price, is the difference between a table and an answer.
    /// </remarks>
    private void Mine(uint itemId, OrderBook? book)
    {
        if (board.Listed(itemId) is not { Count: > 0 } listed)
            return;

        var units = listed.Sum(listing => listing.Quantity);
        var cheapest = listed.Min(listing => listing.UnitPrice);

        ImGui.TextColored(Palette.Good, $"you have {units} listed, cheapest at {cheapest:N0}");

        // Undercut is only worth claiming against the same board the listing stands on, and
        // the floor here is the selling board's, which is where my retainers are.
        if (book?.Floor is { } floor && floor < cheapest)
        {
            ImGui.TextColored(
                Palette.Bad,
                $"    undercut: the board is at {floor:N0}, {cheapest - floor:N0} under you");
        }
    }

    /// <summary>
    /// What actually stands behind the floor, as a climb up the book.
    /// </summary>
    /// <remarks>
    /// The floor with three units behind it and the floor with three hundred are completely
    /// different propositions, and the line above this one cannot tell them apart. A few tiers
    /// can: "3 at 48,795, 4 more by 48,799" is the shape of the market, not just its edge.
    ///
    /// Four tiers, because the tooltip is a glance and the far end of a deep book changes no
    /// decision; what is above them is summed rather than dropped, so a wall hiding up there
    /// still registers.
    /// </remarks>
    private static void Depth(OrderBook book)
    {
        var tiers = book.Tiers();

        // One price on the whole board: the line above already said how many stand at it.
        if (tiers.Count < 2)
            return;

        var shown = tiers.Take(4).ToArray();

        var parts = new List<string> { $"{shown[0].CumulativeUnits} at {shown[0].UnitPrice:N0}" };

        for (var i = 1; i < shown.Length; i++)
            parts.Add($"{shown[i].CumulativeUnits - shown[i - 1].CumulativeUnits} more by {shown[i].UnitPrice:N0}");

        var above = book.UnitsListed - shown[^1].CumulativeUnits;
        if (above > 0)
            parts.Add($"{above} dearer still");

        ImGui.TextColored(Palette.Dim, string.Join(", ", parts));
    }

    private void Menu(
        string label,
        uint itemId,
        uint? recipeId,
        Action? refreshTrade,
        IReadOnlyList<MaterialLine>? materials,
        string inputsHeading)
    {
        if (recipeId is { } recipe)
        {
            if (ImGui.MenuItem("Open in crafting log"))
                actions.OpenCraftingLog(recipe);
        }

        if (ImGui.MenuItem("Search the market board"))
            actions.SearchMarketBoard(itemId);

        if (refreshTrade is not null && ImGui.MenuItem("Refresh this trade's prices"))
            refreshTrade();

        // The same three things for each input, because the thing you go and buy is as
        // much the row's business as the thing you sell, and it was reachable only as text
        // in a tooltip. A submenu each, so the flat menu stays about the item named on the row.
        if (materials is { Count: > 0 })
        {
            ImGui.Separator();
            ImGui.TextColored(Palette.Dim, $"   {inputsHeading}");

            foreach (var material in materials)
            {
                ImGui.PushID((int)material.ItemId);

                if (ImGui.BeginMenu($"{material.Quantity}x {material.Name}"))
                {
                    if (ImGui.MenuItem("Search the market board"))
                        actions.SearchMarketBoard(material.ItemId);

                    if (ImGui.MenuItem("Copy name"))
                        ImGui.SetClipboardText(material.Name);

                    ImGui.EndMenu();
                }

                ImGui.PopID();
            }

            ImGui.Separator();
        }

        // The item's own name, not the row's label: on a flip the label is the whole
        // transaction, and what gets pasted into a search box is the thing you sell.
        if (ImGui.MenuItem("Copy name"))
            ImGui.SetClipboardText(items.Name(itemId));

        if (recipeId is not { } craftable)
            return;

        ImGui.Separator();

        if (!actions.CanCraft)
        {
            ImGui.TextColored(Palette.Dim, "   Artisan not found");
        }
        else
        {
            // A busy Artisan would be interrupted, so the entries are shown and disabled rather
            // than hidden, which would read as "this cannot be crafted".
            //
            // Disabled by wrapping, not by the second argument of MenuItem: that argument is
            // "selected", so passing an enabled flag there drew a tick beside every entry.
            var busy = actions.CraftingBusy;

            if (busy)
                ImGui.BeginDisabled();

            // Quantities live in a submenu, so every flat entry in this menu does exactly one
            // thing. "Now" because this really does start crafting rather than queueing anything.
            if (ImGui.BeginMenu("Craft now with Artisan"))
            {
                foreach (var quantity in (int[])[1, 5, 10])
                {
                    if (ImGui.MenuItem($"{quantity}"))
                        actions.Craft(craftable, quantity);
                }

                ImGui.EndMenu();
            }

            if (busy)
            {
                ImGui.EndDisabled();
                ImGui.TextColored(Palette.Dim, "   Artisan is busy");
            }

        }

        ImGui.Separator();

        // Every entry below names where the item ends up. Leaving one of them unnamed made it the
        // hardest to understand of the three, which was the opposite of the intent.
        if (recipeId is not null)
        {
            var waiting = actions.Basket.Count;

            if (ImGui.MenuItem(waiting == 0 ? "Add to Teamcraft list" : $"Add to Teamcraft list ({waiting} so far)"))
                actions.Basket.Add(craftable, itemId, label, 1);

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Collects here first, then opens as one Teamcraft list.\n"
                    + "Teamcraft works out the sub-crafts and exports to Artisan or Vulcan.");
            }
        }

        if (!actions.CanMakeLists)
        {
            ImGui.TextColored(Palette.Dim, "   AllaganTools not found");
            return;
        }

        if (ImGui.MenuItem("Add to a new AllaganTools list"))
            actions.AddToCraftList(label, itemId, 1);

        var existing = actions.CraftLists();
        if (existing.Count > 0 && ImGui.BeginMenu("Add to an AllaganTools list"))
        {
            foreach (var (key, name) in existing)
            {
                if (ImGui.MenuItem(name))
                    actions.AddToExistingList(key, itemId, 1);
            }

            ImGui.EndMenu();
        }
    }

    /// <param name="Sourced">False when the board could not supply it, which is why a row has no price.</param>
    internal readonly record struct MaterialLine(uint ItemId, string Name, int Quantity, long Cost, bool Sourced);
}
