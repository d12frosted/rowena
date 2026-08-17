namespace Splendors.Core.Conversions;

/// <summary>
/// The conversions worth watching, as seed data.
/// </summary>
/// <remarks>
/// Hardcoded for now so the library is useful and testable on day one. It should become a
/// JSON file, because the whole point of the <see cref="Conversion"/> shape is that adding
/// a sink is an edit rather than a build, and every rate in here is a thing Square Enix
/// can change in a patch.
///
/// Rates verified against the game's own vendors. Item ids come from the Item sheet.
/// </remarks>
public static class ConversionCatalog
{
    public static readonly Resource OrangeGatherersScrip = Resource.Currency(41785, "Orange Gatherers' Scrip");
    public static readonly Resource OrangeCraftersScrip = Resource.Currency(41784, "Orange Crafters' Scrip");

    public static readonly Resource MountToken = Resource.Item(41807, "Mount Token");
    public static readonly Resource RroneekHorn = Resource.Item(43598, "Rroneek Horn");
    public static readonly Resource BarreltenderWhistle = Resource.Item(44502, "Barreltender Whistle");

    /// <summary>
    /// The scrip sink. Also the reason a gatherer's time has a gil price at all: scrips
    /// cannot be sold, so this trade is what makes them worth anything.
    /// </summary>
    public static readonly Conversion ScripToToken = new(
        "scrip-to-token",
        "Orange gatherers' scrip to Mount Token",
        [new ResourceAmount(OrangeGatherersScrip, 1_000)],
        [new ResourceAmount(MountToken, 1)],
        "Scrip Exchange");

    public static readonly Conversion TokensToRroneek = new(
        "tokens-to-rroneek",
        "Mount Tokens to Rroneek Horn",
        [new ResourceAmount(MountToken, 100)],
        [new ResourceAmount(RroneekHorn, 1)],
        "Splendors Vendor, Solution Nine");

    public static readonly Conversion TokensToBarreltender = new(
        "tokens-to-barreltender",
        "Mount Tokens to Barreltender Whistle",
        [new ResourceAmount(MountToken, 100)],
        [new ResourceAmount(BarreltenderWhistle, 1)],
        "Splendors Vendor, Solution Nine");

    /// <summary>
    /// Scrips carried all the way to a mount instead of stopping at the token.
    /// </summary>
    /// <remarks>
    /// Composed rather than written out, so the 100,000 is derived from the two published
    /// rates and cannot drift away from them.
    /// </remarks>
    public static readonly Conversion ScripToRroneek =
        ConversionChain.Compose(ScripToToken, TokensToRroneek, MountToken);

    public static readonly Conversion ScripToBarreltender =
        ConversionChain.Compose(ScripToToken, TokensToBarreltender, MountToken);

    public static IReadOnlyList<Conversion> All =>
    [
        ScripToToken,
        TokensToRroneek,
        TokensToBarreltender,
        ScripToRroneek,
        ScripToBarreltender,
    ];
}
