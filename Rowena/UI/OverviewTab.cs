using System.Numerics;
using Dalamud.Bindings.ImGui;
using Rowena.Core.Market;
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
                    .Select(note => new
                    {
                        note.Urgency, note.Band, note.Figure, note.Headline, note.Detail, goes = note.Goes.ToString(),
                    }),
                whileAway = AwayDigest.Fold(notices.All()).Select(line => new { kind = line.Kind.ToString(), line.Text }),
            },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    public void Draw()
    {
        var notes = Notes().OrderBy(note => note.Urgency).ToArray();

        if (notes.Length == 0)
        {
            ImGui.TextColored(
                Palette.Dim,
                "\n    Nothing to report yet. The other tabs fill this in as their prices arrive,\n"
                + "    and the scans they rest on have to be run once each.");
        }
        else
        {
            DrawNotes(notes);
        }

        DrawNotices();
    }

    /// <summary>
    /// The notes, banded by how soon they stop being true.
    /// </summary>
    /// <remarks>
    /// Sorting by urgency was the whole point of the page and a flat list did not show it: six
    /// cards with a rule between each read as six equals. The band heading is what the sort
    /// means, said once per group instead of explained in a caption.
    ///
    /// One table per band but one figure width for all of them, measured from the widest
    /// figure on the page, so the numbers line up down the whole window rather than per group.
    /// Figures are right-aligned: a short one then ends where its sentence starts instead of
    /// floating at the far side of a column sized for the long ones, and digits line up the
    /// way numbers should.
    ///
    /// No row striping. Each band is its own table, so stripes restarted per band and a band
    /// of one row had none, which read as an accident rather than a pattern. The headings
    /// carry the grouping on their own.
    /// </remarks>
    private void DrawNotes(Note[] notes)
    {
        var figureWidth = notes.Max(note => ImGui.CalcTextSize(note.Figure).X) + ImGui.GetStyle().CellPadding.X * 2;

        foreach (var band in notes.GroupBy(note => note.Band))
        {
            ImGui.Spacing();
            ImGui.TextColored(Palette.Dim, band.Key);

            if (!ImGui.BeginTable($"overview-{band.Key}", 2, ImGuiTableFlags.PadOuterX))
                continue;

            ImGui.TableSetupColumn("figure", ImGuiTableColumnFlags.WidthFixed, figureWidth);
            ImGui.TableSetupColumn("what", ImGuiTableColumnFlags.WidthStretch);

            foreach (var note in band)
                Draw(note);

            ImGui.EndTable();
        }
    }

    /// <summary>
    /// One note: the figure, the line with its button on the end, the reason under it.
    /// </summary>
    /// <remarks>
    /// The button follows the headline rather than sitting in a column of its own. A column
    /// puts it at the window's far edge, which on a wide window is nowhere near the line it
    /// acts on; after the sentence it is where the eye already is when it finishes reading.
    /// </remarks>
    private void Draw(Note note)
    {
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(note.Figure).X);
        ImGui.TextColored(note.Colour, note.Figure);

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(note.Headline);
        ImGui.SameLine();

        if (ImGui.SmallButton($"Go##{note.Goes}-{note.Headline}"))
            show(note.Goes);

        ImGui.PushTextWrapPos();
        ImGui.TextColored(Palette.Dim, note.Detail);
        ImGui.PopTextWrapPos();
    }

    /// <summary>
    /// What happened while you were not looking.
    /// </summary>
    /// <remarks>
    /// These used to go to the game's chat log and no longer do. A line in chat is a line in
    /// every screenshot, and nothing this plugin has to say is worth announcing itself in the
    /// game world for. So they wait here instead, which is the only place they were ever
    /// really wanted.
    ///
    /// Folded rather than listed, see <see cref="AwayDigest"/>, and behind a header that is
    /// open while something in it is recent. An hour on, it is a log, and a log should not be
    /// taking up the room the notes want.
    /// </remarks>
    private void DrawNotices()
    {
        var lines = AwayDigest.Fold(notices.All());

        if (lines.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var recent = now - lines[0].At < TimeSpan.FromHours(1);

        ImGui.Spacing();
        ImGui.SetNextItemOpen(recent, ImGuiCond.Once);

        if (!ImGui.CollapsingHeader($"While you were away ({lines.Count})###while-away"))
            return;

        if (!ImGui.BeginTable("while-away", 2, ImGuiTableFlags.PadOuterX))
            return;

        ImGui.TableSetupColumn("when", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("59 min ago").X + ImGui.GetStyle().CellPadding.X * 2);
        ImGui.TableSetupColumn("what", ImGuiTableColumnFlags.WidthStretch);

        foreach (var line in lines)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(Palette.Dim, $"{Phrases.Ago(now - line.At)} ago");
            ImGui.TableNextColumn();
            ImGui.PushTextWrapPos();
            ImGui.TextColored(ColourOf(line.Kind), line.Text);
            ImGui.PopTextWrapPos();
        }

        ImGui.EndTable();
    }

    /// <summary>
    /// The palette's meanings, applied to a notice: gil that came in or is there to take is
    /// <see cref="Palette.Good"/>, a listing being beaten is <see cref="Palette.Bad"/>, and the
    /// briefing is context.
    /// </summary>
    private static Vector4 ColourOf(NoticeKind kind) => kind switch
    {
        NoticeKind.Sale or NoticeKind.VendorFind => Palette.Good,
        NoticeKind.Undercut => Palette.Bad,
        _ => Palette.Dim,
    };

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
                Phrases.Ago(DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(snapshot.At)),
                "since the craft sweep, which has gone stale",
                $"Older than the {config.SweepMaxAgeHours} hours you asked for, so the craft table is "
                + "ranking on prices that have moved.",
                MainWindow.Tab.Craft);
        }
    }
}
