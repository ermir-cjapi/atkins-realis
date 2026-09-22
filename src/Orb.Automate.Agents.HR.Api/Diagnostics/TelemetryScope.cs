namespace Orb.Automate.Agents.HR.Api.Diagnostics;

/// <summary>
/// An idempotent scope that restores previously captured async-flow-local telemetry state exactly
/// once, however many times it is disposed.
/// </summary>
/// <param name="restore">The restoration callback.</param>
internal sealed class TelemetryScope(Action restore) : IDisposable
{
    private Action? restore = restore;

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref this.restore, null)?.Invoke();
}
