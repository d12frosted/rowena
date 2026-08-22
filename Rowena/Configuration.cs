using Dalamud.Configuration;

namespace Rowena;

/// <summary>One of my retainer's listings, as it goes to the configuration file.</summary>
/// <remarks>
/// A plain class rather than the record the rest of the plugin passes around, because this one
/// has to survive being written to disk and read back by a serializer that knows nothing about
/// it.
/// </remarks>
public sealed class StoredListing
{
    public uint ItemId { get; set; }

    public long UnitPrice { get; set; }

    public int Quantity { get; set; }

    public bool IsHq { get; set; }

    public string Retainer { get; set; } = "";

    public uint CityId { get; set; }

    public long SeenAt { get; set; }
}

/// <summary>One of my own sales, as it goes to the configuration file.</summary>
public sealed class StoredSale
{
    public uint ItemId { get; set; }

    public int Quantity { get; set; }

    public long Gil { get; set; }

    public long At { get; set; }

    /// <summary>False when it was read off a retainer's slots rather than announced in chat.</summary>
    public bool Announced { get; set; } = true;
}

/// <summary>One of a retainer's market slots, as it goes to the configuration file.</summary>
public sealed class StoredSlot
{
    public uint ItemId { get; set; }

    public int Quantity { get; set; }

    public long UnitPrice { get; set; }

    public bool IsHq { get; set; }
}

/// <summary>
/// A retainer as it was last seen: what it had listed, and what was in its purse.
/// </summary>
/// <remarks>
/// Both halves are needed. The slots say what went and the purse says whether it sold or was
/// simply taken back.
/// </remarks>
public sealed class StoredRetainer
{
    public ulong RetainerId { get; set; }

    public long Gil { get; set; }

    public long SeenAt { get; set; }

    public List<StoredSlot> Slots { get; set; } = [];
}

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

    /// <summary>
    /// How many of one thing you would realistically gather and list in a day.
    /// </summary>
    /// <remarks>
    /// Zero lets the market decide, which flatters anything cheap that moves in bulk: the board
    /// turns over seventy-five thousand water crystals a day and no one person supplies them, so
    /// ranking on the market's appetite alone puts forty-six gil crystals above four thousand
    /// gil flax. Your own hands are the tighter limit almost always, and this is them.
    /// </remarks>
    public int GatherPerDayCap { get; set; } = 500;

    /// <summary>How many gatherables survive the survey and get their books fetched.</summary>
    public int GatherShortlist { get; set; } = 80;

    /// <summary>The job the gathering table is filtered to, or zero for either.</summary>
    public uint GatherJob { get; set; }

    /// <summary>Whether to hide nodes above your level on the job that gathers them.</summary>
    public bool GatherReachableOnly { get; set; } = true;

    /// <summary>Whether to include things only found on nodes that appear on a clock.</summary>
    public bool GatherIncludeTimed { get; set; } = true;

    /// <summary>How long the session you are planning for is, in minutes. Zero ranks instead of planning.</summary>
    public int GatherSessionMinutes { get; set; }

    /// <summary>
    /// How many items an hour of gathering yields.
    /// </summary>
    /// <remarks>
    /// The one number in a session plan that is assumed rather than known, so it is a setting
    /// rather than a constant and everything built on it says it is scaled by it.
    /// </remarks>
    public int GatherPerHour { get; set; } = 300;

    /// <summary>What a session plan is trying to be good at. Indexes <c>GatherAim</c>.</summary>
    public int GatherAim { get; set; }

    /// <summary>
    /// How many items one visit to a timed node is worth.
    /// </summary>
    /// <remarks>
    /// With <see cref="GatherWindowMinutes"/>, this is what lets a node on a clock be compared
    /// against an ordinary one instead of being dropped for not fitting the assumption. Both are
    /// assumed rather than measured, which is why they are settings.
    /// </remarks>
    public int GatherWindowYield { get; set; } = 40;

    /// <summary>How long the detour to a timed node costs, in minutes.</summary>
    public int GatherWindowMinutes { get; set; } = 5;

    /// <summary>
    /// The world the vendor table is filtered to, or empty for all of them.
    /// </summary>
    /// <remarks>
    /// Buying is per world, so a find on a world I will not travel to is not an opportunity.
    /// Remembered because the answer to "which worlds do I bother with" changes rarely.
    /// </remarks>
    public string VendorWorld { get; set; } = "";

    /// <summary>
    /// What my retainers had listed when the board last said so.
    /// </summary>
    /// <remarks>
    /// Kept because the game only says it when I happen to look up something I am selling, and
    /// that is not something to have to do again after every reload. Stale by nature: a listing
    /// can sell while the game is closed, so each carries when it was seen and the next look at
    /// that item replaces it.
    /// </remarks>
    public List<StoredListing> MyListings { get; set; } = [];

    /// <summary>
    /// What my retainers have sold, newest first.
    /// </summary>
    /// <remarks>
    /// The game keeps no history a plugin can ask for, so this is the only record of it that
    /// survives logging out.
    /// </remarks>
    public List<StoredSale> Sales { get; set; } = [];

    /// <summary>Each retainer as it was last seen, for working out what sold while away.</summary>
    public List<StoredRetainer> Retainers { get; set; } = [];

    /// <summary>
    /// The seller's cut per city, as the game last reported it, and when it stops being true.
    /// </summary>
    /// <remarks>
    /// Kept because the game only says it when asked, at a retainer vocate or a retainer's sell
    /// list, and it holds for hours. Without this, every reload went back to assuming the worst
    /// until somebody went and asked again, which is a poor reason to be wrong about every net
    /// figure on the screen.
    /// </remarks>
    public Dictionary<uint, double> SellerRates { get; set; } = [];

    public long SellerRatesUntil { get; set; }

    /// <summary>
    /// Whether to keep an account of what the plugin is doing where nothing is drawn.
    /// </summary>
    /// <remarks>
    /// On while this is experimental and I am the only one running it: the half of the plugin
    /// that fetches, follows and listens draws nothing, so without this there is no way to tell
    /// a quiet success from a silent failure. The fetch queue, the live feed, the game's own
    /// market packets and any slow redraw all say what they are up to, in the settings tab and
    /// in the Dalamud log.
    /// </remarks>
    public bool Diagnostics { get; set; } = true;

    /// <summary>
    /// Whether to hold a websocket open to Universalis and refetch what it says has changed.
    /// </summary>
    /// <remarks>
    /// Cheaper for them than polling and much fresher for us. What arrives is a signal rather
    /// than prices: the feed sends deltas, and only a fetch is trusted for depth.
    /// </remarks>
    public bool LiveMarket { get; set; } = true;

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
    /// Whether to say when a timed gathering node opens.
    /// </summary>
    /// <remarks>
    /// The only thing this plugin knows that will not still be true in ten minutes, which is
    /// what makes saying it unprompted the right thing rather than an interruption. A window
    /// advertised as four game hours is under twelve real minutes.
    /// </remarks>
    public bool AlertWindows { get; set; }

    /// <summary>How much one has to be worth before a window is worth mentioning.</summary>
    public int AlertWindowWorth { get; set; } = 1_000;

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
