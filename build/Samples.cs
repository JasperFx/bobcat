using System.Collections.Generic;
using System.Linq;
using Npgsql;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

// What samples.yml runs, as `./build.sh Samples`.
//
// The samples are not in bobcat.slnx, so the CI target never touches them. That gap once let nine
// projects rot undetected, and compiling was never enough to call a sample fixed: OutboxDemo
// compiled clean and could not start (Wolverine 6 stopped shipping the runtime compiler), and half
// the samples sat on WolverineFx 6.29.1 / Marten 9.28.0 and died on a TypeLoadException the first
// time they opened a session — for weeks, invisibly, because a build was all CI did. So these
// targets build every sample project AND run the specs of every runnable one.
//
// The samples are Microsoft.Testing.Platform hosts, so `dotnet test` runs them and one scenario is
// one test node. They need Postgres and nothing else.
partial class Build
{
    AbsolutePath SamplesDirectory => RootDirectory / "samples";

    [Parameter("Postgres the sample specs run against, without a database name - each sample gets its own")]
    readonly string SamplesPostgres = "Host=localhost;Port=5445;Username=postgres;Password=postgres";

    // Sample projects that do not compile today, named individually rather than skipped as a group.
    // Started at nine; empty again as of 2026-09-21. Two rules, and the second is the one that
    // matters:
    //
    //   * a project outside this list that fails  -> new rot, fail the build
    //   * a project inside this list that BUILDS  -> the list is stale, fail the build
    //
    // A quarantine that only ever forgives is how a permanently-red job becomes background noise.
    // This one has to shrink: fix a sample, and the build tells you to delete its line. Paths are
    // relative to the repository root, e.g. "samples/OutboxDemo/Tests/Tests.csproj".
    static readonly string[] QuarantinedSamples =
    [
    ];

    // Each runnable sample and the database it uses, matching the default in its own Program.cs.
    // Separate databases rather than one shared: MeetingGroupMonolith puts its event store in a
    // schema called `payments`, which is also the schema PaymentsMonolith uses for everything, so a
    // shared database would have each one's between-scenario reset wiping the other's events.
    //
    // BankAccountES runs its Marten leg here and its Fisher leg in TestSamplesOnFisher, which is the
    // point of that sample: the same handlers against two stores.
    static readonly (string Sample, string Database)[] RunnableSamples =
    [
        ("CqrsMinimalApi", "cqrs_minimal_api"),
        ("BookingMonolith", "booking"),
        ("CleanArchitectureTodos", "clean_architecture_todos"),
        ("MoreSpeakers", "more_speakers"),
        ("PaymentsMonolith", "inflow"),
        ("EcommerceModularMonolith", "ecommerce"),
        ("MeetingGroupMonolith", "meeting_groups"),
        ("OutboxDemo", "outbox_demo"),
        ("BankAccountES", "bank_account"),
    ];

    Target CompileSamples => _ => _
        .Executes(() =>
        {
            var regressions = new List<string>();
            var recovered = new List<string>();

            foreach (var project in SamplesDirectory.GlobFiles("**/*.csproj").OrderBy(p => p.ToString()))
            {
                var name = RootDirectory.GetRelativePathTo(project).ToString().Replace('\\', '/');
                var quarantined = QuarantinedSamples.Contains(name);

                if (tryRun(() => DotNetBuild(s => s.SetProjectFile(project).SetConfiguration(Configuration))))
                {
                    if (quarantined)
                    {
                        Log.Error("{Project} builds now - remove it from QuarantinedSamples", name);
                        recovered.Add(name);
                    }
                }
                else if (quarantined)
                {
                    Log.Warning("known broken (quarantined): {Project}", name);
                }
                else
                {
                    Log.Error("{Project} failed to build", name);
                    regressions.Add(name);
                }
            }

            Log.Information("quarantined: {Count}", QuarantinedSamples.Length);
            Assert.Empty(regressions, $"Sample(s) newly broken: {string.Join(", ", regressions)}");
            Assert.Empty(recovered, $"Quarantined sample(s) now build - shrink the list: {string.Join(", ", recovered)}");
        });

    Target TestSamples => _ => _
        .DependsOn(CompileSamples)
        .After(Docker)
        .Executes(() =>
        {
            // Created up front because a CI service container only ever makes one database.
            // Tolerates one that already exists, so a local rerun works.
            using (var connection = new NpgsqlConnection($"{SamplesPostgres};Database=postgres"))
            {
                connection.Open();
                foreach (var (_, database) in RunnableSamples)
                {
                    using var exists = new NpgsqlCommand("select 1 from pg_database where datname = @name", connection);
                    exists.Parameters.AddWithValue("name", database);
                    if (exists.ExecuteScalar() != null) continue;

                    using var create = new NpgsqlCommand($"create database \"{database}\"", connection);
                    create.ExecuteNonQuery();
                }
            }

            var failed = new List<string>();
            foreach (var (sample, database) in RunnableSamples)
            {
                Log.Information("Running the {Sample} specs", sample);
                var passed = tryRun(() => DotNetTest(s => s
                    .SetProjectFile(SamplesDirectory / sample / "Tests")
                    .SetConfiguration(Configuration)
                    .SetProcessEnvironmentVariable("ConnectionStrings__Marten", $"{SamplesPostgres};Database={database}")));

                if (!passed) failed.Add(sample);
            }

            Assert.Empty(failed, $"Sample(s) failed their specs: {string.Join(", ", failed)}");
        });

    // No services at all - a SQLite file is the whole store, which is the argument for Fisher as the
    // inner-loop store, and a sample whose point is "the same handlers run on two stores" is only
    // proved by running both.
    Target TestSamplesOnFisher => _ => _
        .DependsOn(CompileSamples)
        .After(TestSamples)
        .Executes(() =>
        {
            var database = TemporaryDirectory / "bank_account.db";
            database.DeleteFile();

            DotNetTest(s => s
                .SetProjectFile(SamplesDirectory / "BankAccountES" / "Tests")
                .SetConfiguration(Configuration)
                .SetProcessEnvironmentVariable("EventStore", "Fisher")
                .SetProcessEnvironmentVariable("ConnectionStrings__Fisher", $"Data Source={database}"));
        });

    // What samples.yml runs.
    Target Samples => _ => _
        .DependsOn(CompileSamples, TestSamples, TestSamplesOnFisher);

    static bool tryRun(System.Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (ProcessException)
        {
            return false;
        }
    }
}
