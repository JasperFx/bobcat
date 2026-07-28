using Bobcat;
using Microsoft.EntityFrameworkCore;

namespace Bobcat.EntityFrameworkCore.Tests;

public class EfRecipeFixture : Fixture
{
}

/// <summary>
/// The whole grammar. No <c>Row</c> body at all — <c>[EfCoreEntities&lt;Customer&gt;]</c> supplies
/// the envelope (resolve the context, add per row, one SaveChangesAsync) and the columns bind
/// straight to <c>Customer</c>'s primary constructor.
/// </summary>
[TableGrammar("the following customers exist")]
[EfCoreEntities<Customer>(ContextType = typeof(ShopContext))]
public class CustomerEntities
{
}

/// <summary>
/// The override: a hand-written <c>Row</c> takes control of construction and the recipe still
/// supplies the envelope and the per-row sink.
/// </summary>
[TableGrammar("the following premium customers exist")]
[EfCoreEntities(ContextType = typeof(ShopContext))]
public class PremiumCustomerEntities
{
    public Customer Row(string name, int orders, [FromScopedService] ShopContext context)
    {
        // Added through the HAND-INJECTED context. It only ever reaches the database if the
        // recipe's single SaveChangesAsync runs on that same instance — which is the whole
        // point of resolving the recipe's context from the scenario scope.
        context.Customers.Add(new Customer($"{name}-audit", "Audit", 0));

        return new Customer(name, "Premium", orders * 10);
    }
}
