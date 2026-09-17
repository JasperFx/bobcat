using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Events.EventModeling;

namespace Bobcat.Monitoring;

/// <summary>
/// Publishes a spec assembly's half of the Event Model to the console the run is already
/// attached to — issue #294, the missing producer behind CritterWatch#1212.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The two halves of an Event Model are compiled into different
/// assemblies: Wolverine's <c>event-model</c> export runs against the HOST and carries slices
/// with no <c>Specifications</c>, while the generator's <c>BobcatEventModelSource</c> carries
/// the <c>Specifications</c> — the spec identities a run outcome joins onto — and is invisible
/// to the host, because the reference points the other way. Issue #268 taught the console to
/// merge per source so the two can coexist, but <em>nothing published the spec half</em>: on a
/// real application the merged model existed only if a human pushed it by hand, and a consumer
/// like CritterWatch saw every slice as "no specification bound". The runner is the one process
/// that holds the generated source and already talks to the console, so it is the one that
/// should push.
/// </para>
/// <para>
/// <b>The same invariant <see cref="MonitorPublisher"/> lives under: a test run is never slowed
/// or failed by the monitor.</b> Nothing here runs unless a console already answered
/// <c>/api/ping</c>; the whole exchange is bounded by <see cref="Ceiling"/> and abandoned when it
/// expires; every failure — transport, timeout, a refusing console, a spec assembly whose
/// generated source will not load — is swallowed. The only thing that ever reaches the run is a
/// line of text, and only for the one case a human has to act on (see the model-name rule below).
/// </para>
/// <para>
/// <b>The source name is the spec assembly's, sanitized.</b> A re-run must REPLACE its own
/// contribution rather than accumulate, which is exactly what #268's per-source wire gives for a
/// stable name. It cannot be the assembly name verbatim: the console turns a source into the file
/// name <c>event-model.{source}.json</c> and refuses any source containing '.', so
/// <c>BankAccountES.Tests</c> would come back as a 400 every single run. Dots (and anything else
/// a file name will not take) become '-'.
/// </para>
/// <para>
/// <b>The model-name rule, and why a mismatch refuses to publish.</b> <c>GET /api/event-model</c>
/// merges only the sources carrying the CURRENT model name, and the current name is whatever was
/// pushed last. So a spec assembly pushing <c>BankAccountES.Tests</c> at a console already
/// serving <c>BankAccountES</c> would not join the host's half — it would HIDE it, and silently,
/// trading one broken picture for another. Issue #294 names this as the thing nothing checks;
/// this checks it. When the console already serves a differently-named model we publish nothing
/// and say why, because the fix is one line in the spec assembly
/// (<c>[assembly: EventModelName("…")]</c>, issue #172) and it belongs to the author, not to us.
/// </para>
/// <para>
/// <b>What is deliberately NOT here: the host half.</b> A runner cannot export the host's chains
/// without referencing Wolverine, which Bobcat's core will not do, and the host half already has
/// a producer — <c>event-model --url</c>, wrapped by <c>bobcat watch-event-model</c>. This closes
/// the half that had none.
/// </para>
/// </remarks>
internal static class SpecEventModelPublisher
{
    /// <summary>
    /// The one type the generator emits per spec assembly. The lookup itself is
    /// <see cref="GeneratedEventModel"/>, shared with issue #338's identity audit.
    /// </summary>
    internal const string GeneratedSourceTypeName = GeneratedEventModel.GeneratedSourceTypeName;

    /// <summary>The route both halves of the Event Model wire are published on (issue #268).</summary>
    internal const string Route = "/api/event-model";

