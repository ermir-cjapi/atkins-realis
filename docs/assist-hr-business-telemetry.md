# Assist | HR business telemetry implementation

The API emits chat lifecycle records to the Application Insights `customEvents` table through the Azure Monitor OpenTelemetry log exporter. The reserved structured-log property `microsoft.custom_event.name` supplies the event name. This page is the single reference for the current event contract, AI accounting rules, metrics, privacy constraints, and validation queries.

## Implemented events

- `ConversationStarted` — once when the client marks the first turn.
- `UserMessageReceived` — once for every accepted user turn.
- `AgentResponseSent` — once after a successful complete or streamed answer.
- `CitationEmitted` — once per source explicitly cited by the completed answer.
- `ConversationClosed` — once when the client supplies close metadata on its final turn.

Feedback and HITL events are intentionally outside this implementation. `ConversationClosed` is an explicit event in this implementation, overriding catalogue section 5.8; downstream KQL must not also materialise it from inactivity or counts will be duplicated.

## Chat request contract

Both `/api/chat` and `/api/chat/stream` require the caller to provide lifecycle truth:

```json
{
  "message": "How much annual leave do I receive?",
  "conversationId": "teams-thread-id",
  "activityId": "bot-activity-id",
  "messageIndex": 1,
  "isConversationStart": true,
  "close": null
}
```

On a closing turn, `close` has this shape:

```json
{
  "conversationDurationSeconds": 180,
  "turnCount": 3,
  "closeReason": "UserEnded",
  "resolved": true
}
```

Valid close reasons are `UserEnded`, `InactivityTimeout`, and `Escalated`. Teams identifiers are non-empty opaque strings with a 512-character maximum. The caller owns ordering, deduplication, start detection, and close detection because no server-side conversation store is in scope.

## Identity and classification

`userId` contains the authenticated UPN by explicit product decision. When `GraphProfile:Enabled` is true, the API uses the developer identity locally or exchanges the validated inbound token on behalf of the user outside Development, then reads `usageLocation`, `department`, `companyName` and `employeeOrgData` (`division`, `costCenter`) from Microsoft Graph. Values that are not populated in Entra ID are emitted as `unknown`; there is no standard Graph business-unit property, so business unit is not emitted. Graph failures produce `unknown` and do not fail the chat turn. The app registration requires delegated Microsoft Graph `User.Read` permission with appropriate tenant consent.

The completed question and answer are classified by a separate Foundry request. Only the validated intent label, canonical exemplar, taxonomy topic/version, confidence and out-of-scope flag are emitted. Raw questions and answer text are not included in custom-event properties.

The classifier receives the model's own reply rather than the citation-validated text, so an out-of-scope rejection is still recognised when citation policy replaces it with the safe fallback. Taxonomy version `1.1` adds the `Other` topic so that general questions outside the HR taxonomy classify successfully instead of failing closed.

## Event contract

Every custom event includes `conversationId`, `activityId`, `correlationId`, `userId`, `userCountry`, `userDepartment`, `userCompany`, `userDivision`, `userCostCenter`, `channel`, `environment`, and `agentVersion`. Custom events must be retained without sampling because dashboard counts are exact business facts.

### AgentResponseSent

`AgentResponseSent` remains response-only: it is emitted once only after a response has been produced. Consumers must deduplicate retries with `conversationId + activityId`. It is not a terminal-attempt event; attempted turns, including cancellation and abandoned streams, are represented by the `chat.turn.*` metrics.

The current event writes `schemaVersion=4` so consumers can identify its shape. Version 4 adds `historyExchangeCount` and `historyCharacters` to version 3, which added `outOfScopeRejected` to version 2. It includes `messageIndex`, `responseMode` (`complete` or `streaming`), `responseOutcome`, `classificationOutcome`, `providerResponseId`, and these groups:

- **History:** `historyExchangeCount` and `historyCharacters` describe the earlier exchanges replayed to the model for the turn. Both are zero for stateless turns (group chats, channels and the HTTP API). History content is never emitted.

