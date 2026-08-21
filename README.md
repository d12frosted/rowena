# Rowena

Works out what a fixed-rate trade in FFXIV is actually worth, by reading how deep the
market board is behind the price it advertises.

Named after the woman who runs every scrip exchange in the game and has never once given
anybody a good deal.

> **Highly experimental, and still being built.** Things move between commits, and nobody
> but me has run this. The arithmetic is tested. The data under it is whatever Universalis
> last heard from somebody's client, so check the board yourself before acting on a number
> here.

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

Most trades are read out of the game's own shop sheets, because the game already publishes
every exchange it operates: every scrip counter, tomestone vendor, hunt shop and seal
quartermaster, machine-readably, updated by the patch itself. Around eleven hundred
exchanges come out of that walk, and none of them can go stale. Only shops some NPC
actually offers count, since the sheets also keep every shop that ever existed, and a
retired exchange would come back as a trade priced in a currency the counter no longer
takes.

That scale forces a discipline the small catalogue never needed: a hundred and fifty
currencies cannot all be on screen or all be priced. So generated trades earn their place
by being runnable, which for anything spending a bound currency means you actually hold
some. Their prices are only fetched on the same rule.

On top of that sits `conversions.json`, hand-written, because a file can say things the
sheets cannot: chains, handoffs, a venue written for a human, and which currencies are
worth watching even at a balance of zero. Where the file and the sheets describe the same
trade, the file wins.

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

`/rowena` opens one window: what you are holding, and what it is worth turning into. Naming
a tab, `/rowena flips`, opens on that one instead of toggling.

Deliberately not a market browser. It only answers the questions that need both halves of
the picture, your balances and the depth of the board, because either alone is already
covered by something else. Sinks are ranked by gil per scrip; flips show outlay, profit,
return and how many runs the book will actually bear.

A strip across the top says which boards you are pricing against, what is in your pockets
and how old the prices are. Every number below is read against it, so it does not scroll
away. Under it, a tab per question:

- **Sinks.** What a bound currency in your pockets is worth spending, one currency at a
  time: pick it, and its sinks rank by gil per unit. Currencies the file names stay on
  screen at zero, since "is it worth going to earn scrips" is asked precisely when you have
  none; the generated ones earn a place by being held.
- **Flips.** Buy the inputs on the board, hand them in, sell what comes out. Each row names
  the transaction rather than the product, says where the hand-in happens, and carries each
  input with its cost in the tooltip. Rows compete for one order book and are sized
  together.
- **Craft.** The swept furnishing ranking and the list you are building from it. Its own tab
  because it runs on its own clock, hours rather than minutes, and because it is the only
  thing here that is a workspace rather than a report. The tab counts what is in the list,
  since the crafting itself happens in somebody else's window and a half-built list is easy
  to forget. Any column sorts, and the sort happens before the table is trimmed to
  twenty-five rows, so "by profit" means the best of all of them rather than the best of the
  ones that happened to survive the default ranking.
- **Settings.** The knobs, with a line under each saying what it does to the plugin rather
  than restating its name. The two that decide whether any other number is right, which
  boards to price against, used to be file-only, and Dalamud's settings gear used to open the
  market screen and call that settings. The catalogue file reloads from here too, without
  touching the plugin.

Tabs rather than one long screen. The furnishing table is twenty-five rows and was pushing
the scrip tables off the top, and nobody has ever needed both in view at once. It also means
the numbers behind a tab you are not looking at are never computed.

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

## Installing

Rowena is not in any plugin repository, so it installs as a Dalamud dev plugin.

```bash
./scripts/install.sh
```

That builds, copies the output next to the game's own data, and registers the copied
assembly as a dev plugin location. `--status` shows what is built and what is installed,
`--dry-run` changes nothing, `--uninstall` undoes it.

Three things about the registration are easy to get wrong and all three fail the same way,
with Dalamud reporting a path that does not exist:

- the path has to name the assembly, not the folder holding it
- DevMode has to be on, or dev plugin locations are never scanned
- the game runs under wine, where `/` is `Z:`, so the path is the Windows-shaped one

Once registered, installing again works with the game running; Dalamud reloads the assembly
itself. Only a registration change has to wait for the game to close, because Dalamud writes
its whole config out on exit and will discard anything edited underneath it.

## Trades that compete are sized together

Sizing each trade on its own overstates all of them, and does it in a way that reads as
opportunity rather than as a warning. Rroneek Horn and Barreltender Whistle both want a
hundred Mount Tokens, and against a book holding 323 they each reported that three runs were
available. Six hundred tokens out of three hundred and twenty-three.

`ConversionAllocation` divides the book instead, committing whichever single run pays most
against whatever is left, repeatedly. Buying deeper only ever costs more, so marginal profit
never rises and greedy is exact rather than approximate. On that same snapshot the answer is
not a split:

| | runs | outlay | profit |
|---|---|---|---|
| Mount Tokens to Barreltender Whistle | 3 | 15,192,842 | 6,182,116 |
| Mount Tokens to Rroneek Horn | 0 | 0 | 0 |

Every token is worth more through Barreltender, so every token goes there. Rows that get
nothing still show what one run would have paid, dimmed, so the comparison is visible rather
than merely absent.

## GatherBuddyReborn is read, not driven

Rowena works out that a chain wants 100,000 scrips. It does not gather them, and it does not
try to start anything either.

There were buttons here for its auto-gather and its collectable routine. They were not worth
having: the list and its settings live in GatherBuddyReborn, so anyone about to gather is
already in that window and will start it there. GBR also handles the scrip cap on its own,
turning collectables in and buying from a configured purchase list, so there was nothing left
for Rowena to usefully drive. Duplicating a control with strictly less capability is worse
than not offering it.

What is read is its state, on one line. That is worth having for a reason the buttons were
not: an earning rate is only meaningful measured over time actually spent gathering, and this
is what distinguishes a working hour from an hour at a menu.

How a currency is earned stays as catalogue data, a `handoff` string on a conversion, which a
chain inherits from its first step since that is where the earning happens. The core never
interprets it. It is the link an activity will need in order to know what it produces.

## Not here yet

- **Artisan.** For a chain that turns out to want crafting rather than gathering.
- **Alerts.** A fill under some price is a live event and currently you have to go looking.
- **Undercut watching.** Covered well enough by Marketbuddy and Dagobert; no reason to
  rebuild it.

## The board taxes both sides

The buyer pays a flat 5% on top of every listing, and the seller receives the listed price
less 0 to 5% depending on the city the retainer stands in. Both cuts are in the model: the
book walk charges the buyer's tax per listing as it goes, so an outlay is gil out of pocket
rather than the sticker, and proceeds are netted at the maximum seller rate, which is what
the three original city states always charge. Parking retainers in a cheaper city is a real
1 to 5% edge the numbers deliberately do not assume.

The rounding is pinned to recorded data: a 146,385 gil listing carries 7,319 tax where 5%
is 7,319.25, and a 7,499,990 one carries 374,999 where 5% is exactly 374,999.5. Floored,
both times, even at the half.
