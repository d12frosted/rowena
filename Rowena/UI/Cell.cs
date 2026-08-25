using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Rowena.UI;

/// <summary>
/// The two things a table cell needs that ImGui does not offer.
/// </summary>
internal static class Cell
{
    /// <summary>
    /// Right-aligns text in the column it is drawn in.
    /// </summary>
    /// <remarks>
    /// A column of gil is read by length as much as by digits, and left-aligned it cannot be: 912,000
    /// and 9,120,000 begin at the same place and end at different ones, so telling them apart means
    /// counting commas. ImGui has no column alignment, so the cursor is nudged along by whatever the
    /// text does not fill.
    /// </remarks>
    public static void Right(Vector4 colour, string text)
    {
        var slack = ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X;

        if (slack > 0f)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + slack);

        ImGui.TextColored(colour, text);
    }

    /// <summary>Right-aligns text in the colour a table uses for everything unremarkable.</summary>
    public static void Right(string text) => Right(Style.Plain, text);

    /// <summary>
    /// How long a sale takes, coloured by how much that should worry you.
    /// </summary>
    /// <remarks>
    /// Within a day is fine, within three is worth knowing, within a week is a commitment, and
    /// beyond that, or never, is the number that decides the trade. "Never" used to sit in the
    /// column in the same white as everything else, which is the wrong colour for the strongest
    /// thing the table can say. Dimmed rows, the ones not being acted on, stay dim: the scale is
    /// for rows you might act on.
    /// </remarks>
    public static void Absorb(double? days, bool dim = false, bool vendored = false)
    {
        // Sold to a vendor: no queue at all, and worth saying as the reason rather than as
        // "<1 day", since the reason is that the board pays less than the vendor after tax.
        if (vendored)
        {
            Right(dim ? Style.Muted : Style.Good, "vendor");
            Style.Explain("A vendor pays more than the board nets after tax, so the output is sold\nto one on the spot. No waiting, no undercutting.");

            return;
        }

        var colour = dim
            ? Style.Muted
            : days switch
            {
                null => Style.Bad,
                < 1d => Style.Good,
                < 3d => Style.Warn,
                < 7d => Style.Hot,
                _ => Style.Bad,
            };

        Right(colour, Phrases.Absorb(days));
    }

    /// <summary>
    /// The header row, with an explanation on the columns whose names are shorthand.
    /// </summary>
    /// <remarks>
    /// Drawn a header at a time rather than by <c>TableHeadersRow</c>, which draws them all and leaves
    /// nothing to hang a tooltip on. "to clear" and "held covers" are not English; the explanations
    /// existed already, but only on the cells, where you would have to suspect the answer before you
    /// could find out what the question was.
    ///
    /// Pass one entry per column, null where the name says enough. The sort click and its arrow are
    /// <c>TableHeader</c>'s own doing, so a sortable table keeps working.
    /// </remarks>
    public static void Headers(string?[] help)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

        for (var column = 0; column < help.Length; column++)
        {
            ImGui.TableSetColumnIndex(column);
            ImGui.TableHeader(ImGui.TableGetColumnName(column));

            if (help[column] is { } text)
                Style.Explain(text);
        }
    }
}
