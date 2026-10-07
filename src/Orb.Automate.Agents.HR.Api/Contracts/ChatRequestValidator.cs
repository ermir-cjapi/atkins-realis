namespace Orb.Automate.Agents.HR.Api.Contracts;

/// <summary>
/// Validates incoming chat requests.
/// </summary>
public static class ChatRequestValidator
{
    private const int MaximumIdentifierLength = 512;

    // Mirrors the maxLength enforced by the Adaptive Card for the refine/regenerate instruction.
    private const int MaxRegenerateInstructionLength = 500;

    // The previous agent response injected into [PREVIOUS AGENT RESPONSE]. Cap at a generous
    // but finite ceiling so a client cannot bypass the Message limit via this field.
    private const int MaxAgentContextLength = 8_000;

    // SMART objective field limits. ObjectiveText is the primary field and is required.
    private const int MaxObjectiveTextLength = 2_000;
    private const int MaxSmartCriterionLength = 500;

    // Email-draft field limits, aligned with the model's output constraints.
    // Subject: model is instructed to keep it under 100 chars; allow a small server-side margin.
    // Body: model is instructed to keep it under 400 words; 4 000 chars is generous but finite.
    private const int MaxDraftSubjectLength = 200;
    private const int MaxDraftBodyLength = 4_000;
    private const int MaxDraftSuggestedRecipientLength = 200;

    // PolicySourceUrls limits apply to both EmailDraft and SmartObjective.
    private const int MaxPolicySourceUrlCount = 20;
    private const int MaxPolicySourceUrlLength = 2_048;

    private static readonly HashSet<string> CloseReasons =
        ["UserEnded", "InactivityTimeout", "Escalated"];

    private static readonly HashSet<string> ValidDraftTones =
        ["professional", "empathetic", "factual"];

