namespace Orb.Automate.Agents.HR.Api.Auth;

/// <summary>
/// Trusted delegated identity for the current Teams turn.
/// </summary>
/// <param name="AccessToken">The validated HR API delegated token.</param>
/// <param name="TenantId">The validated tenant object ID.</param>
/// <param name="UserObjectId">The validated user object ID.</param>
/// <param name="Upn">The validated UPN or email claim, when supplied by Entra.</param>
public sealed record TeamsUserContext(
    string AccessToken,
    string TenantId,
    string UserObjectId,
    string? Upn);

/// <summary>
/// Provides an async-flow-local delegated identity without replacing the Bot Connector principal.
/// </summary>
public interface ITeamsUserContextAccessor
{
    /// <summary>Gets the trusted identity for the current Teams turn.</summary>
    TeamsUserContext? Current { get; }

    /// <summary>Pushes a trusted identity until the returned scope is disposed.</summary>
    IDisposable Push(TeamsUserContext context);
}

/// <summary>
/// Async-local implementation that isolates concurrent Teams turns.
/// </summary>
public sealed class TeamsUserContextAccessor : ITeamsUserContextAccessor
{
    private readonly AsyncLocal<Holder?> _current = new();

    /// <inheritdoc />
    public TeamsUserContext? Current => _current.Value?.Context;

    /// <inheritdoc />
    public IDisposable Push(TeamsUserContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var previous = _current.Value;
        var current = new Holder(context);
        _current.Value = current;
        return new Scope(this, current, previous);
    }

    private sealed record Holder(TeamsUserContext Context);

    private sealed class Scope(
        TeamsUserContextAccessor owner,
        Holder current,
        Holder? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 &&
                ReferenceEquals(owner._current.Value, current))
            {
                owner._current.Value = previous;
            }
        }
    }
}
