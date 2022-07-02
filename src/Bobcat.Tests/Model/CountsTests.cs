using Bobcat.Model;
using Shouldly;

namespace Bobcat.Tests.Model;

public class CountsTests
{
    [Theory]
    [InlineData(0, 0, 0, true)]
    [InlineData(1, 0, 0, true)]
    [InlineData(5, 0, 0, true)]
    [InlineData(0, 1, 0, false)]
    [InlineData(0, 0, 1, false)]
    [InlineData(1, 0, 1, false)]
    [InlineData(1, 1, 0, false)]
    [InlineData(1, 1, 1, false)]
    [InlineData(5, 3, 1, false)]
    public void succeeded_logic(int rights, int wrongs, int errors, bool expected)  
    {
        new Counts{Rights = rights, Wrongs = wrongs, Errors = errors}
            .Succeeded.ShouldBe(expected);
    }
    
    [Fact]
    public void read_success()
    {
        var theCounts = new Counts();
        theCounts.Read(ResultStatus.success);
        theCounts.ShouldBe(new Counts{Rights = 1});
        
    }

    [Fact]
    public void read_error()
    {
        var theCounts = new Counts();
        theCounts.Read(ResultStatus.error);
        theCounts.ShouldBe(new Counts{Errors = 1});
    }
    
    [Fact]
    public void read_missing()
    {
        var theCounts = new Counts();
        theCounts.Read(ResultStatus.missing);
        theCounts.ShouldBe(new Counts{Errors = 1});
    }
    
    [Fact]
    public void read_invalid()
    {
        var theCounts = new Counts();
        theCounts.Read(ResultStatus.invalid);
        theCounts.ShouldBe(new Counts{Errors = 1});
    }

    [Fact]
    public void read_failed()
    {
        var theCounts = new Counts();
        theCounts.Read(ResultStatus.failed);
        theCounts.ShouldBe(new Counts{Wrongs = 1});
    }
    
    [Fact]
    public void read_mix()
    {
        var theCounts = new Counts();
        theCounts.Read(ResultStatus.failed);
        theCounts.Read(ResultStatus.failed);
        theCounts.Read(ResultStatus.error);
        theCounts.Read(ResultStatus.success);
        theCounts.Read(ResultStatus.success);
        theCounts.Read(ResultStatus.success);
        theCounts.ShouldBe(new Counts{Rights = 3, Wrongs = 2, Errors = 1});
    }
}