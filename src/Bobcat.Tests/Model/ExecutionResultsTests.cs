using Bobcat.Model;
using Shouldly;

namespace Bobcat.Tests.Model;

public class ExecutionResultsTests
{
    [Fact]
    public void initial_state()
    {
        var time = DateTimeOffset.Now;
        var execution = new ExecutionResults("foo", time);
        execution.Steps.Any().ShouldBeFalse();
        
        execution.Counts.ShouldBe(new Counts{Rights = 0, Wrongs = 0, Errors = 0});
        execution.StartTime.ShouldBe(time);
    }
    
    [Fact]
    public void add_results_creates_and_appends_step_result()
    {
        var execution = new ExecutionResults("foo", DateTimeOffset.Now);
        var result = execution.StartStep("bar", 25);
        
        result.Start.ShouldBe(25);
        result.StepId.ShouldBe("bar");
        
        execution.Steps.Single().ShouldBe(result);
    }

    [Fact]
    public void end_step_tabulates_and_marks_end    ()
    {
        var execution = new ExecutionResults("foo", DateTimeOffset.Now);
        var result1 = execution.StartStep("bar", 25);
        result1.MarkSuccess();
        execution.Tabulate(result1);
        
        execution.Counts.ShouldBe(new Counts(1, 0, 0));
    }

    [Fact] 
    public void tabulate_multiple_steps()
    {
        var execution = new ExecutionResults("foo", DateTimeOffset.Now);
        var result1 = execution.StartStep("bar", 25);
        result1.MarkSuccess();
        execution.Tabulate(result1);

        var result2 = execution.StartStep("bar2", 51);
        result2.MarkErrored(new DivideByZeroException(), 55);
        execution.Tabulate(result2);
        
        execution.Counts.ShouldBe(new Counts(1, 0, 1));
    }

    [Fact]
    public void exposes_all_exceptions()
    {
        var execution = new ExecutionResults("foo", DateTimeOffset.Now);
        var result1 = execution.StartStep("bar", 25);
        result1.MarkSuccess();
        execution.Tabulate(result1);

        var result2 = execution.StartStep("bar2", 51);
        result2.MarkErrored(new DivideByZeroException(), 55);
        execution.Tabulate(result2);
        
        var result3 = execution.StartStep("bar3", 55);
        result2.MarkErrored(new DivideByZeroException(), 70);
        execution.Tabulate(result3);
        
        execution.AllExceptions().Count().ShouldBe(2);
    }
}