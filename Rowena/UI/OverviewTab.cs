using Dalamud.Bindings.ImGui;
using Rowena.Market;

namespace Rowena.UI;

/// <summary>
/// What to do first, which is the only question worth a window on logging in.
/// </summary>
/// <remarks>
/// Five tabs each answer a good question and none of them answers that one. Opening the plugin
/// and reading all five to work out where to start is exactly the friction this was meant to
/// remove, so this asks each of them for its best answer and puts them in order.
///
/// Ordered by what expires rather than by what pays. A flip worth two million will still be
/// there in ten minutes and a gathering window worth eighty thousand will not, so the window
/// goes first. That is the one ordering a person cannot easily do in their head, since the big
/// number is the one that catches the eye.
///
/// Deliberately says nothing new. Every line here is another tab's own answer, phrased shorter,
/// which is what keeps this from becoming a sixth thing to maintain and a sixth thing to
/// disagree with the others.
/// </remarks>
internal sealed class OverviewTab(
    ConvertTab convert,
    CraftTab crafts,
    VendorTab vendor,
    GatherTab gather,
    SellingTab selling,
    HoardTab hoard,
    Notices notices,
    CraftSweep sweep,
    Configuration config,
    Action<MainWindow.Tab> show)
{
    /// <summary>What the overview is saying, for checking it without a window.</summary>
    public string Dump() =>
        System.Text.Json.JsonSerializer.Serialize(
            new
            {
                notes = Notes().OrderBy(note => note.Urgency)
                    .Select(note => new { note.Urgency, note.Headline, note.Detail, goes = note.Goes.ToString() }),
                whileAway = notices.All().Select(notice => notice.Text),
            },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    public void Draw()
    {
        ImGui.TextUnformatted("What to do first, soonest to expire at the top");
        ImGui.Spacing();

        var notes = Notes().OrderBy(note => note.Urgency).ToArray();

        if (notes.Length == 0)
        {
            ImGui.TextColored(
                Palette.Dim,
                "\n    Nothing to report yet. The other tabs fill this in as their prices arrive,\n"
                + "    and the scans they rest on have to be run once each.");
            return;
        }

        foreach (var note in notes)
            Draw(note);

        DrawNotices();
    }

    /// <summary>
    /// What happened while you were not looking.
    /// </summary>
    /// <remarks>
    /// These used to go to the game's chat log and no longer do. A line in chat is a line in
    /// every screenshot, and nothing this plugin has to say is worth announcing itself in the
    /// game world for. So they wait here instead, which is the only place they were ever
    /// really wanted.
    /// </remarks>
    private void DrawNotices()
    {
        if (notices.All() is not { Count: > 0 } recent)
            return;

        ImGui.Spacing();
        ImGui.TextColored(Palette.Dim, "While you were away");
        ImGui.Indent();

        foreach (var notice in recent)
        {
            ImGui.TextColored(
                Palette.Dim,
                $"{Phrases.Ago(DateTimeOffset.UtcNow - notice.At)} ago: {notice.Text}");
        }

        ImGui.Unindent();
    }

    private void Draw(Note note)
    {
        ImGui.Spacing();
        ImGui.TextColored(note.Colour, note.Headline);

        ImGui.Indent();
        ImGui.TextColored(Palette.Dim, note.Detail);

        if (ImGui.SmallButton($"Go##{note.Headline}"))
            show(note.Goes);

        ImGui.Unindent();
        ImGui.Separator();
    }

    /// <summary>
    /// Every tab's best answer, and the one thing no tab owns.
    /// </summary>
    /// <remarks>
    /// A sweep that has gone stale belongs to nothing in particular: the craft table rests on
    /// it and cannot tell you it is old without the row it would have shown you being wrong
    /// already.
    /// </remarks>
    private IEnumerable<Note> Notes()
    {
        foreach (var note in convert.Headlines())
            yield return note;

        foreach (var note in new[]
                 {
                     gather.Headline(), selling.Headline(), vendor.Headline(), crafts.Headline(), hoard.Headline(),
                 })
        {
            if (note is { } one)
                yield return one;
        }

        // Milliseconds, as the sweep stores them. Read as seconds this is a date past the end
        // of representable time, which is how it announced itself.
        if (sweep.Stored() is { } snapshot
            && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(snapshot.At) > config.SweepAge())
        {
            yield return new Note(
                Note.Housekeeping,
                Palette.Dim,
                "The craft sweep has gone stale",
                $"Older than the {config.SweepMaxAgeHours} hours you asked for, so the craft table is "
                + "ranking on prices that have moved.",
                MainWindow.Tab.Craft);
        }
    }
}
