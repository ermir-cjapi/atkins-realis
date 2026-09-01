# ServiceNow AI Search tool prerequisites

## Purpose

This document defines only the platform, security, configuration, and operational prerequisites for implementing the ARIA `get_scoped_knowledge_articles` tool.

The tool searches ServiceNow through the AI Search API and returns bounded, normalized search results to the HR agent. It is separate from the Azure Bot identity, Teams publishing, and the unimplemented `Get Employee Country` tool.

## Target integration

### Search request

```text
GET https://<service-now-instance>/api/atki2/ai_search?upn=<authenticated-user-upn>&q=<search-term>
Authorization: Bearer <service-now-oauth-access-token>
```

The development endpoint is:

```text
https://atkinsdev.service-now.com/api/atki2/ai_search
```

### OAuth token request

The current implementation assumption is:

```text
POST https://<service-now-instance>/oauth_token.do
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials&client_id=<client-id>&client_secret=<client-secret>
```

No OAuth scope or audience is assumed. This contract must be confirmed with the ServiceNow owner before implementation is treated as production-ready.

## 1. ServiceNow prerequisites

The ServiceNow team must provide one OAuth application per environment, or explicitly approve a shared application.

Required values for DEV, UAT, and PROD:

| Value | Secret | Notes |
|---|---:|---|
| ServiceNow instance URL | No | For example, `https://atkinsdev.service-now.com` |
| AI Search path | No | Expected: `/api/atki2/ai_search` |
| OAuth token URL | No | Expected: `https://<instance>/oauth_token.do` |
| OAuth client ID | Treat as protected configuration | Store in Key Vault with the client secret |
| OAuth client secret | **Yes** | Never store in source control or pipeline YAML |
| OAuth scope | No | Required only if ServiceNow configures one |
| OAuth audience | No | Required only if ServiceNow configures one |

The ServiceNow owner must confirm:

1. The OAuth application supports `client_credentials`.
2. Whether client credentials are sent in the form body or with HTTP Basic authentication.
3. Whether `scope` or `audience` is required.
4. The access-token lifetime and token response shape.
5. The OAuth client is authorized to invoke `/api/atki2/ai_search`.
6. The API permits the HR App Service network origin.
7. The `upn` parameter is used only to scope results and does not require delegated ServiceNow user authentication.
8. Expected behavior for unknown, disabled, guest, and non-employee UPNs.
9. Rate limits, result limits, timeout expectations, and `429 Retry-After` behavior.
10. Whether the API contract and available result tables differ by environment.

## 2. Trusted user identity prerequisite

The tool must receive the current employee's UPN from a trusted authenticated channel. The model must not generate it, and callers must not be allowed to provide an arbitrary UPN in the chat message or tool arguments.

For Teams traffic, the trusted identity should originate from the Microsoft 365 Agents SDK after Bot Connector authentication. Confirm which of these is available:

1. A verified UPN directly on the authenticated Teams activity or turn context.
2. The user's Entra object ID, followed by an approved server-side lookup that resolves it to a UPN.
3. Teams SSO, if a delegated user token is required to obtain the UPN.

The implementation owner must document:

- The authoritative claim or activity property.
- Any required object-ID-to-UPN lookup service.
- Behavior when no verified UPN is available.
- Treatment of guest users and alternate email addresses.

If the direct `/api/chat` endpoints remain available, they require their own authenticated identity design. A request-body or untrusted forwarded header is not sufficient.

The bot-only Entra client ID and client secret do not automatically provide an end-user UPN. The bot registration must not be treated as user authentication for this tool.

## 3. Azure Key Vault prerequisites

Use an existing RBAC-enabled Azure Key Vault for ServiceNow OAuth credentials.

Required vault information for each environment:

| Value | Required |
|---|---:|
| Subscription ID | Yes |
| Resource group | Yes |
| Key Vault name | Yes |
| RBAC authorization enabled | Yes |
| Client ID secret name | Yes |
| Client secret secret name | Yes |

Recommended versionless secret names:

- `servicenow-ai-search-client-id`
- `servicenow-ai-search-client-secret`

Requirements:

1. Populate the secrets before deploying the application settings that reference them.
2. Grant the HR API system-assigned managed identity the **Key Vault Secrets User** role on the vault or on the individual secrets.
3. Use versionless App Service Key Vault references so normal secret rotation does not require a configuration change.
4. Do not expose secret values to Bicep outputs, pipeline logs, deployment history, application logs, or telemetry.
5. Record the credential owner, expiry or rotation date, emergency revocation process, and validation procedure.
6. Confirm that the pipeline deployment identity can create the required RBAC assignment. It does not need permission to read the secret values.

Recommended App Service settings:

| Setting | Source |
|---|---|
| `ServiceNow__ClientId` | Key Vault reference |
| `ServiceNow__ClientSecret` | Key Vault reference |

## 4. Non-secret application configuration

Configure these values independently for DEV, UAT, and PROD:

| Setting | Example |
|---|---|
| `ServiceNow__Endpoint` | `https://atkinsdev.service-now.com` |
| `ServiceNow__SearchPath` | `/api/atki2/ai_search` |
| `ServiceNow__TokenEndpoint` | `https://atkinsdev.service-now.com/oauth_token.do` |
| `ServiceNow__RequestTimeoutSeconds` | `30` |
| `ServiceNow__MaxResults` | Product-approved limit |
| `ServiceNow__MaxTitleCharacters` | Product-approved limit |
| `ServiceNow__MaxTextCharacters` | Product-approved limit |
| `ServiceNow__MaxColumnValueCharacters` | Product-approved limit |

Store non-secret environment values in the existing environment-specific Azure DevOps variable groups or in Bicep parameters. Do not hard-code environment URLs in application code.

