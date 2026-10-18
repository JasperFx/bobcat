namespace Bobcat.Runtime;

/// <summary>
/// Validates the harness environment once, before anything runs, so a broken environment aborts
/// in seconds instead of after thousands of identical failures.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no Bobcat <c>IEnvironmentCheck</c>.</strong> A check is a delegate that throws to
/// fail, and <see cref="ITestResource.Check"/> mirrors JasperFx's <c>IStatefulResource.Check</c> by
/// shape, so one resource can satisfy both. The results are Bobcat's own <see cref="PreflightResults"/>
/// since bobcat#744, when core stopped referencing JasperFx; the checks JasperFx collects from a
/// container (<c>AddContainerChecks</c>) moved to WolverineFx.Bobcat.
/// </para>
/// <para>
/// The contract everywhere is "throw to fail": a check that returns has passed.
/// </para>
/// </remarks>
public sealed class Preflight
{
    private readonly List<(string Description, Func<CancellationToken, Task> Check)> _checks = new();

    /// <summary>
    /// Adds an ad-hoc check: a delegate that throws to fail. No <c>IServiceProvider</c>, because a
    /// supervisor running outside any host does not have one.
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
    public async Task<PreflightResults> Run(CancellationToken token = default)
    {
        var results = new PreflightResults();

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

    /// <summary>A short, readable account of a failed preflight.</summary>
    public static string Describe(PreflightResults results)
    {
        var lines = results.Failures
            .Select(f => $"  ✗ {f.Description}: {f.Exception.Message}")
            .ToList();

        return $"Environment preflight failed ({lines.Count} of " +
               $"{lines.Count + results.Successes.Length} checks):{Environment.NewLine}" +
               string.Join(Environment.NewLine, lines);
    }
}
