using System.Collections;
using System.Runtime.CompilerServices;
using Bobcat.Engine;
using Bobcat.Runtime;

namespace Bobcat.CodeFirst;

/// <summary>
/// The code-first way to author a feature: a class whose <c>[Scenario]</c> methods declare
/// <c>Given</c>/<c>When</c>/<c>Then</c> steps in plain C#, with IntelliSense end to end and no
/// <c>.feature</c> file or source generator in the loop. What a scenario method builds is the same
/// <see cref="FeatureDefinition"/> / <see cref="DelegateExecutionStep"/> model the Gherkin generator
/// emits, so it renders, supervises, retries and reports identically.
/// </summary>
/// <remarks>
/// <para>
/// <b>Compose, then execute.</b> A scenario method is invoked on a fresh instance every time the
/// scenario runs, and each <c>Given</c>/<c>When</c>/<c>Then</c> call <i>records</i> a step rather
/// than running it; the engine then executes the recorded steps in order with the timeout,
/// continuation rules and observers every other scenario gets. Two consequences: the method body
/// itself cannot <c>await</c> a step's outcome (use a <see cref="Captured{T}"/> handle and read
/// <c>.Value</c> inside a later step), and anything that needs the step context belongs inside a
/// step body, where <see cref="Fixture.Context"/> is set.
/// </para>
/// <para>
/// <b>Failure semantics.</b> A <c>Given</c> or <c>When</c> that throws is critical and stops the
/// scenario, exactly as in Gherkin. A <c>Then</c> body that throws — a Shouldly assertion, a
/// comparison — is recorded as an <i>assertion failure</i> on that step and the scenario continues,
/// so every disagreement is gathered rather than only the first. That is the
/// <c>ProjectionScenario</c> contract (action failure stops, assertion failures accumulate), and it
/// is the one deliberate divergence from a Gherkin <c>[Then]</c> method, which the generator treats
/// as critical when it throws. <see cref="SpecCriticalException"/> and
/// <see cref="SpecCatastrophicException"/> keep their meaning everywhere.
/// </para>
/// <para>
/// A specification <i>is</i> a <see cref="Fixture"/> — the same <c>Context</c>, the same recovery
/// hint attributes, one fresh instance per scenario. Registered with
/// <see cref="SpecificationRunnerExtensions.AddSpecification{T}"/> or found by
/// <see cref="SpecificationRunnerExtensions.ScanForSpecifications"/>.
/// </para>
/// </remarks>
public abstract class Specification : Fixture
{
    private List<PendingStep>? _pending;
    private readonly List<Fixture> _hosted = new();

    // --- composition -------------------------------------------------------------------------

    /// <summary>
    /// Run <paramref name="declare"/> against this instance in composition mode and append the
    /// steps it declared to <paramref name="plan"/>. Called by the <see cref="ScenarioDefinition"/>
    /// that <see cref="SpecificationFeature"/> builds; not meant for user code.
    /// </summary>
    internal void Compose(ExecutionPlan plan, Action<Specification> declare)
    {
        if (_pending != null)
            throw new InvalidOperationException("A scenario is already being composed on this specification instance.");

        _pending = new List<PendingStep>();
        try
        {
            try
            {
                declare(this);
            }
            catch (Exception ex)
            {
                // A mistake in the scenario method itself — reading a Captured value too early, say —
                // is reported as that scenario failing to compose, not as the whole run falling over.
                var captured = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                _pending.Clear();
                _pending.Add(new PendingStep(StepKind.SetUp, "composing the scenario", (_, _, _) =>
                {
                    captured.Throw();
                    return Task.CompletedTask;
                }));
            }

            var number = 0;
            foreach (var pending in _pending)
            {
                number++;
                plan.Add(pending.Materialize($"step-{number}", this));
            }
        }
        finally
        {
            _pending = null;
        }
    }

    private PendingStep add(StepKind kind, string text, StepBody body)
    {
        if (_pending == null)
        {
            throw new InvalidOperationException(
                $"{GetType().Name} is not composing a scenario. Given/When/Then may only be called from within a " +
                "[Scenario] method while the scenario is being built — not from a constructor, a step body, or a hook.");
        }

        var pending = new PendingStep(kind, text, body);
        _pending.Add(pending);
        return pending;
    }

