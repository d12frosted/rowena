using Dalamud.Bindings.ImGui;
using Rowena.Core.Market;
using Rowena.Game;
using Rowena.Market;

namespace Rowena.UI;

/// <summary>
/// What I already have listed, and whether it is doing anything.
/// </summary>
/// <remarks>
/// The other tabs all ask what to acquire. This is the only one that looks at what is already
/// sitting on a retainer, which is where a good deal of gil quietly is not.
///
/// Not an undercut tool first. Undercutting is the one move the board makes easy and it is
/// often the wrong one: the question is never "is somebody cheaper than me" but "how long
/// until the board has eaten through everyone cheaper than me". Three units ahead on a board
/// that sells ten a day are gone this afternoon; dropping your price to jump them is a haircut
/// for nothing. So every row shows the queue and what chasing it would cost, and leaves the
/// decision where it belongs.
///
/// Having decided, though, the doing should be cheap. Each undercut row carries the price that
/// would put it first, and the game's price dialog fills it in when opened on that listing;
/// items I have chosen to leave alone are marked and left alone.
///
/// What is listed comes from the game rather than from Universalis, so it is exact: opening a
/// retainer reads its twenty market slots, and it is remembered across sessions. A board search
/// for one item is the fresher word on that item, so the two are folded together. Each listing
/// is netted at its own retainer's city rate rather than the worst of them, since that is a
/// number the game has actually said.
/// </remarks>
internal sealed class SellingTab
{
    private static readonly string?[] Help =
    [
        null,
        "What you are asking per unit, and what you keep of it after that retainer's city\n"
        + "takes its cut.",
        "How many you have out at that price.",
        "Units listed below yours. The board serves them first, so this is the queue, and it\n"
        + "is the number that decides whether being undercut matters at all.",
        "What it has actually been changing hands for lately. The listings say what people\n"
        + "hope to get; this says what they got, and where the two disagree this is the one\n"
        + "worth believing.",
        "How many of these your own retainers have sold lately, and what you got for one.\n"
        + "Everything else here is a fact about other people; this is the only column that is\n"
        + "a fact about your prices.",
        "How long until the last of yours goes, at the rate the board has been selling them:\n"
        + "the queue ahead plus your own stock, over sales a day.",
        "What matching the current floor would cost you per unit. Not a recommendation, a\n"
        + "price tag on one.",
        "What to ask to actually sell: the cheapest listing in front of yours less the margin\n"
        + "from settings, or, where nobody is paying even the cheapest listing, what recent sales\n"
        + "went for less the margin. Where nothing is under yours and the next listing is far\n"
        + "enough up, the target raises to sit the margin under it instead. Opening the retainer's\n"
        + "price dialog on this listing fills it in. Ignored items show the number but are never\n"
        + "filled in.",
        null,
    ];

    private readonly BoardWatcher board;
    private readonly Boards boards;
    private readonly ItemCells cells;
    private readonly Configuration config;
    private readonly SalesLog sales;
    private readonly Undercutting undercutting;
    private readonly RetainerSellFill sellFill;
    private readonly Action<IReadOnlyList<uint>> refresh;

    private readonly Rebuilt<Model> model;

    public SellingTab(
        BoardWatcher board,
        Boards boards,
        ItemCells cells,
        Configuration config,
        Diagnostics diagnostics,
        SalesLog sales,
        Undercutting undercutting,
        RetainerSellFill sellFill,
        Action<IReadOnlyList<uint>> refresh)
    {
        this.board = board;
        this.boards = boards;
        this.cells = cells;
        this.config = config;
        this.sales = sales;
        this.undercutting = undercutting;
        this.sellFill = sellFill;
        this.refresh = refresh;

        model = new Rebuilt<Model>("selling", Build, diagnostics);
    }

