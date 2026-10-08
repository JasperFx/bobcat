using Shouldly;

namespace Bobcat.Generators.Tests;

// `var theAppointmentId = Guid.NewGuid();` in a test names that value theAppointmentId in the spec
public class DeclaredValueTests
{
    private static string? generated(string body)
    {
        var outcome = GeneratorHarness.Run($$"""
            using System;
            namespace Specs
            {
                public sealed class FactAttribute : System.Attribute;
                {{body}}
            }
            """);

        outcome.CompilationErrors.ShouldBeEmpty();
        return outcome.Result.Results.SelectMany(r => r.GeneratedSources)
            .Where(s => s.HintName == "BobcatDeclaredValues.g.cs")
            .Select(s => s.SourceText.ToString())
            .FirstOrDefault();
    }

    [Fact]
    public void a_local_in_a_test_is_declared_under_its_name()
    {
        var source = generated("""
            public class appointments
            {
                [Fact]
                public void a_test()
                {
                    var theAppointmentId = Guid.NewGuid();
                    var later = Guid.CreateVersion7();
                }
            }
            """);

        source.ShouldNotBeNull();
        source.ShouldContain("ScenarioValues.Declare(global::System.Guid.NewGuid(), \"theAppointmentId\")");
        source.ShouldContain("ScenarioValues.Declare(global::System.Guid.CreateVersion7(), \"later\")");
    }

    [Fact]
    public void a_field_or_property_on_a_specs_abstract_base_is_declared_too()
    {
        var source = generated("""
            public abstract class OrderSpec
            {
                protected readonly Guid TheOrder = Guid.NewGuid();
                protected Guid TheCustomer { get; } = Guid.NewGuid();
            }
            """);

        source.ShouldNotBeNull();
        source.ShouldContain("\"TheOrder\"");
        source.ShouldContain("\"TheCustomer\"");
    }

    [Fact]
    public void a_local_inside_a_lambda_in_a_test_class_is_declared()
    {
        var source = generated("""
            public class appointments
            {
                [Fact]
                public void a_test()
                {
                    Func<Guid> make = () => { var inner = Guid.NewGuid(); return inner; };
                }
            }
            """);

        source.ShouldNotBeNull();
        source.ShouldContain("\"inner\"");
    }

    [Fact]
    public void application_code_in_the_project_is_left_alone()
    {
        generated("""
            public class OrderHandler
            {
                public Guid Handle() { var id = Guid.NewGuid(); return id; }
            }
            """).ShouldBeNull();
    }

    [Fact]
    public void a_guid_with_no_name_of_its_own_is_left_alone()
    {
        generated("""
            public class appointments
            {
                [Fact]
                public void a_test()
                {
                    Guid assigned;
                    assigned = Guid.NewGuid();
                    Console.WriteLine(Guid.NewGuid());
                    var fromString = Guid.Parse("8b2c4f5e-0000-0000-0000-000000000000");
                }
            }
            """).ShouldBeNull();
    }

    // A plain analyzer reference gets no buildTransitive opt-in: an interceptor there is a hard CS9137
    [Fact]
    public void nothing_is_intercepted_unless_the_project_opted_bobcat_generated_into_interceptors()
    {
        DeclaredValues.InterceptorsEnabled(Microsoft.CodeAnalysis.CSharp.CSharpParseOptions.Default).ShouldBeFalse();
        DeclaredValues.InterceptorsEnabled(Microsoft.CodeAnalysis.CSharp.CSharpParseOptions.Default
            .WithFeatures([new("InterceptorsNamespaces", "My.Own;Bobcat.Generated")])).ShouldBeTrue();
    }
}
