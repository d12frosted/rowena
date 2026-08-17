# Splendors

Works out what a fixed-rate trade in FFXIV is actually worth, by reading how deep the
market board is behind the price it advertises.

The name is Rowena's, since the scrip counter is where most of these trades happen.

## Why

Every market tool shows the cheapest listing. The cheapest listing is a bad summary of a
market, and acting on it loses money.

A recorded snapshot of Mount Token on Light, floor 48,795:

| | gil |
|---|---|
| what the floor implies for 100 units | 4,879,500 |
| what 100 units actually cost | 5,012,665 |
| the difference | 133,165 |

The blended price is 50,127 and the last unit costs 50,895. That gap turned a trade that
looked like 21% into one that pays 17.5%. Same board, same moment, just read properly.

Splendors prices an order by walking the book, not by multiplying the floor.

## What it models

A **conversion** is a fixed-rate trade at some counter in the world: hand these in, receive
those. Scrips into a token, tokens into a mount, seals into anything.

This is the gap the crafting-profit tools cannot cover. They model recipes, so they price
materials-in, product-out. A scrip exchange is not a recipe and neither is a token counter,
and both turn out to be where the margin is.

Conversions **chain**, and that matters more than it sounds. Selling the intermediate is the
obvious move and it is the worse one:

| | gil per orange gatherers' scrip |
|---|---|
| gather, mint tokens, sell tokens | 46.36 |
| gather, mint tokens, redeem for a mount, sell the mount | 58.90 |

27% more gil for the same gathering, from the same snapshot. `ConversionChain.Compose`
derives the composed rate from the two published ones, so 100,000 scrips per mount is
computed rather than typed and cannot drift away from them.

Two things a margin needs to survive contact with reality, both carried on every quote:

- **Executability.** Wanting a hundred of something does not mean a hundred exist. Inputs
  the board cannot supply are reported, not silently priced at zero.
- **Absorption.** Mount Token moves 80 units a day; the mount itself moves 1.57. A margin
  you cannot sell into is not a margin, so `DaysToAbsorb` sits next to the profit rather
  than being buried in a fudge factor on the price.

`LargestProfitableSize` answers the question that follows from all of this: not "is this
profitable" but "how many can I actually do before the book eats the margin".

## Layout

```
Splendors.Core/          plain .NET, no Dalamud, no game types
  Market/                listings, order books, depth, tax
  Conversions/           resources, conversions, chaining, evaluation, seed catalogue
  Universalis/           parsing and fetching
Splendors.Tests/         xunit, with recorded Universalis responses
```

The core is deliberately free of Dalamud so it builds and runs anywhere. All of the
arithmetic lives there, which is where the mistakes will be, so none of it should need a
running client to test. Iterating market maths through a plugin reload loop is no way to
live.

```bash
dotnet test
```

## Not here yet

- **The plugin.** Everything that genuinely needs the client: current scrip balances
  against the 4,000 cap, gil, inventory, retainer stock. Knowing whether to buy or to
  gather depends on what you are already holding, and only the client knows that.
- **A JSON catalogue.** `ConversionCatalog` is hardcoded seed data. The whole point of the
  conversion shape is that adding a sink should be an edit, not a build, and every rate in
  it is something a patch can change.
- **Handing work off.** GatherBuddyReborn for the gathering that feeds a chain, Artisan for
  anything that turns out to want crafting.
- **Undercut watching.** Covered well enough by Marketbuddy and Dagobert; no reason to
  rebuild it.

## One thing to verify

The 5% is modelled as coming out of the seller's proceeds, which is how gil-making
arithmetic is normally written. Universalis reports a `tax` field on each listing, which
hints the board may charge it buy-side instead. The rounding is pinned to real data (a
146,385 gil listing carries 7,319 tax, so the fraction is dropped, not rounded), but which
side pays is worth confirming in game. If it is the other way round, `MarketTax` is the only
place that changes.
