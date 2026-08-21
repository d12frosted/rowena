using System.Net.WebSockets;

namespace Rowena.Core.Universalis;

/// <summary>Something happened to an item on a world.</summary>
/// <param name="Event">The channel it came from: listings added or removed, or a sale.</param>
public readonly record struct MarketChange(string Event, uint ItemId, uint WorldId);

/// <summary>
/// Universalis pushing changes as they happen, so the cache stops being something to refresh.
/// </summary>
/// <remarks>
/// A signal, deliberately, and not a source of prices. The feed sends what changed rather than
/// what is: measured against the live board, a listings/add carried one listing for an item the
/// world had thirty-seven of. Keeping a book up to date from those would mean replaying every
/// event without ever missing one across every disconnect, and a book that quietly drifted
/// would be the one failure this plugin cannot afford, since depth is the whole point.
///
/// So what arrives here is "this item moved", and what follows is a refetch of that item
/// through the ordinary queue. The push decides when, the fetch decides what. It also costs
/// Universalis less than polling, which is the polite way round.
///
/// Subscriptions are per world, so a data centre is one per world in it. Measured on Shiva,
/// one world is about thirty-five messages a minute across the three channels, so eight worlds
/// is a handful a second before filtering.
/// </remarks>
public sealed class MarketFeed : IDisposable
{
    private static readonly Uri Endpoint = new("wss://universalis.app/api/ws");

    /// <summary>Added, removed, sold: all three mean the book is not what it was.</summary>
    private static readonly string[] Channels = ["listings/add", "listings/remove", "sales/add"];

    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60)];

    private readonly CancellationTokenSource stopping = new();
    private readonly Func<byte[], int, MarketChange?> read;

    private uint[] worlds = [];
    private Task? running;

    public MarketFeed() => read = Decode;

    /// <summary>Raised for every change on a watched world. Off the socket's thread.</summary>
    public event Action<MarketChange>? Changed;

    /// <summary>Whether the socket is up right now.</summary>
    public bool Connected { get; private set; }

    /// <summary>The last thing to go wrong, for saying so rather than going quiet.</summary>
    public string? LastError { get; private set; }

    /// <summary>How many changes have arrived since the feed started.</summary>
    public long Received { get; private set; }

    /// <summary>The channel name the feed expects for one event on one world.</summary>
    public static string Channel(string channel, uint worldId) => $"{channel}{{world={worldId}}}";

    /// <summary>
    /// Watches these worlds, starting or restarting the connection as needed.
    /// </summary>
    /// <remarks>
    /// Restarts rather than adjusting the subscriptions in place. The set changes when a
    /// character logs in on a different data centre, which is rare enough that reconnecting is
    /// simpler than tracking what is subscribed to what.
    /// </remarks>
    public void Watch(IEnumerable<uint> worldIds)
    {
        var wanted = worldIds.Distinct().OrderBy(id => id).ToArray();

        if (wanted.SequenceEqual(worlds))
            return;

        worlds = wanted;
        running ??= Task.Run(() => Run(stopping.Token));
    }

    public void Dispose()
    {
        stopping.Cancel();
        stopping.Dispose();
    }

    private async Task Run(CancellationToken cancellationToken)
    {
        var failures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var subscribed = worlds;

            try
            {
                if (subscribed.Length == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await Session(subscribed, cancellationToken).ConfigureAwait(false);
                failures = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception error)
            {
                LastError = error.Message;
                failures++;
            }
            finally
            {
                Connected = false;
            }

            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                await Task.Delay(Backoff[Math.Min(failures, Backoff.Length - 1)], cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task Session(uint[] subscribed, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(Endpoint, cancellationToken).ConfigureAwait(false);

        foreach (var world in subscribed)
        {
            foreach (var channel in Channels)
            {
                var message = Bson.Document(("event", "subscribe"), ("channel", Channel(channel, world)));
                await socket
                    .SendAsync(message, WebSocketMessageType.Binary, true, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        Connected = true;
        LastError = null;

        var buffer = new byte[1 << 18];

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            // The set of worlds changed under us; the loop above will reconnect with the new one.
            if (!worlds.SequenceEqual(subscribed))
                return;

            var offset = 0;
            WebSocketReceiveResult result;

            do
            {
                if (offset >= buffer.Length)
                    throw new InvalidDataException("A frame arrived larger than anything expected.");

                result = await socket
                    .ReceiveAsync(new ArraySegment<byte>(buffer, offset, buffer.Length - offset), cancellationToken)
                    .ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                offset += result.Count;
            }
            while (!result.EndOfMessage);

            if (read(buffer, offset) is { } change)
            {
                Received++;
                Changed?.Invoke(change);
            }
        }
    }

    /// <summary>
    /// Reads a frame, or null when it is not one of ours.
    /// </summary>
    /// <remarks>
    /// A frame that cannot be read is noted and dropped rather than killing the session: the
    /// shapes belong to somebody else and one unfamiliar message is not a reason to stop
    /// listening to the rest.
    /// </remarks>
    private MarketChange? Decode(byte[] buffer, int length)
    {
        try
        {
            var document = Bson.Read(buffer.AsSpan(0, length));

            if (document.GetValueOrDefault("event") is not string name
                || document.GetValueOrDefault("item") is not { } item
                || document.GetValueOrDefault("world") is not { } world)
            {
                return null;
            }

            return new MarketChange(name, Convert.ToUInt32(item), Convert.ToUInt32(world));
        }
        catch (Exception error)
        {
            LastError = error.Message;
            return null;
        }
    }
}
