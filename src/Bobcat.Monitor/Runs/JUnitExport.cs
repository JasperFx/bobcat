using System.Globalization;
using System.Xml.Linq;

namespace Bobcat.Monitor.Runs;

/// <summary>
/// The lossy compatibility floor: JUnit XML, natively ingested by GitLab, Jenkins, Azure
/// DevOps, and CircleCI. Retry history and step detail deliberately do not travel here —
/// that's CTRF's job; this exists so the monitor's output drops into any CI system unmodified.
/// </summary>
public static class JUnitExport
{
    public static string Render(RunProjection run)
    {
        var features = run.Scenarios
            .OrderBy(s => s.Feature).ThenBy(s => s.Scenario)
            .GroupBy(s => s.Feature)
            .ToArray();

        var suites = new XElement("testsuites",
            new XAttribute("name", run.Suite),
            new XAttribute("tests", run.Scenarios.Count),
            new XAttribute("failures", run.Scenarios.Count(s => s.Outcome == "Failed")),
            new XAttribute("errors", run.Scenarios.Count(s => s.Outcome == "Aborted")),
            features.Select(feature => new XElement("testsuite",
                new XAttribute("name", feature.Key),
                new XAttribute("tests", feature.Count()),
                new XAttribute("failures", feature.Count(s => s.Outcome == "Failed")),
                new XAttribute("errors", feature.Count(s => s.Outcome == "Aborted")),
                feature.Select(scenario =>
                {
                    var testcase = new XElement("testcase",
                        new XAttribute("classname", scenario.Feature),
                        new XAttribute("name", scenario.Scenario),
                        new XAttribute("time",
                            ((scenario.DurationMs ?? 0) / 1000.0).ToString("0.###", CultureInfo.InvariantCulture)));

                    switch (scenario.Outcome)
                    {
                        case "Failed":
                            testcase.Add(new XElement("failure",
                                new XAttribute("message", scenario.ErrorMessage ?? "failed")));
                            break;
                        case "Aborted":
                            testcase.Add(new XElement("error",
                                new XAttribute("message", scenario.ErrorMessage ?? "aborted")));
                            break;
                        case null:
                            // No terminal outcome — exported mid-run or the publisher died.
                            testcase.Add(new XElement("skipped",
                                new XAttribute("message", "no terminal result reported")));
                            break;
                    }

                    return testcase;
                }))));

        return new XDocument(new XDeclaration("1.0", "utf-8", null), suites).ToString();
    }
}
