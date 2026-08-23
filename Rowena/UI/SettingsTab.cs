using Dalamud.Bindings.ImGui;
using Rowena.Core.Conversions;
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
    Realised realised,
    MarketCache market,
    CatalogFile catalogue,
    Trades trades,
    Balances balances,
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

        Group("What I have actually been managing");

        DrawRealised();

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

        Group("Undercutting");

        changed |= Number(
            "Gil under the cheapest listing in front", config.UndercutBy, value => config.UndercutBy = Math.Max(0, value),
            "The undercut price is the cheapest listing in front of yours less this. Being first in\n"
            + "the queue is the whole point, and a gil does it as well as a thousand; this only keeps\n"
            + "a tie from deciding.");

        changed |= Toggle(
            "Fill the undercut price into the retainer's price dialog", config.UndercutFillsPrice,
            value => config.UndercutFillsPrice = value,
            "When the game's price dialog opens on one of your undercut listings, the field is set to\n"
            + "the undercut price. Only the field: confirming it is still the game's button, and items\n"
            + "you have marked as ignored on the Selling tab are never touched."
            + (config.UndercutIgnored.Count > 0
                ? $"\nIgnoring {config.UndercutIgnored.Count} item{(config.UndercutIgnored.Count == 1 ? "" : "s")} at the moment."
                : ""));

        changed |= Toggle(
            "Confirm the price dialog when repricing from the overlay", config.UndercutConfirms,
            value => config.UndercutConfirms = value,
            "A repricing started from the overlay's button also presses the dialog's confirm, so a\n"
            + "run over a whole retainer is one click. Off, each dialog waits for you. A dialog you\n"
            + "opened by hand is never confirmed for you either way.");

        changed |= Toggle(
            "Show the undercut column beside the retainer's sell list", config.UndercutOverlay,
            value => config.UndercutOverlay = value,
            "One row per listing, in the list's order, with the undercut price and a button that\n"
            + "opens the game's price dialog on that listing with the price filled in.");

        Group("The craft sweep");

        changed |= Toggle(
            "Only look at furnishings", config.CraftFurnishingsOnly, value => config.CraftFurnishingsOnly = value,
            "Off covers everything a crafter can sell, which is nine and a half thousand things\n"
            + "rather than nine hundred. Craftables are a good market and almost all of them are\n"
            + "the same kind of market, so ranking inside them hid every other kind. On is a\n"
            + "narrower, faster sweep. Takes effect on the next sweep.");

        changed |= Number(
            "Crafts to cost", config.FurnishingShortlist, value => config.FurnishingShortlist = value,
            "How many survive the first pass and get their materials priced. The leaders are\n"
            + "comfortably inside sixty.");

        changed |= Number(
            "Of those, kept for quiet markets", config.CraftNicheSlots, value => config.CraftNicheSlots = value,
            "Turnover is what a thing costs times how fast it sells, so ranking on it leans\n"
            + "towards things that move. A market that turns over little because almost nobody\n"
            + "wants it is also one almost nobody is supplying, and these slots are the only way\n"
            + "such a thing is ever costed at all. Zero is the old behaviour.");

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
            "Say when I am undercut into a queue", config.AlertUndercut,
            value => config.AlertUndercut = value,
            "Arrives when the price moves rather than on a timer. Not whenever somebody is cheaper\n"
            + "than me, which is the easy question and usually the wrong one: three units ahead on a\n"
            + "board selling ten a day are gone this afternoon. Only when the queue is longer than\n"
            + "the days of selling I said I wanted.");

        changed |= Toggle(
            "Say when a vendor find appears", config.AlertVendorFind,
            value => config.AlertVendorFind = value,
            "Something listed for less than a vendor pays. The one alert that is worthless late:\n"
            + "these are underpriced by definition, so anybody else watching can see them too and\n"
            + "they are gone within minutes. Uses the same floor as the vendor tab.");

        changed |= Toggle(
            "Say when a timed node opens", config.AlertWindows, value => config.AlertWindows = value,
            "The only thing here that will not still be true in ten minutes. Noted on the Overview,\n"
            + "like everything else: nothing this plugin has to say is worth writing into the game\n"
            + "for. A window advertised as four game hours is under twelve real minutes.");

        changed |= Number(
            "Only for nodes paying at least", config.AlertWindowWorth, value => config.AlertWindowWorth = value,
            "Gil a unit, net. Every window in the game turning up on the Overview would be noise;\n"
            + "the ones worth crossing a zone for are not.");

        Group("The strip across the top");

        changed |= DrawPinnedCurrencies();

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
    /// What share of a market my sales have been coming to.
    /// </summary>
    /// <remarks>
    /// Every ranking here is a ceiling that assumes taking every sale at today's price. This is
    /// the only thing that says how far short of it I actually land, and it is a measurement
    /// rather than a fudge: what the boards turned over is known and what I sold is recorded.
    /// </remarks>
    private void DrawRealised()
    {
        if (realised.Share is not { } share)
        {
            ImGui.TextColored(
                Palette.Dim,
                "    Every gil a day figure here is a ceiling: it assumes you take every sale at\n"
                + $"    today's price. How far short of it you land is not measured yet, because\n"
                + $"    {realised.Missing}.\n"
                + "    Weighed against a fraction of your sales the answer would be drawn from\n"
                + "    whichever items happened to be priced, which is not a sample of anything.");

            return;
        }

        ImGui.TextColored(
            Palette.Good,
            $"    You have been taking about {share:P0} of a market, from {realised.Seen} sales of your own\n"
            + "    against what those boards turned over in the same time.");

        ImGui.TextColored(
            Palette.Dim,
            $"    Weighed over {realised.Coverage:P0} of your recent sales. The rest are things Universalis\n"
            + "    reports no sale rate for, which no amount of waiting fixes.");

        ImGui.TextColored(
            Palette.Dim,
            "    So a row promising a million a day is nearer "
            + $"{realised.Expect(1_000_000):N0}. The rankings still order by\n"
            + "    the ceiling, which is the right order; this is what the number means.");
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
    /// <summary>
    /// Which currencies are always up there, one checkbox per currency the catalogue knows.
    /// </summary>
    /// <remarks>
    /// Everything else shows only as a warning when it nears its cap, so this is the whole
    /// answer to "why is that one there". The list is the catalogue's rather than your
    /// pockets', so a currency can be pinned before the first one is earned.
    /// </remarks>
    private bool DrawPinnedCurrencies()
    {
        ImGui.TextColored(
            Palette.Dim,
            "Always shown, whatever the balance, in this order. Anything unticked appears only once\n"
            + "it is into the last tenth of its cap, in red, since what is earned past the cap is lost.");

        var changed = false;
        var byId = trades.Currencies.ToDictionary(currency => currency.Id);
        var pinned = config.PinnedCurrencies;

        // The pinned ones first, in strip order, each with a way to move it. Arrows rather
        // than drag and drop: four rows do not need a gesture, and a gesture cannot be
        // undone by clicking the other arrow.
        // Walked off a copy, since unticking one edits the list under the loop.
        foreach (var (id, index) in pinned.ToArray().Select((id, index) => (id, index)))
        {

            ImGui.BeginDisabled(index == 0);
            if (ImGui.ArrowButton($"##up{id}", ImGuiDir.Up))
            {
                (pinned[index - 1], pinned[index]) = (pinned[index], pinned[index - 1]);
                changed = true;
            }
            ImGui.EndDisabled();

            ImGui.SameLine();
            ImGui.BeginDisabled(index == pinned.Count - 1);
            if (ImGui.ArrowButton($"##down{id}", ImGuiDir.Down))
            {
                (pinned[index + 1], pinned[index]) = (pinned[index], pinned[index + 1]);
                changed = true;
            }
            ImGui.EndDisabled();

            ImGui.SameLine();

            // A pinned id the catalogue no longer knows still gets a row, so it can be unpinned.
            if (byId.TryGetValue(id, out var currency))
                changed |= PinBox(currency);
            else if (Unpin(id, $"Unknown currency {id}"))
                changed = true;
        }

        // The sheets know about a hundred and fifty currencies, most of which nobody holds.
        // The ones in your pockets or the catalogue are the likely next pins; the rest are
        // there, folded away, for the day one of them matters.
        var rest = trades.Currencies
            .Where(currency => !pinned.Contains(currency.Id))
            .OrderBy(currency => currency.Name, StringComparer.Ordinal)
            .ToArray();
        var near = rest
            .Where(currency => trades.IsWatched(currency) || balances.Held(currency) > 0)
            .ToArray();

        foreach (var currency in near)
            changed |= PinBox(currency);

        if (near.Length < rest.Length
            && ImGui.CollapsingHeader($"Everything else ({rest.Length - near.Length})##pinrest"))
        {
            foreach (var currency in rest.Except(near))
                changed |= PinBox(currency);
        }

        ImGui.Spacing();
        return changed;
    }

    private bool PinBox(Resource currency)
    {
        var label = trades.IsWatched(currency) ? $"{currency.Name} (in the catalogue)" : currency.Name;

        if (config.PinnedCurrencies.Contains(currency.Id))
            return Unpin(currency.Id, label);

        var pinned = false;

        if (!ImGui.Checkbox($"{label}##pin{currency.Id}", ref pinned))
            return false;

        // New pins go last. Where it belongs is a choice, and the arrows are right there.
        config.PinnedCurrencies.Add(currency.Id);
        return true;
    }

    /// <summary>A ticked box that, unticked, drops its id from the pinned list.</summary>
    private bool Unpin(uint id, string label)
    {
        var pinned = true;

        if (!ImGui.Checkbox($"{label}##pin{id}", ref pinned))
            return false;

        config.PinnedCurrencies.Remove(id);
        return true;
    }

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
