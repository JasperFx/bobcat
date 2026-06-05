using Bobcat.Engine;
using Bobcat.Engine.Verification;
using Bobcat.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Bobcat.Tests.Engine;

public class ClockTests
{
    [Fact]
    public void controllable_provider_freezes_and_advances()
    {
        var start = new DateTimeOffset(2026, 6, 5, 9, 0, 0, TimeSpan.Zero);
        var clock = new ControllableTimeProvider(start);

        clock.GetUtcNow().ShouldBe(start);
        clock.Advance(TimeSpan.FromHours(2));
        clock.GetUtcNow().ShouldBe(start.AddHours(2));
        clock.SetUtcNow(start);
        clock.GetUtcNow().ShouldBe(start);
    }

    [Fact]
    public void ambient_clock_resets_to_controllable()
    {
        try
        {
            var clock = BobcatClock.ResetToControllable();
            BobcatClock.Current.ShouldBeSameAs(clock);
            BobcatClock.Controllable().ShouldBeSameAs(clock);
        }
        finally
        {
            BobcatClock.Reset();
        }
    }

    [Theory]
    [InlineData("TODAY", "2026-06-05")]
    [InlineData("TODAY+3", "2026-06-08")]
    [InlineData("TODAY-1", "2026-06-04")]
    [InlineData("TODAY + 5 days", "2026-06-10")]
    public void resolves_today_tokens(string token, string expectedDate)
    {
        var clock = new ControllableTimeProvider(new DateTimeOffset(2026, 6, 5, 9, 0, 0, TimeSpan.Zero));
        RelativeTimeResolver.TryResolve(token, clock, out var resolved, out var note).ShouldBeTrue();
        DateOnly.FromDateTime(resolved).ShouldBe(DateOnly.Parse(expectedDate));
        note.ShouldContain("→");
    }

    [Fact]
    public void resolves_now_with_duration_offset()
    {
        var clock = new ControllableTimeProvider(new DateTimeOffset(2026, 6, 5, 9, 0, 0, TimeSpan.Zero));
        RelativeTimeResolver.TryResolve("NOW + 30 minutes", clock, out var resolved, out _).ShouldBeTrue();
        resolved.ShouldBe(new DateTime(2026, 6, 5, 9, 30, 0));
    }

    [Fact]
    public void non_token_text_is_not_resolved()
    {
        RelativeTimeResolver.TryResolve("2026-06-05", TimeProvider.System, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void share_clock_registers_ambient_delegating_provider()
    {
        var provider = new ServiceCollection().ShareClock().BuildServiceProvider();
        var resolved = provider.GetRequiredService<TimeProvider>();
        resolved.ShouldBeOfType<AmbientClockTimeProvider>();

        try
        {
            var clock = BobcatClock.ResetToControllable();
            clock.SetUtcNow(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
            resolved.GetUtcNow().ShouldBe(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        }
        finally
        {
            BobcatClock.Reset();
        }
    }
}
