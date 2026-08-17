namespace Rowena.Core.Conversions;

/// <summary>
/// Whether a resource can be bought and sold, or only earned.
/// </summary>
/// <remarks>
/// The distinction drives all the arithmetic. An item has a market price, so spending it
/// has a gil cost and producing it has a gil value. A currency has neither: scrips cannot
/// be bought, so the only way to price them is backwards, by what the best sink converts
/// them into.
/// </remarks>
public enum ResourceKind
{
    /// <summary>Tradable. Has an order book.</summary>
    Item,

    /// <summary>Bound. Earned in game, never bought. Scrips, tomestones, seals.</summary>
    Currency,
}

/// <summary>Something a conversion consumes or produces.</summary>
/// <remarks>
/// Equality deliberately ignores <see cref="Name"/>. The name is there so a quote reads
/// like English; identity is the kind and the id. Without this, the same item spelled two
/// ways in two catalogue entries would silently fail to link when chaining conversions.
/// </remarks>
public readonly record struct Resource(ResourceKind Kind, uint Id, string Name)
{
    public static Resource Item(uint id, string name) => new(ResourceKind.Item, id, name);

    public static Resource Currency(uint id, string name) => new(ResourceKind.Currency, id, name);

    public bool Equals(Resource other) => Kind == other.Kind && Id == other.Id;

    public override int GetHashCode() => HashCode.Combine(Kind, Id);

    public override string ToString() => Name;
}

/// <summary>A quantity of a resource.</summary>
public readonly record struct ResourceAmount(Resource Resource, int Quantity)
{
    public ResourceAmount Scaled(int factor) => this with { Quantity = Quantity * factor };

    public override string ToString() => $"{Quantity}x {Resource.Name}";
}
