using Dalamud.Bindings.ImGui;
using Rowena.Game;
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
internal sealed class SettingsTab(
    Configuration config,
    GatherClock clock,
    MarketCache market,
    CatalogFile catalogue,
    Trades trades,
    BoardWatcher board,
    DiagnosticsPanel diagnostics,
    Action refreshPrices,
    Action save)
{
    private const float NumberWidth = 90f;
    private const float TextWidth = 220f;

    private string? reloadReport;
    private bool reloadFailed;

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
            + "correct, so keep it generous. A row saying it only saw part of the book wants this\n"
            + "raised. Takes effect when the plugin next loads.");

        changed |= Number(
            "Most runs to size", config.SizingCap, value => config.SizingCap = value,
            "The largest number of runs a flip is allowed to be sized at.");

        changed |= Number(
            "Days of selling to plan for", config.SellingHorizonDays, value => config.SellingHorizonDays = value,
            "A sink ranks by what it would bank within this many days, and a flip is sized to the runs\n"
            + "the board would absorb in them: in both, the runs you can afford capped by the runs that\n"
            + "would sell. Prices stay at the floor; what gives is volume. Ranking on the rate alone put\n"
            + "items nobody has ever bought at the top.");

        Group("The furnishing sweep");

        changed |= Toggle(
            "Only look at furnishings", config.CraftFurnishingsOnly, value => config.CraftFurnishingsOnly = value,
            "Off covers everything a crafter can sell, which is nine and a half thousand things\n"
            + "rather than nine hundred. Furnishings are a good market and almost all of them are\n"
            + "the same kind of market, so ranking inside them hid every other kind. On is a\n"
            + "narrower, faster sweep. Takes effect on the next sweep.");

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

        Group("Gathering");

        changed |= Number(
            "How many of one thing you gather a day", config.GatherPerDayCap, value => config.GatherPerDayCap = value,
            "Zero lets the market decide, which flatters anything cheap that moves in bulk: the board\n"
            + "turns over seventy-five thousand water crystals a day and nobody supplies them alone.\n"
            + "Your own hands are the tighter limit almost always.");

        DrawGatherPace();

        changed |= Toggle(
            "Plan with the rate I actually gather at", config.GatherUseMeasured,
            value => config.GatherUseMeasured = value,
            "Counts what arrives in your bags while a node is open, and the time between one\n"
            + "gather and the next. Travel counts, because an hour of gathering is mostly getting\n"
            + "to the next node; long gaps do not, because standing about is not gathering.");

        changed |= Number(
            "Items you gather an hour", config.GatherPerHour, value => config.GatherPerHour = value,
            "Used until enough has been watched to say better, and whenever the measurement is\n"
            + "switched off. Everything in a session plan scales by whichever of the two is in use,\n"
            + "and the plan says which.");

        changed |= Number(
            "A timed node is worth (items)", config.GatherWindowYield, value => config.GatherWindowYield = value,
            "With the next one, what lets a node on a clock be weighed against an ordinary one\n"
            + "rather than dropped for not fitting the assumption. A windowful for the price of the\n"
            + "detour is usually a bargain, which is why they are worth going out of your way for.");

        changed |= Number(
            "The detour to one costs (minutes)", config.GatherWindowMinutes, value => config.GatherWindowMinutes = value,
            "Travel and waiting, not just the gathering. Raise it and timed nodes stop being worth\n"
            + "a short trip, which is the honest behaviour when they are far away.");

        changed |= Number(
            "Gatherables to rank", config.GatherShortlist, value => config.GatherShortlist = value,
            "How many survive the survey and get a full book fetched. The survey itself covers every\n"
            + "marketable gatherable and costs about eight requests.");

        Group("The vendor scan");

        changed |= Number(
            "Smallest find worth showing", config.VendorFindFloor, value => config.VendorFindFloor = value,
            "A stack listed a gil under the vendor price is arithmetic, not an opportunity. Smaller\n"
            + "finds are counted rather than dropped, so the table never hides anything silently.");

        changed |= Number(
            "Candidates to cost", config.VendorCandidatesToCost, value => config.VendorCandidatesToCost = value,
            "How many of the scan's candidates get a full book fetched, widest margin first. The\n"
            + "survey is cheap and wide; this is the expensive half, eight ids a request.");

        Group("Live prices");

        changed |= Toggle(
            "Follow the board as it changes", config.LiveMarket, value => config.LiveMarket = value,
            "Holds a socket open to Universalis, which pushes what changed as it happens. It is a\n"
            + "signal, not prices: what it names is refetched properly, since the feed sends only what\n"
            + "moved and a book rebuilt from those would drift. Cheaper for them than asking repeatedly.");

        Group("Login and alerts");

        changed |= Toggle(
            "Brief me when I log in", config.BriefOnLogin, value => config.BriefOnLogin = value,
            "One line on the Overview after the prices are refetched: what is near its cap, what the\n"
            + "flips pay, how old the sweep is. Nothing is ever written into the game itself, so this\n"
            + "waits in the window rather than arriving in your chat log. /rowena brief asks again.");

        changed |= Toggle(
            "Say when a currency nears its cap", config.AlertNearCap, value => config.AlertNearCap = value,
            "Once, when it enters the last tenth; again only after it has been spent back down.");

        changed |= Number(
            "Say when a flip returns at least (%)", config.AlertFlipReturnPercent, value => config.AlertFlipReturnPercent = value,
            "Once per trade while it holds. Zero turns it off. Read off cached prices, so it is as\n"
            + "fresh as the last fetch and never fetches on its own.");

        changed |= Toggle(
            "Say when the sweep goes stale", config.AlertStaleSweep, value => config.AlertStaleSweep = value,
            "Once, when the furnishing sweep passes its re-sweep age.");

        changed |= Toggle(
            "Say when a timed node opens", config.AlertWindows, value => config.AlertWindows = value,
            "The only thing here that will not still be true in ten minutes. Noted on the Overview,\n"
            + "like everything else: nothing this plugin has to say is worth writing into the game\n"
            + "for. A window advertised as four game hours is under twelve real minutes.");

        changed |= Number(
            "Only for nodes paying at least", config.AlertWindowWorth, value => config.AlertWindowWorth = value,
            "Gil a unit, net. Every window in the game turning up on the Overview would be noise;\n"
            + "the ones worth crossing a zone for are not.");

        Group("Hand-off");

        changed |= Text(
            "Artisan list name", config.ArtisanListName, value => config.ArtisanListName = value,
            "The name the imported crafting list arrives under.");

        Group("What the game has told us");

        DrawTaxRates();

        Group("The catalogue");

        ImGui.TextColored(
            Palette.Dim,
            "Which trades exist is a file, so you can add your own. Reload reads it again without\n"
            + "touching the plugin; a broken edit keeps the trades you already have and says what\n"
            + "was wrong with it.");

        ImGui.TextUnformatted(catalogue.Path);

        if (ImGui.Button("Copy path"))
            ImGui.SetClipboardText(catalogue.Path);

        ImGui.SameLine();

        if (ImGui.Button("Reload"))
            Reload();

        if (reloadReport is { } report)
            ImGui.TextColored(reloadFailed ? Palette.Bad : Palette.Dim, report);

        Group("When something is not working");

        diagnostics.Draw();

        if (!changed)
            return;

        // Pushed rather than read, because the cache was handed its lifetime when it was built and
        // would otherwise keep the old one until the plugin reloaded.
        market.Ttl = config.PriceTtl();
        market.BookBatchSize = config.PriceBatchSize;
        market.SummaryBatchSize = config.SurveyBatchSize;
        save();
    }

    /// <summary>
    /// What gathering has actually been measured at, if anything.
    /// </summary>
    /// <remarks>
    /// Shown with how much it rests on, because a rate is only as good as the time behind it and
    /// a number with no provenance invites more trust than it has earned.
    /// </remarks>
    private void DrawGatherPace()
    {
        if (clock.PerHour is not { } rate)
        {
            ImGui.TextColored(
                Palette.Dim,
                $"    Nothing measured yet: {clock.Tally.Seconds / 60:F0} minutes of gathering watched so far,\n"
                + "    and ten are wanted before a rate off it is worth quoting.");
            return;
        }

        ImGui.TextColored(
            Palette.Good,
            $"    Measured: {rate:F0} items an hour, from {clock.Tally.Items:N0} items over "
            + $"{clock.Tally.Seconds / 60:F0} minutes.");

        ImGui.SameLine();

        if (ImGui.SmallButton("Forget it"))
            clock.Forget();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("For when it stops describing how you gather: a new job, or better gear.");
    }

    /// <summary>
    /// The seller's cut per city, once the game has said what it is.
    /// </summary>
    /// <remarks>
    /// Nought to five percent, moving daily, and every other number here has been assuming the
    /// worst. Shown rather than merely used, because the cheapest city is worth knowing: moving
    /// a retainer is a one-off errand that pays on every sale it ever makes.
    /// </remarks>
    private void DrawTaxRates()
    {
        if (board.SellerRates is not { Count: > 0 } rates)
        {
            ImGui.TextColored(
                Palette.Dim,
                "The seller's cut is nought to five percent by city and moves daily. Open a market\n"
                + "board once and the game says what it is today; until then the worst is assumed.");
            return;
        }

        var cheapest = rates.OrderBy(entry => entry.Value).First();

        ImGui.TextColored(
            Palette.Dim,
            $"Selling from {Cities.Name(cheapest.Key)} costs {cheapest.Value:P0} today, the cheapest of them. "
            + "Rowena prices\nwith the worst of the cities you actually have retainers in.");

        if (!ImGui.BeginTable("tax-rates", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            return;

        ImGui.TableSetupColumn("City", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("seller pays", ImGuiTableColumnFlags.WidthFixed, 90);
        Cell.Headers([null, null]);

        foreach (var (city, rate) in rates.OrderBy(entry => entry.Value))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Cities.Name(city));
            ImGui.TableNextColumn();
            Cell.Right(rate <= cheapest.Value ? Palette.Good : Palette.Plain, $"{rate:P0}");
        }

        ImGui.EndTable();
    }

    /// <summary>
    /// Reads the file again and swaps the trades in when it parses.
    /// </summary>
    /// <remarks>
    /// A successful reload also refetches prices, because new trades arrive with no books
    /// and a table of "no prices yet" reads as a reload that did not work. A failed one
    /// changes nothing: mid-session, falling back to the shipped copy would replace the
    /// trades being looked at, which is worse than keeping them.
    /// </remarks>
    private void Reload()
    {
        var (catalog, report) = catalogue.TryLoad();

        reloadFailed = catalog is null;
        reloadReport = report;

        if (catalog is null)
            return;

        trades.Replace(catalog);
        refreshPrices();
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

    private static bool Toggle(string label, bool value, Action<bool> set, string caption)
    {
        var edited = value;
        var changed = ImGui.Checkbox(label, ref edited);

        if (changed)
            set(edited);

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
