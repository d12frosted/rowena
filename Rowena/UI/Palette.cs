using System.Numerics;

namespace Rowena.UI;

/// <summary>
/// The colours the window speaks in, and what each one means.
/// </summary>
/// <remarks>
/// Each carries exactly one meaning: <see cref="Good"/> is gil you would make, <see cref="Bad"/>
/// is gil or data you have not got, <see cref="Dim"/> is context you are not being asked to act
/// on, and <see cref="Plain"/> is everything else. Colouring anything for decoration would spend
/// the only signal a table of numbers has.
///
/// <see cref="Warm"/> and <see cref="Hot"/> exist for the one measurement that is a scale rather
/// than a verdict: how long a sale takes. A day and a month are both "some days", and the column
/// was reading as a column of numbers when it is the column most likely to turn a good trade
/// into a bad one. See <see cref="Cell.Absorb"/> for the thresholds.
/// </remarks>
internal static class Palette
{
    public static readonly Vector4 Dim = new(0.60f, 0.60f, 0.62f, 1f);
    public static readonly Vector4 Plain = new(1f, 1f, 1f, 1f);
    public static readonly Vector4 Good = new(0.40f, 0.80f, 0.45f, 1f);
    public static readonly Vector4 Warm = new(0.85f, 0.78f, 0.35f, 1f);
    public static readonly Vector4 Hot = new(0.90f, 0.60f, 0.30f, 1f);
    public static readonly Vector4 Bad = new(0.85f, 0.45f, 0.40f, 1f);
}