    /// <summary>
    /// Make a fixture instance available to step bodies, with its <see cref="Fixture.Context"/>
    /// kept in step with this specification's. This is how a specification borrows a vocabulary
    /// that lives on a fixture base class — typed event-sourcing steps, say — without inheriting it.
    /// </summary>
    protected TFixture Host<TFixture>() where TFixture : Fixture, new()
    {
        var fixture = new TFixture();
        _hosted.Add(fixture);
        return fixture;
    }

    /// <summary>Called before every step body so the fixtures in play see the live context.</summary>
    private void bind(IStepContext context)
    {
        Context = context;
        foreach (var fixture in _hosted) fixture.Context = context;
    }

    // --- raw step ----------------------------------------------------------------------------

    /// <summary>
    /// The escape hatch: a step of any kind whose body sees the context, its own
    /// <see cref="StepResult"/> (to mark cells) and the cancellation token. Everything else here
    /// is built on it.
    /// </summary>
    protected StepHandle Step(StepKind kind, string text, Func<IStepContext, StepResult, CancellationToken, Task> body)
        => new(add(kind, text, (ctx, result, ct) => body(ctx, result, ct)));

    // --- Given -------------------------------------------------------------------------------

    protected StepHandle Given(string text, Action body) => new(add(StepKind.Given, text, plain(body)));
    protected StepHandle Given(string text, Func<Task> body) => new(add(StepKind.Given, text, plain(body)));
    protected StepHandle Given(string text, Func<IStepContext, Task> body) => new(add(StepKind.Given, text, plain(body)));

    protected Captured<T> Given<T>(string text, Func<T> body) => capture(StepKind.Given, text, (_, _) => Task.FromResult(body()));
    protected Captured<T> Given<T>(string text, Func<Task<T>> body) => capture(StepKind.Given, text, (_, _) => body());
    protected Captured<T> Given<T>(string text, Func<IStepContext, Task<T>> body) => capture(StepKind.Given, text, (ctx, _) => body(ctx));

    // --- When --------------------------------------------------------------------------------

    protected StepHandle When(string text, Action body) => new(add(StepKind.When, text, plain(body)));
    protected StepHandle When(string text, Func<Task> body) => new(add(StepKind.When, text, plain(body)));
    protected StepHandle When(string text, Func<IStepContext, Task> body) => new(add(StepKind.When, text, plain(body)));

    protected Captured<T> When<T>(string text, Func<T> body) => capture(StepKind.When, text, (_, _) => Task.FromResult(body()));
    protected Captured<T> When<T>(string text, Func<Task<T>> body) => capture(StepKind.When, text, (_, _) => body());
    protected Captured<T> When<T>(string text, Func<IStepContext, Task<T>> body) => capture(StepKind.When, text, (ctx, _) => body(ctx));

    // --- Then --------------------------------------------------------------------------------

    /// <summary>An assertion step: anything the body throws is an assertion failure, not a crash.</summary>
    protected StepHandle Then(string text, Action body) => new(add(StepKind.Then, text, assertion(plain(body))));
    protected StepHandle Then(string text, Func<Task> body) => new(add(StepKind.Then, text, assertion(plain(body))));
    protected StepHandle Then(string text, Func<IStepContext, Task> body) => new(add(StepKind.Then, text, assertion(plain(body))));

    /// <summary>
    /// Observe a value and, through the returned expectation, say what it should be:
    /// <c>Then("the balance", () => account.Balance).ShouldBe(50m)</c> renders as
    /// "the balance should be 50" with the expected and actual side by side. With no
    /// expectation attached the step simply records the value.
    /// </summary>
    protected ValueExpectation<T> Then<T>(string text, Func<T> actual) => value(text, (_, _) => Task.FromResult(actual()));
    protected ValueExpectation<T> Then<T>(string text, Func<Task<T>> actual) => value(text, (_, _) => actual());
    protected ValueExpectation<T> Then<T>(string text, Func<IStepContext, Task<T>> actual) => value(text, (ctx, _) => actual(ctx));

    /// <summary>
    /// The same, with the step text taken from the source of the lambda itself:
    /// <c>Then(() => account.Balance).ShouldBe(50m)</c> renders as "account.Balance should be 50".
    /// </summary>
    protected ValueExpectation<T> Then<T>(Func<T> actual, [CallerArgumentExpression(nameof(actual))] string? expression = null)
        => value(StepText.FromExpression(expression), (_, _) => Task.FromResult(actual()));

