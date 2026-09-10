using Alba;
using Bobcat.Alba;
using Bobcat.Engine;
using JasperFx.CommandLine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Bobcat.Runtime;

/// <summary>
/// Diagnostics for the Alba host bootstrap footguns documented in docs/sample-wiring.md.
/// </summary>
public static class AlbaResourceDiagnostics
{
    /// <summary>
    /// Turn a content-root failure out of WebApplicationFactory — a bare
    /// <see cref="DirectoryNotFoundException"/> when the root it guessed is not there, or the
    /// <see cref="InvalidOperationException"/> it throws when no solution file is above the test
    /// output — into a clear, actionable configuration error. Other exceptions pass through
    /// unchanged.
    /// </summary>
    public static Exception WrapStartException(Exception ex, string program, string? contentRoot = null)
        => IsContentRootFailure(ex)
            ? new BobcatConfigurationException(ContentRootHelp(program, contentRoot, ex), ex)
            : ex;

    public static bool IsContentRootFailure(Exception ex)
        => ex is DirectoryNotFoundException
           || (ex is InvalidOperationException && ex.Message.StartsWith("Solution root could not be located", StringComparison.Ordinal));

    /// <summary>
    /// Guidance for a content-root failure. <paramref name="contentRoot"/> is Bobcat's own
    /// resolution when it made one (<see cref="AlbaResource{TProgram}"/>); it is null for the
    /// factory-delegate <see cref="AlbaResource"/>, which never sees a <c>TProgram</c> and so
    /// leaves the resolution to WebApplicationFactory. <paramref name="failure"/> is the exception
    /// itself, read for the path it names.
    /// </summary>
    public static string ContentRootHelp(string program, string? contentRoot = null, Exception? failure = null)
    {
        var used = contentRoot == null ? "" : $" Bobcat resolved it to: {contentRoot}.";
        var suspect = failure == null ? "" : DescribeSuspectRoot(failure);

        var fix = contentRoot == null
            // The delegate form: the host is built by the caller's own lambda, so that lambda is
            // where the root gets pinned. Naming AlbaResource<TProgram>.WithContentRoot here would
            // be advice for a type this caller is not using.
            ? "Fix: call .UseContentRoot(path) on the builder inside the factory you passed to " +
              "AlbaResource, or use AlbaResource<TProgram>, which resolves the root itself"
            : "Fix: call AlbaResource<TProgram>.WithContentRoot(path)";

        return $"Alba could not resolve the content root while starting host '{program}'.{used}{suspect} " +
               "Bobcat resolves it from MvcTestingAppManifest.json in the test output, then " +
               "[assembly: WebApplicationFactoryContentRoot(...)], then the project directory below the solution, " +
               "then the test output directory itself — so reaching this usually means no solution file is above the " +
               $"test output, or the directory it found is not the one the host wants. {fix}, or add " +
               "[assembly: WebApplicationFactoryContentRoot(\"<HostAssemblyName>\", \"<relative path from the test output>\", \"appsettings.json\", \"1\")] " +
               "to the test assembly. See docs/sample-wiring.md footgun 2.";
    }

    /// <summary>
    /// Read the path out of the failure and say what is wrong with it — issue #274 asked for
    /// exactly this one line, because the whole error was a bare path and the reader had to guess.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The doubled tail is worth calling out by name because it has one cause and one cure.
    /// WebApplicationFactory's last-resort fallback is <c>&lt;solution dir&gt;/&lt;assembly
    /// name&gt;</c>, taken <em>unchecked</em> — so a repository whose solution file sits in a
    /// directory named after the project (<c>ShipmentTracking/ShipmentTracking.sln</c> beside
    /// <c>ShipmentTracking/*.csproj</c>, a completely ordinary layout) resolves to
    /// <c>…/ShipmentTracking/ShipmentTracking</c>, which does not exist. Reading that path, the
    /// name appears twice and looks like a Bobcat bug; it is the documented guess, landing badly.
    /// </para>
    /// <para>
    /// Deliberately descriptive, never corrective. Bobcat does not rewrite the path — a directory
    /// that happens to nest a same-named folder is legal, and silently retargeting someone's
    /// content root is a worse failure than the one being explained.
    /// </para>
    /// </remarks>
    public static string DescribeSuspectRoot(Exception failure)
    {
        var path = ExtractPath(failure.Message);
        if (path == null) return "";

        var trimmed = path.TrimEnd('/', '\\');
        var leaf = Path.GetFileName(trimmed);
        var parent = Path.GetFileName(Path.GetDirectoryName(trimmed) ?? "");

        var doubled = leaf.Length > 0 && string.Equals(leaf, parent, StringComparison.OrdinalIgnoreCase)
            ? $" The last two segments repeat ('{parent}/{leaf}'), which is the shape of " +
              "WebApplicationFactory's unchecked <solution directory>/<assembly name> fallback " +
              "landing in a repository whose solution file sits in a directory named after the project."
            : "";

        return $" The content root it tried was '{path}', which does not exist.{doubled}";
    }

