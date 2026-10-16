using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// bobcat#601: a step helper declared on a generic class is intercepted by a generic interceptor
/// over the class's type parameters, so the generated file compiles. It used to name a receiver
/// <c>Base&lt;T&gt;</c> with no <c>T</c> in scope — CS0246 at every call site.
/// </summary>
public class GenericStepHelperTests
{
    private static string spec(string body) =>
        $$"""
        using System.Threading.Tasks;
        using Bobcat;

        namespace Compliance;

        public sealed class FactAttribute : System.Attribute;

        public class TransportComplianceFixture;
        public class RabbitFixture : TransportComplianceFixture;

        {{body}}
        """;

    private static void compiles(string source, int interceptors)
    {
        var outcome = GeneratorHarness.Run(spec(source));
        var code = outcome.GeneratedSource("BobcatStepInterceptors");

        // That something was intercepted at all: a compile check over an empty file passes.
        var applications = code.Split("[global::System.Runtime.CompilerServices.InterceptsLocationAttribute(").Length - 1;
        applications.ShouldBe(interceptors, code);

        outcome.CompilationErrors.ShouldBeEmpty(code);
    }

    [Fact]
    public void a_call_inside_the_generic_class_itself()
    {
        compiles("""
            [BobcatFeature("Compliance")]
            public abstract class TransportCompliance<T> where T : TransportComplianceFixture, new()
            {
                [When("a message is sent")]
                internal void aMessageIsSent() { }

                [Then("it arrived in {count} ms")]
                internal Task<bool> itArrived(T fixture, int count) => Task.FromResult(true);

                [Given("the fixture")]
                internal T theFixture() => new T();

                [Fact]
                public async Task can_send_from_one_node_to_another()
                {
                    aMessageIsSent();
                    var fixture = theFixture();
                    await itArrived(fixture, 3);
                }
            }
            """, interceptors: 3);
    }

    [Fact]
    public void the_interceptor_carries_the_class_type_parameters_and_their_constraints()
    {
        var code = GeneratorHarness.Run(spec("""
            [BobcatFeature("Compliance")]
            public abstract class TransportCompliance<T> where T : TransportComplianceFixture, new()
            {
                [When("a message is sent")]
                internal void aMessageIsSent() { }

                [Fact]
                public void can_send() => aMessageIsSent();
            }
            """)).GeneratedSource("BobcatStepInterceptors");

        code.ShouldContain("__BobcatStep0<T>(");
        code.ShouldContain("this global::Compliance.TransportCompliance<T> receiver");
        code.ShouldContain("where T : global::Compliance.TransportComplianceFixture, new()");
    }

    [Fact]
    public void calls_from_a_closed_subclass_and_a_generic_subclass()
    {
        compiles("""
            public abstract class TransportCompliance<T> where T : TransportComplianceFixture, new()
            {
                [When("a message is sent with {fixture}")]
                internal void aMessageIsSent(T fixture) { }
            }

            [BobcatFeature("Rabbit")]
            public class RabbitCompliance : TransportCompliance<RabbitFixture>
            {
                [Fact]
                public void sends() => aMessageIsSent(new RabbitFixture());
            }

            public abstract class Intermediate<TF> : TransportCompliance<TF> where TF : TransportComplianceFixture, new()
            {
                [Fact]
                public void sends() => aMessageIsSent(new TF());
            }
            """, interceptors: 2);
    }

    [Fact]
    public void a_generic_helper_on_a_generic_class()
    {
        compiles("""
            [BobcatFeature("Compliance")]
            public abstract class TransportCompliance<T> where T : class
            {
                [Given("a {message}")]
                internal TMessage given<TMessage>(TMessage message) where TMessage : notnull => message;

                [When("one is made")]
                internal TMessage made<TMessage>() where TMessage : new() => new TMessage();

                [Fact]
                public void sends()
                {
                    given("hello");
                    made<RabbitFixture>();
                }
            }
            """, interceptors: 2);
    }

    [Fact]
    public void a_generic_step_on_a_generic_class_gets_both_type_parameter_lists()
    {
        // bobcat#534 meets bobcat#601: TAggregate appears in no parameter, so the generic interceptor
        // must declare the class's T and the method's TAggregate, and pass TAggregate explicitly.
        var outcome = GeneratorHarness.Run(spec("""
            public class Account;

            [BobcatFeature("Compliance")]
            public abstract class TransportCompliance<T> where T : class
            {
                [Given("{key} has already recorded {events}")]
                public Task GivenEventsFor<TAggregate>(string key, params object[] events) => Task.CompletedTask;
            }

            public class RabbitCompliance : TransportCompliance<RabbitFixture>
            {
                [Fact]
                public async Task records() => await GivenEventsFor<Account>("a", new object());
            }
            """));
        var code = outcome.GeneratedSource("BobcatStepInterceptors");

        code.ShouldContain("__BobcatStep0<T, TAggregate>(", customMessage: code);
        code.ShouldContain("receiver.GivenEventsFor<TAggregate>(key, events)", customMessage: code);
        outcome.CompilationErrors.ShouldBeEmpty(code);
    }

    [Fact]
    public void a_helper_on_a_type_nested_in_a_generic_one()
    {
        compiles("""
            public class Outer<TOuter>
            {
                [BobcatFeature("Nested")]
                public class Inner<TInner>
                {
                    [When("a message is sent")]
                    internal TOuter? sent(TInner value) => default;

                    [Fact]
                    public void sends(TInner value) => sent(value);
                }
            }
            """, interceptors: 1);
    }

    [Fact]
    public void a_method_type_parameter_shadowing_the_class_one_is_left_unintercepted()
    {
        // CS0693 is only a warning, but one interceptor cannot declare <T, T>. The call runs as
        // written rather than breaking the consumer's build.
        var outcome = GeneratorHarness.Run(spec("""
            [BobcatFeature("Compliance")]
            public abstract class TransportCompliance<T>
            {
                [When("a message is sent")]
                internal void sent<T>(T value) { }

                [Fact]
                public void sends() => sent(3);
            }
            """));

        outcome.CompilationErrors.ShouldBeEmpty();
    }
}
