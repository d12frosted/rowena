namespace Rowena.Core.Market;

/// <summary>One thing somebody actually did: paid this much a unit, at this moment.</summary>
/// <remarks>
/// The moment matters as much as the price. Twenty sales are the evidence for "what people
/// pay", and a market that moved last week leaves half of them describing a price that no
/// longer exists; without the when, the old half votes with the same weight as the new.
/// A sale from a source that did not say when carries <see langword="default"/> and reads
/// as ancient, which errs towards the whole list speaking rather than a false week.
/// </remarks>
public readonly record struct Sale(long UnitPrice, DateTimeOffset At);
