using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// bobcat#573: the traits a projected test declares through its own framework's attributes reach its
/// manifest entry, read by shape because core names no framework; and the coordinates the generator
/// binds are what a runner adapter reads back by test method.
/// </summary>
public class SpecTraitsTests
{
    [Trait("Category", "Slow")]
    public class TraitedSpecs
    {
        [Trait("Owner", "payments")]
        [Category("Nightly")]
        [Property("Priority", "High")]
        public void a_traited_spec()
        {
        }
    }

    // Stand-ins shaped like NUnit's [Category] and TUnit's / NUnit's [Property]
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public sealed class CategoryAttribute(string name) : Attribute
    {
        public string Name { get; } = name;
    }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public sealed class PropertyAttribute(string name, string value) : Attribute
    {
        public string Name { get; } = name;
        public string Value { get; } = value;
    }

    [Fact]
    public void declared_traits_are_read_from_the_method_and_its_class_by_shape()
    {
        var entry = SpecTraits.WithDeclaredTraits(
            new SpecManifestEntry("Traited/a traited spec", typeof(TraitedSpecs).FullName, nameof(TraitedSpecs.a_traited_spec)),
            typeof(TraitedSpecs).Assembly);

        entry.Traits.ShouldNotBeNull();
        entry.Traits["Category"].ShouldBe(["Slow", "Nightly"], ignoreOrder: true);
        entry.Traits["Owner"].ShouldBe(["payments"]);
        entry.Traits["Priority"].ShouldBe(["High"]);
    }

    [Fact]
    public void a_test_that_cannot_be_found_keeps_its_entry_unchanged()
    {
        var entry = new SpecManifestEntry("Gone/a test", "No.Such.Class", "nothing");
        SpecTraits.WithDeclaredTraits(entry, typeof(TraitedSpecs).Assembly).ShouldBeSameAs(entry);
    }

    [Fact]
    public void bound_coordinates_are_read_back_by_test_method()
    {
        var uid = "Coordinates/" + Guid.NewGuid().ToString("n");
        try
        {
            DeclaredSteps.Bind(uid, typeof(TraitedSpecs).FullName!, nameof(TraitedSpecs.a_traited_spec), null,
                "Deposit funds", "Money in", null, "Command");

            DeclaredSteps.CoordinatesFor(typeof(TraitedSpecs), nameof(TraitedSpecs.a_traited_spec))
                .ShouldBe(new SpecCoordinates("Deposit funds", "Money in", null, "Command"));

            var bound = DeclaredSteps.BindingFor(uid)!;
            bound.Slice.ShouldBe("Deposit funds");
            bound.Pattern.ShouldBe("Command");
        }
        finally
        {
            DeclaredSteps.Remove(uid);
        }

        DeclaredSteps.CoordinatesFor(typeof(TraitedSpecs), nameof(TraitedSpecs.a_traited_spec)).ShouldBeNull();
    }
}