    /// <summary>
    /// The path named by a WebApplicationFactory content-root failure. Both messages that reach
    /// here quote one, and neither is structured, so this reads it back out — returning null
    /// rather than guessing when the shape is unfamiliar.
    /// </summary>
    public static string? ExtractPath(string message)
    {
        // DirectoryNotFoundException from the factory is the path and nothing else; the
        // solution-root InvalidOperationException quotes it after "application root ".
        const string marker = "application root ";
        var index = message.IndexOf(marker, StringComparison.Ordinal);
        if (index >= 0)
        {
            var rest = message[(index + marker.Length)..].Trim().TrimEnd('.');
            return rest.Length > 0 ? rest : null;
        }

        var candidate = message.Trim();
        return candidate.Length > 0 && (candidate[0] == '/' || candidate.Contains(":\\", StringComparison.Ordinal))
               && !candidate.Contains(' ')
            ? candidate
            : null;
    }
}

/// <summary>
/// A test resource that wraps an Alba IAlbaHost built from a user-provided factory.
/// Use this when you want full control over IHostBuilder construction rather than
/// bootstrapping from a TProgram entry point.
/// </summary>
public class AlbaResource : IHostResource, IAlbaResource, IRestartableResource, IHttpResource
{
    private readonly Func<Task<IAlbaHost>> _factory;
    private readonly Func<IAlbaHost, Task>? _reset;
    private readonly ScenarioScope _scope;
    private IAlbaHost? _albaHost;

    /// <summary>
    /// The underlying IAlbaHost. Use this for Scenario() calls and Alba-specific APIs.
    /// </summary>
    public IAlbaHost AlbaHost => _albaHost
        ?? throw new InvalidOperationException($"AlbaResource '{Name}' has not been started.");

    /// <summary>
    /// IHostResource.Host — IAlbaHost extends IHost, returned directly.
    /// </summary>
    public IHost Host => AlbaHost;

    public IServiceProvider RootServices => _scope.Root;
    public IServiceProvider CurrentServices => _scope.Current;

    public string Name { get; }

    public AlbaResource(Func<Task<IAlbaHost>> factory, string? name = null, Func<IAlbaHost, Task>? reset = null)
    {
        _factory = factory;
        Name = name ?? "AlbaHost";
        _reset = reset;
        _scope = new ScenarioScope(Name, () => _albaHost?.Services);
    }

    public AlbaResource(Func<IAlbaHost> factory, string? name = null, Func<IAlbaHost, Task>? reset = null)
        : this(() => Task.FromResult(factory()), name, reset)
    {
    }

    /// <summary>
    /// Prepare the process for bootstrapping a <c>Program.Main</c> that ends in JasperFx's
    /// <c>RunJasperFxCommands</c> — every Wolverine and Marten application's does. Idempotent;
    /// called by both resources' <c>Start</c>, public so a bare <c>AlbaHost.For&lt;T&gt;</c>
    /// outside any resource can ask for the same preparation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>JasperFxEnvironment.AutoStartHost = true</c> is what JasperFx itself documents for
    /// WebApplicationFactory testing. Under the factory, the entry point runs on a background
    /// thread with the factory's synthesized arguments (<c>--environment=Development
    /// --contentRoot=… --applicationName=…</c>), reaches <c>RunJasperFxCommands</c>, and the
    /// command runner parses a command line that was never meant for it. With the flag on,
    /// JasperFx starts the already-built host before parsing anything (its run command then
    /// skips the redundant start) and tolerates the flags it does not own; without it, the host
    /// is left to a race between the factory's start and the run command's, and the usage graph
    /// treats the factory's flags as a usage error on hosts with commands of their own.
    /// </para>
    /// <para>
    /// <strong>It is a process-wide static</strong>, and this sets it unconditionally and never
    /// sets it back — deliberately. The flag only changes behaviour for a command line run against
    /// an <em>already built</em> <c>IHost</c>, which in a test process is always one that
    /// WebApplicationFactory is driving; and the flag is read on the entry point's thread at a
    /// moment Bobcat cannot see, so "set it for the duration of Start" would be a race wearing a
    /// scope's clothing. A test process that genuinely needs it off can clear it after the hosts
    /// have started.
    /// </para>
    /// </remarks>
    public static void PrepareJasperFxHosting()
    {
        if (!JasperFxEnvironment.AutoStartHost) JasperFxEnvironment.AutoStartHost = true;
    }

