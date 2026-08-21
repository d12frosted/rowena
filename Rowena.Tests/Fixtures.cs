using Rowena.Core.Market;
using Rowena.Core.Universalis;

namespace Rowena.Tests;

/// <summary>
/// Universalis responses recorded from Light, so the suite runs offline and the numbers
/// asserted below stay the numbers that were actually on the board when they were written.
/// </summary>
internal static class Fixtures
{
    public const string MountToken = "light-41807.json";
    public const string RroneekHorn = "light-43598.json";
    public const string BarreltenderWhistle = "light-44502.json";

    /// <summary>The aggregated endpoint's answer for a bench, a wardrobe and a Mount Token.</summary>
    public const string Aggregated = "light-aggregated.json";

    /// <summary>The same call scoped to one world, which carries a world branch as well as a dc one.</summary>
    public const string AggregatedWorld = "shiva-aggregated.json";

    /// <summary>
    /// A listing recorded at 48,795 gil for three units, carrying a tax of 7,319. Used to
    /// pin how the board rounds its cut.
    /// </summary>
    public const long RecordedListingTotal = 146_385;
    public const long RecordedListingTax = 7_319;

    /// <summary>
    /// A Barreltender listing whose 5% lands on exactly half a gil: 7,499,990 recorded
    /// with a tax of 374,999. Pins that the half is dropped rather than rounded up.
    /// </summary>
    public const long RecordedHalfGilListingTotal = 7_499_990;
    public const long RecordedHalfGilListingTax = 374_999;

    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>A frame recorded off the live websocket, which speaks BSON rather than JSON.</summary>
    public static byte[] Bytes(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static OrderBook Book(string name) => UniversalisJson.ParseItem(Read(name));
}
