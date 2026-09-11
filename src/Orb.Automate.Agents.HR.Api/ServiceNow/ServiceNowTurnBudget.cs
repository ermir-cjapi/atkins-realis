using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.ServiceNow;

/// <summary>
/// Provides the ServiceNow aggregate budget associated with the current agent turn.
/// </summary>
public interface IServiceNowTurnContextAccessor
{
    /// <summary>Begins a turn scope.</summary>
    IDisposable BeginTurn();

    /// <summary>Gets the current turn, if the call is running inside an agent turn.</summary>
    ServiceNowTurnContext? Current { get; }
}

/// <summary>
/// Carries one hard deadline and completed-operation reuse across ServiceNow tool calls in a turn.
/// </summary>
public sealed class ServiceNowTurnContext : IDisposable
{
    private readonly CancellationTokenSource _budgetSource;
    private readonly TimeProvider _timeProvider;
    private readonly long _started;
    private readonly TimeSpan _budget;
    private readonly ConcurrentDictionary<string, Task<ServiceNowSearchResponse>> _operations = new(StringComparer.Ordinal);

    internal ServiceNowTurnContext(TimeProvider timeProvider, TimeSpan budget)
    {
        _timeProvider = timeProvider;
        _budget = budget;
        _started = timeProvider.GetTimestamp();
        _budgetSource = new CancellationTokenSource(budget, timeProvider);
    }

    /// <summary>Gets whether the turn's ServiceNow budget has expired.</summary>
    public bool IsExpired => _budgetSource.IsCancellationRequested || Remaining <= TimeSpan.Zero;

    /// <summary>Gets the remaining aggregate ServiceNow time.</summary>
    public TimeSpan Remaining
    {
        get
        {
            var remaining = _budget - _timeProvider.GetElapsedTime(_started);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>Gets the budget cancellation token.</summary>
    public CancellationToken BudgetToken => _budgetSource.Token;

    internal Task<ServiceNowSearchResponse> GetOrAdd(
        string operationKey,
        Func<Task<ServiceNowSearchResponse>> operation) =>
        _operations.GetOrAdd(operationKey, static (_, factory) => factory(), operation);

    /// <inheritdoc />
    public void Dispose() => _budgetSource.Dispose();
}

/// <summary>
/// Async-local implementation that isolates concurrent agent turns.
/// </summary>
public sealed class ServiceNowTurnContextAccessor(
    IOptions<ServiceNowOptions> options,
    TimeProvider timeProvider) : IServiceNowTurnContextAccessor
{
    private readonly AsyncLocal<ServiceNowTurnContext?> _current = new();

    /// <inheritdoc />
    public ServiceNowTurnContext? Current => _current.Value;

    /// <inheritdoc />
    public IDisposable BeginTurn()
    {
        var previous = _current.Value;
        var context = new ServiceNowTurnContext(timeProvider, TimeSpan.FromSeconds(options.Value.TurnBudgetSeconds));
        _current.Value = context;
        return new Scope(this, context, previous);
    }

    private sealed class Scope(
        ServiceNowTurnContextAccessor owner,
        ServiceNowTurnContext context,
        ServiceNowTurnContext? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (ReferenceEquals(owner._current.Value, context))
            {
                owner._current.Value = previous;
            }

            context.Dispose();
        }
    }
}