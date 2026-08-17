using Dalamud.Bindings.ImGui;
using Rowena.Market;

namespace Rowena.UI;

/// <summary>
/// The knobs, in the window rather than in a file.
/// </summary>
/// <remarks>
/// These lived only in the configuration JSON, which meant the two that decide whether any number
/// here is even right, which boards to price against, could only be set by alt-tabbing and editing a
/// file. Dalamud's own settings gear opened the market screen, which was worse than having no gear:
/// it said the settings were somewhere they were not.
///
/// Every caption says what the number does to the plugin rather than restating its name. Several of
/// them are measurements rather than preferences, and one of them, the request size, makes sweeps
/// fail rather than go faster when raised, so it says so.
/// </remarks>
internal sealed class SettingsTab(Configuration config, MarketCache market, Action save)
{
    private const float NumberWidth = 90f;
    private const float TextWidth = 220f;

    public void Draw()
    {
        var changed = false;

        Group("Where you are pricing");

        changed |= Text(
            "Buying board", config.Scope, value => config.Scope = value,
            "A world, data centre or region. Empty means the data centre you are logged in to, which\n"
            + "is right almost always: listings sit on worlds and you can travel to any of them.");

        changed |= Text(
            "Selling board", config.HomeScope, value => config.HomeScope = value,
            "Empty means the world you are logged in to. Your retainers sell where they stand, so this\n"
            + "is not the same board as the one above and should usually be a single world.");

        Group("Prices");

        changed |= Number(
            "Refetch after (minutes)", config.PriceTtlMinutes, value => config.PriceTtlMinutes = value,
            "How long a price snapshot is trusted. Universalis is free and crowdsourced and asks\n"
            + "callers to be reasonable; nothing here moves minute to minute in a way that matters.");

        changed |= Number(
            "Listings per item", config.ListingDepth, value => config.ListingDepth = value,
            "How deep the book is fetched. A shallow fetch is the exact mistake this plugin exists to\n"
            + "correct, so keep it generous. Takes effect when the plugin next loads.");

        changed |= Number(
            "Most runs to size", config.SizingCap, value => config.SizingCap = value,
            "The largest number of runs a flip is allowed to be sized at.");

        Group("The furnishing sweep");

        changed |= Number(
            "Furnishings to cost", config.FurnishingShortlist, value => config.FurnishingShortlist = value,
            "How many survive the first pass and get their materials priced. The leaders are\n"
            + "comfortably inside sixty.");

        changed |= Number(
            "Re-sweep after (hours)", config.SweepMaxAgeHours, value => config.SweepMaxAgeHours = value,
            "Hours, not the minutes the price tables want. Choosing what to make needs a rough map,\n"
            + "not live depth, and treating the two the same means either a stale flip or a sweep you\n"
            + "cannot afford to repeat.");

        changed |= Number(
            "Crafts a day you can do", config.CraftsPerDayCap, value => config.CraftsPerDayCap = value,
            "Zero lets the market decide, which flatters anything quick to make. Retainer slots\n"
            + "usually bind before the market's appetite does.");

        changed |= Number(
            "Ids per price request", config.PriceBatchSize, value => config.PriceBatchSize = value,
            "Eight, measured rather than chosen. Universalis times out at ten seconds and its response\n"
            + "time grows with the id count, so raising this does not make a sweep faster: it makes it\n"
            + "fail.");

        changed |= Number(
            "Ids per summary request", config.SurveyBatchSize, value => config.SurveyBatchSize = value,
            "A hundred, comfortably. A summary carries no depth, which is why the first pass of a\n"
            + "sweep can be this wide and the second cannot.");

        Group("Hand-off");

        changed |= Text(
            "Artisan list name", config.ArtisanListName, value => config.ArtisanListName = value,
            "The name the imported crafting list arrives under.");

        Group("The catalogue");

        ImGui.TextColored(
            Palette.Dim,
            "Which trades exist is a file, so you can add your own. It is read once, when the plugin\n"
            + "loads; a broken edit costs you the edit and falls back to the shipped copy.");

        var path = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "conversions.json");
        ImGui.TextUnformatted(path);

        if (ImGui.Button("Copy path"))
            ImGui.SetClipboardText(path);

        if (!changed)
            return;

        // Pushed rather than read, because the cache was handed its lifetime when it was built and
        // would otherwise keep the old one until the plugin reloaded.
        market.Ttl = config.PriceTtl();
        save();
    }

    private static void Group(string title)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted(title);
        ImGui.Separator();
    }

    private static bool Number(string label, int value, Action<int> set, string caption)
    {
        ImGui.SetNextItemWidth(NumberWidth);

        var edited = value;
        var changed = ImGui.InputInt(label, ref edited);

        if (changed)
            set(Math.Max(0, edited));

        Caption(caption);
        return changed;
    }

    private static bool Text(string label, string value, Action<string> set, string caption)
    {
        ImGui.SetNextItemWidth(TextWidth);

        var edited = value;
        var changed = ImGui.InputText(label, ref edited, 64);

        if (changed)
            set(edited);

        Caption(caption);
        return changed;
    }

    private static void Caption(string caption)
    {
        ImGui.Indent();
        ImGui.TextColored(Palette.Dim, caption);
        ImGui.Unindent();
        ImGui.Spacing();
    }
}
