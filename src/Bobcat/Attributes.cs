using System.Text.RegularExpressions;

namespace Bobcat;

/// <summary>
/// Names the Event Model this assembly's specs contribute slices to. Without it the generated
/// <c>BobcatEventModelSource</c> names its model after the spec assembly — and upstream,
/// <c>EventModelDiscovery.Assemble</c> folds descriptors together <b>by model name</b>, so a spec
/// assembly called <c>BankAccountES.Tests</c> can never merge with the Wolverine-derived model,
/// which is named for the <i>service</i> (<c>opts.ServiceName</c>). Set this to the service name
/// and the specification slices land on the same model as the chains they describe
/// (issue #172).
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class EventModelNameAttribute : Attribute
{
    public string Name { get; }
    public EventModelNameAttribute(string name) => Name = name;
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
/// <b>One family.</b> This is also the base class of <see cref="GivenAttribute"/>,
/// <see cref="WhenAttribute"/>, <see cref="ThenAttribute"/> and <see cref="CheckAttribute"/>: a method
/// carrying any of them is intercepted at the call site when an ordinary xUnit, TUnit, NUnit or MSTest
/// test calls it, and records itself as a step.
/// </para>
/// <para>
/// <b>Keywordless is a real choice, not a default.</b> Storyteller sentences and Gauge steps read as
/// prose, and "Multiply by 3 then add 4" is not a Given, a When or a Then.
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
    /// <b>Which one is in force is decided per PLACEHOLDER, not per expression and not per
    /// project:</b> a built-in type word wins, so <c>{int}</c> stays a Cucumber capture even on a
    /// method with a parameter called <c>int</c> and nothing that compiled before means anything
    /// different; a placeholder that is not a built-in word and does name a parameter is the named
    /// form. An expression with no placeholders reads identically either way, and <b>mixing the two
    /// is allowed</b> — <c>"the {aggregate} has {count} events"</c> is a natural thing to write and
    /// there is no ambiguity in it, because each placeholder is resolved on its own.
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
/// <b>On a parameter</b> it names the column the parameter binds to — a row method
/// <c>TableRunner.Run</c> calls. <b>On a property</b> it names the column a
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

