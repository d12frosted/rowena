namespace Rowena.Core.Conversions;

/// <summary>
/// A fixed-rate trade at some counter in the world: hand these in, receive those.
/// </summary>
/// <remarks>
/// This covers the things the crafting-profit tools structurally cannot see. They model
/// recipes, so they can price "materials in, product out", but a scrip exchange is not a
/// recipe and neither is a token counter. Both are fixed-rate conversions, and both turn
/// out to be where the margin is.
///
/// Nothing here knows about crafting. A recipe is expressible as a conversion if it ever
/// needs to be, but the reason this type exists is the trades that are not recipes.
/// </remarks>
/// <param name="Handoff">
/// An opaque hint naming how the inputs are come by, for whatever can act on it.
/// </param>
/// <remarks>
/// The core deliberately does not interpret <paramref name="Handoff"/>. Knowing that scrips
/// are earned by gathering collectables is useful, but knowing which plugin to ask about it
/// is not the core's business, and putting that knowledge here would drag a Dalamud concern
/// into the one project that has no game dependency. It is a string the catalogue sets and
/// an integration recognises, or ignores.
/// </remarks>
public sealed record Conversion(
    string Id,
    string Name,
    IReadOnlyList<ResourceAmount> Inputs,
    IReadOnlyList<ResourceAmount> Outputs,
    string Venue,
    string? Handoff = null)
{
    /// <summary>The same trade taken <paramref name="runs"/> times.</summary>
    public Conversion Scaled(int runs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(runs, 1);

        return this with
        {
            Inputs = [.. Inputs.Select(input => input.Scaled(runs))],
            Outputs = [.. Outputs.Select(output => output.Scaled(runs))],
        };
    }

    /// <summary>How many of <paramref name="resource"/> one run consumes. Zero if none.</summary>
    public int Consumes(Resource resource) =>
        Inputs.Where(input => input.Resource == resource).Sum(input => input.Quantity);

    /// <summary>How many of <paramref name="resource"/> one run produces. Zero if none.</summary>
    public int Produces(Resource resource) =>
        Outputs.Where(output => output.Resource == resource).Sum(output => output.Quantity);

    public override string ToString() =>
        $"{string.Join(" + ", Inputs)} -> {string.Join(" + ", Outputs)} ({Venue})";
}
