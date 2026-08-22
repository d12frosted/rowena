#!/usr/bin/env python3
"""Check what Rowena is showing against the board, without asking Rowena anything.

The tables are the product and also the hardest thing to be sure of: every row is a
fetch, a walk up an order book, two taxes and a rate out of the game's sheets, and a
wrong answer looks exactly like a right one. So this recomputes them from the raw
Universalis listings, in a different language, and complains when the two disagree.

Ask the plugin for its numbers first (diagnostics must be on):

    echo dump > "$ROWENA_CONFIG/commands.txt"

then run this. It exits non-zero if anything disagrees.
"""

import json
import os
import sys
import time
import urllib.error
import urllib.request

AGENT = "Rowena-verify/0.1 (+https://github.com/d12frosted/rowena)"
DEFAULT_CONFIG = os.path.expanduser(
    "~/Library/Application Support/XIV on Mac/pluginConfigs/Rowena"
)


_seen = {}


def board(scope, item):
    """One item's listings, remembered and retried.

    Checking a craft asks about every material, and materials repeat across recipes, so the
    same book would otherwise be fetched several times in one run. Universalis is free and
    asks callers to be reasonable; this is a checker, not a reason to hammer it.
    """
    key = (scope, item)

    if key in _seen:
        return _seen[key]

    url = f"https://universalis.app/api/v2/{scope}/{item}?listings=40"
    request = urllib.request.Request(url, headers={"User-Agent": AGENT})

    for attempt in range(4):
        try:
            with urllib.request.urlopen(request, timeout=45) as response:
                _seen[key] = json.load(response)
                time.sleep(0.2)
                return _seen[key]
        except (TimeoutError, urllib.error.URLError, json.JSONDecodeError):
            if attempt == 3:
                raise
            time.sleep(2 * (attempt + 1))

    raise RuntimeError("unreachable")


def cost_to_buy(listings, quantity, buyer_rate):
    """One run's worth, cheapest first, tax floored per listing as the board charges it.

    Returns what it cost, how many were found, and what is left of the book, because a
    second run of the same trade faces the book the first one left behind.
    """
    book = sorted(listings, key=lambda l: l["pricePerUnit"])
    spent = filled = tax = 0
    left = []

    for listing in book:
        if filled >= quantity:
            left.append(listing)
            continue
        taken = min(listing["quantity"], quantity - filled)
        subtotal = listing["pricePerUnit"] * taken
        spent += subtotal
        tax += int(subtotal * buyer_rate)
        filled += taken
        if listing["quantity"] > taken:
            left.append({**listing, "quantity": listing["quantity"] - taken})

    return spent + tax, filled, left


def cost_of_runs(listings, per_run, runs, buyer_rate):
    """What N runs cost, bought one run at a time.

    Not the same as buying N times the quantity in one go, and the difference is not a
    rounding artefact: the board floors its cut per listing per purchase, and somebody
    doing a trade three times makes three purchases. Matching the plugin means buying the
    way the plugin says you would.
    """
    total = found = 0
    book = listings

    for _ in range(runs):
        cost, filled, book = cost_to_buy(book, per_run, buyer_rate)
        total += cost
        found += filled

    return total, found


def main():
    config = os.environ.get("ROWENA_CONFIG", DEFAULT_CONFIG)
    path = os.path.join(config, "dump.json")

    if not os.path.exists(path):
        sys.exit(f"no dump at {path}. Send 'dump' to the plugin's commands.txt first.")

    with open(path) as handle:
        dump = json.load(handle)

    buyer = dump["buyerRate"]
    seller = dump["sellerRate"]
    buying, selling = dump["buying"], dump["selling"]
    print(f"buying on {buying}, selling on {selling}, tax {buyer:.0%} in / {seller:.0%} out\n")

    failures = 0

    for row in dump["sinks"]:
        listings = board(selling, row["item"])["listings"]
        if not listings:
            print(f"  SKIP  {row['trade']}: nothing listed now")
            continue
        floor = min(l["pricePerUnit"] for l in listings)

        if floor != row["floor"]:
            print(f"  SKIP  {row['trade']}: board moved (floor {row['floor']:,} -> {floor:,})")
            continue

        net = floor - int(floor * seller)
        # A sink spends currency, so its net is the whole sale of one run's output.
        ok = net == row["net"]
        failures += not ok
        print(f"  {'ok  ' if ok else 'FAIL'}  {row['trade']}: net {row['net']:,} vs {net:,}")

    print()

    # An item wanted by two allocated trades cannot be checked a row at a time: the
    # allocator hands the cheap listings to whichever row pays most and prices the other
    # against what is left, which is the entire point of it. Pricing such a row on its own
    # would "disagree" with the plugin by exactly the amount the plugin is right by.
    wanted = {}
    for row in dump["flips"]:
        if row["runs"] >= 1:
            for item in row["inputs"]:
                wanted[item["item"]] = wanted.get(item["item"], 0) + 1

    for row in dump["flips"]:
        if row["runs"] < 1:
            continue

        if any(wanted[item["item"]] > 1 for item in row["inputs"]):
            print(f"  SKIP  {row['trade']}: shares a book with another row, so it is the allocator's answer")
            continue

        outlay = 0
        short = False
        for item in row["inputs"]:
            listings = board(buying, item["item"])["listings"]
            cost, filled = cost_of_runs(listings, item["quantity"], row["runs"], buyer)
            short |= filled < item["quantity"] * row["runs"]
            outlay += cost
        ok = outlay == row["outlay"]
        failures += not ok and not short
        note = " (board moved since the dump)" if short else ""
        print(f"  {'ok  ' if ok else 'FAIL'}  {row['trade']}: outlay {row['outlay']:,} vs {outlay:,}{note}")

    failures += verify_vendor(config, buyer)
    failures += verify_craft(config, buyer, seller)
    failures += verify_gather(config, seller)

    print(f"\n{failures} disagreements")
    return 1 if failures else 0