    public async Task Start()
    {
        PrepareJasperFxHosting();
        try
        {
            _albaHost = await _factory();
        }
        catch (Exception ex)
        {
            // Issue #274. This form builds the host from the caller's own factory, so Bobcat never
            // sees a TProgram and cannot resolve the content root for it — WebApplicationFactory's
            // own guessing applies, including the unchecked <solution>/<assembly> fallback. What it
            // CAN do is explain the failure instead of passing a bare path up, which is all the
            // reader got: "Resource 'AlbaHost' failed to start: …/ShipmentTracking/ShipmentTracking/".
            // Only content-root failures are wrapped; everything else surfaces unchanged.
            throw AlbaResourceDiagnostics.WrapStartException(ex, Name);
        }
    }

    /// <inheritdoc cref="IRestartableResource.Restart"/>
    public async Task Restart(CancellationToken token = default)
    {
        var old = _albaHost ?? throw new InvalidOperationException($"AlbaResource '{Name}' has not been started.");

        // The scenario scope belongs to the old container; it has to go before the container does,
        // and come back on the new one so CurrentServices keeps working for the rest of the scenario.
        var scopeWasOpen = _scope.IsOpen;
        await _scope.End();

        _albaHost = null;
        await old.DisposeAsync();

        _albaHost = await _factory();

        if (scopeWasOpen) await _scope.Begin();
    }

    public async Task ResetBetweenScenarios()
    {
        if (_reset != null)
            await _reset(_albaHost!);
    }

    public ValueTask BeginScenarioScope() => _scope.Begin();

    public ValueTask EndScenarioScope() => _scope.End();

    /// <inheritdoc cref="IHttpResource.SendAsync"/>
    public Task<SpecHttpResponse> SendAsync(SpecHttpRequest request, CancellationToken cancellation = default)
        => AlbaHttpTransport.SendAsync(AlbaHost, request);

    public async ValueTask DisposeAsync()
    {
        await _scope.End();

        if (_albaHost != null)
            await _albaHost.DisposeAsync();
    }
}

/// <summary>
/// A test resource that wraps an Alba IAlbaHost for ASP.NET Core integration testing.
/// Uses AlbaHost.For&lt;TProgram&gt; to bootstrap the host from a TProgram entry point —
/// the same pattern used by WebApplicationFactory. TProgram must be a class in an
/// executable assembly that builds an IHost (typically a Program or Startup class).
/// Implements IHostResource so that Wolverine, Marten, and other extensions can locate
/// the host without knowing the specific resource type.
/// </summary>
public class AlbaResource<TProgram> : IHostResource, IAlbaResource, IRestartableResource, IHttpResource where TProgram : class
{
    private readonly Action<IWebHostBuilder>? _configure;
    private readonly IAlbaExtension[] _extensions;
    private readonly Func<IAlbaHost, Task>? _reset;
    private readonly ScenarioScope _scope;
    private IAlbaHost? _albaHost;
    private string? _contentRoot;

    /// <summary>
    /// The underlying IAlbaHost. Use this for Scenario() calls and Alba-specific APIs.
    /// </summary>
    public IAlbaHost AlbaHost => _albaHost
        ?? throw new InvalidOperationException($"AlbaResource '{Name}' has not been started.");

    /// <summary>
    /// IHostResource.Host — IAlbaHost extends IHost, returned directly.
    /// </summary>
    public IHost Host => AlbaHost;

    public IServiceProvider RootServices => _scope.Root;
    public IServiceProvider CurrentServices => _scope.Current;

    public string Name { get; }

    public AlbaResource(string? name = null, Action<IWebHostBuilder>? configure = null,
        Func<IAlbaHost, Task>? reset = null, params IAlbaExtension[] extensions)
    {
        Name = name ?? typeof(TProgram).Name;
        _configure = configure;
        _reset = reset;
        _extensions = extensions;
        _scope = new ScenarioScope(Name, () => _albaHost?.Services);
    }

    /// <summary>
    /// Set the host content root explicitly, bypassing discovery altogether. Rarely needed now:
    /// without it the root is resolved by <see cref="AlbaContentRoot"/> (manifest in the test
    /// output, <c>[WebApplicationFactoryContentRoot]</c>, the project directory below the
    /// solution, then the test output directory), which covers sibling, <c>src/</c>, <c>samples/</c>
    /// and nested-Tests layouts alike. Use this when the host wants a directory none of those are.
    /// </summary>
    public AlbaResource<TProgram> WithContentRoot(string contentRoot)
    {
        _contentRoot = contentRoot;
        return this;
    }

    /// <summary>
    /// How the content root was (or will be) decided for this resource — explicit
    /// <see cref="WithContentRoot"/>, or the outcome of <see cref="AlbaContentRoot.Resolve(System.Reflection.Assembly)"/>.
    /// Evaluated on each start, so it reflects the files present at that moment.
    /// </summary>
    public AlbaContentRoot.Resolution ContentRoot => _contentRoot != null
        ? new AlbaContentRoot.Resolution(_contentRoot, "WithContentRoot")
        : AlbaContentRoot.Resolve(typeof(TProgram).Assembly);

