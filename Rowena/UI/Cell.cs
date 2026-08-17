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
    public static void Right(string text) => Right(Palette.Plain, text);

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

            if (help[column] is { } text && ImGui.IsItemHovered())
                ImGui.SetTooltip(text);
        }
    }
}
