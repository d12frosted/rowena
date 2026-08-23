using System.Numerics;

namespace Rowena.UI;

/// <summary>
/// One thing worth doing, as the overview wants to say it.
/// </summary>
/// <remarks>
/// Every tab already knows its own best answer and had no way to say so briefly. This is that
/// way: a figure, a line, a reason, and where to go to act on it.
/// </remarks>
/// <param name="Urgency">
/// Lower is sooner. Not importance: a gathering window worth eighty thousand outranks a flip
/// worth two million because the flip will still be there in ten minutes and the window will
/// not.
/// </param>
/// <param name="Figure">
/// The number, on its own, so it can be the first thing on the row. A sentence with a number
/// in the middle of it makes the eye hunt; the number in front and the sentence after does
/// not. Empty when there is no honest number, and the headline stands alone.
/// </param>
internal readonly record struct Note(
    int Urgency,
    Vector4 Colour,
    string Figure,
    string Headline,
    string Detail,
    MainWindow.Tab Goes)
{
    /// <summary>Something that expires: a window, a rate, a listing about to be undercut.</summary>
    public const int Expiring = 0;

    /// <summary>Money already yours that is going wrong.</summary>
    public const int AtRisk = 1;

    /// <summary>Money on the table, which will keep.</summary>
    public const int Waiting = 2;

    /// <summary>Worth knowing, worth nothing in particular.</summary>
    public const int Housekeeping = 3;

    /// <summary>The heading the overview groups this under.</summary>
    public string Band => Urgency switch
    {
        Expiring => "Expiring",
        AtRisk => "Going wrong",
        Waiting => "Worth doing",
        _ => "Housekeeping",
    };
}
