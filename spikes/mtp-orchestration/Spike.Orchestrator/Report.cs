namespace Spike.Orchestrator;

public enum Verdict { Yes, No, Partial }

public record Finding(string Host, string Question, string Experiment, Verdict Verdict, string Detail);

/// <summary>Collects each experiment's answer so the findings note is written from evidence.</summary>
public sealed class Report
{
    private readonly List<Finding> _findings = new();

    public void Add(string host, string question, string experiment, Verdict verdict, string detail)
    {
        _findings.Add(new Finding(host, question, experiment, verdict, detail));
        Console.WriteLine($"  [{Symbol(verdict)}] {experiment}: {detail}");
    }

    private static string Symbol(Verdict v) => v switch
    {
        Verdict.Yes => "PASS",
        Verdict.No => "FAIL",
        _ => "PART"
    };

    public void Print()
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 78));
        Console.WriteLine("  SUMMARY");
        Console.WriteLine(new string('=', 78));

        foreach (var group in _findings.GroupBy(f => f.Question).OrderBy(g => g.Key))
        {
            Console.WriteLine();
            Console.WriteLine(group.Key);
            foreach (var finding in group)
                Console.WriteLine($"  {Symbol(finding.Verdict),-4} {finding.Host,-22} {finding.Detail}");
        }

        Console.WriteLine();
        var failures = _findings.Count(f => f.Verdict == Verdict.No);
        var partial = _findings.Count(f => f.Verdict == Verdict.Partial);
        Console.WriteLine($"{_findings.Count} findings — {_findings.Count - failures - partial} pass, {partial} partial, {failures} fail");
    }
}
