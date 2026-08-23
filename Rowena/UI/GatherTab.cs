using Dalamud.Bindings.ImGui;
using Rowena.Core.Market;
using Rowena.Game;
using Rowena.Market;

namespace Rowena.UI;

/// <summary>
/// What to go and pick up: gatherable things ranked by what a day of the board will pay for them.
/// </summary>
/// <remarks>
/// The one table here with no outlay in it. Everything else weighs gil spent against gil
/// returned; this weighs an hour of your time, and the plugin does not yet know what an hour
/// of gathering yields, so it ranks on what the market will take instead. A ceiling, like the
/// craft table's, and said out loud for the same reason.
///
/// Level and job are not decoration. A ranking full of nodes you cannot stand at is one you
/// have to filter in your head, and the game already knows which those are.
/// </remarks>
internal sealed class GatherTab
{
    private const int RowsInTable = 30;

    private static readonly string?[] Help =
    [
        null,
        "The job that gathers it, and the level the node wants.",
        "What one sells for on your world, net of the market's cut, or what a vendor pays\n"
        + "when that is more. A price no recent sale supports is refused rather than quoted.",
        "How many the board is selling in a day, on your world: your retainer sells where\n"
        + "it stands.",
        "Net times whichever is smaller, the sales a day the board makes or the number you\n"
        + "would actually gather in one. Ranked by this. Still a ceiling: it assumes every\n"
        + "one sells at today's price, and says nothing about how long the gathering takes.",
        "Nodes that only appear on a clock, and whether that clock is favourable now. A game\n"
        + "hour is under three minutes of yours, so a window said to last four hours is twelve\n"
        + "minutes of your evening: these are things you walk to now or miss.",
        "What you already have of it, in bags and retainers and listed, and how long the board\n"
        + "needs to get through that. A pile that outlasts the selling horizon is gathering\n"
        + "already done: those rows drop to the bottom and the session plan leaves them out.",
    ];

    /// <summary>The same columns with a session in front of them, where two of them mean something else.</summary>
    private static readonly string?[] PlannedHelp =
    [
        null,
        "How many to bring back. The dearest things first, each capped at the room a new seller\n"
        + "actually has: what the board turns over within the selling horizon, less what is\n"
        + "already listed ahead of you. Then on to the next, until the session is full.",
        Help[1],
        Help[2],
        Help[3],
        "What a day of the board would pay, which is what the ranking used. The session's own\n"
        + "figure is above the table.",
        Help[5],
        Help[6],
    ];

    private readonly GatherSweep sweep;
    private readonly Gatherables gatherables;
    private readonly Boards boards;
    private readonly ItemCells cells;
    private readonly Configuration config;
    private readonly GatherClock clock;
    private readonly Balances balances;
    private readonly RetainerStock stock;
    private readonly BoardWatcher board;

    private readonly Rebuilt<Model> model;

    public GatherTab(
        GatherSweep sweep,
        Gatherables gatherables,
        Boards boards,
        ItemCells cells,
        Configuration config,
        GatherClock clock,
        Balances balances,
        RetainerStock stock,
        BoardWatcher board,
        Diagnostics diagnostics)
    {
        this.sweep = sweep;
        this.gatherables = gatherables;
        this.boards = boards;
        this.cells = cells;
        this.config = config;
        this.clock = clock;
        this.balances = balances;
        this.stock = stock;
        this.board = board;

        model = new Rebuilt<Model>("gather", Build, diagnostics);
    }

