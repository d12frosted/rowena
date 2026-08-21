namespace Rowena.Core.Market;

/// <summary>How much something is waited on, most urgent first.</summary>
/// <remarks>
/// The order of these members is the order work is done in, so they are declared in it.
/// </remarks>
public enum FetchPriority
{
    /// <summary>Somebody pressed something and is looking at the screen.</summary>
    Interactive,

    /// <summary>Keeping the tables current, wanted soon rather than now.</summary>
    Background,

    /// <summary>A scan of hundreds or thousands, wanted eventually.</summary>
    Sweep,
}

/// <summary>Which question is being asked of the board.</summary>
public enum FetchKind
{
    /// <summary>The full book, with its depth. Expensive, a handful of ids a request.</summary>
    Book,

    /// <summary>Price and sale rate only. Cheap, a hundred ids a request.</summary>
    Summary,
}

/// <summary>One request's worth of work: same board, same question, same urgency.</summary>
public readonly record struct FetchBatch(string Scope, FetchKind Kind, FetchPriority Priority, uint[] Ids);

/// <summary>
/// What is still to be asked of the market, in the order it should be asked.
/// </summary>
/// <remarks>
/// There is one fetcher, because Universalis is free and crowdsourced and asks callers to be
/// reasonable, and a plugin that opens ten connections is not. One fetcher means the order
/// matters, and the order used to be "whoever asked first, and everybody else silently gets
/// nothing": a vendor scan is a hundred and seventy requests over several minutes, and while
/// it ran, pressing refresh on a fifteen million gil trade did nothing at all.
///
/// So work is queued rather than attempted, and the queue is sorted by how much somebody is
/// waiting on it. A scan is thousands of ids that can wait; a press is two ids that cannot,
/// and it goes next rather than last. Asking twice for the same thing asks once, and asking
/// again more urgently promotes what is already queued rather than duplicating it.
///
/// Deliberately no fetching in here, and no timers: this is the order of the work, and it is
/// worth being able to test that without a network or a clock.
/// </remarks>
public sealed class FetchQueue
{
    private readonly Dictionary<Key, Entry> pending = [];
    private readonly object gate = new();

    private long sequence;

    /// <summary>How many ids are still to be asked about.</summary>
    public int Pending
    {
        get
        {
            lock (gate)
                return pending.Count;
        }
    }

    /// <summary>How many of them are waited on this much.</summary>
    public int PendingAt(FetchPriority priority)
    {
        lock (gate)
            return pending.Values.Count(entry => entry.Priority == priority);
    }

    /// <summary>
    /// Adds ids to the queue, or promotes them if they are already in it.
    /// </summary>
    /// <remarks>
    /// Never demotes. Something already wanted urgently stays urgent even if a sweep asks for
    /// it again in passing, because the sweep does not know who else is waiting.
    /// </remarks>
    public void Enqueue(string scope, FetchKind kind, IEnumerable<uint> itemIds, FetchPriority priority)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return;

        lock (gate)
        {
            foreach (var itemId in itemIds)
            {
                var key = new Key(scope, kind, itemId);

                if (pending.TryGetValue(key, out var existing))
                {
                    // Keeps its place in the queue: promotion is about urgency, not about
                    // jumping ahead of things that are equally urgent and were asked for first.
                    if (priority < existing.Priority)
                        pending[key] = existing with { Priority = priority };

                    continue;
                }

                pending[key] = new Entry(priority, sequence++);
            }
        }
    }

    /// <summary>
    /// Takes the next request's worth of work, or null when there is nothing to do.
    /// </summary>
    /// <remarks>
    /// A batch is one board and one question, because that is what a request is. Within that,
    /// the most urgent band goes first and the oldest of them leads, so a sweep still comes out
    /// in the order it was queued rather than shuffled.
    /// </remarks>
    public FetchBatch? Next(int maxIds) => Next(_ => maxIds);

    /// <summary>
    /// Takes the next request's worth of work, sized by what kind of question it turns out to be.
    /// </summary>
    /// <remarks>
    /// A summary request carries a hundred ids comfortably and a book request eight, so the size
    /// cannot be chosen until the batch is. The delegate is asked inside the lock, once, and is
    /// expected to be a lookup and nothing else.
    /// </remarks>
    public FetchBatch? Next(Func<FetchKind, int> maxIdsFor)
    {
        lock (gate)
        {
            if (pending.Count == 0)
                return null;

            // The most urgent thing waiting decides the band, and the oldest thing in that
            // band decides which board and question the request is about.
            var lead = pending
                .OrderBy(entry => entry.Value.Priority)
                .ThenBy(entry => entry.Value.Sequence)
                .First();

            var scope = lead.Key.Scope;
            var kind = lead.Key.Kind;
            var priority = lead.Value.Priority;

            var maxIds = Math.Max(1, maxIdsFor(kind));

            var taking = pending
                .Where(entry =>
                    entry.Value.Priority == priority
                    && entry.Key.Kind == kind
                    && string.Equals(entry.Key.Scope, scope, StringComparison.Ordinal))
                .OrderBy(entry => entry.Value.Sequence)
                .Take(maxIds)
                .Select(entry => entry.Key)
                .ToArray();

            foreach (var key in taking)
                pending.Remove(key);

            return new FetchBatch(scope, kind, priority, [.. taking.Select(key => key.ItemId)]);
        }
    }

    /// <summary>Forgets everything queued, for shutdown.</summary>
    public void Clear()
    {
        lock (gate)
            pending.Clear();
    }

    private readonly record struct Key(string Scope, FetchKind Kind, uint ItemId);

    private readonly record struct Entry(FetchPriority Priority, long Sequence);
}
