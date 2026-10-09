using Orb.Automate.Agents.HR.Api.Auth;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>Tests isolation of delegated Teams identity between asynchronous turns.</summary>
public sealed class TeamsUserContextAccessorTests
{
    /// <summary>Nested scopes restore the prior identity and dispose cleanly.</summary>
    [Fact]
    public void NestedScopesRestorePreviousIdentity()
    {
        var accessor = new TeamsUserContextAccessor();
        var first = CreateContext("first@example.com");
        var second = CreateContext("second@example.com");

        using (accessor.Push(first))
        {
            Assert.Same(first, accessor.Current);
            using (accessor.Push(second))
            {
                Assert.Same(second, accessor.Current);
            }

            Assert.Same(first, accessor.Current);
        }

        Assert.Null(accessor.Current);
    }

    /// <summary>Parallel execution contexts cannot observe each other's identity.</summary>
    [Fact]
    public async Task ConcurrentTurnsAreIsolated()
    {
        var accessor = new TeamsUserContextAccessor();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string?> RunAsync(string upn)
        {
            using var scope = accessor.Push(CreateContext(upn));
            await gate.Task;
            return accessor.Current?.Upn;
        }

        var first = RunAsync("first@example.com");
        var second = RunAsync("second@example.com");
        gate.SetResult();

        Assert.Equal("first@example.com", await first);
        Assert.Equal("second@example.com", await second);
        Assert.Null(accessor.Current);
    }

    private static TeamsUserContext CreateContext(string upn) => new(
        $"token-{upn}",
        Guid.NewGuid().ToString(),
        Guid.NewGuid().ToString(),
        upn);
}