using System.Threading.RateLimiting;
using Orb.Automate.Agents.HR.Api.Diagnostics;

namespace Orb.Automate.Agents.HR.Api.ServiceNow;

/// <summary>
/// Applies the shared, bounded token-bucket limit to each actual outbound search attempt.
/// </summary>
public sealed class ServiceNowRateLimitHandler(TokenBucketRateLimiter limiter) : DelegatingHandler
{
    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var lease = await limiter.AcquireAsync(1, cancellationToken);
        if (!lease.IsAcquired)
        {
            ServiceNowDiagnostics.RecordRateRejection();
            throw new ServiceNowException(ServiceNowFailureKind.RateLimited, "The local ServiceNow request queue is full.");
        }

        return await base.SendAsync(request, cancellationToken);
    }
}