Production configuration must require HTTPS URLs and reject missing or invalid settings during startup.

## 5. Network prerequisites

The HR App Service must have outbound HTTPS and DNS access to:

- The environment's ServiceNow instance.
- The ServiceNow OAuth token endpoint.
- Azure Key Vault.
- Existing Azure AI Foundry dependencies.

Confirm with the network and ServiceNow teams:

1. DNS resolution from the App Service Environment.
2. Firewall, proxy, and network security group rules.
3. Whether ServiceNow requires outbound IP allow-listing.
4. TLS inspection compatibility.
5. ServiceNow certificate trust and TLS version support.
6. That redirects do not send OAuth credentials or bearer tokens to another host.

## 6. Application implementation requirements

The implementation must include:

1. A callable Agent Framework function named `get_scoped_knowledge_articles`.
2. One model-visible argument, `q`, containing one to three generated search keywords.
3. Server-side acquisition of the verified current-user UPN.
4. A dedicated OAuth client-credentials token provider.
5. In-memory token caching until shortly before expiry.
6. Serialized token refresh to prevent concurrent refresh storms.
7. A single token refresh and request retry after a ServiceNow `401`.
8. No automatic retries for other failures unless ServiceNow's rate-limit contract is later documented.
9. URL encoding for both `upn` and `q`.
10. Cancellation and bounded request timeouts.
11. Controlled failure results that allow the agent to state that approved guidance could not be verified.

The application must never log:

- Client secrets.
- OAuth access tokens.
- Authorization headers.
- Complete UPNs.
- Raw remote error bodies that may contain sensitive data.

## 7. Response handling requirements

The supplied ServiceNow response contains mixed result types such as `kb_knowledge` and `sc_cat_item`. The tool must support all returned table types rather than assuming every result is a knowledge article.

Return a bounded normalized representation containing:

- Search term and count.
- Result `sysId`.
- Result `table`.
- Plain-text title.
- Plain-text result text or snippet.
- Useful columns with `fieldName`, `label`, `value`, and `displayValue`.

Do not return `resultsActionsMap` or `availableActions` unless a future feature requires ServiceNow actions.

Content handling must:

1. Remove ServiceNow `<highlight>` and `</highlight>` tags.
2. Decode HTML entities such as `&amp;`.
3. Treat all returned content as untrusted data, not instructions to the model.
4. Limit the number of results and the length of every text field.
5. Handle missing properties and future result tables safely.
6. Distinguish an empty successful search from an unavailable or rejected search.
7. Treat a non-200 payload-level `result.status` as a failure even if the HTTP status is `200`.

## 8. Error-handling requirements

Map failures to controlled internal categories without exposing credentials or remote internals:

| Condition | Expected handling |
|---|---|
| Missing verified UPN | Do not call ServiceNow; return identity unavailable |
| Invalid or empty `q` | Reject tool input |
| OAuth `400`/`401` | Authentication unavailable; do not reveal response body |
| Search `401` | Invalidate token and retry once |
| Search `403` | Access denied; no retry |
| Search `429` | Report temporarily unavailable; honor documented policy if supplied |
| Search `5xx` | Report temporarily unavailable |
| Timeout/cancellation | Stop promptly and return or propagate the appropriate controlled outcome |
| Malformed JSON | Report invalid upstream response |
| Payload status other than `200` | Report upstream failure |
| No results | Return a successful empty result |

## 9. Deployment prerequisites

Before deployment to an environment, confirm:

- The ServiceNow OAuth application exists and is enabled.
- The OAuth contract has been tested outside the application.
- The Key Vault secrets exist.
- The HR API managed identity has Key Vault access.
- Non-secret ServiceNow settings are populated.
- The trusted UPN source is implemented and tested.
- Network access to ServiceNow and Key Vault is verified.
- ServiceNow permissions and rate limits are confirmed.
- Application Insights and other telemetry are configured to exclude sensitive data.

## 10. Test and acceptance requirements

Automated tests must cover:

- OAuth request shape and token parsing.
- Token reuse, expiry, concurrent refresh, and invalidation.
- Query-string encoding.
- Bearer-token attachment.
- One-time retry after `401`.
- `403`, `429`, `5xx`, timeout, cancellation, and malformed responses.
- Payload-level status errors.
- Mixed `kb_knowledge` and `sc_cat_item` results.
- Highlight removal and HTML entity decoding.
- Empty and truncated results.
- Trusted UPN resolution and missing identity.
- Tool registration under `get_scoped_knowledge_articles`.
- Safe tool outcomes for success, no results, and unavailable service.

Environment acceptance must verify:

1. A valid authenticated employee causes ServiceNow to receive the correct encoded UPN.
2. The model cannot override the UPN through its tool arguments or conversation text.
3. OAuth tokens are reused and refreshed correctly.
4. Relevant mixed-table results are available to the agent.
5. No OAuth secret, access token, authorization header, or complete UPN appears in logs or telemetry.
6. ServiceNow outages produce the approved guidance-unavailable response rather than a general-knowledge answer.

## 11. Required ownership decisions

The following values and decisions remain external prerequisites:

| Decision or value | Owner |
|---|---|
| DEV/UAT/PROD ServiceNow instance URLs | ServiceNow platform owner |
| OAuth token contract, scope, and audience | ServiceNow platform owner |
| OAuth client creation and permissions | ServiceNow platform owner |
| Trusted Teams user-to-UPN mechanism | Teams/identity engineering |
| Existing Key Vault identifiers | Azure platform owner |
| Secret creation and rotation | Secrets operations |
| App Service outbound access and allow-listing | Network/security team |
| Search result and text limits | HR product/engineering |
| Telemetry redaction validation | Security/engineering |

The tool should not be enabled in an environment until these dependencies are satisfied and recorded.
