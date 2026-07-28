using Bobcat;
using Bobcat.Runtime;
using global::Marten;

namespace Bobcat.Marten.Tests;

/// <summary>A Marten document. Columns bind to the record's primary constructor.</summary>
public record Customer(string Name, string Region, int Orders)
{
    public Guid Id { get; set; }
}

public class MartenRecipeFixture : Fixture
{
    /// <summary>
    /// Reads back through <c>Context</c>, which resolves the scenario-scoped session — the same
    /// instance the recipe wrote through. The documents are only visible here because the
    /// recipe's single SaveChangesAsync already committed them.
    /// </summary>
    [Then("the stored customers should be")]
    [SetVerification(KeyColumns = "Name")]
    public async Task<IEnumerable<Customer>> StoredCustomers()
    {
        var session = Context!.GetHostService<IDocumentSession>();
        return await session.Query<Customer>().ToListAsync();
    }
}

/// <summary>
/// The whole grammar. No <c>Row</c> body — <c>[MartenEntities&lt;Customer&gt;]</c> supplies the
/// envelope (resolve the session, Store per row, one SaveChangesAsync) and the columns bind
/// straight to <c>Customer</c>'s primary constructor.
/// </summary>
[TableGrammar("the following customers exist")]
[MartenEntities<Customer>]
public class CustomerDocuments
{
}

/// <summary>
/// The override: a hand-written <c>Row</c> takes control of construction while the recipe still
/// supplies the envelope and the per-row sink.
/// </summary>
[TableGrammar("the following premium customers exist")]
[MartenEntities]
public class PremiumCustomerDocuments
{
    public Customer Row(string name, int orders, [FromScopedService] IDocumentSession session)
    {
        // Stored through the HAND-INJECTED session. It only reaches Postgres if the recipe's
        // single SaveChangesAsync runs on that same instance — which is the whole point of
        // resolving the recipe's session from the scenario scope.
        session.Store(new Customer($"{name}-audit", "Audit", 0));

        return new Customer(name, "Premium", orders * 10);
    }
}
