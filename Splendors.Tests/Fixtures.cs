using Splendors.Core.Market;
using Splendors.Core.Universalis;

namespace Splendors.Tests;

/// <summary>
/// Universalis responses recorded from Light, so the suite runs offline and the numbers
/// asserted below stay the numbers that were actually on the board when they were written.
/// </summary>
internal static class Fixtures
{
    public const string MountToken = "light-41807.json";
    public const string RroneekHorn = "light-43598.json";

    /// <summary>
    /// A listing recorded at 48,795 gil for three units, carrying a tax of 7,319. Used to
    /// pin how the board rounds its cut.
    /// </summary>
    public const long RecordedListingTotal = 146_385;
    public const long RecordedListingTax = 7_319;

    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static OrderBook Book(string name) => UniversalisJson.ParseItem(Read(name));
}