    /// <summary>
    /// The wire shape the console's <c>EventModelStore</c> reads and every consumer of
    /// <c>@jasperfx/event-model-vue</c> renders: camelCase members, PascalCase enum values.
    /// </summary>
    internal static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(namingPolicy: null) }
    };

    /// <summary>
    /// The hard ceiling on the whole exchange — one GET plus one PUT per spec assembly.
    /// </summary>
    /// <remarks>
    /// Paid once, at the start of a run, and only when a console answered the ping a moment
    /// earlier — which is to say only when a developer is watching one on localhost, which is
    /// exactly when they want the model to be there. Two seconds is far more than a small JSON
    /// document over loopback needs and small enough that a console that has wedged since the
    /// ping cannot hold a suite up.
    /// </remarks>
    internal static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Publish the Event Model half of every spec assembly contributing to this run. Returns the
    /// source names actually published, which is what the tests assert on; callers ignore it.
    /// </summary>
    /// <param name="publisher">A publisher whose console has already answered the ping.</param>
    /// <param name="assemblies">
    /// The assemblies the run's features were compiled into. The descriptor is a compile-time
    /// fact about the WHOLE assembly, so a filtered run still publishes the whole half — a
    /// <c>--tag</c> narrows what executes, never what the model declares.
    /// </param>
    /// <param name="notify">
    /// Where the one actionable message goes; null to say nothing. The runner routes it to
    /// <c>Console.WriteLine</c> behind <c>SuppressConsoleOutput</c>, which means a
    /// <c>bobcat run</c> shows it and an MTP host does not — under MTP the platform owns stdout,
    /// and the same gate already silences preflight failures there.
    /// </param>
    internal static Task<IReadOnlyList<string>> PublishAll(
        MonitorPublisher publisher, IEnumerable<Assembly> assemblies, Action<string>? notify = null)
        => PublishAll(publisher, Discover(assemblies), notify);

    /// <summary>
    /// The same publish over halves already discovered — the seam the policy tests drive, so the
    /// model-name rule and the rejection path can be exercised without a spec assembly whose
    /// generated source happens to say the right thing.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> PublishAll(
        MonitorPublisher publisher, IReadOnlyList<Half> halves, Action<string>? notify = null)
    {
        if (halves.Count == 0) return [];

        var published = new List<string>();

        using var cts = new CancellationTokenSource(Ceiling);

        // Read the console's current model name ONCE. Every half in this run is asked the same
        // question, and asking per assembly would also read back a name our own first push had
        // just set.
        var current = await publisher.CurrentEventModelName(cts.Token);

        foreach (var half in halves)
        {
            if (current is not null && !string.Equals(current, half.Descriptor.Name, StringComparison.Ordinal))
            {
                notify?.Invoke(
                    $"Bobcat did not publish the Event Model half of '{half.Assembly}': this console is "
                    + $"serving a model named '{current}' and the half is named '{half.Descriptor.Name}'. "
                    + $"{Route} merges only the sources naming the CURRENT model, so publishing this one "
                    + "would hide the other half rather than join it. Name them alike with "
                    + $"[assembly: EventModelName(\"{current}\")] on {half.Assembly}.");
                continue;
            }

            var json = JsonSerializer.Serialize(half.Descriptor, Wire);
            var push = await publisher.PublishEventModel(half.Source, json, cts.Token);

            if (push.Published)
            {
                published.Add(half.Source);

                // An empty console has no name to collide with, so the first half through the
                // door names the model — and every half after it in THIS run is then held to the
                // same rule the console would hold it to on the next one. Without this, a run
                // spanning two differently-named spec assemblies would end with the second having
                // hidden the first.
                current ??= half.Descriptor.Name;
            }
            else if (push.Refusal is not null)
            {
                notify?.Invoke($"Bobcat could not publish the Event Model half of '{half.Assembly}': {push.Refusal}");
            }
        }

        return published;
    }

    /// <summary>One spec assembly's contribution: what to push, and the source name to push it as.</summary>
    internal sealed record Half(string Assembly, string Source, EventModelDescriptor Descriptor);

    /// <summary>
    /// The generated Event Model half of each assembly that has one. An assembly whose specs
    /// declare no slices has no generated source at all (the generator emits it only when
    /// <c>slices.Count > 0</c>), and a descriptor that came back empty or unnamed is skipped —
    /// pushing one would claim the console's current model name for a document saying nothing.
    /// </summary>
    internal static IReadOnlyList<Half> Discover(IEnumerable<Assembly> assemblies)
    {
        var halves = new List<Half>();
        var seen = new HashSet<Assembly>();

        foreach (var assembly in assemblies)
        {
            if (!seen.Add(assembly)) continue;

            var descriptor = describe(assembly);
            if (descriptor is null || descriptor.Slices.Count == 0) continue;
            if (string.IsNullOrWhiteSpace(descriptor.Name)) continue;

            var name = assembly.GetName().Name ?? "specs";
            halves.Add(new Half(name, SourceNameFor(name), descriptor));
        }

        return halves;
    }

    /// <summary>
    /// The assembly's generated descriptor, or null when it has none — or when reading it
    /// failed, because a spec assembly that will not give up its model is not a reason to disturb
    /// a run. That swallow is this caller's, not <see cref="GeneratedEventModel"/>'s: the same
    /// failure has to be loud for an audit, which would otherwise report every declared scenario
    /// as uncovered.
    /// </summary>
    private static EventModelDescriptor? describe(Assembly assembly)
    {
        try
        {
            return GeneratedEventModel.For(assembly);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A source name the console will accept for this assembly: letters, digits, '-' and '_'.
    /// </summary>
    /// <remarks>
    /// <c>EventModelStore.TryStore</c> refuses anything else, '.' included, because the source
    /// becomes the <c>event-model.{source}.json</c> file name. Assembly names are dotted almost
    /// by definition, so without this every push from a real spec assembly is a 400 — and a
    /// silent one, since a failed push never reaches the run.
    /// </remarks>
    internal static string SourceNameFor(string assemblyName)
    {
        var chars = assemblyName
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')
            .ToArray();

        var source = new string(chars).Trim('-');
        return source.Length == 0 ? "specs" : source;
    }
}
