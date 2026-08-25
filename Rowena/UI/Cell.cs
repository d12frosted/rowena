using System.Numerics;
using Dalamud.Bindings.ImGui;
using Rowena.Core.Market;

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
    /// How old the board reading behind a row is, and whether it can still be acted on.
    /// </summary>
    /// <remarks>
    /// Muted while the answer is good for something, since at rest it is only there to be
    /// glanced past. Warn once it is past its shelf life, or was never fetched at all: those
    /// mean the same thing for the verdict beside it, that the verdict was reached on something
    /// other than the board as it stands, and that wants a person before it wants a plan.
    ///
    /// Not on the Hot scale. That scale belongs to how long a sale takes and to nothing else;
    /// this is two states and a number, not a gradient.
    /// </remarks>
    public static void Age(Freshness freshness)
    {
        Right(
            freshness.Standing == Standing.Fresh ? Style.Muted : Style.Warn,
            freshness.Age is { } age ? Phrases.Ago(age) : "none");

        Style.Explain(freshness.Standing switch
        {
            Standing.Unknown =>
                "Nobody has asked the board about this one yet. The column beside it is not saying\n"
                + "there is nothing to do; it is saying nothing at all. Refresh and it will answer.",
            Standing.Stale =>
                $"Read {Phrases.Ago(freshness.Age!.Value)} ago, which is past the shelf life set in\n"
                + "settings. Whatever the row beside it says was true then, not now.",
            _ => $"Read {Phrases.Ago(freshness.Age!.Value)} ago.",
        });
    }

    /// <summary>
    /// What to do about a floor that has fallen a long way under a listing.
    /// </summary>
    /// <remarks>
    /// The accent goes on buying the cheap stock out, because that is the only one of these
    /// that is an open want rather than a reason not to act. Taking a listing off the board
    /// wants a person, so it warns; sitting tight and accepting a moved price are states at
    /// rest and stay quiet. Nothing is drawn where there is no argument to make.
    /// </remarks>
    public static void Chase(ChaseVerdict chase, bool dim = false)
    {
        if (Phrases.Chase(chase.Call) is not { Length: > 0 } word)
            return;

        Right(
            dim ? Style.Muted : chase.Call switch
            {
                ChaseCall.BuyOut => Style.Accent,
                ChaseCall.Withdraw => Style.Warn,
                _ => Style.Muted,
            },
            word);

        Style.Explain(Phrases.ChaseWhy(chase));
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