    /// <summary>What the table is claiming, for checking it against the board.</summary>
    public string Dump() =>
        System.Text.Json.JsonSerializer.Serialize(
            new
            {
                selling = boards.Scope.Selling,
                sellerRate = boards.Tax.SellerRate,
                survey = sweep.Current.Detail,
                session = config.GatherSessionMinutes,
                perHour = PerHour,
                measured = Measured,
                watchedMinutes = Math.Round(clock.Tally.Seconds / 60, 1),
                watchedItems = clock.Tally.Items,
                horizon = config.SellingHorizon(),
                plan = model.Current.Plan is { } plan
                    ? new { aim = Aim.ToString(), capacity = plan.Capacity, units = plan.Units, worth = plan.Worth, minutes = plan.Minutes, things = plan.Take.Count, timed = plan.Timed, shut = plan.Shut }
                    : null,
                rows = model.Current.Rows.Take(12).Select(row => new
                {
                    name = row.Name,
                    item = row.ItemId,
                    job = row.Job,
                    level = row.Level,
                    each = row.Each,
                    floor = boards.Selling(row.ItemId)?.Floor ?? 0,
                    salesPerDay = row.SalesPerDay,
                    gilPerDay = row.GilPerDay,
                    held = row.Held,
                    mineListed = row.MineListed,
                    backlogDays = row.Backlog,
                    timed = row.Timed,
                    opensIn = Math.Round(row.OpensIn, 1),
                    openFor = Math.Round(row.OpenFor, 1),
                    windowIs = Math.Round(row.WindowIs, 1),
                    reachable = row.Reachable,
                    take = model.Current.Plan is { } take && take.Take.TryGetValue(row.ItemId, out var units) ? units : 0,
                }),
            },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    /// <summary>Sets the session the table is planning for, for driving the window without a mouse.</summary>
    public void PlanFor(int minutes)
    {
        config.GatherSessionMinutes = minutes;
        model.Invalidate();
    }

    /// <summary>Sets what the session is aiming at, for driving the window without a mouse.</summary>
    public void AimFor(GatherAim aim)
    {
        config.GatherAim = (int)aim;
        model.Invalidate();
    }

    /// <summary>Includes or drops timed nodes, for driving the window without a mouse.</summary>
    public void IncludeTimed(bool include)
    {
        config.GatherIncludeTimed = include;
        model.Invalidate();
    }

    /// <summary>
    /// What the overview should say about gathering, which is only ever about the clock.
    /// </summary>
    /// <remarks>
    /// The one thing in this plugin that actually expires. A flip worth two million will still
    /// be there in ten minutes; a node worth eighty thousand will not, and that is the whole
    /// reason this outranks bigger numbers on the overview.
    /// </remarks>
    public Note? Headline()
    {
        var open = model.Current.Rows.Where(row => row is { Timed: true, OpensIn: <= 0 }).ToArray();

        if (open.Length == 0)
            return null;

        // Ordered rather than MinBy so the first element is a row rather than a maybe-row: the
        // list is known not to be empty a line above, and saying so twice is worse than not
        // asking.
        var soonest = open.OrderBy(row => row.OpenFor).First();

        return new Note(
            Note.Expiring,
            Palette.Good,
            $"{soonest.OpenFor:F0} min",
            open.Length == 1
                ? $"until {soonest.Name} shuts"
                : $"until the first of {open.Length} timed nodes shuts",
            $"Worth {open.Sum(row => row.Each * 40):N0} if you fill a window of each. "
            + "Game hours are minutes here.",
            MainWindow.Tab.Gather);
    }

    /// <summary>
    /// Which timed nodes are standing there right now, worth most first.
    /// </summary>
    /// <remarks>
    /// For the alerts, which are the only place in this plugin where being told something
    /// unprompted is clearly right: everything else here waits until it is asked, because
    /// everything else here will still be true in ten minutes.
    /// </remarks>
    public IReadOnlyList<OpenWindow> OpenNow() =>
    [
        .. model.Current.Rows
            .Where(row => row is { Timed: true, OpensIn: <= 0 })
            .OrderByDescending(row => row.Each)
            .Select(row => new OpenWindow(row.ItemId, row.Name, row.Each, row.OpenFor)),
    ];

    /// <summary>A timed node that is up, and how long for.</summary>
    internal readonly record struct OpenWindow(uint ItemId, string Name, long Each, double Minutes);

    /// <inheritdoc cref="ConvertTab.Warmers"/>
    public IReadOnlyList<Action> Warmers => [() => _ = model.Current];

    public void Draw(string selling)
    {
        ImGui.TextUnformatted("Worth gathering: what the board will take in a day, and what it pays");

        DrawSweep(selling);
        DrawFilters();
        DrawTable();
    }

    private void DrawSweep(string selling)
    {
        var scan = sweep.Current;

        if (scan.Running)
        {
            ImGui.TextColored(Palette.Dim, $"  {scan.Detail}");
            return;
        }

        if (ImGui.Button(scan.ReadyAt is null ? "Survey" : "Survey again"))
            sweep.Start(selling, config.GatherShortlist, config.SweepAge());

        ImGui.SameLine();

        if (scan.State == GatherSweep.Phase.Failed)
        {
            ImGui.TextColored(Palette.Bad, scan.Detail);
            return;
        }

        var age = scan.ReadyAt is { } at ? $"{Phrases.Ago(DateTimeOffset.UtcNow - at)} old, " : "";

        ImGui.TextColored(
            Palette.Dim,
            scan.HasResults
                ? $"  {age}{scan.Detail}"
                : "  not surveyed yet. Seven hundred odd items, so this is seconds rather than minutes.");
    }

    private void DrawFilters()
    {
        var current = model.Current;

        ImGui.SetNextItemWidth(140f);

        if (ImGui.BeginCombo("##gather-job", config.GatherJob switch { 16 => "Miner", 17 => "Botanist", _ => "Either job" }))
        {
            foreach (var (id, name) in new (uint, string)[] { (0, "Either job"), (16, "Miner"), (17, "Botanist") })
            {
                if (ImGui.Selectable(name, config.GatherJob == id))
                {
                    config.GatherJob = id;
                    model.Invalidate();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();

        var reachable = config.GatherReachableOnly;

        if (ImGui.Checkbox("Only what I can gather", ref reachable))
        {
            config.GatherReachableOnly = reachable;
            model.Invalidate();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"Your miner is {gatherables.LevelOf(16)} and your botanist is {gatherables.LevelOf(17)}.\n"
                + "Nodes above that are hidden rather than dimmed, since a list to go and gather\n"
                + "from should be a list you can act on.");
        }

        ImGui.SameLine();

        DrawSession();

        if (config.GatherSessionMinutes > 0)
        {
            ImGui.SameLine();
            DrawAim();
        }

        ImGui.SameLine();

        var timed = config.GatherIncludeTimed;

        if (ImGui.Checkbox("Include timed nodes", ref timed))
        {
            config.GatherIncludeTimed = timed;
            model.Invalidate();
        }

        if (current.Rows.Length > 0)
        {
            ImGui.SameLine();

            // Handed over rather than driven, the same division as the crafting list and
            // Artisan: this decides what is worth gathering, and the plugin that gathers does
            // the gathering.
            if (ImGui.Button("Copy for GatherBuddy"))
            {
                // The auto-gather list rather than the gather window preset. The window preset
                // is what the overlay shows; this is the one auto-gather works through, and it
                // is the only one of the two with anywhere to put an amount.
                ImGui.SetClipboardText(Rowena.Core.Lists.GatherList.ForAutoGather(
                    "Rowena",
                    $"worth gathering, {DateTime.Now:d MMM HH:mm}",
                    current.Rows.ToDictionary(
                        row => row.ItemId,
                        row => current.Plan is { } plan && plan.Take.TryGetValue(row.ItemId, out var take)
                            ? take
                            : 0)));
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "One paste, as an auto-gather list.\n"
                    + "\n"
                    + "1. Press this.\n"
                    + "2. Open GatherBuddy, Auto-Gather tab, and its list selector.\n"
                    + "3. Press the clipboard button and give the list a name.\n"
                    + "\n"
                    + (current.Plan is null
                        ? "Everything shown goes in, so filter first if you want less."
                        : "The plan goes in with how many of each to gather, so the amounts come\n"
                          + "across rather than staying in this table."));
            }

            ImGui.SameLine();

            if (ImGui.Button("Copy names"))
                ImGui.SetClipboardText(string.Join("\n", current.Rows.Select(row => row.Name)));

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The same list as plain names, one per line, for anything else.");
        }
    }

    /// <summary>
    /// How long you have, which turns the ranking into a list with amounts on it.
    /// </summary>
    /// <remarks>
    /// The question a ranking cannot answer. "Worth gathering" is a property of an item; "worth
    /// gathering this evening" is a property of an item, a market and an hour, and the last two
    /// were missing.
    /// </remarks>
    /// <summary>
    /// What the session is trying to be good at, since these pull against each other.
    /// </summary>
    /// <remarks>
    /// A choice rather than a default because there is no right answer: the most gil is the most
    /// gil in one thing, which is also the most exposed to that one thing's price moving, and the
    /// first version of this planner filled every session from ten minutes to two hours with the
    /// same single item.
    /// </remarks>
    private void DrawAim()
    {
        ImGui.SetNextItemWidth(150f);

        if (ImGui.BeginCombo("##gather-aim", Aims[(int)Aim].Name))
        {
            foreach (var (aim, name, help) in Aims)
            {
                if (ImGui.Selectable(name, Aim == aim))
                {
                    config.GatherAim = (int)aim;
                    model.Invalidate();
                }

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(help);
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Aims[(int)Aim].Help);
    }

    private static readonly (GatherAim Aim, string Name, string Help)[] Aims =
    [
        (GatherAim.MostGil, "Most gil",
            "The dearest things the board has room for, however few that turns out to be.\n"
            + "Often one thing, which is the most gil and the most exposed to that one thing's\n"
            + "price moving while you are still selling it."),
        (GatherAim.MixedBag, "A mixed bag",
            "The same ranking, with nothing more than a quarter of the trip. Earns less on\n"
            + "paper and does not hand the whole session to one price."),
        (GatherAim.SellsSoonest, "Sells soonest",
            "Only what tomorrow's board will take, rather than the week's. The same ranking on\n"
            + "a shorter view: a smaller trip, and none of it still sitting in a retainer\n"
            + "when you next log in."),
    ];

    private void DrawSession()
    {
        ImGui.SetNextItemWidth(130f);

        var label = config.GatherSessionMinutes switch
        {
            0 => "Just rank them",
            60 => "An hour",
            var minutes => $"{minutes} minutes",
        };

        if (ImGui.BeginCombo("##gather-session", label))
        {
            foreach (var (minutes, name) in new (int, string)[]
                     {
                         (0, "Just rank them"),
                         (10, "10 minutes"),
                         (30, "30 minutes"),
                         (60, "An hour"),
                         (120, "120 minutes"),
                     })
            {
                if (ImGui.Selectable(name, config.GatherSessionMinutes == minutes))
                {
                    config.GatherSessionMinutes = minutes;
                    model.Invalidate();
                }
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Turns the ranking into a shopping list with amounts on it: the dearest things the\n"
                + "board still has room for, existing sellers counted, until the time is up.\n"
                + "\n"
                + (Measured
                    ? $"Uses the {PerHour} items an hour you have actually been gathering, measured over\n"
                      + $"{clock.Tally.Seconds / 60:F0} minutes of it."
                    : $"Assumes {PerHour} items an hour. Nothing has been measured yet, so that is a guess;\n"
                      + "it corrects itself once you have gathered for a while."));
        }
    }

    /// <summary>
    /// What the session comes to, and what it is assuming to say so.
    /// </summary>
    /// <remarks>
    /// The assumption is stated every time rather than buried in the settings, because every
    /// number beside it is scaled by a figure nobody has measured.
    /// </remarks>
    private void DrawPlan(Plan plan)
    {
        if (plan.Units == 0)
        {
            ImGui.TextColored(
                Palette.Bad,
                "    Nothing worth the trip: no item here has a board that would take any of it.");
            return;
        }

        ImGui.TextColored(
            Palette.Good,
            $"    {plan.Units:N0} items, about {plan.Worth:N0} gil, selling over {config.SellingHorizon()} days.");

        var spare = config.GatherSessionMinutes - plan.Minutes;

        ImGui.TextColored(
            Palette.Dim,
            $"    {Rows(plan)} in the order to do them, across {plan.Minutes} of your {config.GatherSessionMinutes} minutes, "
            + (Measured
                ? $"at the {PerHour} items an hour you actually gather. "
                : $"assuming {PerHour} items an hour, which is a guess. ")
            + (spare > 1
                ? $"\n    The board runs out before you do: nothing is worth the last {spare} minutes, so a\n    shorter trip earns nearly the same."
                : ""));

        if (plan.Timed > 0)
        {
            ImGui.TextColored(
                Palette.Good,
                $"    {plan.Timed} of these are timed and their windows are open, or will be before you\n"
                + "    are done. Take them first: a window is minutes, not hours.");
        }

        if (plan.Shut > 0)
        {
            ImGui.TextColored(
                Palette.Dim,
                $"    {plan.Shut} timed items are shut for longer than this trip, so they are left out of it.\n"
                + "    They are still in the ranking with the time until they come round.");
        }
    }

    private string Rows(Plan plan) =>
        plan.Take.Count == 1 ? "One thing" : $"{plan.Take.Count} different things";

    private void DrawTable()
    {
        var current = model.Current;

        if (current.Rows.Length == 0)
        {
            if (sweep.Current.HasResults)
                ImGui.TextColored(Palette.Dim, "    Nothing matches those filters.");

            return;
        }

        if (current.Hidden > 0)
            ImGui.TextColored(Palette.Dim, $"    {current.Hidden} more hidden by the filters.");

        if (current.Plan is { } plan)
            DrawPlan(plan);

        var columns = current.Plan is null ? 7 : 8;

        if (!ImGui.BeginTable("gather", columns, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);

        if (current.Plan is not null)
            ImGui.TableSetupColumn("take", ImGuiTableColumnFlags.WidthFixed, 60);

        ImGui.TableSetupColumn("job", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("each", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("sales/day", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("gil/day", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("node", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("you hold", ImGuiTableColumnFlags.WidthFixed, 110);
        Cell.Headers(current.Plan is null ? Help : PlannedHelp);

        foreach (var row in current.Rows)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            cells.Draw(row.Name, row.ItemId);

            if (current.Plan is { } take)
            {
                ImGui.TableNextColumn();
                Cell.Right(Palette.Good, $"{take.Take[row.ItemId]:N0}");
            }

            ImGui.TableNextColumn();
            ImGui.TextColored(row.Reachable ? Palette.Dim : Palette.Bad, $"{row.Job} {row.Level}");

            if (!row.Reachable && ImGui.IsItemHovered())
                ImGui.SetTooltip("Above your level on that job.");

            ImGui.TableNextColumn();
            Cell.Right($"{row.Each:N0}");

            ImGui.TableNextColumn();
            Cell.Right(Palette.Dim, $"{row.SalesPerDay:F1}");

            ImGui.TableNextColumn();
            Cell.Right(Palette.Good, $"{row.GilPerDay:N0}");

            ImGui.TableNextColumn();
            DrawWindow(row);

            ImGui.TableNextColumn();
            DrawHeld(row);
        }

        ImGui.EndTable();
    }

    /// <summary>
    /// What is already yours, and whether that is the reason not to gather more.
    /// </summary>
    /// <remarks>
    /// A thousand of something in a retainer is not a reason to stop showing the row, since
    /// a conversion chain may still want it. It is a reason to stop ranking it as if the board
    /// were waiting for it, and the number says why.
    /// </remarks>
    private void DrawHeld(Row row)
    {
        var mine = row.Held + row.MineListed;

        if (mine == 0)
        {
            ImGui.TextColored(Palette.Dim, "     -");
            return;
        }

        Cell.Right(
            row.Backlogged ? Palette.Bad : Palette.Dim,
            row.Backlog is { } days ? $"{mine:N0} ({Phrases.Absorb(days)})" : $"{mine:N0} (never)");

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"{row.Held:N0} in bags and retainers, {row.MineListed:N0} listed. At {row.SalesPerDay:F1} a day the board\n"
                + (row.Backlog is { } left
                    ? $"needs {Phrases.Absorb(left)} to get through what you already have"
                    : "is not getting through what you already have")
                + (row.Backlogged
                    ? $", which is past the {config.SellingHorizon()} days you said\n"
                      + "you wanted to be selling. Gathering more of this is gathering for later."
                    : "."));
        }
    }

    /// <summary>
    /// Whether the clock is favourable, in minutes you can feel.
    /// </summary>
    /// <remarks>
    /// In game hours a timed node reads as something to get round to. In real minutes it is a
    /// four hour window that lasts twelve of them, which is the difference between a list you
    /// can act on and one you cross-check somewhere else.
    /// </remarks>
    private void DrawWindow(Row row)
    {
        if (!row.Timed)
        {
            ImGui.TextColored(Palette.Dim, "always");
            return;
        }

        if (row.OpensIn <= 0)
        {
            ImGui.TextColored(Palette.Good, $"{row.OpenFor:F0} min left");

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Open now. Go.");

            return;
        }

        ImGui.TextColored(row.OpensIn <= 15 ? Palette.Plain : Palette.Dim, $"in {row.OpensIn:F0} min");

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"Shut. Comes round in {row.OpensIn:F0} minutes and stays for {row.WindowIs:F0}.\n"
                + "Game hours, so a window is minutes rather than an afternoon.");
        }
    }

    /// <summary>What the shortlist is worth against the books the cache holds now.</summary>
    private Model Build()
    {
        var scan = sweep.Current;

        if (!scan.HasResults)
            return new Model([], 0, null);

        var tax = boards.Tax;
        var now = EorzeaClock.MinuteOfDay(DateTimeOffset.UtcNow);
        var byItem = gatherables.All().ToDictionary(gatherable => gatherable.ItemId);
        var levels = new Dictionary<uint, int>();

        var rows = new List<Row>();
        var hidden = 0;

        // What is already mine goes out before anything gathered today does, so it is room
        // the board does not have for gathering. Bags, retainers, and what is listed.
        var carried = balances.Carrying();
        var stored = stock.Held();
        var horizon = config.SellingHorizon();

        foreach (var itemId in scan.Shortlist)
        {
            if (!byItem.TryGetValue(itemId, out var gatherable))
                continue;

            if (boards.Selling(itemId) is not { } book)
                continue;

            // Valued the same way every other output here is: the board net of its cut, or a
            // vendor when that pays more, and refused outright when no recent sale supports it.
            if (VendorFloor.Value(book, boards.Vendor(itemId), 1, tax) is not { } sale)
                continue;

            if (!levels.TryGetValue(gatherable.JobId, out var level))
                levels[gatherable.JobId] = level = gatherables.LevelOf(gatherable.JobId);

            var reachable = level >= gatherable.Level;

            if ((config.GatherJob != 0 && gatherable.JobId != config.GatherJob)
                || (config.GatherReachableOnly && !reachable)
                || (!config.GatherIncludeTimed && gatherable.Timed))
            {
                hidden++;
                continue;
            }

            // Whichever runs out first, the market's appetite or your hands. Without the
            // second, a forty-six gil crystal that the board churns by the ten thousand
            // outranks everything worth walking to.
            var perDay = config.GatherPerDayCap > 0
                ? Math.Min(book.SaleVelocityPerDay, config.GatherPerDayCap)
                : book.SaleVelocityPerDay;

            var held = carried.GetValueOrDefault(itemId) + stored.GetValueOrDefault(itemId);
            var mineListed = board.Listed(itemId).Sum(listing => listing.Quantity);
            var backlog = GatherPlan.Backlog(held, mineListed, book.SaleVelocityPerDay);

            rows.Add(new Row(
                itemId,
                cells.Name(itemId),
                gatherable.Job,
                gatherable.Level,
                reachable,
                gatherable.Timed,
                sale.Net,
                book.SaleVelocityPerDay,
                book.UnitsListed,
                (long)(sale.Net * perDay),
                held,
                mineListed,
                backlog,
                held + mineListed > 0 && (backlog is null || backlog > horizon),
                gatherable.Windows.Count == 0 ? 0 : Real(EorzeaWindow.NextOpen(gatherable.Windows, now) ?? 0),
                gatherable.Windows.Count == 0 ? 0 : Real(EorzeaWindow.LeftOf(gatherable.Windows, now)),
                gatherable.Windows.Count == 0 ? 0 : Real(gatherable.Windows.Max(window => window.LengthMinutes))));
        }

        // What you already have enough of goes last, whatever it would pay: the board is not
        // waiting for more of it, it is waiting to get through yours.
        var ranked = rows.OrderBy(row => row.Backlogged).ThenByDescending(row => row.GilPerDay).ToArray();

        return config.GatherSessionMinutes > 0
            ? Planned(ranked, hidden)
            : new Model([.. ranked.Take(RowsInTable)], hidden, null);
    }

    /// <summary>
    /// The ranking turned into a session: what to bring back from the time you have.
    /// </summary>
    /// <remarks>
    /// The ranking answers what is worth gathering and leaves the harder question alone. An hour
    /// is not a day, and the best hour is not the best row repeated until the hour is up: gather
    /// two hundred of the top thing and most of the pile sits in a retainer for a fortnight,
    /// because the board takes forty a day.
    ///
    /// Timed nodes are left out rather than ranked in. Every other item costs about the same
    /// minute, which is what makes filling the session with the dearest ones the right answer;
    /// a node on a clock does not cost minutes at all, it costs being there, and pretending
    /// otherwise would have the plan cheerfully ask for two hundred of something that yields one
    /// windowful. They stay in the ranking, which is where they are useful.
    /// </remarks>
    private Model Planned(Row[] ranked, int hidden)
    {
        var byItem = ranked.ToDictionary(row => row.ItemId);
        var perHour = PerHour;
        var capacity = (int)Math.Round(perHour * (config.GatherSessionMinutes / 60d));

        // A node on a clock is only worth planning around if the clock is favourable while you
        // are out. Four game hours is under twelve real minutes, so most timed nodes are not
        // "later today", they are now or not at all.
        var reachable = ranked.Where(row => !row.Timed || row.OpensIn < config.GatherSessionMinutes).ToArray();

        var basket = GatherPlan.For(
            reachable.Select(row => new GatherCandidate(row.ItemId, row.Each, row.SalesPerDay, row.Listed, row.Timed, row.Held)),
            capacity,
            config.SellingHorizon(),
            Aim,
            config.GatherWindowYield,
            perHour * (config.GatherWindowMinutes / 60d));

        var plan = new Plan(
            capacity,
            basket.Sum(portion => portion.Units),
            GatherPlan.Worth(basket),
            basket.ToDictionary(portion => portion.ItemId, portion => portion.Units),
            (int)Math.Round(basket.Sum(portion => portion.Cost) / perHour * 60d),
            basket.Count(portion => byItem[portion.ItemId].Timed),
            ranked.Count(row => row.Timed) - reachable.Count(row => row.Timed));

        // Ordered by the clock rather than by worth, which turns a list into an itinerary: what
        // is open now and closing soonest, then what comes round next, then the things standing
        // there all day to fill the gaps between windows.
        return new Model(
            [.. basket.Select(portion => byItem[portion.ItemId]).OrderBy(Turn).ThenByDescending(row => row.Each)],
            hidden,
            plan);
    }

    /// <summary>Where a row falls in the evening: open now, coming round, or any time at all.</summary>
    private static (int Stage, double When) Turn(Row row) => row switch
    {
        { Timed: false } => (2, 0),
        { OpensIn: <= 0 } => (0, row.OpenFor),
        _ => (1, row.OpensIn),
    };

    /// <summary>A stretch of the game's clock, in minutes of ours.</summary>
    private static double Real(int eorzeaMinutes) => EorzeaClock.ToReal(eorzeaMinutes).TotalMinutes;

    /// <summary>
    /// How many items an hour to plan with: what has been watched, or what was typed in.
    /// </summary>
    /// <remarks>
    /// The measurement wins when there is one, because a reading of how you actually gather
    /// beats a number somebody guessed. Until then the typed one stands, and the plan says
    /// which it used.
    /// </remarks>
    private int PerHour =>
        config.GatherUseMeasured && clock.PerHour is { } measured
            ? Math.Max(1, (int)Math.Round(measured))
            : Math.Max(1, config.GatherPerHour);

    private bool Measured => config.GatherUseMeasured && clock.PerHour is not null;

    private GatherAim Aim =>
        Enum.IsDefined((GatherAim)config.GatherAim) ? (GatherAim)config.GatherAim : GatherAim.MostGil;

    private sealed record Row(
        uint ItemId,
        string Name,
        string Job,
        int Level,
        bool Reachable,
        bool Timed,
        long Each,
        double SalesPerDay,
        int Listed,
        long GilPerDay,
        int Held,
        int MineListed,
        double? Backlog,
        bool Backlogged,
        double OpensIn,
        double OpenFor,
        double WindowIs);

    /// <summary>A session's worth of gathering, and what it would come to.</summary>
    private sealed record Plan(
        int Capacity,
        int Units,
        long Worth,
        Dictionary<uint, int> Take,
        int Minutes,
        int Timed,
        int Shut);

    private sealed record Model(Row[] Rows, int Hidden, Plan? Plan);
}
