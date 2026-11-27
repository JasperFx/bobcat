using System.Text.Json;
using Bobcat.Ui.Contracts;
using Bobcat.Ui.Runs;
using Json.Schema;
using Shouldly;

namespace Bobcat.Ui.Tests;

/// <summary>
/// bobcat#848: a run exported as Cucumber Messages is an envelope stream the Cucumber-family reporters
/// can read, so every line is validated against the protocol's own schema
/// (<c>Fixtures/cucumber-messages/messages.schema.json</c>, release 35.0.0, vendored with its provenance
/// in the README beside it) — not against what this export happens to write.
/// </summary>
public class CucumberMessagesExportTests
{
    private static readonly Guid runId = Guid.Parse("7d3e1f4a-6b2c-4c8e-9a1d-2f3b4c5d6e7f");
    private static readonly DateTimeOffset t0 = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    // JsonSchema.Net registers the schema's $id once; a second FromFile of the same file is refused
    private static readonly Lazy<JsonSchema> schema = new(() =>
        JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cucumber-messages", "messages.schema.json")));

    private static RunProjection run()
    {
        var run = new RunProjection(runId);
        var events = new MonitorEvent[]
        {
            new RunStarted(runId, "Wallet.Specs", "/repo", "feature/wallets", "in-process", t0, 4, Tag: "plan-node-17"),

            // A clean pass with cells on its Then and a log line on its When
            new ScenarioStarted(runId, "Wallet/credit a wallet", "Wallet", "credit a wallet", 1, t0.AddSeconds(1)),
            new StepStarted(runId, "Wallet/credit a wallet", "s1", "Given", "a wallet with balance 10"),
            new StepFinished(runId, "Wallet/credit a wallet", "s1", "Passed", 5, null),
            new StepStarted(runId, "Wallet/credit a wallet", "s2", "When", "the wallet is credited 5"),
            new StepFinished(runId, "Wallet/credit a wallet", "s2", "Passed", 20, null, Logs: ["posting CreditWallet"]),
            new StepStarted(runId, "Wallet/credit a wallet", "s3", "Then", "the balance is 15"),
            new StepFinished(runId, "Wallet/credit a wallet", "s3", "Passed", 2, null,
                Cells: [new StepCell("Balance", "ok", "15", "15")]),
            new ScenarioFinished(runId, "Wallet/credit a wallet", "CleanPass", 1, 27, null, At: t0.AddSeconds(2),
                Reports: [new ScenarioReportInfo("Events appended", ["Type", "Stream"], [new StepCell("Type", "value", Value: "WalletCredited"), new StepCell("Stream", "value", Value: "wallet-1")])]),

            // A failure, with the disagreeing cell and the exception
            new ScenarioStarted(runId, "Wallet/debit an empty wallet", "Wallet", "debit an empty wallet", 1, t0.AddSeconds(3)),
            new StepStarted(runId, "Wallet/debit an empty wallet", "s1", "When", "the wallet is debited 5"),
            new StepFinished(runId, "Wallet/debit an empty wallet", "s1", "Failed", 7, "expected a refusal",
                Cells: [new StepCell("Refused", "failed", "true", "false")]),
            new ScenarioFinished(runId, "Wallet/debit an empty wallet", "Failed", 1, 7, "expected a refusal", At: t0.AddSeconds(4)),

            // A pass on retry: the first attempt's steps are archived by the RetryScheduled
            new ScenarioStarted(runId, "Ledger/settle the day", "Ledger", "settle the day", 1, t0.AddSeconds(5)),
            new StepStarted(runId, "Ledger/settle the day", "s1", "When", "the day is settled"),
            new StepFinished(runId, "Ledger/settle the day", "s1", "Failed", 100, "broker not ready"),
            new RetryScheduled(runId, "Ledger/settle the day", 2, "RetryInProcess", "slow broker"),
            new ScenarioStarted(runId, "Ledger/settle the day", "Ledger", "settle the day", 2, t0.AddSeconds(6)),
            new StepStarted(runId, "Ledger/settle the day", "s1", "When", "the day is settled"),
            new StepFinished(runId, "Ledger/settle the day", "s1", "Passed", 30, null),
            new ScenarioFinished(runId, "Ledger/settle the day", "PassOnRetry", 2, 30, null, At: t0.AddSeconds(7)),

            // A forwarded foreign test: a verdict and no steps at all (issue #195)
            new ScenarioStarted(runId, "Ledger/nightly batch", "Ledger", "nightly batch", 1, t0.AddSeconds(8)),
            new ScenarioFinished(runId, "Ledger/nightly batch", "Failed", 1, 12, "Shouldly.ShouldAssertException: expected 7", At: t0.AddSeconds(9)),

            new RunFinished(runId, 1, 2, 2, 1, 0, t0.AddSeconds(10)),
        };

        foreach (var e in events) run.Apply(e, e is RunStarted ? t0 : t0.AddSeconds(10));
        return run;
    }

    private static List<JsonElement> envelopes(string ndjson)
        => ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement).ToList();

