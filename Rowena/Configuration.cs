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

    /// <summary>
    /// The world to price sales against, or empty for the one you are logged in to.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Scope"/> because buying and selling do not happen on the same board.
    /// Your retainers sell where they stand.
    /// </remarks>
    public string HomeScope { get; set; } = "";

    /// <summary>How long a price snapshot is trusted before it is refetched.</summary>
    /// <remarks>
    /// Universalis is free and crowdsourced and asks callers to be reasonable. Nothing here
    /// changes minute to minute in a way that matters, so a few minutes is plenty.
    /// </remarks>
    public int PriceTtlMinutes { get; set; } = 10;

    /// <summary>The same span, floored at a minute. A method, so it stays out of the saved file.</summary>
    public TimeSpan PriceTtl() => TimeSpan.FromMinutes(Math.Max(1, PriceTtlMinutes));

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
    /// The same span, floored at an hour.
    /// </summary>
    /// <remarks>
    /// A method rather than a property so it does not end up in the saved file as a duplicate of the
    /// number it is derived from. Two callers, which is why the floor lives here: a zero typed into
    /// the settings would otherwise mean "everything is stale" in one place and "resweep constantly"
    /// in the other.
    /// </remarks>
    public TimeSpan SweepAge() => TimeSpan.FromHours(Math.Max(1, SweepMaxAgeHours));

    /// <summary>
    /// How many crafts a day you could actually perform, or zero for "the market decides".
    /// </summary>
    /// <remarks>
    /// Without this the only ceiling on a craft is the market's appetite, which flatters
    /// anything quick to make. Retainer slots usually bind before either.
    /// </remarks>
    public int CraftsPerDayCap { get; set; }

    /// <summary>
    /// How many days of selling a sink is judged over.
    /// </summary>
    /// <remarks>
    /// A sink is ranked by what it would actually bank within this horizon: runs you can afford,
    /// capped by runs the board would absorb in the time. A week by default. The per-unit rate
    /// is still shown, but ranking on it alone crowned items nobody had ever bought.
    /// </remarks>
    public int SellingHorizonDays { get; set; } = 7;

    /// <summary>The same span, floored at a day.</summary>
    public int SellingHorizon() => Math.Max(1, SellingHorizonDays);

    /// <summary>
    /// The smallest vendor find worth showing, in gil.
    /// </summary>
    /// <remarks>
    /// Somebody listing a stack of five at one gil under the vendor price is arithmetic, not
    /// an opportunity, and a table full of those buries the one that is worth the trip.
    /// </remarks>
    public int VendorFindFloor { get; set; } = 1_000;

    /// <summary>
    /// How many of the scan's candidates get their book fetched in full.
    /// </summary>
    /// <remarks>
    /// The survey says an item is worth a look; only a full book says how many units are
    /// cheap enough and what they pay. Each one is a request, so this is the knob between a
    /// thorough scan and a polite one. Ranked by margin per unit, which is what a summary
    /// supports.
    /// </remarks>
    public int VendorCandidatesToCost { get; set; } = 120;

    /// <summary>Whether logging in earns one line in chat saying what is worth knowing.</summary>
    public bool BriefOnLogin { get; set; } = true;

    /// <summary>Whether a currency entering the last tenth of its cap is said in chat, once.</summary>
    public bool AlertNearCap { get; set; } = true;

    /// <summary>
    /// The return a flip has to reach before it is said in chat, in percent. Zero turns it off.
    /// </summary>
    public int AlertFlipReturnPercent { get; set; } = 100;

    /// <summary>Whether a furnishing sweep older than its re-sweep age is said in chat, once.</summary>
    public bool AlertStaleSweep { get; set; } = true;

    /// <summary>
    /// The currency the sink table was last looking at, by item id. Zero for "whichever comes first".
    /// </summary>
    /// <remarks>
    /// Remembered because one table is shown at a time and the one you were reading is the one you
    /// will want again. An id that is no longer in your pockets falls back quietly.
    /// </remarks>
    public uint SinkCurrency { get; set; }

    /// <summary>
    /// Crafts queued up for an Artisan list that has not been exported yet.
    /// </summary>
    /// <remarks>
    /// In the configuration and not the price cache: a basket is something you meant rather than
    /// something that was fetched, so it should survive a reload for the same reason a setting does.
    /// </remarks>
    public List<BasketItem> ArtisanBasket { get; set; } = [];

    /// <summary>The name given to the list Artisan imports.</summary>
    public string ArtisanListName { get; set; } = "Rowena picks";

    public sealed class BasketItem
    {
        /// <summary>Artisan keys list entries by recipe, not by item.</summary>
        public uint RecipeId { get; set; }

        public uint ItemId { get; set; }

        public string Name { get; set; } = "";

        public int Quantity { get; set; }
    }
}
