using Bobcat.Engine;
using Shouldly;

namespace Bobcat.Tests.Model;

public class StepResultTests
{
    private readonly StepResult theStepResult = new StepResult("B", 11);

    [Fact]
    public void initial_props()
    {
        theStepResult.StepId.ShouldBe("B");
        theStepResult.Start.ShouldBe(11);
        
        theStepResult.StepStatus.ShouldBe(ResultStatus.ok);
    }

    [Fact]
    public void mark_end()
    {
        theStepResult.MarkEnded(22);
        theStepResult.End.ShouldBe(22);
        theStepResult.StepStatus.ShouldBe(ResultStatus.ok);
    }

    [Fact]
    public void mark_success()
    {
        theStepResult.MarkSuccess();
        theStepResult.StepStatus.ShouldBe(ResultStatus.success);
    }
    
    [Fact]
    public void mark_errored()
    {
        var ex = new DivideByZeroException();
        theStepResult.MarkErrored(ex, 33);
        
        theStepResult.Exception
            .ShouldBe(ex);
        
        theStepResult.StepStatus.ShouldBe(ResultStatus.error);
        theStepResult.End.ShouldBe(33);
    }

    [Theory]
    [InlineData(0, 0, 0, ResultStatus.ok)]
    [InlineData(1, 0, 0, ResultStatus.success)]
    [InlineData(0, 1, 0, ResultStatus.failed)]
    [InlineData(0, 0, 1, ResultStatus.error)]
    [InlineData(1, 0, 0, ResultStatus.ok, ResultStatus.success)]
    [InlineData(2, 0, 0, ResultStatus.ok, ResultStatus.success, ResultStatus.success)]
    [InlineData(2, 1, 0, ResultStatus.ok, ResultStatus.success, ResultStatus.success, ResultStatus.failed)]
    [InlineData(2, 0, 1, ResultStatus.ok, ResultStatus.success, ResultStatus.success, ResultStatus.error)]
    [InlineData(2, 0, 2, ResultStatus.ok, ResultStatus.success, ResultStatus.success, ResultStatus.error, ResultStatus.invalid)]
    [InlineData(2, 0, 3, ResultStatus.ok, ResultStatus.success, ResultStatus.success, ResultStatus.error, ResultStatus.invalid, ResultStatus.missing)]
    public void mark_cells(int rights, int wrongs, int errors, params ResultStatus[] statuses)
    {
        var result = new StepResult(Guid.NewGuid().ToString(), 0, statuses.First());
        
        var cells = statuses.Skip(1)
            .Select(status => new CellResult(Guid.NewGuid().ToString(), status, "whatever"))
            .ToArray();
    
        result.MarkCells(cells);
        var counts = new Counts();
        result.Tabulate(counts);
        
        counts.ShouldBe(new Counts{Rights = rights, Wrongs = wrongs, Errors = errors});
    }

    [Fact]
    public void exceptions_enumeration()
    {
        theStepResult.MarkCells(new CellResult("foo", ResultStatus.success, "all good"));
        
        // Initial state
        theStepResult.AllExceptions().Any().ShouldBeFalse();
        
        // Add an exception
        theStepResult.MarkErrored(new DivideByZeroException(), 55);
        theStepResult.AllExceptions().Single().ShouldBeOfType<DivideByZeroException>();

        // Get the exception from a cell result
        theStepResult.MarkCells(new CellResult("bar", ResultStatus.error, "Bad!")
            { Exception = new BadImageFormatException() });
        
        theStepResult.AllExceptions().Count().ShouldBe(2);
        theStepResult.AllExceptions().Last().ShouldBeOfType<BadImageFormatException>();
    }
}