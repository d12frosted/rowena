# The ledger style

Rowena's signature, written down. It is Tataru's ledger style, worn by a sibling:
`Rowena/UI/Style.cs` enforces this guide; this file explains it. When the two
disagree, fix one of them in the same change.

## The creed

A ledger is read in one pass or it is not read at all.

- Names on the left, states and numbers flush right where they form a column.
- One accent, for what still wants doing, used for nothing else.
- Everything finished is quieter than everything open.
- Each fact is said once, in the place that can act on it.
- Detail that is only sometimes wanted lives in a tooltip, not on the line.
- A bar stands in for any pair of numbers somebody would otherwise have to divide.
- The one loud control is the one that moves the machinery.

## The palette

Every colour in the window is one of these tokens. No literals at call sites; the
only exception is data-derived colour (item icons, job icons).

| Token    | Meaning                                                       |
| -------- | ------------------------------------------------------------- |
| `Accent` | What still wants doing. Used for nothing else.                 |
| `Brand`  | The accent worn thin. Masthead only: the feather and its rule. |
| `Plain`  | A row's own words.                                             |
| `Muted`  | Everything that supports the accent: leads, states at rest.    |
| `Good`   | Finished, met, ready, gil coming in.                           |
| `Warn`   | Needs a person's attention before the plan works.              |
| `Hot`    | Rowena's own: the step between warn and bad on the one scale.  |
| `Bad`    | Broken, or dead. Rare by design.                               |
| `Paper`  | The window's ground: warm near-black.                          |
| `Veil`   | The faintest wash of light: empty cells, idle chrome.          |
| `Rule`   | An edge or a divider: barely more present than the veil.       |

`Accent` discipline is the heart of the guide: if accent appears anywhere that is
not an open, actionable want (a listing to reprice, a call worth a decision), the
signature is already eroding. `Brand` exists precisely so the masthead does not
spend the accent.

`Hot` is Rowena's one addition, and it exists for the one measurement this ledger
reads as a scale rather than a verdict: how long a sale takes. Within a day is
`Good`, within three is `Warn`, within a week is `Hot`, and beyond, or never, is
`Bad`. The thresholds live in `Cell.Absorb`.

## The shell

The window wears its own chrome - paper, silver trim on the title bar and active
tab, veiled frames and scrollbars - pushed by `Style.Shell()` around the whole
frame (`PreDraw`/`PostDraw`, not inside `Draw`), so the window looks the same on
every install regardless of the user's Dalamud theme. The palette assumes dark
paper; owning the background is what makes that assumption safe.

The trim is the one place a sibling wears its own metal: Tataru is trimmed in
bronze, Rowena in tarnished silver, both in the same restrained register.

Every window of ours opens with `Style.Masthead(name, context)`: the feather in
brand, the plugin's name in plain, the context against the right edge, and a thin
brand rule beneath. The main window carries the logged-in character; the retainer
overlay carries the retainer it is reading. The feather is the family mark and it
stays; the name is the individual.

## Composition

- A row is a sentence: muted lead, plain value, quiet actions riding along,
  verdict trailing right (`Style.Trailing`). In a table, `Cell.Right` is the same
  idea per column.
- Say each fact once. A summary yields to detail that is on screen.
- Empty states go through `Style.Nothing`: one quiet sentence with air around it,
  naming the way forward when there is one. `nothing is listed under its vendor
  price right now, which is the usual answer` is the register.

## Words

- States, verdicts and everyday actions are lowercase fragments: `done`, `sit
  tight`, `2 to reprice`, `vendor pays more`, `scan again`.
- Proper names keep their capitals (`Artisan`, `Teamcraft`, `GatherBuddy`);
  in-game names that read ambiguously in a sentence wear quotes.
- Counts read `3 of 12`, joined facts read `a, b, c`.
- Tooltips (`Style.Explain`) are full sentences with capitals and periods; they
  carry the why, the line carries the what.
- Headings (`Style.Heading`) are lowercase at the call site; the style uppercases
  them. A heading names a section of the window, never a form field.

## Controls

Four tiers, quietest first:

1. `Style.TrailingRemove` - the destructive x: nearly invisible, right edge, as
   far from content as the row allows.
2. `Style.Quiet` - everyday row actions (`ignore`, `copy names`, `+`): reads as
   text until hovered.
3. `Style.Row` - a small real button for actions that move machinery but stay on
   the row (`set`, `stop`, `refresh prices`, `copy for Artisan`).
4. `Style.Commit` - the one way a surface says "do it" (`sweep`, `survey`, `scan
   the board`, `reprice all`): full height, accent word. There is exactly one per
   surface.

Checkboxes are for durable preferences (Settings) and person-only facts;
everything the game can answer is watched, not ticked.

## Sizes

Every fixed length goes through `Style.Px`, which scales design pixels by the
user's global scale. Font-derived sizes (`GetFontSize`, `GetTextLineHeight`) are
already scaled and stay as they are. Item icons scale once, inside
`ItemCells.RawIcon`, so call sites keep speaking in design pixels. No bare pixel
literal reaches ImGui.

## Progress

`Style.Progress` is the only bar: accent while underway, quiet green when full,
veil for the track, thin.

## Enforcement

- New drawing goes through `Style` helpers. Raw `ImGui.TextColored` is fine when
  the colour is a state ternary over tokens; raw colour literals are not.
- Raw `ImGui.Button`/`SmallButton` never appears in a tab; one of the four tiers
  does.
- `ImGui.Separator` is acceptable (the shell colours it to `Rule`), but ask
  whether a `Gap` says it more quietly.
- Before shipping a surface, read it against the creed: is anything said twice,
  is the accent spent on something that is not a want, does the eye travel
  further than the reading measure?

## Porting to a sibling plugin

Copy `Style.cs` whole, keep the tokens and their meanings, change only the trim
if the sibling wants its own metal. Reuse the masthead with the sibling's name
and the same feather - the feather is the family mark, the name is the
individual. Bring this file along and edit its examples; the creed does not
change.
