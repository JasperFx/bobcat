using Bobcat;

namespace Bobcat.Tests.Partial;

// The C# shapes a partial object has to build (bobcat#417). Each one is a shape real event,
// command and read-model types take, not a contrived corner.

public enum OrderStatus
{
    Placed = 1,
    Shipped = 2
}

public record PositionalOrder(Guid OrderId, string Customer, decimal Total, int Lines, OrderStatus Status,
    DateTimeOffset PlacedAt);

public record MixedRecord(Guid Id, string Name)
{
    public string? Note { get; init; }
    public int Priority { get; init; }
    public string Region { get; init; } = "unassigned";
}

public record struct Point(int X, int Y);

public readonly record struct Money(decimal Amount, string Currency);

public class SettableCustomer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Nickname { get; set; }
    public List<string> Tags { get; set; } = ["seeded"];
    public CustomerAddress? Address { get; set; }
    public DateTime? LastSeen { get; set; }
    public int Visits { get; set; }
}

public class UninitializedCustomer
{
    public string Name { get; set; } = null!;
    public CustomerAddress Address { get; set; } = null!;
    public IReadOnlyList<int> Scores { get; set; } = null!;
}

public class InitCustomer
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "initial";
    public int Visits { get; init; }
}

public class RequiredCustomer
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public int Age { get; set; }
}

public class PrivateSetters
{
    public PrivateSetters(Guid id, string name)
    {
        Id = id;
        Name = name;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public int Count { get; set; }
}

public class ImmutableLine
{
    public ImmutableLine(string sku, int quantity, decimal price = 9.99m)
    {
        Sku = sku;
        Quantity = quantity;
        Price = price;
    }

    public string Sku { get; }
    public int Quantity { get; }
    public decimal Price { get; }
    public decimal Total => Quantity * Price;
}

public class SeveralConstructors
{
    public SeveralConstructors() { }

    public SeveralConstructors(string name) => Name = name;

    public SeveralConstructors(string name, int count)
    {
        Name = name;
        Count = count;
    }

    public string Name { get; } = "none";
    public int Count { get; }
    public string? Label { get; set; }
}

public record CustomerAddress(string Street, string City, string Zip);

public record Customer(Guid Id, string Name, CustomerAddress Address);

public record Basket(Guid Id, string[] Skus, IReadOnlyList<int> Quantities, HashSet<string> Codes,
    Dictionary<string, int> Counts);

public record Nullables(int? Count, string? Note, Guid? ParentId, DateTime? When);

public record Temporal(DateTime At, DateOnly On, TimeOnly Time, DateTimeOffset Stamp, TimeSpan Duration);

public class Titled
{
    [Header("Order #")] public string OrderNumber { get; set; } = "";
    public int Quantity { get; set; }
}

public class WithFields
{
    public string Name = "";
    public int Count;
    public readonly string Fixed = "fixed";
}

public record Node(string Name, Node? Parent);

public record SelfReferencing(string Name, SelfReferencingChild Child);

public record SelfReferencingChild(string Label, SelfReferencing Owner);
