namespace Bobcat.TUnit.Tests.Filtering;

// Same method name on two classes, and a sibling method in the same class, so a filter that
// matches by method name alone, or by class alone, runs more than one test and is caught.
// Each test records its name when BOBCAT_TUNIT_PROBE names a file, which is how the filter probe
// sees exactly what a filtered child run executed.
public class Alpha
{
    [Test]
    public async Task one() => await Probe.Record("Alpha.one");

    [Test]
    public async Task two() => await Probe.Record("Alpha.two");
}

public class Beta
{
    [Test]
    public async Task one() => await Probe.Record("Beta.one");
}

internal static class Probe
{
    private static readonly Lock _gate = new();

    // Locked: TUnit runs tests in parallel, and unsynchronised appends lose lines.
    public static Task Record(string name)
    {
        var path = Environment.GetEnvironmentVariable("BOBCAT_TUNIT_PROBE");
        if (path is null) return Task.CompletedTask;
        lock (_gate) File.AppendAllText(path, name + Environment.NewLine);
        return Task.CompletedTask;
    }
}

// Edge shapes a spec identity can take: a data-driven test (one method, several cases) and a
// nested class (CLR name Outer+Inner).
public class Gamma
{
    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task with_arguments(int n) => await Probe.Record($"Gamma.with_arguments({n})");
}

public class Outer
{
    public class Inner
    {
        [Test]
        public async Task one() => await Probe.Record("Outer.Inner.one");
    }
}
