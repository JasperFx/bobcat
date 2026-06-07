using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

public class ProgramCollisionGuardTests
{
    [Fact]
    public void reports_collision_when_entry_and_another_assembly_both_have_program()
    {
        var message = BobcatRunner.DescribeProgramCollision(
            "MySample.Tests", entryHasProgram: true,
            others: new[] { ("MySample", true) });

        message.ShouldNotBeNull();
        message.ShouldContain("MySample.Tests");
        message.ShouldContain("MySample");
        message.ShouldContain("top-level statements");
    }

    [Fact]
    public void no_collision_when_entry_has_no_program()
    {
        BobcatRunner.DescribeProgramCollision("MySample.Tests", entryHasProgram: false,
            others: new[] { ("MySample", true) }).ShouldBeNull();
    }

    [Fact]
    public void no_collision_when_no_other_assembly_has_program()
    {
        BobcatRunner.DescribeProgramCollision("MySample.Tests", entryHasProgram: true,
            others: new[] { ("Bobcat", false), ("Marten", false) }).ShouldBeNull();
    }

    [Fact]
    public void guard_does_not_throw_in_a_normal_process()
    {
        // The xUnit test host has no global-namespace 'Program' colliding with another,
        // so the live guard must be a no-op here.
        Should.NotThrow(BobcatRunner.GuardAgainstProgramCollision);
    }
}
