using Bobcat.Engine;

namespace Bobcat.CritterStack;

/// <summary>
/// The document-store lane of the shipped Critter Stack vocabulary (issue #270): arrange documents,
/// assert one document's state, assert a document's absence — for the ordinary Wolverine
/// application that is <b>not</b> event sourced.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes.</b> Measured on a real document-backed sample — Wolverine + a document
/// store + Alba, <c>Storage.Insert</c>, <c>[Entity]</c>, a revisioned document — exactly four of the
/// ten shipped steps applied, and all four were the messaging and HTTP halves. Nothing in the
/// vocabulary could arrange a document or assert one, so the project had to write a private grammar
/// before it could write its first scenario. That is an adoption cliff, and it was invisible from
/// the event-sourced samples because they never reach it.
/// </para>
/// <para>
/// <b>Composition, not a third monolith.</b> This is a module, dropped in beside whatever else a
/// fixture carries: <c>[IncludeGrammars(typeof(DocumentGrammars))]</c>. A document-backed app
/// composes it with <see cref="HttpGrammars"/> and gets a full vocabulary — arrange documents, act
/// over HTTP or the bus, assert the response, the messages sent, and the documents written — with
/// no event store anywhere. It derives from <see cref="Fixture"/> rather than
/// <see cref="CritterStackFixture"/> deliberately: deriving from the latter would duplicate every
/// store step into BOBCAT013 ambiguity, the same reason <see cref="HttpGrammars"/> does not.
/// </para>
/// <para>
/// <b>Store-agnostic.</b> Everything goes through <see cref="DocumentStores"/> and the
/// <c>JasperFx.Events.Documents</c> abstractions, so this package still references no Marten, no
/// Polecat and no Fisher.
/// </para>
/// </remarks>
public class DocumentGrammars : Fixture
{
    private readonly string? _hostResource;
    private readonly string? _storeName;

    public DocumentGrammars(string? hostResource = null, string? storeName = null)
    {
        _hostResource = hostResource;
        _storeName = storeName;
    }

    private IStepContext Ctx => Context ?? throw new InvalidOperationException(
        "No IStepContext is set on the grammar module — a document grammar step ran outside a scenario.");

    /// <summary>
    /// Arrange: every row becomes one document of the named type, stored and committed before the
    /// act runs.
    /// </summary>
    /// <remarks>
    /// Partial by the same rule the event arrange follows (issue #241): a <c>Given</c> names the
    /// fields the behaviour under test depends on, and the rest of a wide document is not part of
    /// the scenario. A column matching nothing is still refused by name, because that is a typo or
    /// a field renamed since the spec was written.
    /// </remarks>
    [Given("documents of type {document}")]
    public async Task GivenDocumentsOfType(Type document, StepTable rows)
    {
        var documents = RecordBuilding.BuildAll(
            document, rows, $"Given documents of type {document.Name}", partial: true);

        await DocumentStores.StoreAllAsync(
            Ctx.EventStore(_hostResource, _storeName), document, documents, Ctx.Cancellation);

        Ctx.RecordTouchedType(document);
    }

    /// <summary>
    /// Assert the state of one document, comparing <b>only the columns the row names</b> — the
    /// rule issue #241 established for events, followed here rather than inventing a second
    /// convention. A document has fields the scenario does not care about, and demanding a column
    /// for each makes the table say things the scenario does not mean.
    /// </summary>
    [Then("the {document} with id {string} has")]
    public async Task ThenTheDocumentWithIdHas(Type document, string id, StepTable expected)
    {
        var identity = DocumentStores.IdentityOf(document, id);
        var stored = await DocumentStores.LoadAsync(
            Ctx.EventStore(_hostResource, _storeName), document, identity, Ctx.Cancellation);

        if (stored == null)
            throw new SpecAssertionException(
                $"Expected a {document.Name} document with id '{id}', but none exists.");

        Ctx.RecordTouchedType(document);

        var row = expected.AsDictionaries().FirstOrDefault()
                  ?? throw new SpecCriticalException(
                      $"'Then the {document.Name} with id \"{id}\" has' needs at least one table row.");

        var failures = new List<string>();
        foreach (var (column, value) in row)
        {
            var property = document.GetProperty(column);
            if (property == null)
            {
                failures.Add($"{column}: no such property on {document.Name}");
                continue;
            }

            var actual = property.GetValue(stored);
            var expectedValue = GherkinValue.Convert(value, property.PropertyType);
            if (!Equals(actual, expectedValue))
                failures.Add($"{column}: expected {value}, was {actual}");
        }

        if (failures.Count > 0)
            throw new SpecAssertionException(
                $"{document.Name} document '{id}' did not match: {string.Join("; ", failures)}");
    }

    /// <summary>
    /// Assert no document of the named type has this id — the deletion and refusal case, and the
    /// counterpart of <see cref="ThenTheDocumentWithIdHas"/>.
    /// </summary>
    [Then("no {document} exists with id {string}")]
    public async Task ThenNoDocumentExistsWithId(Type document, string id)
    {
        var identity = DocumentStores.IdentityOf(document, id);
        var stored = await DocumentStores.LoadAsync(
            Ctx.EventStore(_hostResource, _storeName), document, identity, Ctx.Cancellation);

        Ctx.RecordTouchedType(document);

        if (stored != null)
            throw new SpecAssertionException(
                $"Expected no {document.Name} document with id '{id}', but one exists: {stored}.");
    }
}
