using System.Text.RegularExpressions;

namespace Bobcat;

/// <summary>
/// Sets the title used to match this fixture to a Gherkin Feature.
/// If not specified, the title is derived from the class name
/// (e.g., OrderAggregateFixture → "Order Aggregate").
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class FixtureTitleAttribute : Attribute
{
    public string Title { get; }
    public FixtureTitleAttribute(string title) => Title = title;
}

/// <summary>
/// Names the Event Model this assembly's specs contribute slices to. Without it the generated
/// <c>BobcatEventModelSource</c> names its model after the spec assembly — and upstream,
/// <c>EventModelDiscovery.Assemble</c> folds descriptors together <b>by model name</b>, so a spec
/// assembly called <c>BankAccountES.Tests</c> can never merge with the Wolverine-derived model,
/// which is named for the <i>service</i> (<c>opts.ServiceName</c>). Set this to the service name
/// and the Gherkin/code-first slices land on the same model as the chains they describe
/// (issue #172).
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class EventModelNameAttribute : Attribute
{
    public string Name { get; }
    public EventModelNameAttribute(string name) => Name = name;
}

/// <summary>
/// Marks a static method the generated Microsoft.Testing.Platform entry point calls to
/// configure the suite — <c>static void Configure(BobcatRunner runner)</c>, any method and
/// type name. This is the seam for registering resources, failure policies, and anything else
/// a hand-written <c>Main</c> would have done, in a spec project that lets Bobcat.Generators
/// emit the entry point (issue #207). The generated <c>Main</c> scans the assembly for features
/// and code-first specifications first, then calls every <c>[BobcatConfiguration]</c> method in
/// a deterministic order (sorted by declaring type, then method name).
/// </summary>
/// <remarks>
/// Ignored — with a build warning saying so — when the assembly declares its own entry point,
/// because a hand-written <c>Main</c> owns configuration completely. The method must be static,
/// return <c>void</c>, take exactly one <c>BobcatRunner</c> parameter, and be reachable from
/// generated code (not private).
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BobcatConfigurationAttribute : Attribute;

/// <summary>
/// Composes shared/library grammar modules into a fixture. The generator also scans the
/// listed module types for [Given]/[When]/[Then]/[Check] methods and matches their steps to
/// the feature, alongside the fixture's own. Repeatable/composable. Modules are instantiated
/// once per scenario; a module that inherits <see cref="Fixture"/> receives the step context.
/// </summary>
/// <remarks>
/// <para>
/// <b>Parameterized modules (issue #212, phase 2):</b> constructor arguments after the module type
/// flow to the module's construction — <c>[IncludeGrammars(typeof(WalletGrammars), "/api/wallet")]</c>
/// — so one grammar type binds against different targets without a subclass per binding. The
/// vocabulary stays a compile-time fact (the generator reads the steps from the type symbol); only
/// the <em>binding</em> becomes a construction fact. Attribute arguments are limited to constants
/// and <c>typeof</c> by the CLR, and that is deliberate: anything richer — a configured resource, a
/// delegate — should reach the module from the scenario scope by type, which
/// <see cref="Engine.IStepContext.SetState{T}"/> and service injection into the module's
/// constructor already handle. Constructor parameters the literals do not cover are resolved like
/// step parameters (IStepContext, test resources, scoped services); trailing optional parameters
/// may be omitted.
/// </para>
/// <para>
/// The attribute is discovered on the fixture <em>and its base classes</em> (most-derived wins for
/// a module type declared at several levels, so a derived fixture can re-parameterize a
/// base-declared module). <b>One instance per module type per fixture</b> — the same module type
/// declared twice on one class is a compile error (BOBCAT018), because two instances of one
/// vocabulary would make every step text ambiguous (the problem BOBCAT013 exists to close). A
/// fixture that genuinely needs two HTTP surfaces gets two thin module subclasses with distinct
/// step texts.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class IncludeGrammarsAttribute : Attribute
{
    public Type[] Modules { get; }

    /// <summary>Constructor arguments for a single-module include, in positional order. Empty for the multi-module form.</summary>
    public object?[] Arguments { get; } = [];

    public IncludeGrammarsAttribute(params Type[] modules) => Modules = modules;

    /// <summary>Include one module, constructed with <paramref name="arguments"/> (plus DI-resolved parameters) per scenario.</summary>
    public IncludeGrammarsAttribute(Type module, params object?[] arguments)
    {
        Modules = [module];
        Arguments = arguments;
    }
}

