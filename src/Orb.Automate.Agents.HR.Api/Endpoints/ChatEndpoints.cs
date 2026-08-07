using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api.Agent;
using Orb.Automate.Agents.HR.Api.Contracts;
using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.Endpoints;

internal static class ChatEndpoints
{
    internal static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var chat = endpoints.MapGroup("/api/chat").WithTags("Chat");

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
        IOptions<AgentOptions> options,
        CancellationToken cancellationToken)
    {
        var errors = ChatRequestValidator.Validate(request, options.Value.MaxInputCharacters);
        return errors.Count > 0
            ? TypedResults.ValidationProblem(errors)
            : TypedResults.Ok(await service.GetResponseAsync(request.Message, cancellationToken));
    }

    private static async Task<Results<EmptyHttpResult, ValidationProblem>> HandleStreamChatAsync(
        ChatRequest request,
        IAgentChatService service,
        IOptions<AgentOptions> options,
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
        await foreach (var update in service.StreamResponseAsync(request.Message, cancellationToken))
        {
            await response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(update)}\n\n", cancellationToken);
            await response.Body.FlushAsync(cancellationToken);
        }

        await response.WriteAsync("event: done\ndata: {}\n\n", cancellationToken);
        return TypedResults.Empty;
    }
}