    private static JsonElement one(JsonElement envelope) => envelope.EnumerateObject().Single().Value;

    private static IEnumerable<JsonElement> ofKind(List<JsonElement> all, string kind)
        => all.Where(e => e.TryGetProperty(kind, out _)).Select(e => e.GetProperty(kind));

    [Fact]
    public void every_envelope_validates_against_the_cucumber_messages_schema()
    {
        var all = envelopes(CucumberMessagesExport.Render(run()));

        all.Count.ShouldBeGreaterThan(20);
        foreach (var envelope in all)
        {
            envelope.EnumerateObject().Count().ShouldBe(1, "an envelope has exactly one field set");
            var result = schema.Value.Evaluate(envelope, new EvaluationOptions { OutputFormat = OutputFormat.List });
            result.IsValid.ShouldBeTrue(envelope.GetRawText() + "\n" + string.Join("\n",
                result.Details.Where(x => x.Errors is { Count: > 0 }).Select(x => $"{x.InstanceLocation}: {string.Join(", ", x.Errors!.Values)}")));
        }
    }

    [Fact]
    public void the_stream_opens_with_meta_and_the_run_tag_rides_the_git_block()
    {
        var all = envelopes(CucumberMessagesExport.Render(run()));

        var meta = one(all[0]);
        all[0].EnumerateObject().Single().Name.ShouldBe("meta");
        meta.GetProperty("protocolVersion").GetString().ShouldBe(CucumberMessagesExport.ProtocolVersion);
        meta.GetProperty("implementation").GetProperty("name").GetString().ShouldBe("bobcat");

        var git = meta.GetProperty("ci").GetProperty("git");
        git.GetProperty("tag").GetString().ShouldBe("plan-node-17");
        git.GetProperty("branch").GetString().ShouldBe("feature/wallets");
        git.GetProperty("remote").GetString().ShouldBe("/repo");
        meta.GetProperty("ci").GetProperty("buildNumber").GetString().ShouldBe(runId.ToString());
    }

    [Fact]
    public void one_feature_per_bobcat_feature_one_scenario_per_spec_and_the_steps_from_the_rendered_text()
    {
        var all = envelopes(CucumberMessagesExport.Render(run()));

        ofKind(all, "source").Select(s => s.GetProperty("uri").GetString()).ShouldBe(["bobcat://Ledger.feature", "bobcat://Wallet.feature"]);
        ofKind(all, "gherkinDocument").Count().ShouldBe(2);
        ofKind(all, "pickle").Select(p => p.GetProperty("name").GetString()).ShouldBe(["nightly batch", "settle the day", "credit a wallet", "debit an empty wallet"]);

        var wallet = ofKind(all, "source").Single(s => s.GetProperty("uri").GetString() == "bobcat://Wallet.feature").GetProperty("data").GetString()!;
        wallet.ShouldBe(
            """
            Feature: Wallet

              Scenario: credit a wallet
                Given a wallet with balance 10
                When the wallet is credited 5
                Then the balance is 15

              Scenario: debit an empty wallet
                When the wallet is debited 5

            """.ReplaceLineEndings("\n"));

        // Every location points at the line of the text it describes
        var document = ofKind(all, "gherkinDocument").Single(d => d.GetProperty("uri").GetString() == "bobcat://Wallet.feature");
        var lines = wallet.Split('\n');
        foreach (var child in document.GetProperty("feature").GetProperty("children").EnumerateArray())
        {
            var scenario = child.GetProperty("scenario");
            lines[scenario.GetProperty("location").GetProperty("line").GetInt32() - 1].ShouldContain("Scenario: " + scenario.GetProperty("name").GetString());
            foreach (var step in scenario.GetProperty("steps").EnumerateArray())
            {
                lines[step.GetProperty("location").GetProperty("line").GetInt32() - 1].Trim()
                    .ShouldBe(step.GetProperty("keyword").GetString() + step.GetProperty("text").GetString());
            }
        }

        // A pickle step points at its AST step, and a test step at its pickle step
        var pickle = ofKind(all, "pickle").Single(p => p.GetProperty("name").GetString() == "credit a wallet");
        var astIds = document.GetProperty("feature").GetProperty("children").EnumerateArray()
            .SelectMany(c => c.GetProperty("scenario").GetProperty("steps").EnumerateArray()).Select(s => s.GetProperty("id").GetString()).ToList();
        pickle.GetProperty("steps").EnumerateArray().ShouldAllBe(s => astIds.Contains(s.GetProperty("astNodeIds")[0].GetString()));
        pickle.GetProperty("steps")[0].GetProperty("type").GetString().ShouldBe("Context");

        var testCase = ofKind(all, "testCase").Single(t => t.GetProperty("pickleId").GetString() == pickle.GetProperty("id").GetString());
        var pickleStepIds = pickle.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToList();
        testCase.GetProperty("testSteps").EnumerateArray().Select(s => s.GetProperty("pickleStepId").GetString()).ShouldBe(pickleStepIds);
    }

