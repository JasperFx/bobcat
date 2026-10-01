using Bobcat;

namespace Bobcat.Acceptance.Tests;

public enum Tier
{
    Free,
    Pro,
    Enterprise
}

/// <summary>Storyteller's <c>CreateNewObject&lt;T&gt;</c> shape: a record a table row builds.</summary>
public record Subscription(string Customer, Tier Tier, DateOnly RenewsOn, string Currency = "USD");

/// <summary>
/// A table the step runs itself — <c>RunTable</c> through a private row method, and
/// <c>BuildRows</c> straight into objects — rather than one the generator binds.
/// </summary>
public class RunTableFixture : Fixture
{
    internal readonly List<string> Log = new();
    internal string Flushed = "";
    internal Subscription[] Built = [];

    public void BeforeEach()
    {
        Log.Clear();
        Flushed = "";
        Built = [];
    }

    /// <summary>The envelope is the method body: clear above the rows, flush once below them.</summary>
    [Given("the members are")]
    public async Task TheMembersAre(StepTable table)
    {
        Log.Clear();
        await RunTable(nameof(addMember), table);
        Flushed = string.Join("|", Log);
    }

    private void addMember([Header("Member Name")] string member, Tier tier = Tier.Free)
        => Log.Add($"{member}:{tier}");

    /// <summary>An async row method is awaited before the next row runs.</summary>
    [Given("the members are added slowly")]
    public Task TheMembersAreAddedSlowly(StepTable table) => RunTable(nameof(addMemberAsync), table);

    private async Task addMemberAsync(string member)
    {
        await Task.Yield();
        Log.Add(member);
    }

    /// <summary>A decision table: the one column no parameter claims is the expected output.</summary>
    [Then("doubling gives")]
    public Task DoublingGives(StepTable table) => RunTable(nameof(doubled), table);

    private int doubled(int input) => input * 2;

    [Given("the subscriptions are")]
    public void TheSubscriptionsAre(StepTable table) => Built = BuildRows<Subscription>(table);

    [Check("every member was logged")]
    public bool EveryMemberWasLogged() => Log.Count > 0;
}
