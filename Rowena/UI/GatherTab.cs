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
        "Nodes that only appear on a clock. A different errand from one always standing\n"
        + "there, and worth knowing before setting out.",
    ];

    private readonly GatherSweep sweep;
    private readonly Gatherables gatherables;
    private readonly Boards boards;
    private readonly ItemCells cells;
    private readonly Configuration config;

    private readonly Rebuilt<Model> model;

    public GatherTab(
        GatherSweep sweep,
        Gatherables gatherables,
        Boards boards,
        ItemCells cells,
        Configuration config,
        Diagnostics diagnostics)
    {
        this.sweep = sweep;
        this.gatherables = gatherables;
        this.boards = boards;
        this.cells = cells;
        this.config = config;

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
                    timed = row.Timed,
                    reachable = row.Reachable,
                }),
            },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

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

        var timed = config.GatherIncludeTimed;

        if (ImGui.Checkbox("Include timed nodes", ref timed))
        {
            config.GatherIncludeTimed = timed;
            model.Invalidate();
        }

        if (current.Rows.Length > 0)
        {
            ImGui.SameLine();

            // A list to take away, since the gathering itself happens in another plugin's window.
            if (ImGui.Button("Copy list"))
                ImGui.SetClipboardText(string.Join("\n", current.Rows.Select(row => row.Name)));

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The names shown, one per line, for pasting into whatever does your gathering.");
        }
    }

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

        if (!ImGui.BeginTable("gather", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("job", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("each", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("sales/day", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("gil/day", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("node", ImGuiTableColumnFlags.WidthFixed, 70);
        Cell.Headers(Help);

        foreach (var row in current.Rows)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            cells.Draw(row.Name, row.ItemId);

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
            ImGui.TextColored(row.Timed ? Palette.Bad : Palette.Dim, row.Timed ? "timed" : "always");
        }

        ImGui.EndTable();
    }

    /// <summary>What the shortlist is worth against the books the cache holds now.</summary>
    private Model Build()
    {
        var scan = sweep.Current;

        if (!scan.HasResults)
            return new Model([], 0);

        var tax = boards.Tax;
        var byItem = gatherables.All().ToDictionary(gatherable => gatherable.ItemId);
        var levels = new Dictionary<uint, int>();

        var rows = new List<Row>();
        var hidden = 0;

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

            rows.Add(new Row(
                itemId,
                cells.Name(itemId),
                gatherable.Job,
                gatherable.Level,
                reachable,
                gatherable.Timed,
                sale.Net,
                book.SaleVelocityPerDay,
                (long)(sale.Net * perDay)));
        }

        return new Model([.. rows.OrderByDescending(row => row.GilPerDay).Take(RowsInTable)], hidden);
    }

    private sealed record Row(
        uint ItemId,
        string Name,
        string Job,
        int Level,
        bool Reachable,
        bool Timed,
        long Each,
        double SalesPerDay,
        long GilPerDay);

    private sealed record Model(Row[] Rows, int Hidden);
}