/// <summary>
/// Marks a method as a Given step (data setup).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class GivenAttribute : StepAttribute
{
    public GivenAttribute(string expression) : base(expression) { }

    public override string Keyword => "Given";
}

/// <summary>
/// Marks a method as a When step (action under test).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class WhenAttribute : StepAttribute
{
    public WhenAttribute(string expression) : base(expression) { }

    public override string Keyword => "When";
}

/// <summary>
/// Marks a method as a Then step (assertion).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class ThenAttribute : StepAttribute
{
    public ThenAttribute(string expression) : base(expression) { }

    public override string Keyword => "Then";
}

/// <summary>
/// A step that spells <b>no keyword</b> — <c>[Step("Multiply by {multiplier} then add {delta}")]</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One family, both lanes.</b> This is also the base class of <see cref="GivenAttribute"/>,
/// <see cref="WhenAttribute"/>, <see cref="ThenAttribute"/> and <see cref="CheckAttribute"/>, and
/// every one of them works in <b>both</b> authoring styles: matched against a <c>.feature</c> file's
/// step text on a fixture, and intercepted at the call site when an ordinary xUnit or TUnit test
/// calls the method directly. There used to be a second vocabulary for the second case
/// (<c>[BobcatStep]</c>) and the split had no reason behind it — a step's text and keyword are the
/// same facts whichever way the step is reached.
/// </para>
/// <para>
/// <b>Keywordless is a real choice, not a default.</b> Storyteller sentences and Gauge steps read as
/// prose, and "Multiply by 3 then add 4" is not a Given, a When or a Then. In the Gherkin lane a
/// keywordless step matches under <em>any</em> keyword — the same rule <c>[TableGrammar]</c> has
/// always followed — so the feature file decides, which is where that decision belongs.
/// </para>
/// <para>
/// <b>Two expression syntaxes, both first class.</b> See <see cref="StepAttribute.Expression"/>.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public class StepAttribute : Attribute
{
    public StepAttribute(string expression) => Expression = expression;

    /// <summary>
    /// The step's text, in <b>either</b> of two syntaxes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Cucumber expressions</b> capture by TYPE and position: <c>"the left operand is {int}"</c>,
    /// plus <c>{string}</c>, <c>{word}</c>, the Event Modeling type words (<c>{aggregate}</c>,
    /// <c>{command}</c>, <c>{event}</c>, …), optional text and raw regex.
    /// </para>
    /// <para>
    /// <b>Named templates</b> — Storyteller's <c>[FormatAs]</c> syntax — capture by PARAMETER NAME:
    /// <c>"Adding {x} to {y} should equal {sum}"</c> over <c>double Adding(double x, double y, double
    /// sum)</c>. The parameter's own type decides how the cell is read, so the step text says what the
    /// value <i>is</i> rather than merely what type it has, and the same string renders the sentence
    /// when the method is called from C#.
    /// </para>
    /// <para>
    /// <b>Which one is in force is decided per expression, not per project:</b> when every
    /// placeholder names a parameter of the method, it is a named template; otherwise it is a
    /// Cucumber expression. An expression with no placeholders reads identically either way. Mixing
    /// the two in one expression is <c>BOBCAT028</c> — there is no reading of <c>{int} plus {y}</c>
    /// that is not a guess.
    /// </para>
    /// </remarks>
    public string Expression { get; }

    /// <summary>
    /// Given / When / Then / Check, or the empty string for a step that spells no keyword.
    /// </summary>
    /// <remarks>
    /// Read by the generator from the attribute's TYPE, never by instantiating it — so a project's
    /// own <c>[GivenEvents]</c> deriving from <see cref="GivenAttribute"/> inherits the keyword with
    /// no registration anywhere.
    /// </remarks>
    public virtual string Keyword => "";
}

