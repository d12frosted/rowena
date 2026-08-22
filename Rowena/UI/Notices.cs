namespace Rowena.UI;

/// <summary>
/// Things worth telling you, kept where only you will look.
/// </summary>
/// <remarks>
/// These used to go to the game's chat log. They do not any more, and nothing here writes into
/// the game at all: a line appearing in chat is a line in every screenshot, and a plugin that
/// announces itself in the game world is a plugin somebody can notice. Whatever this has to
/// say, it says inside its own window.
///
/// A rolling handful, newest first, cleared when the plugin restarts. This is a thing to
/// glance at rather than a record to keep, and the record already exists in the diagnostics.
/// </remarks>
internal sealed class Notices
{
    /// <summary>How many are kept. Enough for a session, short enough to read at a glance.</summary>
    private const int Keep = 12;

    private readonly List<Notice> notices = [];
    private readonly object gate = new();

    /// <summary>One thing worth saying, and when it was said.</summary>
    internal readonly record struct Notice(DateTimeOffset At, string Text);

    public void Add(string text)
    {
        lock (gate)
        {
            notices.Insert(0, new Notice(DateTimeOffset.UtcNow, text));

            if (notices.Count > Keep)
                notices.RemoveRange(Keep, notices.Count - Keep);
        }
    }

    /// <summary>Everything worth glancing at, newest first.</summary>
    public IReadOnlyList<Notice> All()
    {
        lock (gate)
            return [.. notices];
    }
}