    /// <summary>What the overview should say about what is already listed.</summary>
    public Note? Headline()
    {
        var wanting = model.Current.Rows
            .Where(row => row.Reading.Call
                is not (ListingCall.Hold or ListingCall.Wait or ListingCall.Unknown))
            .ToArray();

        if (wanting.Length == 0)
            return null;

        var worst = wanting[0];

        return new Note(
            Note.AtRisk,
            Style.Bad,
            wanting.Length == 1 ? "1 listing" : $"{wanting.Length} listings",
            wanting.Length == 1
                ? $"{worst.Name}, at a price it will not sell at"
                : "of yours want a decision",
            $"{worst.Name}: asking {worst.Reading.Mine:N0}, "
            + (worst.Reading.TypicalSale is { } paid ? $"sells for {paid:N0}." : "nothing has sold lately.")
            + $" {wanting.Sum(row => row.Reading.NetHolding * row.Units):N0} gil is sitting behind these.",
            MainWindow.Tab.Selling);
    }

    /// <inheritdoc cref="ConvertTab.Warmers"/>
    public IReadOnlyList<Action> Warmers => [() => _ = model.Current];

    /// <summary>What the table is claiming, for checking it against the board.</summary>
    public string Dump() =>
        System.Text.Json.JsonSerializer.Serialize(
            new
            {
                selling = boards.Scope.Selling,
                listed = board.ListedItems().Count,
                rows = model.Current.Rows.Select(row => new
                {
                    name = row.Name,
                    item = row.ItemId,
                    mine = row.Reading.Mine,
                    units = row.Units,
                    ahead = row.Reading.UnitsAhead,
                    floor = row.Reading.Floor,
                    days = row.Reading.DaysToClear,
                    net = row.Reading.NetHolding,
                    haircut = row.Reading.Haircut,
                    typical = row.Reading.TypicalSale,
                    couldAsk = row.Reading.CouldAsk,
                    soldUnits = row.Sold?.Units ?? 0,
                    soldEach = row.Sold?.Each ?? 0,
                    call = row.Reading.Call.ToString(),
                    retainer = row.Retainer,
                    hq = row.IsHq,
                    undercutTo = row.Undercut?.Target,
                    undercutBelow = row.Undercut?.Below,
                    undercutWhy = row.Undercut?.Why.ToString(),
                    ignored = row.Ignored,
                }),
            },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    public void Draw(string selling)
    {
        Style.Muffled($"What you have listed on {selling}, and whether it is going anywhere.");

        var current = model.Current;

        var (seen, of) = board.RetainersSeen();

        if (board.ListedItems().Count == 0)
        {
            Style.Nothing(
                seen == 0
                    ? "nothing known yet; open each retainer once and the game says what it has out,\n"
                      + "and it is remembered after that, so this only has to happen once a retainer"
                    : $"nothing listed on the {(seen == 1 ? "retainer" : $"{seen} retainers")} opened so far");
            return;
        }

        if (Style.Row("refresh prices", "Refetches the books your own listings are sitting in."))
            refresh([.. board.ListedItems()]);

        ImGui.SameLine();
        DrawTally(current);

        if (current.Rows.Length == 0)
        {
            Style.Nothing("no prices for them yet");
            return;
        }

        DrawTable(current);
    }

    /// <summary>The one line worth reading if you read nothing else.</summary>
    private void DrawTally(Model current)
    {
        if (current.Rows.Length == 0)
        {
            ImGui.TextColored(Style.Muted, "  fetching...");
            return;
        }

        var worth = current.Rows.Sum(row => row.Reading.NetHolding * row.Units);
        var wanting = current.Rows.Count(row => row.Reading.Call is not (ListingCall.Hold or ListingCall.Wait));

        ImGui.TextColored(Style.Muted, $"  {current.Rows.Length} listings, {worth:N0} gil if it all sells. ");
        ImGui.SameLine();

        var (seen, of) = board.RetainersSeen();

        if (of > seen)
        {
            ImGui.TextColored(Style.Warn, $"{seen} of {of} retainers seen. ");
            Style.Explain(
                "A retainer's listings are read when it is opened, so what the others have out\n"
                + "is not known yet. This list is what it is, not what you have.");

            ImGui.SameLine();
        }

        ImGui.TextColored(
            wanting == 0 ? Style.Good : Style.Accent,
            wanting == 0 ? "nothing needs doing" : $"{wanting} worth a look");

        var undercut = current.Rows.Count(row => row.Undercut is { Why: not UndercutWhy.RoomAbove } && !row.Ignored);
        var raise = current.Rows.Count(row => row.Undercut is { Why: UndercutWhy.RoomAbove } && !row.Ignored);

        if (undercut + raise > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(
                Style.Accent,
                (undercut, raise) switch
                {
                    (_, 0) => $"{undercut} to reprice",
                    (0, _) => $"{raise} to raise",
                    _ => $"{undercut} to reprice, {raise} to raise",
                });
            Style.Explain(
                "Listings with somebody cheaper in front of them, priced where nobody is buying,\n"
                + "or so far under the next listing that gil is left behind, not counting the ones\n"
                + "you said to leave alone. Open the retainer and the column beside its sell list\n"
                + "does the rest.");
        }

        var recently = sales.Since(DateTimeOffset.UtcNow.AddDays(-SinceDays));

        if (recently.Count == 0)
        {
            ImGui.TextColored(
                Style.Muted,
                $"    Nothing of yours has sold in the {SinceDays} days this has been watching. Sales are\n"
                + "    recorded as the game announces them, so this fills in as they happen.");
            return;
        }

        var inferred = recently.Count(sale => !sale.Announced);

        ImGui.TextColored(
            Style.Muted,
            $"    You have sold {recently.Sum(sale => sale.Quantity):N0} things for "
            + $"{recently.Sum(sale => sale.Gil):N0} gil in the last {SinceDays} days."
            + (inferred > 0 ? $" {inferred} of those were worked out rather than announced." : ""));
    }

    private void DrawTable(Model current)
    {
        if (!ImGui.BeginTable("selling", 10, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("asking", ImGuiTableColumnFlags.WidthFixed, Style.Px(110));
        ImGui.TableSetupColumn("units", ImGuiTableColumnFlags.WidthFixed, Style.Px(50));
        ImGui.TableSetupColumn("ahead", ImGuiTableColumnFlags.WidthFixed, Style.Px(60));
        ImGui.TableSetupColumn("sells for", ImGuiTableColumnFlags.WidthFixed, Style.Px(90));
        ImGui.TableSetupColumn("you sold", ImGuiTableColumnFlags.WidthFixed, Style.Px(90));
        ImGui.TableSetupColumn("clears in", ImGuiTableColumnFlags.WidthFixed, Style.Px(80));
        ImGui.TableSetupColumn("chasing costs", ImGuiTableColumnFlags.WidthFixed, Style.Px(100));
        ImGui.TableSetupColumn("undercut to", ImGuiTableColumnFlags.WidthFixed, Style.Px(150));
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, Style.Px(110));
        Cell.Headers(Help);

        foreach (var row in current.Rows)
        {
            var reading = row.Reading;

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            cells.Draw(row.Name, row.ItemId);

            ImGui.TableNextColumn();
            Cell.Right($"{reading.Mine:N0}");
            Style.Explain(
                $"{reading.NetHolding:N0} kept per unit, from {row.Retainer} in {Cities.Name(row.CityId)}.\n"
                + $"The floor is {reading.Floor:N0}.");

            ImGui.TableNextColumn();
            Cell.Right(Style.Muted, $"{row.Units:N0}");

            ImGui.TableNextColumn();
            Cell.Right(reading.UnitsAhead == 0 ? Style.Good : Style.Plain, $"{reading.UnitsAhead:N0}");

            ImGui.TableNextColumn();
            Cell.Right(Sold(reading), reading.TypicalSale is { } paid ? $"{paid:N0}" : "-");

            ImGui.TableNextColumn();
            DrawMine(row);

            ImGui.TableNextColumn();
            Cell.Right(
                reading.DaysToClear is null ? Style.Bad : Style.Muted,
                Phrases.Absorb(reading.DaysToClear));

            ImGui.TableNextColumn();
            Cell.Right(Style.Muted, reading.Haircut == 0 ? "-" : $"-{reading.Haircut:N0}");

            ImGui.TableNextColumn();
            DrawUndercut(row);

            ImGui.TableNextColumn();
            DrawCall(row);
        }

        ImGui.EndTable();
    }

    /// <summary>
    /// The undercut price, and the two things that can be done about it.
    /// </summary>
    /// <remarks>
    /// The button fills the game's price dialog if it is open on this listing, and otherwise
    /// puts the number on the clipboard and says which retainer to open. The toggle says to
    /// stop nagging about this item: the number stays visible, the dialog is left alone.
    /// </remarks>
    private void DrawUndercut(Row row)
    {
        ImGui.PushID((int)row.ItemId);

        if (row.Undercut is not { } plan)
        {
            Cell.Right(Style.Good, "-");
            Style.Explain("Nothing is listed below yours, it is priced where things sell, and the next\nlisting is close enough that raising would earn nothing worth the bother.");

            ImGui.PopID();
            return;
        }

        if (Style.Quiet(
            row.Ignored ? "watch" : "ignore",
            row.Ignored
                ? "Start filling the price dialog in for this item again."
                : "Leave this item where it is. It still shows here, just not as something to do."))
        {
            undercutting.Ignore(row.ItemId, !row.Ignored);
            model.Invalidate();
        }

        ImGui.SameLine();

        if (Style.Row(
            "set",
            sellFill.Open() is { } open && open.ItemId == row.ItemId
                ? $"Puts {plan.Target:N0} into the open price dialog. You still confirm it."
                : $"Copies {plan.Target:N0} to the clipboard. To change the price, open {row.Retainer},\n"
                  + "adjust this listing, and the dialog fills it in on its own."))
        {
            if (!sellFill.Fill(row.ItemId, plan.Target))
                ImGui.SetClipboardText($"{plan.Target}");
        }

        ImGui.SameLine();
        Cell.Right(
            row.Ignored ? Style.Muted : Style.Accent,
            plan.Why switch
            {
                UndercutWhy.NobodyPays => $"~{plan.Target:N0}",
                UndercutWhy.RoomAbove => $"^{plan.Target:N0}",
                _ => $"{plan.Target:N0}",
            }
            + (row.IsHq && plan.Why == UndercutWhy.NobodyPays ? " HQ?" : ""));

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                plan.Why switch
                {
                    UndercutWhy.NobodyPays =>
                        $"Nobody is paying this, cheapest on the board or not: what actually sells goes for\n"
                        + $"about {plan.Below:N0}. Asking {plan.Target:N0} puts you {config.UndercutBy:N0} under that.",
                    UndercutWhy.RoomAbove =>
                        $"Nothing is under yours and the next listing sits at {plan.Below:N0}. Asking {plan.Target:N0}\n"
                        + $"keeps you the cheapest and is {plan.Target - row.Reading.Mine:N0} more a unit, and recent sales\n"
                        + "say somebody pays up there.",
                    _ =>
                        $"{plan.UnitsAhead:N0} units sit in front of yours, the cheapest at {plan.Below:N0}. Asking\n"
                        + $"{plan.Target:N0} puts you first, {config.UndercutBy:N0} under it.",
                }
                + (row.IsHq && plan.Why == UndercutWhy.NobodyPays
                    ? "\n\nThis listing is HQ and recent sales are not split by quality, so what people\n"
                      + "pay may be for NQ. Check before taking this number."
                    : "")
                + (row.Ignored ? "\n\nYou said to leave this one alone, so the price dialog is not filled in." : ""));
        }

        ImGui.PopID();
    }

