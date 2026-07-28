using Spike.Orchestrator;

// Probe harness for issue #43. Runs the same battery of experiments against every MTP host
// given on the command line and prints a per-question verdict table.
var hosts = args.Where(a => !a.StartsWith("--")).ToList();
var verbose = args.Contains("--verbose");

if (hosts.Count == 0)
{
    Console.WriteLine("usage: Spike.Orchestrator <path-to-mtp-host> [more hosts…] [--verbose]");
    return 1;
}

var report = new Report();

foreach (var host in hosts)
{
    var name = Path.GetFileName(host);
    Console.WriteLine();
    Console.WriteLine(new string('=', 78));
    Console.WriteLine("  " + name);
    Console.WriteLine(new string('=', 78));

    var experiments = new Experiments(host, name, verbose, report);

    await experiments.DiscoverAndRunEverything();
    await experiments.UidsAreStableAcrossProcesses();
    await experiments.SelectiveRerunOfOneTest();
    await experiments.RunOneTestAloneInAFreshProcess();
    await experiments.TraitsSurviveTheWire();
    await experiments.HostCrashMidRun();
    await experiments.CancellationOfARunningTest();
    await experiments.StartupCost();
}

report.Print();
return 0;