- **Scope:** `outOfScopeRejected` is the classifier's judgement of whether the answer told the user that all or part of their request is outside what the assistant can help with. It is null when classification did not succeed.
- **Usage:** nullable input, output, total, cached-input and reasoning tokens for `main`, `classifier` and `combined`, plus `*UsageAvailable`, `*UsageComplete` and physical call counts. Existing `promptTokens` and `completionTokens` retain their original meaning: main-agent input and output totals.
- **Models:** `modelDeployment` is configured routing intent. `finalMainModelId`, `finalClassifierModelId` and `distinctActualModelIds` are provider-returned facts. `multipleModelsObserved` indicates routing or model change. Actual model identifiers are event properties only, never metric tags. `providerResponseId` is the single response identifier for the delivered answer; per-component response identifiers are deliberately not duplicated.
- **Timing:** `groundingDurationMs` is always zero — retrieval is model-controlled via `search_hr_knowledge` and contributes to `modelDurationMs` as part of the tool-loop rounds. `modelDurationMs` is logical Agent Framework generation time including all tool-loop rounds; `classificationDurationMs` is the separate classifier operation; `totalTurnDurationMs` covers the service turn. `providerTtftMs` is time from streaming provider enumeration to first non-empty text. `streamingOutputBuffered` indicates citation-policy buffering (true for regenerate turns with a previous draft or objective).
- **Grounding:** tool invocation counts, retrieved, returned and filtered result counts, `retrievedSourceSystems`, citation-validation outcome, citable, cited and stale source counts, nullable citation coverage, and safe-fallback status.

#### Usage availability versus completeness

Absent provider counts are never converted to zero. Totals sum only the values the provider actually reported, so:

- `*UsageAvailable` means at least one contributing call reported a count.
- `*UsageComplete` means **every** physical call in that scope reported usage.

Treat a total with `*UsageComplete = false` as a lower bound rather than an exact figure. This most commonly occurs when a tool-loop round returns no usage payload.

#### Token accounting boundary

Every physical provider call below Agent Framework orchestration is counted once. Main totals therefore include all tool-loop rounds. Classifier totals contain only the dedicated classification call. Combined totals are the sum of every observation in the turn; logical `AgentResponse.Usage` is deliberately not added again.

#### Grounding source fields

Two related but distinct fields exist and must not be substituted for one another:

| Field | Meaning |
|-------|---------|
| `groundingSource` | Citation-derived: `SphereOnly`, `ServiceNowOnly`, `Both` or `NoneCited`. |
| `retrievedSourceSystems` | Retrieval-derived: which systems returned results, regardless of citation. |

#### Citation coverage definition

Coverage is computed over **distinct source documents**, not chunks or citation rows:

- Citable = distinct SharePoint parent documents offered in the grounding ledger plus distinct ServiceNow records made available to the model.
- Cited = distinct citable documents referenced by the delivered answer.
- Coverage = cited / citable, and is unavailable when nothing was citable.

Multiple chunks of one document, and repeated citations of the same source, therefore count once.

#### Outcomes

Classifier semantic outcomes are `succeeded`, `invalid_response` and `failed`. Invalid and failed classifications still produce the controlled `unknown` classification; caller cancellation propagates. Turn metric outcomes are `success`, `safe_fallback`, `model_failure`, `retrieval_failure`, `cancelled` and `abandoned`. SSE delivery is separately recorded as `completed` or `client_aborted`.

#### Event size

The current `AgentResponseSent` contract emits roughly fifty custom dimensions. Confirm Application Insights ingestion volume, retention and the no-sampling exemption for this event before enabling it in production.

### Other events

- `ConversationStarted` contains only the common dimensions and is emitted when `isConversationStart` is true.
- `UserMessageReceived` adds `messageIndex` and `messageLength`; it never contains message text.
- `CitationEmitted` adds the approved citation metadata `fileId`, `fileName`, `sourceSystem`, `sitePath`, `kbNumber`, and `freshnessFlag`.
- `ConversationClosed` adds `conversationDurationSeconds`, `turnCount`, `closeReason`, and `resolved` when the client supplies closure metadata.

## OpenTelemetry metrics

Meter: `Orb.Automate.Agents.HR.ChatAI`.

- `chat.turn.count`, `chat.turn.duration`
- `ai.provider.call.count`, `ai.provider.call.duration`, `ai.provider.ttft`
- `ai.provider.token.count`, `chat.turn.token.count`
- `chat.grounding.duration`, `chat.grounding.result.count`
- `chat.tool.invocation.count`, `chat.tool.duration`
- `chat.citation.validation.count`, `chat.citation.coverage`
- `chat.classification.count`
- `chat.sse.first_flush`, `chat.sse.outcome.count`

`ai.provider.token.count` and `chat.turn.token.count` describe the same tokens at different scopes: one per physical call, one per completed turn. **Never add the two series together.** Both are tagged by `component`, and `chat.turn.token.count` publishes only `main_agent` and `intent_classifier`, so summing it without a component filter is safe and never double counts.