    /// <summary>
    /// Colours what it sells for against what you are asking.
    /// </summary>
    /// <remarks>
    /// The listings say what people hope to get and this says what they got. Where the two
    /// disagree by a lot, this is the one worth believing, so the disagreement is what the
    /// colour marks.
    /// </remarks>
    private static System.Numerics.Vector4 Sold(ListingDiagnosis reading) =>
        reading.TypicalSale is not { } paid || paid <= 0
            ? Style.Muted
            : reading.Mine > paid ? Style.Warn : Style.Good;

    /// <summary>
    /// What my own retainers have managed with this lately.
    /// </summary>
    /// <remarks>
    /// The only column here that is a fact about my prices rather than about other people's.
    /// Recorded from the game's own announcements, so it is exact and costs nothing, and it is
    /// the answer to the question the market data cannot reach: not what this sells for, but
    /// whether mine sells.
    /// </remarks>
    private void DrawMine(Row row)
    {
        if (row.Sold is not { Units: > 0 } mine)
        {
            ImGui.TextColored(Style.Muted, "     -");
            return;
        }

        Cell.Right(Style.Good, $"{mine.Units} @ {mine.Each:N0}");
        Style.Explain(
            $"You have sold {mine.Units} of these in the last {SinceDays} days for {mine.Gil:N0} gil,\n"
            + $"which is {mine.Each:N0} each after fees. The last went "
            + $"{Phrases.Ago(DateTimeOffset.UtcNow - mine.Last)} ago."
            + (mine.Inferred > 0
                ? $"\n\n{mine.Inferred} of these were not announced in chat: the game only says so while\n"
                  + "you are online, so they were read off the retainer's slots and purse instead."
                : ""));
    }

