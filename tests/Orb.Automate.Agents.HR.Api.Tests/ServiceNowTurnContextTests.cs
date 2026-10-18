using Orb.Automate.Agents.HR.Api.Options;
using Orb.Automate.Agents.HR.Api.ServiceNow;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>Verifies lazy, shared ServiceNow deadlines and turn scope lifecycle.</summary>
public sealed class ServiceNowTurnContextTests
{
    /// <summary>Creating or inspecting a turn does not create a timer.</summary>
    [Fact]
    public void UnstartedBudgetDoesNotExpire()
    {
        var time = new TrackingTimeProvider();
        using var context = new ServiceNowTurnContext(time, TimeSpan.FromSeconds(10));

        time.Advance(TimeSpan.FromMinutes(1));

        Assert.False(context.IsExpired);
        Assert.Equal(TimeSpan.FromSeconds(10), context.Remaining);
        Assert.Equal(0, time.TimerCount);
    }

    /// <summary>Parallel first calls share one timer and cancellation token.</summary>
    [Fact]
    public async Task ConcurrentStartsCreateOneTimer()
    {
        var time = new TrackingTimeProvider();
        using var context = new ServiceNowTurnContext(time, TimeSpan.FromSeconds(10));

        var tokens = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => context.BudgetToken)));

        Assert.All(tokens, token => Assert.Equal(tokens[0], token));
        Assert.Equal(1, time.TimerCount);
    }

    /// <summary>Further calls preserve the original deadline, including its exact expiry boundary.</summary>
    [Fact]
    public void StartedBudgetIsNotRenewed()
    {
        var time = new TrackingTimeProvider();
        using var context = new ServiceNowTurnContext(time, TimeSpan.FromSeconds(10));

        var token = context.BudgetToken;
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(token, context.BudgetToken);
        Assert.Equal(TimeSpan.FromSeconds(6), context.Remaining);
        Assert.False(context.IsExpired);

        time.Advance(TimeSpan.FromSeconds(6));
        Assert.True(context.IsExpired);
        Assert.Equal(TimeSpan.Zero, context.Remaining);
        Assert.Equal(1, time.TimerCount);
    }

    /// <summary>Disposal is idempotent and no new work can start after disposal.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposedBudgetCannotStart(bool startBeforeDisposal)
    {
        var time = new TrackingTimeProvider();
        var context = new ServiceNowTurnContext(time, TimeSpan.FromSeconds(10));
        if (startBeforeDisposal)
        {
            _ = context.BudgetToken;
        }

        context.Dispose();
        context.Dispose();

        Assert.Throws<ObjectDisposedException>(() => context.BudgetToken);
        Assert.Equal(startBeforeDisposal ? 1 : 0, time.TimerCount);
    }

    /// <summary>Nested scopes restore the previous turn and do not share its timer.</summary>
    [Fact]
    public void NestedScopesRestorePreviousTurn()
    {
        var time = new TrackingTimeProvider();
        var accessor = new ServiceNowTurnContextAccessor(
            Microsoft.Extensions.Options.Options.Create(new ServiceNowOptions { TurnBudgetMilliseconds = 10_000 }), time);
        using (accessor.BeginTurn())
        {
            var outer = accessor.Current;
            using (accessor.BeginTurn())
            {
                Assert.NotSame(outer, accessor.Current);
                _ = accessor.Current!.BudgetToken;
            }

            Assert.Same(outer, accessor.Current);
            Assert.Equal(TimeSpan.FromSeconds(10), outer!.Remaining);
        }

        Assert.Null(accessor.Current);
        Assert.Equal(1, time.TimerCount);
    }

    private sealed class TrackingTimeProvider : TimeProvider
    {
        private long _timestamp;
        private int _timerCount;

        public int TimerCount => Volatile.Read(ref _timerCount);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Volatile.Read(ref _timestamp);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _timerCount);
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }
}