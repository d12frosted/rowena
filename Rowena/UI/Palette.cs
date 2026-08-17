using System.Numerics;

namespace Rowena.UI;

/// <summary>
/// The four colours the window speaks in.
/// </summary>
/// <remarks>
/// Four, and not a theme. Each one carries exactly one meaning: <see cref="Good"/> is gil you would
/// make, <see cref="Bad"/> is gil or data you have not got, <see cref="Dim"/> is context you are not
/// being asked to act on, and <see cref="Plain"/> is everything else. Colouring anything for
/// decoration would spend the only signal a table of numbers has.
/// </remarks>
internal static class Palette
{
    public static readonly Vector4 Dim = new(0.60f, 0.60f, 0.62f, 1f);
    public static readonly Vector4 Plain = new(1f, 1f, 1f, 1f);
    public static readonly Vector4 Good = new(0.40f, 0.80f, 0.45f, 1f);
    public static readonly Vector4 Bad = new(0.85f, 0.45f, 0.40f, 1f);
}
