namespace Bobcat;

/// <summary>
/// Marks a class as a <b>table grammar</b>: ONE grammar that runs Before-once → per-row →
/// After-once inside a single scenario. The surface syntax is a normal Gherkin step plus a
/// trailing data table — no new keywords.
///
/// <para>Internals are discovered by convention: methods named <c>Before</c>, <c>Row</c>, and
/// <c>After</c> (the <c>Async</c> suffix is recognized), with <c>[Before]</c>/<c>[Row]</c>/
/// <c>[After]</c> as the overrides. The generator instantiates the class fresh per execution,
/// so <c>Before</c> can open a session into a field that <c>After</c> then saves — that is what
/// makes batched save-once work.</para>
///
/// <para>Columns bind to <c>Row</c> parameters by header name with type conversion; a parameter
/// whose type no cell can produce is injected from the scenario's DI scope instead. When
/// <c>Row</c> returns a value and exactly one column is left unbound, that column is treated as
/// the <b>expected</b> output and compared per row — a decision table.</para>
///
/// <para>Failure semantics: a throw from <c>Before</c> is critical (the scenario aborts and rows
/// are skipped, but <c>After</c> still runs as cleanup); per-row comparison failures gather and
/// render the full table; <c>SpecCatastrophicException</c> stops the suite. <c>After</c> always
/// runs, in a <c>finally</c>.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class TableGrammarAttribute : Attribute
{
    /// <summary>The Gherkin step text, in Cucumber Expression syntax.</summary>
    public string Expression { get; }

    public TableGrammarAttribute(string expression) => Expression = expression;
}

/// <summary>
/// Overrides the naming convention to mark the once-before method of a
/// <see cref="TableGrammarAttribute"/> class.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class BeforeAttribute : Attribute { }

/// <summary>
/// Overrides the naming convention to mark the per-row method of a
/// <see cref="TableGrammarAttribute"/> class.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class RowAttribute : Attribute { }

/// <summary>
/// Overrides the naming convention to mark the once-after method of a
/// <see cref="TableGrammarAttribute"/> class. Always runs, even when a row threw.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class AfterAttribute : Attribute { }
