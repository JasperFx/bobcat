namespace Bobcat.Mtp.GeneratedHost;

public class OrderingFixture : Fixture
{
    private int _items;

    [Given("an empty cart")]
    public void EmptyCart() => _items = 0;

    [When("{int} items are added")]
    public void AddItems(int count) => _items += count;

    [When("the cart is cleared")]
    public void ClearCart() => _items = 0;

    [Check("the cart holds {int} items")]
    public bool CartHolds(int expected) => _items == expected;
}

public class ShippingFixture : Fixture
{
    private int _weight;

    [Given("a parcel weighing {int} kg")]
    public void Parcel(int weight) => _weight = weight;

    [Check("the label reads {string}")]
    public bool LabelReads(string expected) => expected == $"{_weight} kg";
}
