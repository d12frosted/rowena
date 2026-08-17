using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
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
internal sealed class ItemCells(Items items, ITextureProvider textures, ItemActions actions, MarketCache market)
{
    private static readonly Vector4 Dim = new(0.60f, 0.60f, 0.62f, 1f);
    private static readonly Vector4 Bad = new(0.85f, 0.45f, 0.40f, 1f);

    private const float IconSize = 20f;

    /// <summary>Draws the icon for an item, or a matching gap when there is none.</summary>
    public void Icon(uint itemId, float size = IconSize)
    {
        var entry = items.Get(itemId);

        if (entry.HasIcon
            && textures.GetFromGameIcon(new GameIconLookup(entry.Icon)).GetWrapOrDefault() is { } texture)
        {
            ImGui.Image(texture.Handle, new Vector2(size, size));
            return;
        }

        // A gap the size of an icon, so rows without one still line up.
        ImGui.Dummy(new Vector2(size, size));
    }

    /// <summary>
    /// An item's icon and name, clickable.
    /// </summary>
    /// <param name="recipeId">
    /// When the item is crafted, the recipe behind it. Left-clicking then opens the crafting log,
    /// which is the one action worth having without a menu.
    /// </param>
    /// <param name="materials">Shown in the tooltip, so a craft can be judged without clicking.</param>
    public void Draw(
        string label,
        uint itemId,
        uint? recipeId = null,
        IReadOnlyList<MaterialLine>? materials = null)
    {
        ImGui.PushID($"{itemId}-{recipeId ?? 0}");

        Icon(itemId);
        ImGui.SameLine();

        if (ImGui.Selectable(label) && recipeId is { } recipe)
            actions.OpenCraftingLog(recipe);

        if (ImGui.IsItemHovered())
            Tooltip(label, itemId, recipeId, materials);

        if (ImGui.BeginPopupContextItem("actions"))
        {
            Menu(label, itemId, recipeId);
            ImGui.EndPopup();
        }

        ImGui.PopID();
    }

    private void Tooltip(string label, uint itemId, uint? recipeId, IReadOnlyList<MaterialLine>? materials)
    {
        ImGui.BeginTooltip();

        Icon(itemId, 32f);
        ImGui.SameLine();
        ImGui.TextUnformatted(label);

        var book = market.Book(itemId);
        if (book?.Floor is { } floor)
        {
            ImGui.TextColored(
                Dim,
                $"{floor:N0} gil, {book.UnitsListed} listed, {book.SaleVelocityPerDay:F1} sold a day");
        }
        else
        {
            ImGui.TextColored(Bad, "nothing listed");
        }

        if (materials is { Count: > 0 })
        {
            ImGui.Separator();
            ImGui.TextColored(Dim, "materials");

            foreach (var material in materials)
            {
                Icon(material.ItemId, 16f);
                ImGui.SameLine();

                if (material.Sourced)
                    ImGui.TextUnformatted($"{material.Quantity}x {material.Name}   {material.Cost:N0}");
                else
                    ImGui.TextColored(Bad, $"{material.Quantity}x {material.Name}   not on the board");
            }
        }

        ImGui.Separator();
        ImGui.TextColored(
            Dim,
            recipeId is null ? "right-click for actions" : "click to open the crafting log, right-click for more");

        ImGui.EndTooltip();
    }

    private void Menu(string label, uint itemId, uint? recipeId)
    {
        if (recipeId is { } recipe)
        {
            if (ImGui.MenuItem("Open in crafting log"))
                actions.OpenCraftingLog(recipe);
        }

        if (ImGui.MenuItem("Search the market board"))
            actions.SearchMarketBoard(itemId);

        if (ImGui.MenuItem("Link in chat"))
            actions.LinkInChat(itemId);

        if (recipeId is not { } craftable)
            return;

        ImGui.Separator();

        if (actions.CanCraft)
        {
            // A busy Artisan would be interrupted, so the entries are shown and disabled rather
            // than hidden, which would read as "this cannot be crafted".
            var busy = actions.CraftingBusy;

            foreach (var quantity in (int[])[1, 5, 10])
            {
                if (ImGui.MenuItem($"Craft {quantity} with Artisan", !busy))
                    actions.Craft(craftable, quantity);
            }

            if (busy)
                ImGui.TextColored(Dim, "   Artisan is busy");
        }
        else
        {
            ImGui.TextColored(Dim, "   Artisan not found");
        }

        if (actions.CanMakeLists)
        {
            if (ImGui.MenuItem("Add 5 to an AllaganTools list"))
                actions.AddToCraftList(label, itemId, 5);
        }
        else
        {
            ImGui.TextColored(Dim, "   AllaganTools not found");
        }
    }

    /// <param name="Sourced">False when the board could not supply it, which is why a row has no price.</param>
    internal readonly record struct MaterialLine(uint ItemId, string Name, int Quantity, long Cost, bool Sourced);
}
