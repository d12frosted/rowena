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
quartermaster, machine-readably, updated by the patch itself. Around two and a half thousand
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
a tab, `/rowena vendor`, opens on that one instead of toggling.

Deliberately not a market browser. It only answers the questions that need both halves of
the picture, your balances and the depth of the board, because either alone is already
covered by something else. Sinks are ranked by what they would actually bank within a week,
not by the prettiest rate; flips show outlay, profit, return and how many runs the book will
actually bear.

A strip across the top says which boards you are pricing against, what is in your pockets
and how old the prices are. Every number below is read against it, so it does not scroll
away. Pockets means gil and the currencies you pinned in Settings (the four current scrips
to start with, gatherers' before crafters'), in the order you arranged them there, each
with its cap; any other currency shows up there only when it is into
the last tenth of its cap, in red, since whatever is earned past the cap is lost. Under it,
a tab per question:

- **Sinks.** What a bound currency in your pockets is worth spending, one currency at a
  time: pick it, and its sinks rank by what a week of selling would bank, which is the runs
  you can afford capped by the runs the board will take. The gil-per-unit rate is still
  there, but a rate nobody buys at is theory. Currencies the file names stay on
  screen at zero, since "is it worth going to earn scrips" is asked precisely when you have
  none; the generated ones earn a place by being held.
- **Flips.** Buy the inputs on the board, hand them in, sell what comes out. Each row names
  the transaction rather than the product, says where the hand-in happens, and carries each
  input with its cost in the tooltip. Rows compete for one order book and are sized
  together.
- **Vendor.** What is listed for less than a vendor will pay for it. Its own tab because it
  runs on its own clock, minutes to scan and useful for hours, and because it is the only
  table here that is not a judgement: every other one weighs a margin against how long it
  takes to sell, and this one has no market risk in it at all. The scan finds which items
  are worth watching; what they are worth is computed from the cache, so a row is as fresh
  as the last fetch and a find somebody else has bought out simply stops being shown.
- **Gather.** What to go and pick up, ranked by what a day of the board will pay for it. The
  one table with no outlay in it: every other weighs gil spent against gil returned, and this
  weighs an hour of your time. Seven hundred marketable gatherables is eight requests to
  survey, so it costs seconds rather than the minutes a furnishing sweep does. Filtered to a
  job and to what your levels can actually reach, since a list you have to filter in your
  head is not a list, and nodes that only appear on a clock say so, because that is a
  different errand. Fishing is deliberately absent: bait, weather and a cast that can fail
  are not things a market tool knows, and ranking a fish beside a mining node would promise
  an hour it cannot deliver.
  Given how long you actually have, the ranking becomes a list with amounts on it: the
  dearest things the board still has room for, in the order to gather them. Room is the
  board's throughput over the selling horizon less what is already listed, because a
  hundred and thirty a day is the whole market and the people already selling are ahead of
  you in it. So is your own pile: what sits in bags and retainers and what you already have
  listed goes out before anything gathered today, so the room it takes is not room for
  gathering. Nine hundred and ninety-nine of something that sells twelve a day is eighty
  days of stock; the row says so, drops to the bottom, and the plan leaves it out, since
  gathering more of it is gathering for later. Timed nodes are priced by what the detour
  costs rather than dropped, since a
  windowful for the price of the trip is usually a bargain and that is the whole reason
  they are worth going out of your way for.

  Optionally it notes when a paying window opens. That is the only unprompted thing in here,
  and it earns it by being the only thing that will not still be true in ten minutes. It is
  noted on the Overview rather than said in the game: see below.

  Timed nodes carry their windows, read off the game's own tables, and the tab counts them
  down in minutes you can feel rather than the game's hours. That conversion is the point:
  a window advertised as four hours lasts under twelve real minutes, and a two hour one under
  six. Read in game hours a timed node looks like something to get round to; read properly it
  is something you walk to now or miss. So a plan takes only the windows that are open, or
  will be before you are done, and lists them in the order the clock opens them: what is
  shutting soonest first, then what comes round next, with the all-day nodes last to fill the
  gaps between.

  Three aims, because they pull against each other: the most gil is the most gil in one
  thing, which is also the most exposed when that one thing's price moves while you are
  still selling it. A mixed bag caps any one thing at a quarter of the trip and earns
  perhaps a third less on paper. Sells soonest is the same ranking over tomorrow rather
  than the week, so none of it is still sitting in a retainer when you next log in.

  How much an hour yields is measured rather than assumed. What arrives in the bags while a
  node is open is counted, and so is the time from one gather to the next: travel counts,
  because an hour of gathering is mostly getting to the next node, and long gaps do not,
  because standing about is not gathering. Ten minutes of it are wanted before a rate off it
  is worth quoting, and the typed number stands until then. What a window is worth is still
  assumed, so it stays a setting and everything scaled by it says so.

  The list goes over to GatherBuddyReborn in one paste, as a gather window preset. Its IPC
  offers a version, a name lookup and a switch for auto-gather and nothing for managing what
  is in a list, but its own import takes a gzipped preset behind a version byte, so that is
  the hand-off: this decides what is worth gathering, and the plugin that gathers gathers it.
  A format rather than an interface, so the shape is pinned by a test that takes the string
  apart again.
