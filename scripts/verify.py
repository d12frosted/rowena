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
import urllib.request

AGENT = "Rowena-verify/0.1 (+https://github.com/d12frosted/rowena)"
DEFAULT_CONFIG = os.path.expanduser(
    "~/Library/Application Support/XIV on Mac/pluginConfigs/Rowena"
)


def board(scope, item):
    url = f"https://universalis.app/api/v2/{scope}/{item}?listings=40"
    request = urllib.request.Request(url, headers={"User-Agent": AGENT})
    return json.load(urllib.request.urlopen(request, timeout=30))


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

    print(f"\n{failures} disagreements")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
