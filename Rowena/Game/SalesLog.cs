using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Rowena.Core.Market;

namespace Rowena.Game;

/// <summary>One of my own sales, as it happened.</summary>
internal readonly record struct Sale(uint ItemId, int Quantity, long Gil, DateTimeOffset At)
{
    /// <summary>What one of them fetched, net of the fees the message has already taken off.</summary>
    public long Each => Quantity > 0 ? Gil / Quantity : Gil;
}

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
/// Kept to a few hundred, newest first. This is evidence about how things have been selling
/// lately, not an accounting ledger, and a file that grows forever to answer a question about
/// the last fortnight is a file nobody wants.
/// </remarks>
internal sealed class SalesLog : IDisposable
{
    /// <summary>How many are kept before the oldest are dropped.</summary>
    private const int Keep = 500;

    private readonly IChatGui chat;
    private readonly Configuration config;
    private readonly Action save;
    private readonly Diagnostics diagnostics;
    private readonly IPluginLog log;

    private readonly List<Sale> sales;
    private readonly object gate = new();

    public SalesLog(
        IChatGui chat,
        Configuration config,
        Action save,
        Diagnostics diagnostics,
        IPluginLog log)
    {
        this.chat = chat;
        this.config = config;
        this.save = save;
        this.diagnostics = diagnostics;
        this.log = log;

        sales =
        [
            .. config.Sales.Select(stored => new Sale(
                stored.ItemId,
                stored.Quantity,
                stored.Gil,
                DateTimeOffset.FromUnixTimeSeconds(stored.At))),
        ];

        chat.ChatMessage += OnMessage;
    }

    public void Dispose() => chat.ChatMessage -= OnMessage;

    /// <summary>Everything remembered, newest first.</summary>
    public IReadOnlyList<Sale> All()
    {
        lock (gate)
            return [.. sales];
    }

    /// <summary>What one item has sold for lately, newest first.</summary>
    public IReadOnlyList<Sale> For(uint itemId)
    {
        lock (gate)
            return [.. sales.Where(sale => sale.ItemId == itemId)];
    }

    /// <summary>Everything sold since a moment, for asking how the week went.</summary>
    public IReadOnlyList<Sale> Since(DateTimeOffset at)
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

            var sale = new Sale(item.ItemId, sold.Quantity, sold.Gil, DateTimeOffset.UtcNow);

            lock (gate)
            {
                sales.Insert(0, sale);

                if (sales.Count > Keep)
                    sales.RemoveRange(Keep, sales.Count - Keep);

                config.Sales =
                [
                    .. sales.Select(one => new StoredSale
                    {
                        ItemId = one.ItemId,
                        Quantity = one.Quantity,
                        Gil = one.Gil,
                        At = one.At.ToUnixTimeSeconds(),
                    }),
                ];
            }

            save();
            diagnostics.Note("sales", $"sold {sold.Quantity}x {item.ItemId} for {sold.Gil:N0}");
        }
        catch (Exception error)
        {
            log.Warning(error, "Could not record a retainer sale.");
        }
    }
}