    /// <summary>
    /// Validates the request payload against required input rules.
    /// </summary>
    /// <param name="request">The request to validate.</param>
    /// <param name="maxInputCharacters">The maximum allowed message length.</param>
    /// <returns>A validation error dictionary keyed by field name.</returns>
    public static Dictionary<string, string[]> Validate(ChatRequest request, int maxInputCharacters)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            errors[nameof(request.Message)] = ["Message is required."];
        }
        else if (request.Message.Length > maxInputCharacters)
        {
            errors[nameof(request.Message)] = [$"Message cannot exceed {maxInputCharacters} characters."];
        }

        ValidateIdentifier(request.ConversationId, nameof(request.ConversationId), errors);
        ValidateIdentifier(request.ActivityId, nameof(request.ActivityId), errors);

        if (request.MessageIndex is null or < 1)
        {
            errors[nameof(request.MessageIndex)] = ["MessageIndex must be a positive one-based index."];
        }
        else if (request.IsConversationStart != (request.MessageIndex == 1))
        {
            errors[nameof(request.MessageIndex)] =
                ["IsConversationStart must be true exactly when MessageIndex is 1."];
        }

        if (request.Close is { } close)
        {
            if (close.ConversationDurationSeconds < 0)
            {
                errors[$"{nameof(request.Close)}.{nameof(close.ConversationDurationSeconds)}"] =
                    ["ConversationDurationSeconds cannot be negative."];
            }

            if (close.TurnCount < 1 || (request.MessageIndex is { } index && close.TurnCount != index))
            {
                errors[$"{nameof(request.Close)}.{nameof(close.TurnCount)}"] =
                    ["TurnCount must equal MessageIndex on the closing turn."];
            }

            if (!CloseReasons.Contains(close.CloseReason))
            {
                errors[$"{nameof(request.Close)}.{nameof(close.CloseReason)}"] =
                    ["CloseReason must be UserEnded, InactivityTimeout, or Escalated."];
            }
        }

        if (request.PreviousDraft is not null && request.PreviousObjective is not null)
        {
            errors[nameof(request.PreviousObjective)] =
                ["PreviousDraft and PreviousObjective cannot both be supplied on the same request."];
        }

        if (request.RegenerateInstruction is { Length: > MaxRegenerateInstructionLength })
        {
            errors[nameof(request.RegenerateInstruction)] =
                [$"RegenerateInstruction cannot exceed {MaxRegenerateInstructionLength} characters."];
        }

        if (request.AgentContext is { Length: > MaxAgentContextLength })
        {
            errors[nameof(request.AgentContext)] =
                [$"AgentContext cannot exceed {MaxAgentContextLength} characters."];
        }

        if (request.PreviousObjective is { } objective)
        {
            ValidatePreviousObjective(objective, errors);
        }

        if (request.PreviousDraft is { } draft)
        {
            ValidatePreviousDraft(draft, errors);
        }

        return errors;
    }

    private static void ValidatePreviousObjective(SmartObjective objective, Dictionary<string, string[]> errors)
    {
        const string prefix = nameof(ChatRequest.PreviousObjective);

        if (string.IsNullOrWhiteSpace(objective.ObjectiveText))
        {
            errors[$"{prefix}.{nameof(objective.ObjectiveText)}"] =
                ["PreviousObjective.ObjectiveText is required."];
        }
        else if (objective.ObjectiveText.Length > MaxObjectiveTextLength)
        {
            errors[$"{prefix}.{nameof(objective.ObjectiveText)}"] =
                [$"PreviousObjective.ObjectiveText cannot exceed {MaxObjectiveTextLength} characters."];
        }

        ValidateSmartCriterion(objective.Specific, $"{prefix}.{nameof(objective.Specific)}", errors);
        ValidateSmartCriterion(objective.Measurable, $"{prefix}.{nameof(objective.Measurable)}", errors);
        ValidateSmartCriterion(objective.Achievable, $"{prefix}.{nameof(objective.Achievable)}", errors);
        ValidateSmartCriterion(objective.Relevant, $"{prefix}.{nameof(objective.Relevant)}", errors);
        ValidateSmartCriterion(objective.TimeBound, $"{prefix}.{nameof(objective.TimeBound)}", errors);

        ValidatePolicySourceUrls(objective.PolicySourceUrls, $"{prefix}.{nameof(objective.PolicySourceUrls)}", errors);
    }

    private static void ValidatePreviousDraft(EmailDraft draft, Dictionary<string, string[]> errors)
    {
        const string prefix = nameof(ChatRequest.PreviousDraft);

        if (draft.Subject.Length > MaxDraftSubjectLength)
        {
            errors[$"{prefix}.{nameof(draft.Subject)}"] =
                [$"PreviousDraft.Subject cannot exceed {MaxDraftSubjectLength} characters."];
        }

        if (draft.Body.Length > MaxDraftBodyLength)
        {
            errors[$"{prefix}.{nameof(draft.Body)}"] =
                [$"PreviousDraft.Body cannot exceed {MaxDraftBodyLength} characters."];
        }

        if (draft.SuggestedRecipient is { Length: > MaxDraftSuggestedRecipientLength })
        {
            errors[$"{prefix}.{nameof(draft.SuggestedRecipient)}"] =
                [$"PreviousDraft.SuggestedRecipient cannot exceed {MaxDraftSuggestedRecipientLength} characters."];
        }

        if (!ValidDraftTones.Contains(draft.Tone))
        {
            errors[$"{prefix}.{nameof(draft.Tone)}"] =
                ["PreviousDraft.Tone must be one of: professional, empathetic, factual."];
        }

        ValidatePolicySourceUrls(draft.PolicySourceUrls, $"{prefix}.{nameof(draft.PolicySourceUrls)}", errors);
    }

    private static void ValidateSmartCriterion(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (value is { Length: > MaxSmartCriterionLength })
        {
            errors[field] = [$"{field} cannot exceed {MaxSmartCriterionLength} characters."];
        }
    }

    private static void ValidatePolicySourceUrls(
        IReadOnlyList<string>? urls, string field, Dictionary<string, string[]> errors)
    {
        if (urls is null)
        {
            return;
        }

        if (urls.Count > MaxPolicySourceUrlCount)
        {
            errors[field] = [$"{field} cannot contain more than {MaxPolicySourceUrlCount} URLs."];
            return;
        }

        for (var i = 0; i < urls.Count; i++)
        {
            if (urls[i].Length > MaxPolicySourceUrlLength)
            {
                errors[$"{field}[{i}]"] =
                    [$"{field}[{i}] cannot exceed {MaxPolicySourceUrlLength} characters."];
            }
        }
    }

    private static void ValidateIdentifier(
        string? value,
        string propertyName,
        IDictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[propertyName] = [$"{propertyName} is required."];
        }
        else if (value.Length > MaximumIdentifierLength)
        {
            errors[propertyName] = [$"{propertyName} cannot exceed {MaximumIdentifierLength} characters."];
        }
    }
}
