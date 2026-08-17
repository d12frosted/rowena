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
}