    protected ValueExpectation<T> Then<T>(Func<Task<T>> actual, [CallerArgumentExpression(nameof(actual))] string? expression = null)
        => value(StepText.FromExpression(expression), (_, _) => actual());

    /// <summary>A boolean check: <c>false</c> fails the step (and the scenario continues), <c>true</c> passes it.</summary>
    protected StepHandle Check(string text, Func<bool> predicate) => check(text, (_, _) => Task.FromResult(predicate()));
    protected StepHandle Check(string text, Func<Task<bool>> predicate) => check(text, (_, _) => predicate());
    protected StepHandle Check(string text, Func<IStepContext, Task<bool>> predicate) => check(text, (ctx, _) => predicate(ctx));

    /// <summary>
    /// Set verification over whatever <paramref name="actual"/> returns:
    /// <c>ThenRows("the open orders", () => query()).KeyedBy("Id").ShouldMatch(new { Id = 1, Total = 9.5m }, ...)</c>.
    /// Renders as the same per-cell-coloured table a Gherkin <c>[SetVerification]</c> does.
    /// </summary>
    protected SetExpectation ThenRows<TCollection>(string text, Func<TCollection> actual) where TCollection : IEnumerable
        => rows(text, (_, _) => Task.FromResult<IEnumerable>(actual()));

    protected SetExpectation ThenRows<TCollection>(string text, Func<Task<TCollection>> actual) where TCollection : IEnumerable
        => rows(text, async (_, _) => await actual());

    protected SetExpectation ThenRows<TCollection>(string text, Func<IStepContext, Task<TCollection>> actual) where TCollection : IEnumerable
        => rows(text, async (ctx, _) => await actual(ctx));

    // --- plumbing ----------------------------------------------------------------------------

    internal delegate Task StepBody(IStepContext context, StepResult result, CancellationToken token);

    private StepBody plain(Action body) => (_, _, _) => { body(); return Task.CompletedTask; };
    private StepBody plain(Func<Task> body) => (_, _, _) => body();
    private StepBody plain(Func<IStepContext, Task> body) => (ctx, _, _) => body(ctx);

    private Captured<T> capture<T>(StepKind kind, string text, Func<IStepContext, CancellationToken, Task<T>> body)
    {
        Captured<T> captured = null!;
        var pending = add(kind, text, async (ctx, _, ct) => captured.Set(await body(ctx, ct)));
        captured = new Captured<T>(pending);
        return captured;
    }

    private ValueExpectation<T> value<T>(string text, Func<IStepContext, CancellationToken, Task<T>> actual)
    {
        var expectation = new ValueExpectation<T>(text);
        var pending = add(StepKind.Then, text, assertion(async (ctx, result, ct) =>
        {
            var value = await actual(ctx, ct);
            expectation.Evaluate(value, result);
        }));
        expectation.Attach(pending);
        return expectation;
    }

    private StepHandle check(string text, Func<IStepContext, CancellationToken, Task<bool>> predicate)
        => new(add(StepKind.Then, text, assertion(async (ctx, result, ct) =>
        {
            if (await predicate(ctx, ct)) result.MarkSuccess();
            else result.MarkFailed();
        })));

    private SetExpectation rows(string text, Func<IStepContext, CancellationToken, Task<IEnumerable>> actual)
    {
        var expectation = new SetExpectation(text);
        var pending = add(StepKind.Then, text, assertion(async (ctx, result, ct) =>
        {
            var value = await actual(ctx, ct);
            expectation.Evaluate(value, result);
        }));
        expectation.Attach(pending);
        return expectation;
    }

    /// <summary>
    /// The Then contract: a thrown exception is an assertion failure on this step, recorded with
    /// its message, and the scenario goes on to the next step. Spec-level exceptions and a
    /// cancellation of the scenario keep their engine meaning.
    /// </summary>
    private static StepBody assertion(StepBody body) => async (ctx, result, ct) =>
    {
        try
        {
            await body(ctx, result, ct);
        }
        catch (SpecCatastrophicException) { throw; }
        catch (SpecCriticalException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            result.MarkCells(new CellResult("assertion", ResultStatus.failed, firstLine(ex.Message)) { Exception = ex });
            result.MarkFailed();
        }
    };

