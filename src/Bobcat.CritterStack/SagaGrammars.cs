using System.Reflection;
using System.Text.Json;
using Bobcat.Engine;
using Bobcat.Runtime;
using JasperFx.Descriptors;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;

namespace Bobcat.CritterStack;

/// <summary>
/// The saga lane of the shipped Critter Stack vocabulary (issue #281): assert that a Wolverine
/// saga is active and what state it holds, or that no saga of a type exists for an id.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately two steps, and no "is complete".</b> A saga that completes is deleted by every
/// Wolverine saga storage (one that completes inside its start handler is never inserted at all),
/// so "complete" and "never started" are the same row in storage: none. A step claiming completion
/// from that would pass for a saga that never ran — the spec-that-cannot-fail #273 was about. What
/// storage <em>can</em> say is said plainly: <c>is active</c>, and <c>no … exists</c>. A scenario
/// that shows the saga active and then shows it gone reads as completion without the step having
/// to infer it. Evidence-gated completion was considered and deferred (2026-09-10).
/// </para>
/// <para>
/// <b>No arrange.</b> Sagas start the way the application starts them — a Wolverine message
/// (<c>When {command} is received</c>) or an HTTP call (<c>When {command} is posted to …</c>) — so
/// there is no <c>Given</c> here. Wolverine's saga surface is read-only across stores, and a
/// store-specific write path would be a second way to arrange state the real handlers never produce.
/// </para>
/// <para>
/// <b>Store-agnostic.</b> Everything goes through <c>IWolverineRuntime.SagaStorage</c>, the one
/// read-only view Wolverine aggregates over every registered saga storage — Marten, Polecat, Fisher,
/// EF Core, RavenDB and the RDBMS providers — so this package still references none of them.
/// </para>
/// <para>
/// <b>An unknown saga type is refused, never read as absent.</b> The storage view answers null both
/// for "no instance" and for "no storage owns this type", so a saga whose handler was never
/// discovered, or whose persistence was never configured, would make <c>no … exists</c> pass
/// vacuously. Each step first checks the type is registered and names the ones that are.
/// </para>
/// </remarks>
public class SagaGrammars : Fixture
{
    private readonly string? _hostResource;

    public SagaGrammars(string? hostResource = null)
    {
        _hostResource = hostResource;
    }

    private IStepContext Ctx => Context ?? throw new InvalidOperationException(
        "No IStepContext is set on the grammar module — a saga grammar step ran outside a scenario.");

    /// <summary>
    /// Assert a saga of the named type is stored under this id and not completed, and — when a
    /// table is given — that its state matches <b>only the columns the row names</b>, the rule
    /// issue #241 set for events and #270 followed for documents.
    /// </summary>
    [Then("the {saga} with id {string} is active")]
    public async Task ThenTheSagaWithIdIsActive(Type saga, string id, StepTable? expected)
    {
        if (expected is { Rows.Count: > 1 })
            throw new SpecCriticalException(
                $"'Then the {saga.Name} with id \"{id}\" is active' describes one saga, so it takes at most "
                + $"one table row of its state, but got {expected.Rows.Count}.");

        var state = await readAsync(saga, id);
        Ctx.RecordTouchedType(saga);

        if (state == null)
            throw new SpecAssertionException(
                $"Expected an active {saga.Name} with id '{id}', but none is stored. A saga is deleted when it "
                + "completes, so it may have finished already — or it never started.");

        if (state.IsCompleted)
            throw new SpecAssertionException(
                $"Expected an active {saga.Name} with id '{id}', but the stored one is marked completed.");

        if (expected is not { Rows.Count: 1 }) return;

        var failures = new List<string>();
        foreach (var (column, value) in expected.AsDictionaries()[0])
        {
            var property = saga.GetProperty(column,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null)
            {
                failures.Add($"{column}: no such property on {saga.Name}");
                continue;
            }

            if (!tryGetStateValue(state.State, property.Name, out var element))
            {
                failures.Add($"{column}: not present in the stored {saga.Name} state");
                continue;
            }

            var actual = element.Deserialize(property.PropertyType);
            var expectedValue = GherkinValue.Convert(value, property.PropertyType);
            if (!Equals(actual, expectedValue))
                failures.Add($"{column}: expected {value}, was {actual}");
        }

        if (failures.Count > 0)
            throw new SpecAssertionException(
                $"{saga.Name} '{id}' did not match: {string.Join("; ", failures)}");
    }

    /// <summary>
    /// Assert no saga of the named type is stored under this id — the saga that finished, or the
    /// one a refused message never started. Says nothing about which: storage cannot tell them apart.
    /// </summary>
    [Then("no {saga} exists with id {string}")]
    public async Task ThenNoSagaExistsWithId(Type saga, string id)
    {
        var state = await readAsync(saga, id);
        Ctx.RecordTouchedType(saga);

        if (state != null)
            throw new SpecAssertionException(
                $"Expected no {saga.Name} with id '{id}', but one is stored: {state.State}");
    }

    private async Task<SagaInstanceState?> readAsync(Type saga, string id)
    {
        var storage = Ctx.GetResource<IHostResource>(_hostResource).RootServices
            .GetRequiredService<IWolverineRuntime>().SagaStorage;

        var registered = await storage.GetRegisteredSagasAsync(Ctx.Cancellation);
        if (!registered.Any(x => x.StateType.FullName == saga.FullName))
        {
            var known = registered.Count == 0
                ? "none"
                : string.Join(", ", registered.Select(x => x.StateType.FullName));
            throw new SpecCriticalException(
                $"No saga storage in this host knows {saga.FullName}, so reading it would prove nothing — "
                + "an unknown type and a missing saga look the same. Is its handler discovered, and is saga "
                + $"persistence configured (e.g. IntegrateWithWolverine())? Registered sagas: {known}.");
        }

        return await storage.ReadSagaAsync(saga.FullName!, identityOf(saga, id), Ctx.Cancellation);
    }

    /// <summary>
    /// The id as the saga's own <c>Id</c> type, so no provider has to guess at a string. Falls back
    /// to the string when the saga declares no <c>Id</c> property.
    /// </summary>
    private static object identityOf(Type saga, string id)
    {
        var idType = saga.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?.PropertyType;
        return idType == null ? id : GherkinValue.Convert(id, idType) ?? id;
    }

    private static bool tryGetStateValue(JsonElement state, string name, out JsonElement value)
    {
        if (state.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in state.EnumerateObject())
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
