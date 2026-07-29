using JasperFx.Environment;

namespace Bobcat.Runtime;

/// <summary>
/// Validates the harness environment once, before anything runs, so a broken environment aborts
/// in seconds instead of after thousands of identical failures.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no Bobcat <c>IEnvironmentCheck</c>.</strong> JasperFx already owns this
/// concept — <see cref="EnvironmentCheckResults"/>, <see cref="EnvironmentChecker"/>,
/// <see cref="EnvironmentCheckException"/> — and it collects checks from
/// <c>JasperFx.Resources.IStatefulResource.Check</c>, <c>ISystemPart.AssertEnvironmentAsync</c>
/// and Microsoft's <c>IHealthCheck</c>. Inventing a parallel interface would have meant Critter
/// Stack users writing their checks twice.
/// </para>
/// <para>
/// The contract everywhere is "throw to fail": a check that returns has passed.
/// </para>
/// </remarks>
public sealed class Preflight
{
    private readonly List<(string Description, Func<CancellationToken, Task> Check)> _checks = new();

    /// <summary>
    /// Adds an ad-hoc check. Equivalent to JasperFx's <c>LambdaCheck</c>, minus the
    /// <c>IServiceProvider</c> that a supervisor running outside any host does not have.
    /// </summary>
    public Preflight Add(string description, Func<CancellationToken, Task> check)
    {
        _checks.Add((description, check));
        return this;
    }

    public Preflight Add(string description, Action check)
        => Add(description, _ => { check(); return Task.CompletedTask; });

    public bool IsEmpty => _checks.Count == 0;

    /// <summary>
    /// Runs every check, gathering results rather than stopping at the first failure — the point
    /// of a preflight is to tell you everything that is wrong in one go.
    /// </summary>
    public async Task<EnvironmentCheckResults> Run(CancellationToken token = default)
    {
        var results = new EnvironmentCheckResults();

        foreach (var (description, check) in _checks)
        {
            try
            {
                await check(token);
                results.RegisterSuccess(description);
            }
            catch (Exception e)
            {
                results.RegisterFailure(description, e);
            }
        }

        return results;
    }

    /// <summary>
    /// Adds a check per registered resource, calling <see cref="ITestResource.Check"/>.
    /// </summary>
    public Preflight AddResourceChecks(IEnumerable<ITestResource> resources)
    {
        foreach (var resource in resources)
        {
            var captured = resource;
            Add($"Resource '{captured.Name}'", token => captured.Check(token));
        }

        return this;
    }

    /// <summary>
    /// Adds every check the container knows about — <c>ISystemPart</c>, <c>IStatefulResource</c>
    /// and <c>IHealthCheck</c> — by delegating to JasperFx's own executor.
    /// </summary>
    public Preflight AddContainerChecks(string description, IServiceProvider services)
        => Add(description, async token =>
        {
            var results = await EnvironmentChecker.ExecuteAllEnvironmentChecks(services, token);
            results.Assert(); // throws EnvironmentCheckException listing every failure
        });

    /// <summary>A short, readable account of a failed preflight.</summary>
    public static string Describe(EnvironmentCheckResults results)
    {
        var lines = results.Failures
            .Select(f => $"  ✗ {f.Description}: {f.Exception.Message}")
            .ToList();

        return $"Environment preflight failed ({lines.Count} of " +
               $"{lines.Count + results.Successes.Length} checks):{Environment.NewLine}" +
               string.Join(Environment.NewLine, lines);
    }
}
