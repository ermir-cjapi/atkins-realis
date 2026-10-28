using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Orb.Automate.Agents.HR.Api.Agent;

/// <summary>
/// Development-only decorator for the hosted agent used by DevUI. DevUI calls the agent directly
/// and so bypasses <see cref="AgentChatService"/>; this decorator appends the same "Sources:"
/// footer the Teams channel shows, listing only retrieved passages the answer actually cites.
/// </summary>
/// <param name="agent">The hosted agent to decorate.</param>
internal sealed class DevUiSourcesAgent(AIAgent agent) : DelegatingAIAgent(agent)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Gets the undecorated agent, used by <see cref="AgentChatService"/>.</summary>
    public AIAgent Agent { get; } = agent;

    /// <inheritdoc />
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.RunCoreAsync(messages, session, options, cancellationToken);

        var sources = new List<CitationSource>();
        foreach (var message in response.Messages)
        {
            CollectSources(message.Contents, sources);
        }

        var resolution = CitationResolver.Resolve(response.Text, sources);
        if (response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant) is not { } last)
        {
            return response;
        }

        if (resolution.RequiresFallback)
        {
            // Same policy as AgentChatService: uncited SharePoint evidence yields the safe answer.
            foreach (var message in response.Messages.Where(m => m.Role == ChatRole.Assistant))
            {
                foreach (var content in message.Contents.OfType<TextContent>().ToList())
                {
                    message.Contents.Remove(content);
                }
            }

            last.Contents.Add(new TextContent(CitationResolver.InsufficientGroundingMessage));
        }
        else if (CitationResolver.FormatSourcesFooter(resolution.Cited) is { Length: > 0 } footer)
        {
            last.Contents.Add(new TextContent(footer));
        }

        return response;
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sources = new List<CitationSource>();
        var text = new StringBuilder();
        string? responseId = null;
        string? messageId = null;

        await foreach (var update in base.RunCoreStreamingAsync(messages, session, options, cancellationToken))
        {
            CollectSources(update.Contents, sources);
            if (!string.IsNullOrEmpty(update.Text))
            {
                text.Append(update.Text);
                messageId = update.MessageId ?? messageId;
            }

            responseId = update.ResponseId ?? responseId;
            yield return update;
        }

        // Streamed text has already been shown, so it cannot be replaced; when citation policy
        // fails, append the safe answer so the DevUI tester sees the outcome Teams would give.
        var resolution = CitationResolver.Resolve(text.ToString(), sources);
        var footer = resolution.RequiresFallback
            ? $"\n\n{CitationResolver.InsufficientGroundingMessage}"
            : CitationResolver.FormatSourcesFooter(resolution.Cited);
        if (footer.Length > 0)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, footer)
            {
                ResponseId = responseId,
                MessageId = messageId,
            };
        }
    }

    /// <summary>Reads passages from <c>search_hr_knowledge</c> results carried in the agent output.</summary>
    private static void CollectSources(IEnumerable<AIContent> contents, List<CitationSource> sources)
    {
        foreach (var result in contents.OfType<FunctionResultContent>())
        {
            JsonElement element;
            try
            {
                element = result.Result switch
                {
                    null => default,
                    JsonElement json => json,
                    string json => JsonSerializer.Deserialize<JsonElement>(json),
                    var value => JsonSerializer.SerializeToElement(value, JsonOptions),
                };
            }
            catch (JsonException)
            {
                continue;
            }

            if (GetProperty(element, "passages") is not { ValueKind: JsonValueKind.Array } passages)
            {
                continue;
            }

            foreach (var passage in passages.EnumerateArray())
            {
                var title = GetString(passage, "title") ?? string.Empty;
                var url = GetString(passage, "url");
                var kbNumber = GetString(passage, "kbNumber");
                if (url is not null || kbNumber is not null || title.Length > 0)
                {
                    sources.Add(new CitationSource(
                        url is not null ? CitationSourceKind.SharePoint : CitationSourceKind.ServiceNow,
                        title,
                        url,
                        kbNumber));
                }
            }
        }
    }

    private static JsonElement? GetProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string? GetString(JsonElement element, string name) =>
        GetProperty(element, name) is { ValueKind: JsonValueKind.String } value &&
        value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