Allowed tags are bounded enums or configuration: `component`, `streaming_mode`, controlled `outcome`, `failure_kind`, `tool`, `invocation_mode`, `token_type`, `grounding_source`, `result_type` and `configured_deployment`. Metrics never carry user, conversation, activity, correlation, response or actual-model identifiers; URLs; titles; file or KB identifiers; queries; exception data; or response bodies.

No raw prompts, answers, search queries, document bodies, credentials, authorization values or exception messages are emitted by the aggregate telemetry. Classifier-supplied `normalizedIntent` and `intentExemplar` are length-capped, normalised and rejected when they copy the question or answer, so model-generated free text cannot leak into business events. This implementation does not estimate or emit monetary cost.

## Privacy and data protection

`userId` contains the authenticated UPN by explicit product decision, which makes every Assist | HR custom event personal data. Confirm the DPIA, retention period and access controls for the Application Insights workspace before enabling production export. Graph enrichment is attributed only when the Graph profile belongs to the same UPN as the chat user; otherwise `userCountry`, `userDepartment`, `userCompany`, `userDivision` and `userCostCenter` remain `unknown`. Locally this means a developer credential never enriches a different configured test identity.

## KQL validation and analysis

Validate the complete lifecycle event stream:

```kusto
customEvents
| where timestamp > ago(1h)
| where name in ("ConversationStarted", "UserMessageReceived", "AgentResponseSent", "CitationEmitted", "ConversationClosed")
| extend
  conversationId = tostring(customDimensions.conversationId),
  activityId = tostring(customDimensions.activityId),
  correlationId = tostring(customDimensions.correlationId),
  userId = tostring(customDimensions.userId),
  environment = tostring(customDimensions.environment)
| project timestamp, name, conversationId, activityId, correlationId, userId, environment, customDimensions
| order by timestamp asc
```

Analyse the current `AgentResponseSent` contract:

Token fields are nullable. `tolong()` on an absent dimension yields null, so use `coalesce` only where a zero default is genuinely intended.

```kusto
customEvents
| where name == "AgentResponseSent"
| extend schemaVersion = toint(customDimensions.schemaVersion)
| where schemaVersion >= 2
| extend conversationId = tostring(customDimensions.conversationId),
         activityId = tostring(customDimensions.activityId),
         combinedInputTokens = tolong(customDimensions.combinedInputTokens),
         combinedOutputTokens = tolong(customDimensions.combinedOutputTokens),
         combinedTotalTokens = tolong(customDimensions.combinedTotalTokens),
         combinedUsageComplete = tobool(customDimensions.combinedUsageComplete),
         totalTurnDurationMs = tolong(customDimensions.totalTurnDurationMs),
         citationCoverage = todouble(customDimensions.citationCoverage)
| summarize arg_max(timestamp, *) by conversationId, activityId
| project timestamp, conversationId, activityId, combinedInputTokens,
          combinedOutputTokens, combinedTotalTokens, combinedUsageComplete,
          totalTurnDurationMs, citationCoverage
```

Exclude incomplete usage when an exact total is required:

```kusto
customEvents
| where name == "AgentResponseSent"
| extend conversationId = tostring(customDimensions.conversationId),
         activityId = tostring(customDimensions.activityId),
         combinedTotalTokens = tolong(customDimensions.combinedTotalTokens),
         combinedUsageComplete = tobool(customDimensions.combinedUsageComplete)
| summarize arg_max(timestamp, *) by conversationId, activityId
| where combinedUsageComplete
| summarize exactTurnTokens = sum(combinedTotalTokens) by bin(timestamp, 1h)
```

```kusto
customEvents
| where name == "AgentResponseSent"
| extend mainCalls = toint(customDimensions.mainModelCallCount),
         classifierCalls = toint(customDimensions.classifierModelCallCount),
         routed = tobool(customDimensions.multipleModelsObserved)
| summarize responses=count(), mainCalls=sum(mainCalls), classifierCalls=sum(classifierCalls),
            routedResponses=countif(routed) by bin(timestamp, 1h)
```

Count out-of-scope rejections by topic:

```kusto
customEvents
| where name == "AgentResponseSent" and toint(customDimensions.schemaVersion) >= 3
| extend conversationId = tostring(customDimensions.conversationId),
         activityId = tostring(customDimensions.activityId)
| summarize arg_max(timestamp, *) by conversationId, activityId
| where tobool(customDimensions.outOfScopeRejected)
| summarize rejections = count() by topic = tostring(customDimensions.topic), bin(timestamp, 1d)
```