    private static string firstLine(string message)
    {
        var newline = message.IndexOfAny(['\r', '\n']);
        return newline < 0 ? message : message[..newline];
    }

    /// <summary>
    /// A step as declared, before it is materialized into the plan. Kept mutable until the scenario
    /// method returns so a fluent tail (<c>.ShouldBe</c>, <c>.WithRows</c>) can still shape it.
    /// </summary>
    internal sealed class PendingStep
    {
        private readonly StepBody _body;
        private RowTable? _table;

        public PendingStep(StepKind kind, string text, StepBody body)
        {
            Kind = kind;
            Text = text;
            _body = body;
        }

        public StepKind Kind { get; }
        public string Text { get; set; }

        public void ShowRows(IEnumerable<object?> rows) => _table = RowTable.Describe(rows);

        public DelegateExecutionStep Materialize(string id, Specification owner)
        {
            var table = _table;
            return new DelegateExecutionStep(id, Kind, Text, async (ctx, result, ct) =>
            {
                owner.bind(ctx);
                // The input table goes on first so it renders even when the body fails.
                table?.ApplyTo(result);
                await _body(ctx, result, ct);
            });
        }
    }
}

/// <summary>
/// A declared step, handed back so the declaration can be decorated. Today that means
/// <see cref="WithRows"/>; the handle exists so there is a place for more without another overload set.
/// </summary>
public sealed class StepHandle
{
    private readonly Specification.PendingStep _pending;

    internal StepHandle(Specification.PendingStep pending) => _pending = pending;

    /// <summary>
    /// Render these objects as the step's input table — the events a Given appended, the command a
    /// When sent — each row described by its public properties (see <see cref="RowTable"/>).
    /// </summary>
    public StepHandle WithRows(params object?[] rows)
    {
        _pending.ShowRows(rows);
        return this;
    }

    /// <inheritdoc cref="WithRows(object?[])"/>
    public StepHandle WithRows(IEnumerable<object?> rows)
    {
        _pending.ShowRows(rows);
        return this;
    }
}

/// <summary>
/// The outcome of a <c>Given</c>/<c>When</c> step, readable from the body of any later step.
/// Declared during composition, filled in during execution — which is why <see cref="Value"/>
/// throws if read too early.
/// </summary>
public sealed class Captured<T>
{
    private T _value = default!;
    private Specification.PendingStep? _pending;

    internal Captured(Specification.PendingStep pending) => _pending = pending;

    public bool HasValue { get; private set; }

    /// <summary>
    /// Render these objects as the producing step's input table — the command a When sent, say —
    /// the same decoration <see cref="StepHandle.WithRows(object?[])"/> gives a step without a value.
    /// </summary>
    public Captured<T> WithRows(params object?[] rows)
    {
        _pending?.ShowRows(rows);
        return this;
    }

    /// <inheritdoc cref="WithRows(object?[])"/>
    public Captured<T> WithRows(IEnumerable<object?> rows)
    {
        _pending?.ShowRows(rows);
        return this;
    }

    public T Value => HasValue
        ? _value
        : throw new InvalidOperationException(
            $"This Captured<{typeof(T).Name}> has no value yet: the step that produces it has not executed. " +
            "Read .Value inside the body of a later step, not while the scenario is being composed.");

    internal void Set(T value)
    {
        _value = value;
        HasValue = true;
    }
}

/// <summary>Step-text helpers shared by the code-first surface.</summary>
public static class StepText
{
    /// <summary>
    /// Turn a <c>[CallerArgumentExpression]</c> capture into readable step text: strips the lambda
    /// preamble (<c>() =&gt;</c>, <c>async () =&gt;</c>, <c>_ =&gt;</c>) and collapses whitespace.
    /// </summary>
    public static string FromExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return "(value)";

        var text = string.Join(' ', expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        foreach (var prefix in new[] { "async () =>", "async _ =>", "() =>", "_ =>" })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                text = text[prefix.Length..].Trim();
                break;
            }
        }

        // A block-bodied lambda is not an expression worth echoing.
        if (text.StartsWith('{')) return "(value)";

        if (text.StartsWith("await ", StringComparison.Ordinal)) text = text[6..];
        return text.Length == 0 ? "(value)" : text;
    }
}
