using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Orb.Automate.Agents.HR.Api.Diagnostics;

/// <summary>
/// Records the complete chat input immediately before it is sent to the model.
/// </summary>
public sealed class PayloadLoggingChatClient(
    IChatClient innerClient,
    DevelopmentPayloadWriter payloadWriter) : DelegatingChatClient(innerClient)
{
    private static readonly JsonSerializerOptions LogJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <inheritdoc />
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var materializedMessages = messages.ToList();
        WritePayload(materializedMessages, options, "non_streaming");
        return InnerClient.GetResponseAsync(materializedMessages, options, cancellationToken);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var materializedMessages = messages.ToList();
        WritePayload(materializedMessages, options, "streaming");
        await foreach (var update in InnerClient
            .GetStreamingResponseAsync(materializedMessages, options, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private void WritePayload(IReadOnlyList<ChatMessage> messages, ChatOptions? options, string mode)
    {
        var toolResultCount = messages.Sum(message => message.Contents.Count(content => content is FunctionResultContent));
        var payload = new
        {
            mode,
            stage = toolResultCount == 0 ? "initial_request" : "request_with_tool_results",
            toolResultCount,
            options = ProjectOptions(options),
            messages = messages.Select(ProjectMessage).ToArray()
        };

        var category = toolResultCount == 0
            ? "LLM REQUEST INPUT - INITIAL"
            : "LLM REQUEST INPUT - WITH TOOL RESULTS";
        payloadWriter.Write(category, JsonSerializer.Serialize(payload, LogJsonOptions));
    }

    private static object? ProjectOptions(ChatOptions? options) => options is null
        ? null
        : new
        {
            options.ModelId,
            options.Instructions,
            options.Temperature,
            options.TopP,
            options.MaxOutputTokens,
            options.AllowMultipleToolCalls,
            tools = options.Tools?.Select(tool => new { tool.Name, tool.Description }).ToArray()
        };

    private static object ProjectMessage(ChatMessage message) => new
    {
        role = message.Role.ToString(),
        message.AuthorName,
        message.MessageId,
        contents = message.Contents.Select(ProjectContent).ToArray()
    };

    private static LoggedContent ProjectContent(AIContent content) => content switch
    {
        TextContent text => new("text", text.Text, null, null, null),
        FunctionCallContent call => new("function_call", null, call.CallId, call.Name, MakeSerializable(call.Arguments)),
        FunctionResultContent result => new("function_result", null, result.CallId, null, MakeSerializable(result.Result)),
        _ => new(content.GetType().Name, null, null, null, null)
    };

    private static object? MakeSerializable(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.SerializeToElement(value, LogJsonOptions);
        }
        catch (NotSupportedException)
        {
            return value.ToString();
        }
        catch (JsonException)
        {
            return value.ToString();
        }
    }

    private sealed record LoggedContent(
        string Type,
        string? Text,
        string? CallId,
        string? Name,
        object? Value);
}