using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Rowena.Core.Market;

namespace Rowena.Game;

/// <summary>
/// What I have actually sold, which nothing else knows.
/// </summary>
/// <remarks>
/// Universalis knows what the market did. Nothing knows what I did, and the two are different
/// questions: "this sells for about a thousand" is a fact about other people, while "mine sold
/// for nine hundred after sitting a week" is the one that says whether my prices are any good.
///
/// The game announces every retainer sale in chat and the item arrives as a link rather than a
/// name, so the item is exact and only the numbers are read from the text. Free, and the only
/// record of it that survives logging out, since the game keeps no history a plugin can ask
/// for.
///
/// Kept for months rather than to a count, newest first, in a file of its own: the
/// configuration is rewritten on every small change and should not carry a season of sales
/// along each time. What was in the configuration from before is carried over once.
/// </remarks>
internal sealed class SalesLog : IDisposable
{
    /// <summary>A ceiling nothing should reach: a safety net under the age limit, not a policy.</summary>
    private const int Cap = 50_000;

    private static readonly System.Text.Json.JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private readonly IChatGui chat;
    private readonly Configuration config;
    private readonly string path;
    private readonly Action save;
    private readonly Diagnostics diagnostics;
    private readonly IPluginLog log;

    private List<SaleRecord> sales;
    private readonly object gate = new();

    public SalesLog(
        IChatGui chat,
        Configuration config,
        string path,
        Action save,
        Diagnostics diagnostics,
        IPluginLog log)
    {
        this.chat = chat;
        this.config = config;
        this.path = path;
        this.save = save;
        this.diagnostics = diagnostics;
        this.log = log;

        sales = [.. SalesRetention.Prune(Load().Concat(Carried()), DateTimeOffset.UtcNow, config.SalesKeepDays, Cap)];

        // Once carried over, the configuration's copy is done with. Left there it would be
        // carried over again on every load and counted twice.
        if (config.Sales.Count > 0)
        {
            config.Sales = [];
            Write();
            save();
            diagnostics.Note("sales", $"moved the sales record into its own file, {sales.Count} kept");
        }

        chat.ChatMessage += OnMessage;
    }

    /// <summary>What the file holds, or nothing when there is no file yet.</summary>
    private IEnumerable<SaleRecord> Load()
    {
        try
        {
            if (!File.Exists(path))
                return [];

            var stored = System.Text.Json.JsonSerializer.Deserialize<List<StoredSale>>(File.ReadAllText(path), Options) ?? [];
            return stored.Select(Read);
        }
        catch (Exception error)
        {
            // A record that cannot be read is a record starting over, not a plugin that will not load.
            log.Warning(error, "Could not read the sales record.");
            return [];
        }
    }

    /// <summary>What the configuration still holds from before the record had a file.</summary>
    private IEnumerable<SaleRecord> Carried() => config.Sales.Select(Read);

    private static SaleRecord Read(StoredSale stored) => new(
        stored.ItemId,
        stored.Quantity,
        stored.Gil,
        DateTimeOffset.FromUnixTimeSeconds(stored.At),
        stored.Announced);

    private void Write()
    {
        try
        {
            List<StoredSale> stored;

            lock (gate)
            {
                stored =
                [
                    .. sales.Select(one => new StoredSale
                    {
                        ItemId = one.ItemId,
                        Quantity = one.Quantity,
                        Gil = one.Gil,
                        At = one.At.ToUnixTimeSeconds(),
                        Announced = one.Announced,
                    }),
                ];
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(stored, Options));
        }
        catch (Exception error)
        {
            log.Warning(error, "Could not write the sales record.");
        }
    }

    public void Dispose() => chat.ChatMessage -= OnMessage;

    /// <summary>
    /// Remembers one sale, however it was learned of.
    /// </summary>
    /// <remarks>
    /// Announced sales come from chat and are exact. Inferred ones are read off a retainer's
    /// slots and purse when nobody was online to be told, and they are marked as such rather
    /// than folded in: one is what the game said, the other is what the numbers imply.
    /// </remarks>
    public void Record(uint itemId, int quantity, long gil, bool announced)
    {
        lock (gate)
        {
            sales.Insert(0, new SaleRecord(itemId, quantity, gil, DateTimeOffset.UtcNow, announced));
            sales = [.. SalesRetention.Prune(sales, DateTimeOffset.UtcNow, config.SalesKeepDays, Cap)];
        }

        Write();
    }

    /// <summary>Everything remembered, newest first.</summary>
    public IReadOnlyList<SaleRecord> All()
    {
        lock (gate)
            return [.. sales];
    }

    /// <summary>What one item has sold for lately, newest first.</summary>
    public IReadOnlyList<SaleRecord> For(uint itemId)
    {
        lock (gate)
            return [.. sales.Where(sale => sale.ItemId == itemId)];
    }

    /// <summary>
    /// How much of each item the game announced since a moment, by item.
    /// </summary>
    /// <remarks>
    /// For the reconciliation, which reads a retainer's slots and cannot tell a sale I was told
    /// about from one I was not. Both leave an empty slot and gil in the purse.
    /// </remarks>
    public IReadOnlyDictionary<uint, int> AnnouncedSince(DateTimeOffset at)
    {
        var told = new Dictionary<uint, int>();

        lock (gate)
        {
            foreach (var sale in sales.Where(sale => sale.Announced && sale.At >= at))
                told[sale.ItemId] = told.GetValueOrDefault(sale.ItemId) + sale.Quantity;
        }

        return told;
    }

    /// <summary>Everything sold since a moment, for asking how the week went.</summary>
    public IReadOnlyList<SaleRecord> Since(DateTimeOffset at)
    {
        lock (gate)
            return [.. sales.Where(sale => sale.At >= at)];
    }

    /// <summary>
    /// Records a sale the game has just announced.
    /// </summary>
    /// <remarks>
    /// The item comes from the message's own link rather than from its name, so nothing here
    /// depends on how an item is spelled. The numbers do depend on the wording being English,
    /// which is a real limit: a client in another language records nothing rather than
    /// recording something wrong.
    /// </remarks>
    private void OnMessage(IHandleableChatMessage entry)
    {
        if ((XivChatType)entry.LogKind != XivChatType.RetainerSale)
        {
            // A sale announced under a kind this does not expect would otherwise be lost in
            // silence, and silence is indistinguishable from "nothing has sold yet". Only when
            // the diagnostics are on, and only for lines that are plainly about a sale.
            if (config.Diagnostics && entry.Message.TextValue.Contains("put up for sale"))
                diagnostics.Note("sales", $"a sale arrived as {(XivChatType)entry.LogKind}, not RetainerSale");

            return;
        }

        try
        {
            var message = entry.Message;

            if (message.Payloads.OfType<ItemPayload>().FirstOrDefault() is not { } item)
                return;

            if (SaleMessage.Read(message.TextValue) is not { } sold)
            {
                diagnostics.Note("sales", $"could not read a sale message: {message.TextValue}");
                return;
            }

            Record(item.ItemId, sold.Quantity, sold.Gil, announced: true);
            diagnostics.Note("sales", $"sold {sold.Quantity}x {item.ItemId} for {sold.Gil:N0}");
        }
        catch (Exception error)
        {
            log.Warning(error, "Could not record a retainer sale.");
        }
    }
}
