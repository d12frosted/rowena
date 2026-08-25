namespace Rowena.Core.Market;

/// <summary>What sort of thing a notice is, which decides how the overview folds it.</summary>
public enum NoticeKind
{
    /// <summary>Something a retainer sold. Folded: five sales are one line with one total.</summary>
    Sale,

    /// <summary>
    /// A repricing run: how many listings it changed, or why it stopped. Never folded into
    /// the sales line, since repricing brings in no gil and counting it there reads as a
    /// sale that paid nothing.
    /// </summary>
    Reprice,

    /// <summary>Somebody listed below one of yours.</summary>
    Undercut,

    /// <summary>Something appeared for less than a vendor pays.</summary>
    VendorFind,

    /// <summary>The login briefing and the housekeeping around it.</summary>
    Briefing,
}

/// <summary>
/// One thing worth saying, when it was said, and the numbers behind it where there are any.
/// </summary>
/// <param name="Count">How many things, for notices that are about a count. Zero otherwise.</param>
/// <param name="Gil">How much gil, for notices that are about gil. Zero otherwise.</param>
public readonly record struct Notice(
    NoticeKind Kind,
    DateTimeOffset At,
    string Text,
    int Count = 0,
    long Gil = 0);

/// <summary>
/// The "while you were away" list, reduced to what is worth reading.
/// </summary>
/// <remarks>
/// Each retainer announces its own sales as it is opened, so a session with three retainers
/// says "sold while you were away" three times in a row with three totals to add up. Nobody
/// wants the breakdown by retainer; they want the number. Everything else is one event per
/// line already and is left as it came.
/// </remarks>
public static class AwayDigest
{
    /// <summary>One line of the digest.</summary>
    public readonly record struct Line(NoticeKind Kind, DateTimeOffset At, string Text);

    public static IReadOnlyList<Line> Fold(IReadOnlyList<Notice> notices)
    {
        var lines = new List<Line>();

        var sales = notices.Where(notice => notice.Kind == NoticeKind.Sale).ToArray();

        if (sales.Length == 1)
            lines.Add(new Line(NoticeKind.Sale, sales[0].At, sales[0].Text));
        else if (sales.Length > 1)
        {
            lines.Add(new Line(
                NoticeKind.Sale,
                sales.Max(sale => sale.At),
                $"Sold while you were away: {sales.Sum(sale => sale.Count)} things for {sales.Sum(sale => sale.Gil).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} gil."));
        }

        lines.AddRange(
            notices.Where(notice => notice.Kind != NoticeKind.Sale)
                .Select(notice => new Line(notice.Kind, notice.At, notice.Text)));

        return lines.OrderByDescending(line => line.At).ToArray();
    }
}
