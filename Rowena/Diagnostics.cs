using Dalamud.Plugin.Services;

namespace Rowena;

/// <summary>
/// A running account of what the invisible half of the plugin is doing.
/// </summary>
/// <remarks>
/// Most of what this plugin does now happens where nothing is drawn: a queue deciding what to
/// fetch next, a socket being told an item moved, the game handing over tax rates when a board
/// is opened. When one of those quietly does not happen there is nothing on screen to explain
/// it, and "it does not seem to work" is not something anybody can act on.
///
/// Free when off: the calls are still made, but nothing is kept and nothing is written, so
/// leaving them in the hot paths costs a branch.
///
/// Kept in memory as well as written to the Dalamud log, because the point is to be able to
/// look at the last few minutes without going and finding a file.
/// </remarks>
internal sealed class Diagnostics(Configuration config, IPluginLog log)
{
    /// <summary>How many entries are kept. A few minutes of a busy session.</summary>
    private const int Kept = 300;

    private readonly Queue<Entry> entries = new();
    private readonly object gate = new();

    public bool Enabled => config.Diagnostics;

    /// <param name="Area">Which part of the plugin said it, for reading a mixed list.</param>
    public readonly record struct Entry(DateTime At, string Area, string Message);

    /// <summary>Notes something worth seeing later. Does nothing while diagnostics are off.</summary>
    public void Note(string area, string message)
    {
        if (!Enabled)
            return;

        lock (gate)
        {
            entries.Enqueue(new Entry(DateTime.Now, area, message));

            while (entries.Count > Kept)
                entries.Dequeue();
        }

        log.Information($"[{area}] {message}");
    }

    /// <summary>The entries kept, oldest first.</summary>
    public IReadOnlyList<Entry> Recent()
    {
        lock (gate)
            return [.. entries];
    }

    public void Clear()
    {
        lock (gate)
            entries.Clear();
    }
}