    private void DrawCall(Row row)
    {
        var (colour, label, why) = Advice(row);

        ImGui.TextColored(colour, label);
        Style.Explain(why);
    }

    /// <summary>
    /// The verdict in words, with its reasoning attached.
    /// </summary>
    /// <remarks>
    /// Every one of these says the number it is standing on. A call you cannot check is worth
    /// no more than the guess it replaced, and this is advice about your own gil.
    /// </remarks>
    private (System.Numerics.Vector4 Colour, string Label, string Why) Advice(Row row)
    {
        var reading = row.Reading;
        var queue = $"{reading.UnitsAhead:N0} units are listed below yours";

        return reading.Call switch
        {
            ListingCall.Hold => (
                Style.Good, "nothing to do",
                "Nothing is listed below yours, so you are next. Being undercut later is only\n"
                + "worth reacting to if the queue that appears is a long one."),

            ListingCall.Wait => (
                Style.Good, "sit tight",
                $"{queue}, which the board gets through in {Phrases.Absorb(reading.DaysToClear)}.\n"
                + $"Dropping to {reading.Floor:N0} to jump them would cost {reading.Haircut:N0} a unit and save\n"
                + "you that wait. Rarely worth it."),

            ListingCall.Chase => (
                Style.Accent, "chase or leave it",
                $"{queue}: at the rate this sells, the last of yours goes in\n"
                + $"{Phrases.Absorb(reading.DaysToClear)}, which is longer than you said you wanted to be selling.\n"
                + $"Matching the floor at {reading.Floor:N0} costs {reading.Haircut:N0} a unit. The other answer is\n"
                + "to leave it and forget about it."),

            ListingCall.Vendor => (
                Style.Accent, "vendor pays more",
                $"A vendor hands over {reading.VendorNet:N0} a unit. Selling on the board at your own\n"
                + $"asking price leaves you {reading.NetHolding:N0} after the city's cut, so the board is the\n"
                + "worse counter here even before anybody undercuts you."),

            ListingCall.Overpriced => (
                Style.Accent, "nobody pays this",
                $"This has been changing hands at about {reading.TypicalSale:N0}, against your {reading.Mine:N0}.\n"
                + "Being cheapest on the board is not the same as being priced where people are\n"
                + "actually buying: a wall of listings nobody takes is not a market, and sitting\n"
                + "in it is not a position."),

            ListingCall.Underpriced => (
                Style.Accent, "you could ask more",
                $"You could ask {reading.CouldAsk:N0} and still be the cheapest thing on the board, and\n"
                + $"this has been selling at about {reading.TypicalSale:N0}, so somebody is paying up there.\n"
                + $"Against your {reading.Mine:N0} that is {reading.CouldAsk - reading.Mine:N0} a unit left behind for a queue\n"
                + "position nobody was competing for."),

            ListingCall.Unknown => (
                Style.Muted, "no rate yet",
                "How fast this sells is not known yet. A listings fetch cannot say: that endpoint\n"
                + "counts sales where everything here counts units, and a sale is a listing bought\n"
                + "however many units were in it. The summary that reports units a day has been\n"
                + "asked for; until it lands there is nothing honest to say about the queue."),

            _ => (
                Style.Bad, "nothing sells",
                "The board reports no sales at all for this, so there is no queue to wait out and\n"
                + "no price that makes it move. A vendor, or keep it."),
        };
    }

