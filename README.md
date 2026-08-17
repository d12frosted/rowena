# Rowena

Works out what a fixed-rate trade in FFXIV is actually worth, by reading how deep the
market board is behind the price it advertises.

Named after the woman who runs every scrip exchange in the game and has never once given
anybody a good deal.

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

Rowena prices an order by walking the book, not by multiplying the floor.

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

27% more gil for the same gathering, from the same snapshot. A chain names its steps and the
composed rate is derived from the published ones, so 100,000 scrips per mount is computed
rather than typed and cannot drift away from them.

Two things a margin needs to survive contact with reality, both carried on every quote:

- **Executability.** Wanting a hundred of something does not mean a hundred exist. Inputs
  the board cannot supply are reported, not silently priced at zero.
- **Absorption.** Mount Token moves 80 units a day; the mount itself moves 1.57. A margin
  you cannot sell into is not a margin, so `DaysToAbsorb` sits next to the profit rather
  than being buried in a fudge factor on the price.

`LargestProfitableSize` answers the question that follows from all of this: not "is this
profitable" but "how many can I actually do before the book eats the margin".

## The catalogue

Trades live in `conversions.json`, because every rate in it is something a patch can change
and adding a sink should be an edit, not a rebuild.

```json
{
  "resources": {
    "orange-gatherers-scrip": { "kind": "currency", "id": 41785, "name": "Orange Gatherers' Scrip" },
    "mount-token":            { "kind": "item",     "id": 41807, "name": "Mount Token" }
  },
  "conversions": [
    { "id": "scrip-to-token", "venue": "Scrip Exchange",
      "inputs":  [ { "resource": "orange-gatherers-scrip", "quantity": 1000 } ],
      "outputs": [ { "resource": "mount-token", "quantity": 1 } ] }
  ],
  "chains": [
    { "id": "scrip-to-rroneek", "steps": [ "scrip-to-token", "tokens-to-rroneek" ] }
  ]
}
```

A chain names its steps and nothing else; what links them is inferred from what one produces
and the next consumes, and only has to be named when it is genuinely ambiguous. A catalogue
that names an unknown resource, reuses an id or will not compose fails loudly and says which,
since one that quietly dropped half its entries would read as "nothing is worth doing".

A copy ships embedded, so the library works with no configuration. The plugin writes it out
to its config directory on first run, so there is a real file to edit rather than a schema to
read about. Break that file and you lose your edit, not the plugin.

## The plugin

`/rowena` opens one window: what you are holding, and what it is worth turning into.

Deliberately not a market browser. It only answers the questions that need both halves of
the picture, your balances and the depth of the board, because either alone is already
covered by something else. Sinks are ranked by gil per scrip; flips show outlay, profit,
return and how many runs the book will actually bear.

Balances are the reason this is a plugin at all rather than a script. Whether the answer is
"buy" or "go gather" depends on how many scrips you are sitting on and how close that is to
the cap, and nothing outside the game knows that.

It reads and displays. It does not drive your character.

## Layout

```
Rowena.Core/          plain .NET, no Dalamud, no game types
  Market/             listings, order books, depth, tax
  Conversions/        resources, conversions, chaining, evaluation, catalogue
  Universalis/        parsing and fetching
Rowena.Tests/         xunit, with recorded Universalis responses
Rowena/               the Dalamud plugin, net10.0-windows
  Game/               the only code that touches the client
  Market/             price cache
  UI/                 the window
```

All of the arithmetic is in the core, which is where the mistakes will be, so none of it
needs a running client to test. Iterating market maths through a plugin reload loop is no way
to live. The game-touching surface is one small file, so when a patch moves something there
is one place to look.

```bash
dotnet test                          # core, anywhere
dotnet build Rowena/Rowena.csproj    # plugin, needs Dalamud dev assemblies
```

The plugin build finds Dalamud via `DALAMUD_HOME`, or the usual XIVLauncher and XIV on Mac
locations. Only the plugin project needs it; the core and its tests build without.

## Not here yet

- **Handing work off.** GatherBuddyReborn for the gathering that feeds a chain, Artisan for
  anything that turns out to want crafting.
- **Alerts.** A fill under some price is a live event and currently you have to go looking.
- **Undercut watching.** Covered well enough by Marketbuddy and Dagobert; no reason to
  rebuild it.

## One thing to verify

The 5% is modelled as coming out of the seller's proceeds, which is how gil-making
arithmetic is normally written. Universalis reports a `tax` field on each listing, which
hints the board may charge it buy-side instead. The rounding is pinned to real data (a
146,385 gil listing carries 7,319 tax, so the fraction is dropped, not rounded), but which
side pays is worth confirming in game. If it is the other way round, `MarketTax` is the only
place that changes.
