namespace Bobcat.CritterStack;

/// <summary>
/// The shipped Critter Stack grammar as a <b>mix-in module</b>: compose it into a fixture that
/// cannot derive from <see cref="CritterStackFixture"/> (it already has another base, or it mixes
/// several grammars) with <c>[IncludeGrammars(typeof(CritterStackGrammars))]</c>. It inherits every
/// grammar step and typed method from <see cref="CritterStackFixture"/> and adds nothing — the
/// generator discovers a module's inherited steps the same way it discovers a fixture's (issue #104),
/// so the module route and the base-class route bind exactly the same vocabulary.
/// </summary>
/// <remarks>
/// <b>The base-class route is canonical</b> — <c>public class WithdrawFunds : CritterStackFixture</c>
/// reads as "this fixture <i>is</i> a Critter Stack fixture" and needs no attribute. Reach for this
/// module only when a fixture must keep a different base type; see CLAUDE.md, "Shipped grammar modules".
/// A module is instantiated once per scenario and, because it inherits <see cref="Fixture"/>, receives
/// the step context — so its typed steps resolve the store and tracked session just as the base-class
/// route does.
/// </remarks>
public sealed class CritterStackGrammars : CritterStackFixture;