- **Overview.** What to do first, which is the only question worth a window on logging in.
  Five tabs each answer a good question and none of them answered that one.

  Ordered by what expires rather than by what pays. A flip worth ten million will still be
  there in ten minutes and a gathering window worth a hundred and sixty thousand will not, so
  the window goes first. That is the one ordering nobody does well in their head, because the
  big number is the one that catches the eye.

  It says nothing new: every line is another tab's own answer phrased shorter, which is what
  keeps it from becoming a sixth thing to maintain and a sixth thing to disagree with the
  others.

  Each line leads with its number and sits under a band (expiring, going wrong, worth doing,
  housekeeping) that says what the ordering means. Under the notes, "while you were away"
  folds what happened since login: sales from every retainer become one line with one total,
  and the rest is kept one event per line. It opens on its own while something in it is
  recent and folds away once it is an hour old.
- **Selling.** What is already sitting on a retainer, which is where a good deal of gil
  quietly is not. Every other tab asks what to acquire; this one reads what you have out.

  What you have out comes from the retainers themselves: opening one reads its twenty market
  slots, prices and all, and that is remembered until it is opened again. A board search for
  one item is the fresher word on that item, since a listing can sell between two visits, so
  the two are folded together and the newer one wins. Retainers not opened yet are said to be
  missing rather than quietly left out.

  Not an undercut tool, deliberately. Undercutting is the one move the board makes easy and
  it is usually the wrong one: the question is never "is somebody cheaper than me" but "how
  long until the board has eaten through everyone cheaper than me". Three units ahead on a
  board selling ten a day are gone this afternoon, and dropping your price to jump them is a
  haircut for nothing. So each row shows the queue, what the thing actually changes hands
  for, and what chasing the floor would cost, then says what it thinks and shows its working.

  What it sells for is the number worth having beside what you are asking. Measured: ten
  Mozzarella listed at 389,994 on a board where every listing sits at that level and
  everything that actually trades goes for under 1,500. A wall of listings nobody takes is
  not a market, and being at the front of it is not a position.

  Having decided to undercut, the doing is cheap. Each row with somebody in front of it shows
  the price that would put it first (the cheapest listing ahead, less a margin from settings,
  five gil by default), and opening that listing's price dialog on the retainer fills the
  number in. A row nobody is paying for gets a target too, cheapest on the board or not: what
  recent sales went for, less the margin, once there are enough sales to trust. The front of
  a wall nobody buys from is not a position. Only the field: the game's confirm button is still the one that commits it.
  Items you would rather leave where they are can be ignored per item; the number stays on
  the row, the dialog is left alone, and the ignore clears once nothing of that item is
  listed anymore. Quality counts: cheaper NQ is not in front of an HQ listing, since nobody
  buying HQ takes the NQ instead, while cheaper HQ is in front of an NQ one.

  With a retainer open, the same column sits beside the game's own sell list, one row per
  listing in the list's order, showing the cheapest listing ahead and what yours would be
  cut to. Each undercut row has a button that reprices it through the game's own windows
  (the listing's menu, Adjust Price, the dialog, confirm), and one button does every
  undercut row in turn. Each step waits for its window and the run stops and says so if one
  does not appear, rather than sending a click to whatever is open instead. Confirming can be
  left to you in settings.

  Every row says how old the board reading behind it is, because the verdict beside it is
  only worth that number. Past the shelf life the age turns amber, and a listing nobody has
  looked up yet reads `none` rather than the same quiet dash a correctly priced one gets:
  those two were indistinguishable, so the column had to be checked against the board by
  hand, which is the work it exists to save. Refreshing counts the listings back as their
  answers land and says so when it is done, rather than leaving you to guess whether a press
  went anywhere. The count is of these listings, not of the fetcher's queue: a press made
  during a sweep waits for the sweep, and a count off the queue would say nothing about
  whether these twenty are current.

  Each row shows the move it would make, from what you are asking now to what it would ask
  instead. It used to show the floor and the target, which describes a different move
  entirely: a listing at 4,000 going under a floor of 2,000 read as "2,000 -> 1,995", five
  gil, when what is being given up is 2,005 a unit. What the target sits under is a fact
  about somebody else and lives in the tooltip.

  A move that gives up a quarter of the asking price or more is not competing with the
  listing in front, it is agreeing to a different price for the item, so it gets an argument
  rather than a button. A floor a long way under what the item has actually been selling for
  is somebody clearing a retainer slot, and what follows depends on how much of it there is.
  A couple of units are gone by the afternoon and matching them is a haircut for nothing, so
  the row says to sit tight. Where they are cheap enough to be worth taking, the row prices
  the buy-out instead: what the units under you cost to clear with the buyer's cut, against
  what they fetch back at the going rate after the seller's, since somebody else's panic at
  half price is the best-priced stock on the board and your own listing ends up first
  without moving. Four hundred units is not a panic; where recent sales agree with them the
  price really has moved, and where they do not, the row says to take the listing off the
  board and come back, because the retainer slot is worth more on something that is selling.

  Those rows are left out of "reprice all", and counted separately from it: one person
  clearing a slot should not take a whole retainer down with it. Each still has its own
  button. The argument only gets made where there is evidence for it, so a board that cannot
  say how fast it sells gets the price and no advice.

  Room to raise is measured against the next listing rather than against past sales. A
  nugget at 895 with the next at 900 reads as a third under the going rate if you go by
  history, and raising it earns four gil. Where the room is real, big enough to be worth
  the bother and with recent sales saying somebody pays up there, the row gets a raise
  of its own: the next listing less the margin, filled in through the same price dialog
  as any undercut. Overcutting yourself is the undercutting mistake in the other
  direction, and it gets the same treatment.

  Sales that happened while I was offline are worked out rather than missed. The game
  announces a sale in chat only while you are logged in to hear it, so the rest come from the
  retainer itself: its market listings are an ordinary container of twenty with the prices
  beside them, and comparing that against how it was left says what went. What that cannot
  say is why it went, since a listing that sold and one taken off the market look identical,
  and the purse is what separates them. Those are marked as worked out rather than announced,
  because one is what the game said and the other is what the numbers imply.

  One column there is not about the market at all. Every other number is a fact about other
  people; "you sold" is a fact about my own prices, and it is the answer to the question the
  market data cannot reach: not what this sells for, but whether mine sells. The game
  announces every retainer sale in chat with the item attached as a link, so the item is
  exact and only the numbers are read from the text. The wording is English, which is a real
  limit: a client in another language records nothing rather than something wrong. The
  record is kept for half a year (a setting), in a file of its own, because what sells well
  for you is a question about months rather than a fortnight.

  Listings come from the game rather than Universalis, so they are exact: opening a retainer
  reads what it has out, and it is remembered after that. Each is netted at its own
  retainer's city rate rather than the worst of them, since that is a number the game has
  actually said.
- **Bags.** What to do with the pile. Materials accumulate faster than anybody decides about
  them, and the decision is dull enough that the pile wins. It is not a hard question, only a
  repetitive one: for each stack, does the board pay more than a vendor, will the board take
  it in any reasonable time, and is it wanted for something anyway.

  That last one is why the craft table gets a say. Telling somebody to vendor the materials
  for the thing it just recommended would be the worst advice in here.

  Retainers are covered too. A retainer's pages are only readable while it is open, so what
  each one holds is remembered from the last look and the tab says how old that answer is: an
  hour-old reading of a retainer is the best there is until it is opened again, and far better
  than pretending it is empty.

  On top of the ranking, what to put in the market slots. There are twenty a retainer and
  always more worth selling than slots to sell it from, and the obvious choice is wrong: ranked
  on what a stack is worth, the big slow piles take every slot and sit in them for months. A
  slot is a rate rather than a lump, so it is judged by what sells while it is occupied.
  Measured, four hundred chocobo greens are the second largest pile I own at a hundred and
  eighteen thousand gil and come seventh, because a week of that slot realises seven thousand
  of it. That one change is also the portfolio: nothing has to impose a quota of fast movers
  against slow ones, because pricing the slot by what it turns over prefers the mix on its own.

  A stack with no price yet is not a stack worth nothing. Priced as nothing, everything the
  sweep had not reached read as a confident "vendor it", which is this plugin's own founding
  mistake wearing a different hat: a missing number is not a small number. Anything the board
  trades but has no price for is asked about instead, and only genuinely untradeable things
  are called for the vendor by default.

- **Craft.** The swept ranking and the list you are building from it. Its own tab
  because it runs on its own clock, hours rather than minutes, and because it is the only
  thing here that is a workspace rather than a report. The tab counts what is in the list,
  since the crafting itself happens in somebody else's window and a half-built list is easy
  to forget. Any column sorts, and the sort happens before the table is trimmed to
  twenty-five rows, so "by profit" means the best of all of them rather than the best of the
  ones that happened to survive the default ranking.

  Every craftable thing rather than furnishings only. Furnishings are nine hundred of nine and
  a half thousand and, measured, almost all of them are the same kind of market: thin books
  that sell about as fast as they are listed. Ranking inside that hid every other kind, and
  widening it put weaving, armoury and aspected materials above most of the furnishings.

  Some of the shortlist is set aside for quiet markets. Turnover is what a thing costs times
  how fast it sells, so ranking on it leans towards things that move, and a market that turns
  over little because almost nobody wants it is also one almost nobody is supplying. Those
  slots have a lower bound as well as an upper one: ranked on price alone they fill with
  parked items, since something listed at a billion gil that nobody has ever bought is the
  dearest thing on the board and the least worth costing.

  Each row also says what sort of market it is, which is a different question from what it
  pays. Two rows paying the same are not the same proposition when one sells as fast as it is
  stocked and the other has months of somebody else's stock in front of it. Days of supply,
  what is listed over what sells, carries most of that; a wide spread in recent prices says
  the margin has an error bar rather than a promise.
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

## Read without being opened

Two of the alerts are driven by prices moving rather than by a timer. The cache says which
book it just replaced and only that item is looked at, so the work is proportional to what
actually moved. It matters for exactly two things: somebody undercutting a listing of mine
into a queue longer than I said I wanted to wait, and something appearing on the board for
less than a vendor pays. The second is gone within minutes and is worthless an hour later.

Being undercut is not the trigger; being undercut *into a queue* is. Three units ahead on a
board selling ten a day are gone this afternoon and are not worth being told about.

Every gil a day figure here is a ceiling: it assumes taking every sale at today's price. How
far short of that I actually land is now measured rather than left as a caveat. What each
board turned over is known and what I sold is recorded, so the share is a measurement and not
a fudge factor, weighted by what each market moved rather than averaged across items.

It quotes its own coverage, because some items never get a sale rate at all: Universalis
reports one per world, per data centre and per region, and has none of the first two for some
things and nothing whatever for others. Measured off a third of the sales it read eleven
percent where the fuller set said four, since whatever is left when rates are missing skews
towards the small quiet markets a person takes most of.

Sale rates are units a day and only ever units a day. Universalis reports two: the listings
endpoint counts sales, where a sale is a listing bought however many units were in it, and the
summary endpoint counts units. Everything here weighs a rate against units listed or units to
sell, so the first is a tenfold error in a number that looks perfectly reasonable, and it is
the one a book arrives carrying. A book therefore arrives with no rate at all and the summary
supplies it.

Not knowing a rate is a different answer from knowing it is nought, and they get different
words. Read as "never sells" the gap would put the worst verdict in the table on every listing
the moment it was first looked at.

## Nothing is written into the game

Rowena never speaks into the game unprompted: no login line, no alerts, no toasts, no error
replies. It reads the client's own packets and its chat log, and everything it has to say it
says inside its own window. A line in chat is a line in every screenshot, and none of this is
worth a plugin announcing itself in the world for.

The single exception is "Link in chat" on the right-click menu, which puts an item link in
your own log so you can read the game's real tooltip. Nothing reaches the log unless you
picked it off a menu, and linking an item is a thing people do by hand all day.

Logging in earns one line on the Overview, once the prices have been refetched: what is near its
cap, what the flips pay and which one pays most, how old the furnishing sweep is. The
server info bar carries the single most urgent of those all the time, and opens the tab
it came from when clicked. `/rowena brief` says the line again on demand.

Two alerts, each said once when it becomes true and not again until it has stopped being
true: a currency entering the last tenth of its cap, a flip whose return crosses a
threshold you set. A sweep past its re-sweep age is a standing note on the Overview instead,
since a fact that stays true belongs on the page rather than in the log. All of it is read off the price cache on
a slow clock and fetches nothing on its own. Undercuts are deliberately not here; Marketbuddy
and Dagobert watch those.

## Getting to the counter

A shop's name is the one thing about it nobody can act on, so "where" is who to hand the
trade to and on which map, read from the same sheets: the NPC an exchange is attached to,
through the category picker the scrip and tomestone exchanges sit behind, placed by the
Level sheet. An exchange offered in several cities lists them all, and the one in the zone
you are standing in comes first.

Right-click goes there. Inside the spot's zone that is a walk handed to vnavmesh, the way
a craft is handed to Artisan: one click, that plugin's pathing. From anywhere else it is a
Lifestream teleport to the nearest aetheryte in that zone, nearest measured on the map
rather than guessed, with the walk armed to fire once the zone has loaded and the mesh is
ready. Flagging the spot on the game's own map is always offered, since it always works.

## Not here yet

- **Artisan.** For a chain that turns out to want crafting rather than gathering.
- **Undercut watching.** Covered well enough by Marketbuddy and Dagobert; no reason to
  rebuild it.

## The vendor is the floor

Every tradable item has a vendor price, and the vendor is the one buyer who never
undercuts, never takes a cut and never runs out of appetite. So every output is worth
whichever pays more, the board net of tax or the vendor, and one the vendor wins is sold
the moment it is made: "to clear" says `vendor` instead of a number of days. A board that
nets less than a vendor is a worse trade with extra steps, and the tables say so rather
than quoting it.

The same floor runs the other way, and that one is worth going looking for. A listing
priced under what a vendor pays, the buyer's 5% included, is gil lying on the board: buy
it, walk to any vendor, sell it, with no market risk at all because the vendor is not a
market. It happens when somebody dumps a stack to clear a retainer slot, and nothing in
the game or in any other tool points at it.

So the Vendor tab scans for it, in the same two passes as the furnishing sweep and for the
same reason: sixteen thousand items cannot be fetched in full. Survey them all cheaply, a
hundred a request, then cost only what survives. The filter is sounder than the furnishing
one, because no listing in a book is cheaper than its floor, so a floor that already loses
money after tax cannot hide a bargain underneath it. Nothing is discarded that could have
paid.

Two things it will tell you plainly. Most scans find nothing, which is the honest answer
and not a failure. And the expensive half is capped: the widest margins get costed first,
and the number left uncosted is on screen rather than quietly dropped.

## The board pushes, and the cache listens

Universalis has a websocket, and it is cheaper for them than being asked repeatedly. Rowena
subscribes to every world of the board it prices against and treats what arrives as a
signal rather than as prices.

That distinction is measured rather than assumed. Against the live feed, a `listings/add`
for one item carried a single listing where the world held thirty-seven: it sends what
changed, not what is. A book rebuilt from those would have to replay every event without
ever missing one, across every disconnect, and a book that quietly drifted is the one
failure this plugin cannot afford, since depth is the whole point.

So the push decides *when* and a fetch decides *what*. An item the feed names is refetched
through the ordinary queue, at background priority, with a cooldown so a busy item does not
cause a fetch per twitch. Only items a book is already held for are worth it: an item
nobody has fetched is an item nobody is looking at, and the feed carries thousands of those
an hour. One world measured at about thirty-five messages a minute, so a data centre is a
handful a second before filtering.

BSON both ways, in a page of hand-written code rather than a dependency, pinned by tests
against frames recorded off the live feed.

## The game is a better source than the internet

Universalis is whatever somebody else's client last uploaded. This client's own packets are
what was actually there, at the moment it was there, and they cost nothing. Two things are
taken from them.

The seller's cut, which is nought to five percent depending on the city a retainer stands
in and moves daily. Everything here used to assume the worst; now the game says, Rowena
prices with the worst of the cities you actually sell from, and the settings tab shows the
whole table so the cheapest one is visible. Moving a retainer is a one-off errand that pays
on every sale it ever makes.

My own listings, because a listing carrying one of my retainer ids is mine wherever the
board view came from. An item's tooltip says what I have out and at what price, and says so
in red when the board has gone under me.

What is deliberately not taken from the game is the board as an order book. A listing
carries no world, so a view cannot be attributed to a world or a data centre, and filing
cross-world listings under the wrong board would quietly corrupt the one thing this plugin
is careful about. Universalis stays the source for depth.

## Checking the numbers against the board

Every row is a fetch, a walk up an order book, two taxes and a rate out of the game's
sheets, and a wrong answer looks exactly like a right one. So the tables can be checked
against the market by something that is not this code:

```bash
echo dump > "$ROWENA_CONFIG/commands.txt"   # diagnostics must be on
./scripts/verify.py
```

The plugin writes what it is showing; the script fetches the same items from Universalis
and recomputes each row in another language, then complains if the two disagree. Measured
against a live board, they do not: outlays and net proceeds match to the gil.

Three links in that chain cannot be checked from outside, and were checked in the game
instead. The buyer's cut: a purchase dialog offering one item for 156,567 gil with a fee of
7,455 is a sticker of 149,112 and five percent of it floored, which is what the book walk
charges. The seller's cut: a retainer vocate listing three cities as normal and five as
reduced matches the rates the plugin captured, city for city. And a generated trade at its
counter: the purple scrip exchange charges ten scrips for a Mozzarella, which is what the
sheets said and what the table shows.

Two things the script taught rather than confirmed. Buying three runs of a trade is not the same as
buying three times the quantity, because the board floors its cut per listing per purchase
and three runs are three purchases, so the script has to buy the way the plugin says you
would. And a row whose input is wanted by another row cannot be checked on its own at all:
the allocator gives the cheap listings to whichever pays most and prices the rest against
what is left, so checking it in isolation disagrees by exactly the amount the allocator is
right by. Those rows are skipped and say so.

## Your hands, not the market's appetite

Two tables rank on what a day would pay, and both have to answer the same objection: the
market's appetite is not yours. The board turns over seventy-five thousand water crystals a
day, so ranked on turnover alone a forty-six gil crystal beats four thousand gil flax and
the list is useless. Both tables take a cap on what you would really do in a day, and rank
on whichever runs out first, the board's appetite or your hands.

It is a blunt instrument and says so. What it is standing in for is a measurement nobody
has made yet: how much an hour of gathering actually yields, which is the number that would
make these tables answer the real question rather than approximate it.

## A floor nobody is paying is not a price

The premise here is that the cheapest listing is a bad summary of a market. The worst case
of that is not depth, it is fiction: a Hanya Mask listed at 999,999,999 gil, alone in its
book, against recent sales between 120,000 and 450,000. Taken at face value it made the
craft ranking claim eight hundred and sixty-eight million gil a day and put it top of the
table, above everything real.

So a floor has to be one somebody could be paying, and what people actually paid is the
only evidence of that. A floor far above every recent sale is refused rather than quoted,
and the row is treated as unpriceable, which it is.

It only ever refuses. A floor below recent sales is a bargain, which is the thing this
plugin is looking for, and a book with no sales to judge against is left alone rather than
thrown away: quiet markets are still markets.

## What the numbers do not know

A fetch asks for at most so many listings, and Universalis counts `listingsCount` and
`unitsForSale` from what it returned rather than from the board, so neither can say whether
there was more. Landing exactly on the limit is the only signal there is, and a book that
does is marked as possibly cut off. The recorded Mount Token book is one: forty listings
asked for, forty returned.

What gets cut off is the dear end, so everything in hand is priced correctly and only
running past the end is unknown. That distinction is carried rather than flattened: an
order the board genuinely cannot fill is short, and one that ran past the edge of what was
fetched says so instead, because reporting it as short would claim the market cannot supply
something it may well have. Both stop a trade being quoted; only one of them is a fact
about the market, and only one is fixed by asking for more listings.

Every book also records where it came from, since what this client saw at the board itself
is exact and what Universalis returns is whatever somebody else uploaded, possibly hours
ago.

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