/// <summary>
/// Polling modifier for eventual-consistency assertions. Retries the step's own success
/// criterion — return/out comparison until it matches, a <c>[Check]</c> until it returns
/// true, or a void action until it completes without throwing — attempting at t=0 then
/// every <see cref="PollAt"/> ms until <see cref="TimeoutMs"/> elapses. Exceptions during
/// the window are treated as "not ready yet" and retried; the last one surfaces on timeout.
/// Both values are milliseconds.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class WaitForAttribute : Attribute
{
    public int TimeoutMs { get; }

    /// <summary>Poll interval in milliseconds. Defaults to 100ms.</summary>
    public int PollAt { get; set; } = 100;

    public WaitForAttribute(int timeoutMs) => TimeoutMs = timeoutMs;
}

/// <summary>
/// Overrides the naming convention to mark a fixture method as a per-scenario setup hook.
/// By convention any method named <c>BeforeEach</c> (or <c>BeforeEachAsync</c>) is one already.
/// Runs INSIDE the scenario's DI scope, so it can inject the same scoped services the steps see.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class BeforeEachAttribute : Attribute { }

/// <summary>
/// Overrides the naming convention to mark a fixture method as a per-scenario teardown hook
/// (always runs, even on failure). By convention any method named <c>AfterEach</c> (or
/// <c>AfterEachAsync</c>) is one already. Runs INSIDE the scenario's DI scope.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class AfterEachAttribute : Attribute { }

/// <summary>
/// Overrides the naming convention to mark a <b>static</b> fixture method as a once-per-feature
/// setup hook. By convention any static method named <c>BeforeAll</c> (or <c>BeforeAllAsync</c>)
/// is one already. Runs BEFORE any scenario scope exists, so it may inject the step context,
/// test resources, and root services — but not scoped services.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class BeforeAllAttribute : Attribute { }

/// <summary>
/// Overrides the naming convention to mark a <b>static</b> fixture method as a once-per-feature
/// teardown hook (always runs). By convention any static method named <c>AfterAll</c> (or
/// <c>AfterAllAsync</c>) is one already. Same injection rules as <see cref="BeforeAllAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class AfterAllAttribute : Attribute { }

/// <summary>
/// Marks a method as a boolean check — a Then step that returns bool (true = pass, false = fail).
/// Named "Check" to avoid collision with xUnit's [Fact].
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class CheckAttribute : StepAttribute
{
    public CheckAttribute(string expression) : base(expression) { }

    public override string Keyword => "Then";
}

/// <summary>
/// Marks a step method as accepting table data. Each row in the table
/// becomes a separate invocation of the method.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class TableAttribute : Attribute { }

/// <summary>
/// Marks a step method as a decision table. The accompanying data table is matched
/// positionally per row: columns whose names match input parameters supply inputs,
/// and the remaining columns (matched to <c>out</c> parameters or the method's return
/// value) are <b>expected</b> outputs compared via the type-aware checker. Input cells
/// render plain; expected cells are colored by pass/fail.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class DecisionTableAttribute : Attribute { }

/// <summary>
/// Marks a step method's return value as the actual value compared against an expected
/// capture/column. Optional — a non-void <c>[Then]</c> method is treated as a
/// return-value verification by convention. Use to set an explicit column name in a
/// decision table when the method name is not the desired column.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.ReturnValue)]
public class ExpectedAttribute : Attribute
{
    /// <summary>Optional decision-table column name that maps to the return value.</summary>
    public string? Column { get; set; }

    public ExpectedAttribute() { }
    public ExpectedAttribute(string column) => Column = column;
}