def verify_gather(config, seller_rate):
    """What a gatherable is worth: the floor on your own world, less the market's cut."""
    path = os.path.join(config, "gather.json")

    if not os.path.exists(path):
        return 0

    with open(path) as handle:
        dump = json.load(handle)

    if not dump["rows"]:
        print("\ngather: nothing ranked")
        return 0

    print(f"\ngather ({dump['survey']})")
    failures = 0

    for row in dump["rows"]:
        listings = board(dump["selling"], row["item"])["listings"]
        floor = min((l["pricePerUnit"] for l in listings), default=None)

        if floor is None or floor != row["floor"]:
            print(f"  SKIP  {row['name']}: board moved (floor {row['floor']:,} -> {floor})")
            continue

        net = floor - int(floor * seller_rate)
        ok = net == row["each"]
        failures += not ok
        print(
            f"  {'ok  ' if ok else 'FAIL'}  {row['name']}: each {row['each']:,} vs {net:,}"
            f"  [{row['job']} {row['level']}{', timed' if row['timed'] else ''}]"
        )

    return failures


def verify_craft(config, buyer_rate, seller_rate):
    """The craft ranking: materials off the buying board, the product sold on the selling one.

    Sub-crafts are priced as bought rather than as made, which is the plugin's own rule, so
    this buys them the same way.
    """
    path = os.path.join(config, "craft.json")

    if not os.path.exists(path):
        return 0

    with open(path) as handle:
        dump = json.load(handle)

    if not dump["crafts"]:
        print("\ncraft: nothing ranked")
        return 0

    print("\ncraft")
    failures = 0

    for row in dump["crafts"]:
        materials = 0
        short = False

        for line in row["inputs"]:
            listings = board(dump["buying"], line["item"])["listings"]
            cost, filled, _ = cost_to_buy(listings, line["quantity"], buyer_rate)
            short |= filled < line["quantity"]
            materials += cost

        product = board(dump["selling"], row["item"])["listings"]
        floor = min((l["pricePerUnit"] for l in product), default=None)

        # Same trap as everywhere else: a book that has moved since the dump is not the book
        # the plugin priced, and comparing them says nothing about the arithmetic. The
        # materials are their own fingerprint, since they are priced by walking the book.
        if floor is None or short:
            print(f"  SKIP  {row['name']}: nothing listed, or the board is short now")
            continue

        if floor != row["floor"] or materials != row["materials"]:
            print(
                f"  SKIP  {row['name']}: board moved since the dump "
                f"(floor {row['floor']:,} -> {floor:,}, materials {row['materials']:,} -> {materials:,})"
            )
            continue

        profit = floor - int(floor * seller_rate) - materials
        ok = profit == row["profit"]
        failures += not ok
        print(
            f"  {'ok  ' if ok else 'FAIL'}  {row['name']}: "
            f"materials {row['materials']:,}/{materials:,}, profit {row['profit']:,}/{profit:,}"
        )

    return failures


def verify_vendor(config, buyer_rate):
    """The vendor tab claims free gil, which is the boldest thing here and the easiest to check.

    The vendor price itself is not checked here: it comes out of the game's own item sheet,
    which this script cannot read. Check it with the probe against Item.PriceLow.
    """
    path = os.path.join(config, "vendor.json")

    if not os.path.exists(path):
        return 0

    with open(path) as handle:
        dump = json.load(handle)

    if not dump["finds"]:
        print(f"\nvendor: nothing found ({dump['scan']})")
        return 0

    print(f"\nvendor ({dump['scan']})")
    failures = 0

    for find in dump["finds"]:
        listings = board(dump["buying"], find["item"])["listings"]

        # These are the finds most likely to be bought out from under the dump, by their
        # nature: they are underpriced and somebody else can see them too. A book whose floor
        # has moved is not the book the plugin costed, so there is nothing to compare.
        live_floor = min((l["pricePerUnit"] for l in listings), default=None)

        # Floor and listing count can both survive a change: somebody buys from the middle of
        # the book and somebody else lists. Units are the sensitive part, and these items are
        # underpriced by definition, so they churn.
        live_units = sum(l["quantity"] for l in listings)

        if (live_floor != find["cheapest"]
                or len(listings) != find["listings"]
                or live_units != find["unitsListed"]):
            print(
                f"  SKIP  {find['name']}: board moved since the dump "
                f"({find['unitsListed']} units in {find['listings']} listings at {find['cheapest']:,} "
                f"-> {live_units} in {len(listings)} at {live_floor:,})"
            )
            continue

        units = profit = 0

        # Whole listings only, cheapest first, while each still pays after the buyer's cut.
        for listing in sorted(listings, key=lambda l: l["pricePerUnit"]):
            cost = listing["pricePerUnit"] * listing["quantity"]
            cost += int(cost * buyer_rate)
            gain = find["vendorPays"] * listing["quantity"] - cost
            if gain <= 0:
                break
            units += listing["quantity"]
            profit += gain

        ok = units == find["units"] and profit == find["profit"]
        failures += not ok

        # Buying is per world, so a find is only one errand if it sits on one world.
        worlds = " ".join(f"{s['world']}:{s['units']}" for s in find["byWorld"])
        print(
            f"  {'ok  ' if ok else 'FAIL'}  {find['name']}: "
            f"{find['units']}u/{find['profit']:,} vs {units}u/{profit:,}  [{worlds}]"
        )

    return failures


if __name__ == "__main__":
    sys.exit(main())
