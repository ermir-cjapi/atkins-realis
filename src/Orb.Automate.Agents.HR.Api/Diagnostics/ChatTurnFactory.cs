using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api.Auth;
using Orb.Automate.Agents.HR.Api.Contracts;
using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.Diagnostics;

/// <summary>Creates validated chat turns enriched with common telemetry dimensions.</summary>
public sealed class ChatTurnFactory(
    ICurrentUserProfileProvider profileProvider,
    IHttpContextAccessor httpContextAccessor,
    IHostEnvironment environment,
    IOptions<FoundryOptions> foundryOptions)
{
    private static readonly string AgentVersion =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>Creates a turn from a request that has already passed validation.</summary>
    public async Task<ChatTurn> CreateAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var profile = await profileProvider.GetCurrentAsync(cancellationToken);
        var correlationId = Activity.Current?.TraceId.ToHexString();
        if (string.IsNullOrEmpty(correlationId))
        {
            correlationId = httpContextAccessor.HttpContext?.TraceIdentifier ?? Guid.NewGuid().ToString("N");
        }

        var context = new ChatTelemetryContext(
            request.ConversationId!, request.ActivityId!, correlationId,
            profile.UserId, profile.Country, profile.Department,
            "msteams", NormalizeEnvironment(environment.EnvironmentName), AgentVersion,
            foundryOptions.Value.DeploymentName);

        return new ChatTurn(
            request.Message!, request.ConversationId!, request.ActivityId!, request.MessageIndex!.Value,
            request.IsConversationStart, request.Close, context,
            request.PreviousDraft, request.RegenerateInstruction, request.PreviousObjective,
            request.AgentContext);
    }

    internal static string NormalizeEnvironment(string value) => value.ToUpperInvariant() switch
    {
        "DEVELOPMENT" or "DEV" => "dev",
        "TEST" or "TESTING" or "UAT" => "test",
        "PRODUCTION" or "PROD" => "prod",
        _ => value.ToLowerInvariant()
    };
}