    /// <summary>
    /// Reads every listing against the book it is sitting in.
    /// </summary>
    /// <remarks>
    /// Grouped by item and price, because that is the decision: two stacks at the same price are
    /// one question, however many retainers they are spread over.
    /// </remarks>
    private Model Build()
    {
        var rows = new List<Row>();
        var recently = sales.Since(DateTimeOffset.UtcNow.AddDays(-SinceDays));

        // Only once every retainer has been read: until then "not listed" may just mean "not
        // looked at", and forgetting a decision on that basis is the wrong kind of tidy.
        var (seen, of) = board.RetainersSeen();

        if (seen > 0 && seen >= of)
            undercutting.Prune(board.ListedItems());

        foreach (var itemId in board.ListedItems())
        {
            var book = boards.Selling(itemId);

            if (book is null)
                continue;

            foreach (var group in board.Listed(itemId).GroupBy(listing => (listing.UnitPrice, listing.IsHq)))
            {
                var units = group.Sum(listing => listing.Quantity);
                var first = group.First();

                var reading = ListingDiagnosis.Of(
                    group.Key.UnitPrice,
                    units,
                    book,
                    boards.Vendor(itemId),
                    TaxFor(first.CityId),
                    config.SellingHorizon(),
                    group.Key.IsHq);

                if (reading is not { } read)
                    continue;

                rows.Add(new Row(
                    itemId,
                    cells.Name(itemId),
                    units,
                    group.Count() > 1 ? $"{group.Count()} retainers" : first.Retainer,
                    first.CityId,
                    group.Key.IsHq,
                    read,
                    Sold(itemId, recently),
                    undercutting.Plan(itemId, group.Key.UnitPrice, group.Key.IsHq),
                    undercutting.Ignored(itemId)));
            }
        }

        // Whatever wants a decision first, then whatever has the most gil resting on it.
        return new Model([.. rows.OrderBy(row => Urgency(row.Reading.Call)).ThenByDescending(row => row.Reading.NetHolding * row.Units)]);
    }

