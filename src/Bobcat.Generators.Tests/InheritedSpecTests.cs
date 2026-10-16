using Microsoft.CodeAnalysis;
using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// bobcat#600: a test written once on a shared base and run by every subclass is one specification
/// per subclass, under the subclass's identity — listed, run by identity and checked by BOBCAT036 —
/// whether the base is in the same compilation or a referenced assembly.
/// </summary>
public class InheritedSpecTests
{
    private const string Usings =
        """
        using System;
        using Bobcat;
        using StandIns;
        using Xunit;

        namespace StandIns
        {
            // Bobcat.Xunit's attribute, as far as this compilation needs: a Fact, with a Name. A
            // base read from metadata has to carry a real attribute, not a name nothing binds.
            public class BobcatSpecAttribute : FactAttribute
            {
                public string? Name { get; set; }
            }
        }

        """;

    private const string ConsumerUsings =
        """
        using System;
        using Bobcat;
        using Xunit;

        """;

    private const string SharedBase =
        """
        namespace Compliance
        {
            [BobcatFeature]
            public abstract class TransportCompliance<T>
            {
                [BobcatSpec]
                public void can_request_reply()
                {
                    // Given a transport
                    // Then a request gets its reply
                }

                [BobcatSpec(Name = "retries a failed send")]
                public void retries() { }

                // A plain helper, not a test
                public void helper() { }
            }
        }
        """;

    private const string Subclasses =
        """
        namespace Transports
        {
            [BobcatFeature("TCP transport")]
            public class TcpCompliance : Compliance.TransportCompliance<int>;

            public class RabbitMqTransportCompliance : Compliance.TransportCompliance<string>;
        }
        """;

    [Fact]
    public void each_subclass_binds_the_inherited_tests_under_its_own_identity()
    {
        var outcome = GeneratorHarness.Run(Usings + SharedBase + Subclasses);
        outcome.CompilationErrors.ShouldBeEmpty();

        var registration = outcome.GeneratedSource("BobcatDeclaredSteps");

        registration.ShouldContain(
            """Bind("TCP transport/can request reply", "Transports.TcpCompliance", "can_request_reply");""");
        registration.ShouldContain(
            """Bind("TCP transport/retries a failed send", "Transports.TcpCompliance", "retries");""");
        registration.ShouldContain(
            """Bind("Rabbit Mq Transport Compliance/can request reply", "Transports.RabbitMqTransportCompliance", "can_request_reply");""");

        // The body's steps stay registered where the body is written, and each binding says so
        registration.ShouldContain("""Register("Transport Compliance/can request reply", """);
        registration.ShouldContain(
            """Inherit("TCP transport/can request reply", "Transport Compliance/can request reply");""");

        registration.ShouldNotContain("helper");
    }

    [Fact]
    public void an_abstract_base_binds_no_identity_of_its_own()
    {
        // Its tests only ever run as a subclass's. Listed, the base would be a specification nothing can run.
        var outcome = GeneratorHarness.Run(Usings + SharedBase + Subclasses);

        outcome.GeneratedSource("BobcatDeclaredSteps")
            .ShouldNotContain("""Bind("Transport Compliance/""");
    }

    [Fact]
    public void generic_arity_never_reaches_a_title()
    {
        var outcome = GeneratorHarness.Run(Usings + SharedBase + Subclasses);

        outcome.GeneratedSource("BobcatDeclaredSteps").ShouldNotContain("`");
    }

    [Fact]
    public void a_base_in_a_referenced_assembly_is_read_from_metadata()
    {
        var library = GeneratorHarness.CompileLibrary("Compliance", Usings + SharedBase);

        var outcome = GeneratorHarness.Run(
            ConsumerUsings + Subclasses,
            options: null,
            extraReferences: [MetadataReference.CreateFromImage(library)]);

        outcome.CompilationErrors.ShouldBeEmpty();

        var registration = outcome.GeneratedSource("BobcatDeclaredSteps");
        registration.ShouldContain(
            """Bind("TCP transport/can request reply", "Transports.TcpCompliance", "can_request_reply");""");
        registration.ShouldContain(
            """Bind("Rabbit Mq Transport Compliance/retries a failed send", "Transports.RabbitMqTransportCompliance", "retries");""");
        registration.ShouldContain(
            """Inherit("TCP transport/can request reply", "Transport Compliance/can request reply");""");

        // The library registers its bodies' steps from its own initializer, which is run first
        registration.ShouldContain(
            "RunModuleConstructor(typeof(global::Compliance.TransportCompliance<>).Module.ModuleHandle);");
    }

