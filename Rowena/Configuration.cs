using Dalamud.Configuration;

namespace Rowena;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>
    /// The Universalis scope to price against: a world, data centre or region name.
    /// </summary>
    /// <remarks>
    /// Empty means "work it out from where I am logged in", which is right almost always.
    /// It is settable because the interesting question is sometimes about a neighbouring
    /// data centre: you can carry items home from one, even though you cannot list there.
    /// </remarks>
    public string Scope { get; set; } = "";

    /// <summary>How long a price snapshot is trusted before it is refetched.</summary>
    /// <remarks>
    /// Universalis is free and crowdsourced and asks callers to be reasonable. Nothing here
    /// changes minute to minute in a way that matters, so a few minutes is plenty.
    /// </remarks>
    public int PriceTtlMinutes { get; set; } = 10;

    /// <summary>
    /// How many listings deep to fetch.
    /// </summary>
    /// <remarks>
    /// Deliberately generous. A shallow fetch is exactly the mistake this plugin exists to
    /// correct, and asking for ten listings would quietly reintroduce it.
    /// </remarks>
    public int ListingDepth { get; set; } = 40;

    /// <summary>Largest number of runs to consider when sizing a trade.</summary>
    public int SizingCap { get; set; } = 20;

    /// <summary>
    /// How many item ids go into one Universalis request.
    /// </summary>
    /// <remarks>
    /// Eight, measured rather than chosen, and revised downward once. Universalis times out at ten
    /// seconds and its response time grows with the id count: ten ids reliably 504, and an earlier
    /// reading of twenty only passed because it landed at 8.4 seconds. Raising this does not make a
    /// sweep faster, it makes it fail.
    /// </remarks>
    public int PriceBatchSize { get; set; } = 8;

    /// <summary>
    /// How many item ids go into one summary request.
    /// </summary>
    /// <remarks>
    /// A hundred, and comfortably so: a hundred summarised comes back in under two seconds where ten
    /// with their listings times out. The summary carries no depth, which is why the first pass of a
    /// sweep can be this wide and the second cannot.
    /// </remarks>
    public int SurveyBatchSize { get; set; } = 100;

    /// <summary>
    /// How many furnishings to cost, after ranking them all on revenue potential.
    /// </summary>
    /// <remarks>
    /// The second pass of a sweep prices the materials of these, and materials are where the
    /// request count lives. Sixty is generous enough that the leaders are certainly inside it.
    /// </remarks>
    public int FurnishingShortlist { get; set; } = 60;

    /// <summary>
    /// How old swept prices may be before a sweep is worth repeating.
    /// </summary>
    /// <remarks>
    /// Hours rather than the minutes the flip tables want, and deliberately so. Choosing which of
    /// nine hundred furnishings to make needs a rough map, not live depth, and treating the two
    /// questions the same would mean either a stale flip or an unaffordable sweep.
    /// </remarks>
    public int SweepMaxAgeHours { get; set; } = 12;

    /// <summary>
    /// How many crafts a day you could actually perform, or zero for "the market decides".
    /// </summary>
    /// <remarks>
    /// Without this the only ceiling on a craft is the market's appetite, which flatters
    /// anything quick to make. Retainer slots usually bind before either.
    /// </remarks>
    public int CraftsPerDayCap { get; set; }
}
