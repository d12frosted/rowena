namespace Rowena.UI;

/// <summary>
/// A value rebuilt on a clock rather than per frame.
/// </summary>
/// <remarks>
/// Reading currencies, asking another plugin over IPC and allocating a shared order book are all far
/// too expensive to do per frame, and doing them per frame earned a hitch warning from Dalamud.
/// Everything drawn here is built into a snapshot a couple of times a second and then merely
/// rendered.
///
/// One of these per view rather than one for the window, which is what makes a hidden tab actually
/// free: nobody asks a tab nobody is looking at for its numbers, so nobody builds them.
/// </remarks>
internal sealed class Rebuilt<T>(Func<T> build, TimeSpan? every = null)
    where T : class
{
    /// <summary>
    /// How often the numbers are recomputed.
    /// </summary>
    /// <remarks>
    /// Nothing here changes faster than the eye, and a scrip balance that lags by a fraction of a
    /// second has never misled anyone.
    /// </remarks>
    private static readonly TimeSpan Default = TimeSpan.FromMilliseconds(500);

    private readonly TimeSpan interval = every ?? Default;

    private T? value;
    private DateTime builtAt;

    public T Current
    {
        get
        {
            if (value is not null && DateTime.UtcNow - builtAt < interval)
                return value;

            value = build();
            builtAt = DateTime.UtcNow;
            return value;
        }
    }

    /// <summary>
    /// Throws the snapshot away, for a change the clock cannot see.
    /// </summary>
    /// <remarks>
    /// The clock exists to stop the same answer being recomputed pointlessly, not to make a different
    /// question wait for it. Pressing something and watching nothing happen for half a second reads as
    /// a control that does not work.
    /// </remarks>
    public void Invalidate() => value = null;
}
