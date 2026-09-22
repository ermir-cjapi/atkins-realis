using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using Orb.Automate.Agents.HR.Api.Agent;
using Orb.Automate.Agents.HR.Api.Contracts;
using Orb.Automate.Agents.HR.Api.Diagnostics;
using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.Endpoints;

internal static class ChatEndpoints
{
    /// <summary>
    /// The authorization policy required by all interactive chat endpoints.
    /// </summary>
    internal const string AuthorizationPolicyName = "chat";

    internal static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var chat = endpoints.MapGroup("/api/chat")
            .WithTags("Chat")
            .RequireAuthorization(AuthorizationPolicyName);

        chat.MapPost("/", HandleChatAsync)
            .WithName("Chat")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        chat.MapPost("/stream", HandleStreamChatAsync)
            .WithName("StreamChat")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return endpoints;
    }

    private static async Task<Results<Ok<ChatResponse>, ValidationProblem>> HandleChatAsync(
        ChatRequest request,
        IAgentChatService service,
        ChatTurnFactory turnFactory,
        IOptions<AgentOptions> options,
        CancellationToken cancellationToken)
    {
        var errors = ChatRequestValidator.Validate(request, options.Value.MaxInputCharacters);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var turn = await turnFactory.CreateAsync(request, cancellationToken);
        return TypedResults.Ok(await service.GetResponseAsync(turn, cancellationToken));
    }

    private static async Task<Results<EmptyHttpResult, ValidationProblem>> HandleStreamChatAsync(
        ChatRequest request,
        IAgentChatService service,
        ChatTurnFactory turnFactory,
        IOptions<AgentOptions> options,
        IChatAiMetrics metrics,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var errors = ChatRequestValidator.Validate(request, options.Value.MaxInputCharacters);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Append("X-Accel-Buffering", "no");
        var turn = await turnFactory.CreateAsync(request, cancellationToken);
        var started = Stopwatch.GetTimestamp();
        var flushed = false;
        try
        {
            await foreach (var update in service.StreamResponseAsync(turn, cancellationToken))
            {
                await response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(update)}\n\n", cancellationToken);
                await response.Body.FlushAsync(cancellationToken);
                if (!flushed)
                {
                    flushed = true;
                    metrics.RecordSseFirstFlush(Stopwatch.GetElapsedTime(started));
                }
            }

            await response.WriteAsync("event: done\ndata: {}\n\n", cancellationToken);
            metrics.RecordSseOutcome("completed");
        }
        catch (Exception exception) when (IsClientAbort(exception, cancellationToken))
        {
            metrics.RecordSseOutcome("client_aborted");
            throw;
        }
        return TypedResults.Empty;
    }

    /// <summary>
    /// Classifies only genuine transport aborts. A cancelled request token alone is not sufficient,
    /// so a server-side fault raised while the client happens to disconnect is not mislabelled.
    /// </summary>
    private static bool IsClientAbort(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        OperationCanceledException => cancellationToken.IsCancellationRequested,
        IOException => true,
        _ => false
    };
}
