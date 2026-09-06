namespace Bobcat.CritterStack;

/// <summary>
/// The base fixture for HTTP-driven Critter Stack slices — issue #210, built as issue #212's
/// success measure demands: an <em>assembly</em> of existing pieces, not a third hand-written
/// vocabulary. It is <see cref="CritterStackFixture"/> (the whole store-agnostic vocabulary, the
/// canonical base-class route) plus <see cref="HttpGrammars"/> composed as a module (the mix-in
/// route), with the tracked capture flowing between them over the scenario-state contract. It
/// declares no steps of its own — that thinness is the point.
/// </summary>
/// <remarks>
/// <para>
/// Derive from it and a collapsed endpoint slice — the endpoint <em>is</em> the handler,
/// appending in one transaction — stays in the closed, compile-checked vocabulary:
/// </para>
/// <code>
/// [IncludeGrammars(typeof(HttpGrammars), "/api/wallet")]   // optional: re-parameterize with a route prefix
/// public class CreditWallet : CritterStackHttpFixture;
/// </code>
/// <code language="gherkin">
/// Given no events for Wallet "…"
/// When CreditWallet is posted to "/credit"
///   | WalletId | Amount |
///   | …        | 25     |
/// Then the response is 200
/// And WalletCredited is emitted
/// And the WalletSummary read model contains
///   | Balance |
///   | 25      |
/// </code>
/// <para>
/// Re-declaring <c>[IncludeGrammars(typeof(HttpGrammars), …)]</c> on the derived fixture is how a
/// route prefix (or a named host resource) is bound — the most-derived declaration wins (issue
/// #212 phase 2). The suite must register a test resource implementing
/// <see cref="Runtime.IHttpResource"/> to carry the calls; <c>Bobcat.Alba</c>'s
/// <c>AlbaResource</c> does.
/// </para>
/// </remarks>
[IncludeGrammars(typeof(HttpGrammars))]
public abstract class CritterStackHttpFixture : CritterStackFixture;