/// <summary>
/// Marks a Then method as a set verification step. The method must return
/// IEnumerable of some type. Bobcat compares the returned collection against
/// expected table data, producing per-cell diffs.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class SetVerificationAttribute : Attribute
{
    /// <summary>
    /// Comma-separated column names that uniquely identify a row for matching.
    /// </summary>
    public string KeyColumns { get; set; } = "";

    /// <summary>
    /// When true the rows must also appear in the order the specification writes them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default, because most sets have no meaningful order and asserting one the system
    /// never promised is how a suite acquires a test that fails when nothing broke. Turn it on when
    /// the order is part of what the specification claims — an event stream, a sorted report, a
    /// queue.
    /// </para>
    /// <para>
    /// Rows are still matched by <see cref="KeyColumns"/> first and the order of the matches is
    /// then checked, so an inserted row reads as one extra row rather than as every row after it
    /// disagreeing. See <c>SetVerificationComparer.Compare</c> for why that matters and for what
    /// naming no key columns costs.
    /// </para>
    /// </remarks>
    public bool Ordered { get; set; }

    /// <summary>
    /// For a set of plain values — <c>IEnumerable&lt;string&gt;</c>, <c>IEnumerable&lt;int&gt;</c>,
    /// a set of enum values — the name of the single column each value is compared under.
    /// </summary>
    /// <remarks>
    /// A set of objects takes its columns from the properties the document's headers name; a set of
    /// values has no properties to read, so the fixture says what the one column is called. This is
    /// Storyteller's <c>VerifyStringList(...).Titled("The names in order should be", "Name")</c>,
    /// whose second argument did the same job. Without it a set of strings is compared against the
    /// properties of <c>string</c> — <c>Length</c> and <c>Chars</c> — and every row reads as missing
    /// and extra at once, which is why BOBCAT031 asks for it at build time.
    /// </remarks>
    public string Column { get; set; } = "";
}

/// <summary>
/// The data-table column this parameter or property is known by, when the document should not have
/// to call it by its own code name.
/// </summary>
/// <remarks>
/// <para>
/// Storyteller's <c>[Header("Player Name")]</c>, and for the same reason: the column heading is
/// prose in a document people read, while the parameter name is code. Without it a table that wants
/// to say "Player Name" forces the parameter to be called <c>Player_Name</c> or the column to be
/// called <c>player</c>.
/// </para>
/// <para>
/// <b>On a parameter</b> it names the column the parameter binds to — a <c>[Table]</c> step's row
/// method, or a row method <c>Fixture.RunTable</c> calls. <b>On a property</b> it names the column a
/// set verification compares that property under, which is Storyteller's
/// <c>_.Compare(o =&gt; o.Amount).Header("The Amount")</c>; a set's columns are the result type's
/// properties, so a property is the only thing there is to put it on. On a record, target the
/// property explicitly: <c>record Detail([property: Header("The Amount")] decimal Amount)</c>.
/// </para>
/// <para>
/// <b>The title replaces the name — it is not an alias.</b> A document that titles a column writes
/// the title, and the property name then matches nothing; one column with two spellings is how a
/// grid would end up with two columns for one property. Adding a header to a property is a change to
/// the vocabulary of every document that compares it, exactly as renaming the property would be.
/// </para>
/// <para>
/// <c>KeyColumns</c> names columns as the <em>document</em> writes them, so a titled property is
/// named there by its title.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public class HeaderAttribute : Attribute
{
    public HeaderAttribute(string name) => Name = name;

    public string Name { get; }
}

/// <summary>
/// Overrides the value checker used for a comparison. The supplied type must implement
/// <c>IValueChecker&lt;T&gt;</c> for the value being checked and have a parameterless
/// constructor. Highest precedence in the checker resolution chain.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter | AttributeTargets.Property)]
public class ComparisonAttribute : Attribute
{
    public Type CheckerType { get; }
    public ComparisonAttribute(Type checkerType) => CheckerType = checkerType;
}

/// <summary>
/// Compares numeric values with an absolute tolerance instead of exact equality.
/// Flows into <c>CheckOptions.Tolerance</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter | AttributeTargets.Property)]
public class ApproxAttribute : Attribute
{
    public double Tolerance { get; }
    public ApproxAttribute(double tolerance) => Tolerance = tolerance;
}