    /// The floor Bobcat puts under the hosted application's <em>console</em> logging. Default
    /// <see cref="LogLevel.Warning"/>: a test process's console belongs to the test runner, and an
    /// ASP.NET Core host at its own default <c>Information</c> writes several lines per request —
    /// under Microsoft.Testing.Platform that buries the run summary in request traces. Set to
    /// <c>null</c> to leave the application's logging configuration exactly as it ships.
    /// </summary>
    /// <remarks>
    /// Applied as a filter <em>rule</em> scoped to the console logger provider, not as
    /// <c>SetMinimumLevel</c>: an <c>appsettings.json</c> <c>"Logging:LogLevel:Default"</c> becomes
    /// a rule too, and rules beat the minimum level, so <c>SetMinimumLevel(Warning)</c> silences
    /// nothing on a host that ships with <c>"Default": "Information"</c>. The rule is added before
    /// the user's <c>configure</c> callback runs, so a rule the user adds there for the console
    /// wins (later rule, same specificity), and a category-specific rule from the application's
    /// own configuration wins as it always did (more specific). Other providers — the debug
    /// provider, <c>BobcatLoggerProvider</c>'s per-step capture, a user's Serilog — are untouched.
    /// </remarks>
    public LogLevel? ConsoleLogLevel { get; set; } = LogLevel.Warning;

    /// <summary>Fluent form of <see cref="ConsoleLogLevel"/>.</summary>
    public AlbaResource<TProgram> WithConsoleLogLevel(LogLevel? level)
    {
        ConsoleLogLevel = level;
        return this;
    }

    public async Task Start()
    {
        _albaHost = await boot();
    }

    /// <inheritdoc cref="IRestartableResource.Restart"/>
    /// <remarks>
    /// Boots the new host exactly as <see cref="Start"/> did — same <c>configure</c> callback,
    /// same extensions, same content root — so a restarted application is the same application
    /// with a fresh container, not a differently configured one.
    /// </remarks>
    public async Task Restart(CancellationToken token = default)
    {
        var old = _albaHost ?? throw new InvalidOperationException($"AlbaResource '{Name}' has not been started.");

        // The scenario scope belongs to the old container; it has to go before the container does,
        // and come back on the new one so CurrentServices keeps working for the rest of the scenario.
        var scopeWasOpen = _scope.IsOpen;
        await _scope.End();

        _albaHost = null;
        await old.DisposeAsync();

        _albaHost = await boot();

        if (scopeWasOpen) await _scope.Begin();
    }

    private async Task<IAlbaHost> boot()
    {
        AlbaResource.PrepareJasperFxHosting();

        var contentRoot = ContentRoot;
        var configure = composeConfigure(contentRoot.Path);
        try
        {
            return configure != null
                ? await global::Alba.AlbaHost.For<TProgram>(configure, _extensions)
                : await global::Alba.AlbaHost.For<TProgram>(_extensions);
        }
        catch (Exception ex)
        {
            throw AlbaResourceDiagnostics.WrapStartException(ex, typeof(TProgram).Name, contentRoot.ToString());
        }
    }

    private Action<IWebHostBuilder>? composeConfigure(string? contentRoot)
    {
        if (contentRoot == null && ConsoleLogLevel == null) return _configure;

        var consoleLogLevel = ConsoleLogLevel;
        var userConfigure = _configure;
        return builder =>
        {
            // WebApplicationFactory sets its own guess first and our callback runs after it, so
            // this is the value the host builds with. The user's configure runs last and may
            // still override it.
            if (contentRoot != null) builder.UseContentRoot(contentRoot);
            if (consoleLogLevel != null)
            {
                builder.ConfigureLogging(logging =>
                    logging.AddFilter<ConsoleLoggerProvider>((string?)null, consoleLogLevel.Value));
            }

            userConfigure?.Invoke(builder);
        };
    }

    public async Task ResetBetweenScenarios()
    {
        if (_reset != null)
            await _reset(_albaHost!);
    }

    public ValueTask BeginScenarioScope() => _scope.Begin();

    public ValueTask EndScenarioScope() => _scope.End();

    /// <inheritdoc cref="IHttpResource.SendAsync"/>
    public Task<SpecHttpResponse> SendAsync(SpecHttpRequest request, CancellationToken cancellation = default)
        => AlbaHttpTransport.SendAsync(AlbaHost, request);

    public async ValueTask DisposeAsync()
    {
        await _scope.End();

        if (_albaHost != null)
            await _albaHost.DisposeAsync();
    }
}