    [Fact]
    public void verdicts_durations_and_the_failure_message_ride_the_test_step_results()
    {
        var all = envelopes(CucumberMessagesExport.Render(run()));

        var results = ofKind(all, "testStepFinished").Select(f => f.GetProperty("testStepResult")).ToList();
        results.Count(r => r.GetProperty("status").GetString() == "PASSED").ShouldBe(4);

        var failed = results.Where(r => r.GetProperty("status").GetString() == "FAILED").ToList();
        // In stream order: features and scenarios sort by name, so Ledger's come before Wallet's
        failed.Select(r => r.GetProperty("message").GetString()).ShouldBe(["Shouldly.ShouldAssertException: expected 7", "broker not ready", "expected a refusal"]);

        var debit = ofKind(all, "testStepFinished").Single(f => f.GetProperty("testStepResult").TryGetProperty("message", out var m) && m.GetString() == "expected a refusal");
        debit.GetProperty("testStepResult").GetProperty("duration").GetProperty("nanos").GetInt64().ShouldBe(7_000_000);

        // The run's own clock: the run started at t0, the test run finished when the run did
        one(all.Single(e => e.TryGetProperty("testRunStarted", out _))).GetProperty("timestamp").GetProperty("seconds").GetInt64().ShouldBe(t0.ToUnixTimeSeconds());
        var finished = one(all[^1]);
        all[^1].EnumerateObject().Single().Name.ShouldBe("testRunFinished");
        finished.GetProperty("success").GetBoolean().ShouldBeFalse();
        finished.GetProperty("timestamp").GetProperty("seconds").GetInt64().ShouldBe(t0.AddSeconds(10).ToUnixTimeSeconds());
    }

    [Fact]
    public void a_retried_scenario_is_two_attempts_the_first_marked_as_retried()
    {
        var all = envelopes(CucumberMessagesExport.Render(run()));

        var settle = ofKind(all, "pickle").Single(p => p.GetProperty("name").GetString() == "settle the day").GetProperty("id").GetString();
        var testCaseId = ofKind(all, "testCase").Single(t => t.GetProperty("pickleId").GetString() == settle).GetProperty("id").GetString();
        var starts = ofKind(all, "testCaseStarted").Where(s => s.GetProperty("testCaseId").GetString() == testCaseId).ToList();

        starts.Select(s => s.GetProperty("attempt").GetInt32()).ShouldBe([0, 1]);
        var finishes = starts.Select(s => ofKind(all, "testCaseFinished").Single(f => f.GetProperty("testCaseStartedId").GetString() == s.GetProperty("id").GetString())).ToList();
        finishes.Select(f => f.GetProperty("willBeRetried").GetBoolean()).ShouldBe([true, false]);

        var statuses = starts.Select(s => ofKind(all, "testStepFinished").Single(f => f.GetProperty("testCaseStartedId").GetString() == s.GetProperty("id").GetString())
            .GetProperty("testStepResult").GetProperty("status").GetString()).ToList();
        statuses.ShouldBe(["FAILED", "PASSED"]);
    }

