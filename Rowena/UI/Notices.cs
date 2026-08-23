using Rowena.Core.Market;

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

    public void Add(NoticeKind kind, string text, int count = 0, long gil = 0)
    {
        lock (gate)
        {
            notices.Insert(0, new Notice(kind, DateTimeOffset.UtcNow, text, count, gil));

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
