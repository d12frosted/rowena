namespace Splendors.Core.Conversions;

/// <summary>
/// Joins conversions whose output feeds the next one's input.
/// </summary>
/// <remarks>
/// Worth its own file because the composed rate is routinely better than the rate of
/// either step, and stopping at the first step is the easy mistake. Selling the
/// intermediate is the obvious move and it is the worse one: carrying the same currency
/// all the way to the end of the chain is what pays.
///
/// The two steps are scaled to the lowest common multiple of the linking amount so that
/// nothing is left over. Composing "1,000 scrip gives 1 token" with "100 tokens give 1
/// mount" yields exactly "100,000 scrip gives 1 mount", with no stray tokens to explain.
/// </remarks>
public static class ConversionChain
{
    /// <summary>
    /// Composes <paramref name="first"/> into <paramref name="second"/> through
    /// <paramref name="link"/>, which must be produced by the one and consumed by the other.
    /// </summary>
    public static Conversion Compose(Conversion first, Conversion second, Resource link)
    {
        var produced = first.Produces(link);
        if (produced == 0)
            throw new ArgumentException($"'{first.Id}' does not produce {link.Name}.", nameof(first));

        var needed = second.Consumes(link);
        if (needed == 0)
            throw new ArgumentException($"'{second.Id}' does not consume {link.Name}.", nameof(second));

        var shared = Lcm(produced, needed);
        var firstRuns = shared / produced;
        var secondRuns = shared / needed;

        var inputs = Merge(
        [
            .. first.Inputs.Select(input => input.Scaled(firstRuns)),
            .. second.Inputs.Where(input => input.Resource != link).Select(input => input.Scaled(secondRuns)),
        ]);

        // Anything the first step produced besides the link is still yours, so it stays
        // in the outputs rather than quietly vanishing into the join.
        var outputs = Merge(
        [
            .. second.Outputs.Select(output => output.Scaled(secondRuns)),
            .. first.Outputs.Where(output => output.Resource != link).Select(output => output.Scaled(firstRuns)),
        ]);

        return new Conversion(
            $"{first.Id}+{second.Id}",
            $"{first.Name} then {second.Name}",
            inputs,
            outputs,
            $"{first.Venue} then {second.Venue}");
    }

    private static IReadOnlyList<ResourceAmount> Merge(IEnumerable<ResourceAmount> amounts) =>
    [
        .. amounts
            .GroupBy(amount => amount.Resource)
            .Select(group => new ResourceAmount(group.Key, group.Sum(amount => amount.Quantity))),
    ];

    private static int Lcm(int a, int b) => a / Gcd(a, b) * b;

    private static int Gcd(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);

        return a;
    }
}