    [Fact]
    public void cells_logs_and_reports_are_attachments_on_their_step_or_test_case()
    {
        var all = envelopes(CucumberMessagesExport.Render(run()));
        var attachments = ofKind(all, "attachment").ToList();

        var cells = attachments.Where(a => a.GetProperty("fileName").GetString() == "cells.json").ToList();
        cells.Count.ShouldBe(2);
        cells.All(a => a.TryGetProperty("testStepId", out var step) && step.ValueKind == JsonValueKind.String && a.GetProperty("mediaType").GetString() == "application/json").ShouldBeTrue();
        var balance = JsonDocument.Parse(cells[0].GetProperty("body").GetString()!).RootElement.GetProperty("cells")[0];
        balance.GetProperty("name").GetString().ShouldBe("Balance");
        balance.GetProperty("expected").GetString().ShouldBe("15");

        var log = attachments.Single(a => a.GetProperty("fileName").GetString() == "log.txt");
        log.GetProperty("mediaType").GetString().ShouldBe("text/plain");
        log.GetProperty("body").GetString().ShouldBe("posting CreditWallet");

        var report = attachments.Single(a => a.GetProperty("fileName").GetString() == "Events-appended.json");
        report.TryGetProperty("testStepId", out _).ShouldBeFalse("a report is the scenario's, not a step's");
        var body = JsonDocument.Parse(report.GetProperty("body").GetString()!).RootElement;
        body.GetProperty("title").GetString().ShouldBe("Events appended");
        body.GetProperty("cells")[0].GetProperty("value").GetString().ShouldBe("WalletCredited");
    }

    [Fact]
    public void a_scenario_with_no_steps_gets_one_synthetic_step_carrying_its_verdict()
    {
        var all = envelopes(CucumberMessagesExport.Render(run()));

        var ledger = ofKind(all, "source").Single(s => s.GetProperty("uri").GetString() == "bobcat://Ledger.feature").GetProperty("data").GetString()!;
        ledger.ShouldContain("  Scenario: nightly batch\n    * nightly batch\n");

        var pickle = ofKind(all, "pickle").Single(p => p.GetProperty("name").GetString() == "nightly batch");
        pickle.GetProperty("steps").GetArrayLength().ShouldBe(1);
        pickle.GetProperty("steps")[0].GetProperty("type").GetString().ShouldBe("Unknown");

        var testCaseId = ofKind(all, "testCase").Single(t => t.GetProperty("pickleId").GetString() == pickle.GetProperty("id").GetString()).GetProperty("id").GetString();
        var started = ofKind(all, "testCaseStarted").Single(s => s.GetProperty("testCaseId").GetString() == testCaseId).GetProperty("id").GetString();
        var result = ofKind(all, "testStepFinished").Single(f => f.GetProperty("testCaseStartedId").GetString() == started).GetProperty("testStepResult");
        result.GetProperty("status").GetString().ShouldBe("FAILED");
        result.GetProperty("message").GetString().ShouldBe("Shouldly.ShouldAssertException: expected 7");
        result.GetProperty("duration").GetProperty("nanos").GetInt64().ShouldBe(12_000_000);
    }

    [Fact]
    public void the_export_is_deterministic()
    {
        var first = CucumberMessagesExport.Render(run());
        var second = CucumberMessagesExport.Render(run());

        second.ShouldBe(first);
    }

    [Fact]
    public void a_run_with_no_tag_branch_or_repository_slug_carries_no_ci_block()
    {
        var run = new RunProjection(runId);
        run.Apply(new RunStarted(runId, "Suite", "/somewhere", null, "in-process", t0, 0), t0);
        run.Apply(new RunFinished(runId, 0, 0, 0, 0, 0, t0.AddSeconds(1)), t0.AddSeconds(1));

        var all = envelopes(CucumberMessagesExport.Render(run));
        one(all[0]).TryGetProperty("ci", out _).ShouldBeFalse();
        one(all[^1]).GetProperty("success").GetBoolean().ShouldBeTrue();
        foreach (var envelope in all)
        {
            schema.Value.Evaluate(envelope, new EvaluationOptions { OutputFormat = OutputFormat.Flag }).IsValid.ShouldBeTrue(envelope.GetRawText());
        }
    }
}