    [Fact]
    public void a_test_the_subclass_overrides_is_not_inherited()
    {
        var outcome = GeneratorHarness.Run(Usings + SharedBase +
            """
            namespace Transports
            {
                [BobcatFeature("TCP transport")]
                public class TcpCompliance : Compliance.TransportCompliance<int>
                {
                    public new void retries() { }
                }
            }
            """);

        var registration = outcome.GeneratedSource("BobcatDeclaredSteps");
        registration.ShouldContain("""Bind("TCP transport/can request reply", """);
        registration.ShouldNotContain("""Bind("TCP transport/retries a failed send", """);
    }

    [Fact]
    public void a_test_that_records_nothing_is_not_bound()
    {
        var outcome = GeneratorHarness.Run(Usings +
            """
            public abstract class PlainBase
            {
                [Fact]
                public void plain_fact() { }
            }

            public class PlainSub : PlainBase;
            """);

        outcome.Result.Results.SelectMany(r => r.GeneratedSources)
            .ShouldNotContain(s => s.HintName.Contains("BobcatDeclaredSteps"));
    }

    [Fact]
    public void a_class_level_scenario_on_the_base_opens_the_recording()
    {
        // xUnit inherits a class-level [BobcatScenario] (measured), so the generator does too
        var outcome = GeneratorHarness.Run(Usings +
            """
            [BobcatScenario]
            public abstract class ScenarioBase
            {
                [Fact]
                public void inherited_fact() { }
            }

            public class ScenarioSub : ScenarioBase;
            """);

        outcome.GeneratedSource("BobcatDeclaredSteps")
            .ShouldContain("""Bind("Scenario Sub/inherited fact", "ScenarioSub", "inherited_fact");""");
    }

    [Fact]
    public void two_subclasses_with_one_feature_title_collide()
    {
        var outcome = GeneratorHarness.Run(Usings + SharedBase +
            """
            namespace Transports
            {
                [BobcatFeature("Buffered")]
                public class SqsBufferedCompliance : Compliance.TransportCompliance<int>;

                [BobcatFeature("Buffered")]
                public class KafkaBufferedCompliance : Compliance.TransportCompliance<long>;
            }
            """);

        var errors = outcome.WithId("BOBCAT036").ToList();
        errors.ShouldNotBeEmpty();
        errors.ShouldContain(x => x.GetMessage().Contains("'Buffered/can request reply'")
                                  && x.GetMessage().Contains("Transports.KafkaBufferedCompliance.can_request_reply"));
    }

    [Fact]
    public void overriding_an_inherited_spec_with_a_fact_is_refused()
    {
        var outcome = GeneratorHarness.Run(Usings +
            """
            public abstract class SharedSpecs
            {
                [BobcatSpec]
                public virtual void flaky_on_some_transports() { }
            }

            public class OneTransportSpecs : SharedSpecs
            {
                [Fact(Skip = "not on this transport")]
                public override void flaky_on_some_transports() { }
            }
            """);

        var error = outcome.WithId("BOBCAT040").ShouldHaveSingleItem();
        error.Severity.ShouldBe(DiagnosticSeverity.Error);
        error.GetMessage().ShouldContain("[BobcatSpec(Skip = \"...\")]");
    }

    [Fact]
    public void overriding_an_inherited_spec_with_a_bobcat_spec_is_fine()
    {
        var outcome = GeneratorHarness.Run(Usings +
            """
            public abstract class SharedSpecs
            {
                [BobcatSpec]
                public virtual void flaky_on_some_transports() { }
            }

            public class OneTransportSpecs : SharedSpecs
            {
                [BobcatSpec(Skip = "not on this transport")]
                public override void flaky_on_some_transports() { }
            }
            """);

        outcome.WithId("BOBCAT040").ShouldBeEmpty();
    }
}