    /// <summary>How far back "lately" reaches when counting my own sales.</summary>
    private const int SinceDays = 14;

    /// <summary>My own sales of one item, folded into a total.</summary>
    private static Mine? Sold(uint itemId, IReadOnlyList<SaleRecord> recently)
    {
        var mine = recently.Where(sale => sale.ItemId == itemId).ToArray();

        if (mine.Length == 0)
            return null;

        var units = mine.Sum(sale => sale.Quantity);
        var gil = mine.Sum(sale => sale.Gil);

        return new Mine(
            units,
            gil,
            units > 0 ? gil / units : gil,
            mine.Max(sale => sale.At),
            mine.Count(sale => !sale.Announced));
    }

    private static int Urgency(ListingCall call) => call switch
    {
        ListingCall.Vendor => 0,
        ListingCall.Overpriced => 1,
        ListingCall.Underpriced => 1,
        ListingCall.Chase => 2,
        ListingCall.Stuck => 3,
        ListingCall.Wait => 4,
        _ => 5,
    };

    /// <summary>
    /// The cut for the city a listing is actually standing in.
    /// </summary>
    /// <remarks>
    /// Everywhere else assumes the worst of the cities you have retainers in, because it is
    /// pricing something you have not sold yet and does not know where it would go. Here the
    /// game has said which retainer holds it, so the number can be the real one.
    /// </remarks>
    private MarketTax TaxFor(uint cityId) =>
        board.SellerRates is { } rates && rates.TryGetValue(cityId, out var rate)
            ? boards.Tax.WithSellerRate(rate)
            : boards.Tax;

    /// <summary>What my own retainers have done with one item lately.</summary>
    private readonly record struct Mine(int Units, long Gil, long Each, DateTimeOffset Last, int Inferred);

    private sealed record Row(
        uint ItemId,
        string Name,
        int Units,
        string Retainer,
        uint CityId,
        bool IsHq,
        ListingDiagnosis Reading,
        Mine? Sold,
        UndercutPlan? Undercut,
        bool Ignored);

    private sealed record Model(Row[] Rows);
}
