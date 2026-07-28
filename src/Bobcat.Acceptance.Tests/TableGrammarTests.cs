using Bobcat.Engine;
using Bobcat.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

public class TableGrammarTests
{
    [Fact]
    public async Task batched_setup_opens_once_stores_each_row_and_saves_once()
    {
        CustomerSetupGrammar.Reset();

        var results = await Specs.Run(Table_Grammar_Feature.Define(), "Batched data setup");
        var step = results.Step("the following customers exist");

        step.StepStatus.ShouldBe(ResultStatus.success);

        // Before ran once, After ran once, and every row landed in the single batch.
        CustomerSetupGrammar.Log.ShouldBe(["opened", "saved Acme=3|Globex=1"]);
    }

    [Fact]
    public async Task batched_setup_renders_every_input_cell()
    {
        CustomerSetupGrammar.Reset();

        var results = await Specs.Run(Table_Grammar_Feature.Define(), "Batched data setup");
        var step = results.Step("the following customers exist");

        step.IsSetVerification.ShouldBeTrue();
        step.SetVerificationColumns.ShouldBe(["name", "orders"]);
        step.Cells.Count.ShouldBe(4);
        step.Cells.ShouldAllBe(c => c.Status == ResultStatus.ok);
    }

    [Fact]
    public async Task decision_table_compares_the_leftover_column_against_the_row_return()
    {
        var results = await Specs.Run(Table_Grammar_Feature.Define(), "Decision table");
        var step = results.Step("dividing gives");

        step.StepStatus.ShouldBe(ResultStatus.failed);
        step.SetVerificationColumns.ShouldBe(["dividend", "divisor", "quotient"]);

        // Row 1: 10 / 2 == 5 — passes. Row 2: 9 / 3 == 3, not 4 — fails.
        var firstQuotient = step.Cells.Single(c => c.Name == "quotient" && c.RowIndex == 0);
        firstQuotient.Status.ShouldBe(ResultStatus.success);
        firstQuotient.Actual.ShouldBe("5");

        var secondQuotient = step.Cells.Single(c => c.Name == "quotient" && c.RowIndex == 1);
        secondQuotient.Status.ShouldBe(ResultStatus.failed);
        secondQuotient.Expected.ShouldBe("4");
        secondQuotient.Actual.ShouldBe("3");

        // Input columns render plain, never pass/fail.
        step.Cells.Where(c => c.Name != "quotient").ShouldAllBe(c => c.Status == ResultStatus.ok);
    }

    [Fact]
    public async Task a_throwing_Before_skips_the_rows_but_still_runs_After()
    {
        FailingBeforeGrammar.Reset();

        var results = await Specs.Run(Table_Grammar_Feature.Define(), "A throwing Before still runs After");
        var step = results.Step("the failing setup runs");

        step.StepStatus.ShouldBe(ResultStatus.error);
        step.FailureLevel.ShouldBe(FailureLevel.Critical);
        step.Exception!.Message.ShouldBe("could not open the batch");

        FailingBeforeGrammar.RowCount.ShouldBe(0);
        FailingBeforeGrammar.AfterRan.ShouldBeTrue();
    }

    [Fact]
    public async Task the_whole_envelope_shares_one_scoped_service_instance()
    {
        OrderSetupGrammar.Reset();

        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        runner.AddFeature(Table_Grammar_Scoped_Feature.Define());
        runner.Suite.AddResource(new HostResource(() =>
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddScoped<ISessionMarker, SessionMarker>();
            return builder.Build();
        }));

        var results = await runner.RunAll();

        results.ExitCode.ShouldBe(0);
        OrderSetupGrammar.Recorded.ShouldBe(["ORD-1", "ORD-2"]);

        // Before + two rows + After all saw the one scenario-scoped instance.
        OrderSetupGrammar.SessionsSeen.Count.ShouldBe(4);
        OrderSetupGrammar.SessionsSeen.Distinct().Count().ShouldBe(1);
    }
}
