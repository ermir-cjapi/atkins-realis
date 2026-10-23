# Teams channel integration

## Ownership and topology

Agents - HR hosts both the authenticated channel-neutral chat API and the Microsoft Teams Bot
Connector transport. No separate bot-handler application is required.

The request path is:

1. Microsoft Teams sends an activity through Azure Bot Service.
2. App Gateway and APIM forward the request to `POST /api/messages` on Agents - HR.
3. Agents - HR validates the Bot Connector bearer token against the HR bot application ID.
4. The Agents SDK exchanges or retrieves the employee's HR API token through the `user.read`
  Azure Bot OAuth connection.
5. `HrTeamsAgent` validates that delegated token and pushes a turn-scoped employee identity.
6. `HrTeamsAgent` validates the message and invokes `IAgentChatService`.
7. The Agents SDK sends the reply to the activity's trusted service URL using the HR bot's
   user-assigned managed identity.

The Azure Bot registration and Teams app package remain owned by the Bots repository. The HR App
Service owns Bot Connector activity handling and the HR agent implementation.

## APIM contract

APIM exposes the messaging endpoint configured on the HR Azure Bot and forwards it to:

```http
POST https://<agents-hr-app>/api/messages
Authorization: Bearer <Bot Connector token>
Content-Type: application/json
```

The APIM operation must:

- Preserve the incoming `Authorization` header without replacing it with an APIM managed-identity
  token.
- Accept `POST` only and require no APIM subscription key.
- Route only the HR bot operation to the Agents - HR backend.
- Preserve the request body without interpreting identity fields in the activity payload.
- Reach and resolve the internal App Service Environment hostname.
- Avoid logging activity bodies or bearer tokens.

Bot Connector authentication is enforced by Agents - HR. APIM authentication is not a substitute
for signature, issuer, audience, lifetime, and service URL validation in the application.

## Application configuration

The App Service is assigned both identities:

- Its system-assigned identity for Foundry, Key Vault, Application Insights, and HR API OBO.
- The HR Azure Bot's user-assigned identity for outbound Bot Connector calls.

Required non-secret settings are provisioned by Bicep:

```text
Teams__Enabled=true
Teams__AppId=<HR bot managed-identity client ID>
Teams__TenantId=<tenant ID>
Connections__ServiceConnection__Assembly=Microsoft.Agents.Authentication.Msal
Connections__ServiceConnection__Type=MsalAuth
Connections__ServiceConnection__Settings__AuthType=UserManagedIdentity
Connections__ServiceConnection__Settings__ClientId=<HR bot managed-identity client ID>
Connections__ServiceConnection__Settings__Scopes__0=https://api.botframework.com/.default
ConnectionsMap__0__ServiceUrl=*
ConnectionsMap__0__Connection=ServiceConnection
CloudAdapterOptions__ValidateServiceUrl=true
Teams__AuthorizationHandler=hr
Teams__StorageContainerUri=https://<account>.blob.core.windows.net/agents-hr-authorization
AgentApplication__UserAuthorization__Handlers__hr__Settings__AzureBotOAuthConnectionName=user.read
AgentApplication__UserAuthorization__Handlers__hr__Settings__EnableSso=true
```

`Teams__SsoEnabled` and `AgentApplication__UserAuthorization__AutoSignIn` are both set from the
`teamsSsoEnabled` variable (`true` or `false`) in the `Agents-HR-{ENV}` variable group through
`apiAdditionalAppSettings`. Every variable group must define it.

No client secret is used for Bot Connector traffic or for the `user.read` OAuth connection. The
connection uses the **AAD v2 with Federated Credentials** provider, so the Bot Service Token Store
authenticates to the HR API app registration through a federated identity credential. Add this
credential to the HR API app registration (scenario **Other issuer**) in each environment:

```text
Issuer:   https://login.microsoftonline.com/<tenant-id>/v2.0
Subject:  /eid1/c/pub/t/<base64url tenant-id>/a/9ExAW52n_ky4ZiS_jhpJIQ/<unique-identifier>
Audience: api://AzureADTokenExchange
```

`9ExAW52n_ky4ZiS_jhpJIQ` is the Base64url-encoded Bot Service Token Store application ID.
`<unique-identifier>` is `orbauto-bots-bot-hr-xx-<env>-user.read`, which the Bots deployment sets as
the connection's Unique Identifier. Compute the encoded tenant ID with:

```powershell
[Convert]::ToBase64String(([guid]'<tenant-id>').ToByteArray()).TrimEnd('=').Replace('+','-').Replace('/','_')
```

The provider supports single-tenant app registrations only.

The intended SSO/API registration is
[`orb.automate.bots.oauth.entra`](https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/Overview/appId/66e39820-e7d1-4fc3-8b6a-f33b4618a376)
(client ID `66e39820-e7d1-4fc3-8b6a-f33b4618a376`). Before enabling SSO, it must expose
`access_as_user`, have an Application ID URI used consistently by the Teams manifest, Bot OAuth
connection and `EntraAuthentication:Audience`, use access token version 2, include the required
downstream delegated permissions and consent, pre-authorize the current Teams desktop/mobile and
web clients, and contain the Bot Service Token Store federated credential described above. The
registration was reviewed while those items were still incomplete; do not enable SSO merely by
copying its client ID into configuration.

A separate federated credential on the same registration must trust each Agents - HR App Service
system-assigned identity. The runtime uses that workload identity as its confidential-client
assertion for downstream OBO exchanges; it is independent of both the Bot Service Token Store
credential and the HR bot's user-assigned Connector identity.

The HR bot managed identity cannot replace this app registration for Teams SSO. Keep it as the
Connector identity. For the default public-cloud Bot Service endpoint, the registration also needs
`https://token.botframework.com/.auth/web/redirect`; use the appropriate redirect URI if a regional
or sovereign-cloud token service is selected.

## Authentication boundaries

`POST /api/messages` accepts Bot Connector tokens for the HR Azure Bot audience. The existing
`POST /api/chat` and `POST /api/chat/stream` routes continue to require a delegated HR API token
with the configured `scp` value. A Connector token cannot authorize the direct chat routes.

The Connector token authenticates Azure Bot Service, not the employee. It is never used as the user
assertion for SharePoint OBO or as the source of the employee UPN. The separately acquired SSO
token is validated for signature, tenant issuer, HR API audience, lifetime, `access_as_user`, `tid`,
and `oid` before it enters the async-local turn context. ServiceNow remains fail-closed when that
validated token has no usable UPN/email claim.

## Message behavior

The Teams adapter:

- Requires the `hr` user-authorization handler before running a message route.
- Processes Teams token-exchange activities through the Agents SDK.
- Stores authorization flow and exchange-deduplication state in shared blob storage outside local
  Development.
- Handles message activities.
- Rejects empty and oversized messages.
- Calls the existing `IAgentChatService` inside a disposable, concurrency-isolated user scope.
- In personal chats only, keeps the most recent exchanges (`Agent:HistoryMaxExchanges`, default 5,
  capped at `Agent:HistoryMaxCharacters`, default 12,000) in conversation state and replays them to
  the model as user/assistant messages. The stored assistant text is the reply the user was shown,
  including its sources. The previous user message is also appended to the SharePoint retrieval
  query so follow-up questions keep their topic. History has no expiry; the user clears it by
  sending `/reset`. Group chats and channels stay stateless because their conversation state is
  shared by every member.
- Handles `/signout` (trimmed, case-insensitive) before any other route and without requiring sign-in:
  it signs the user out of the Azure Bot Token Service and clears their bot state. See
  [User commands](#user-commands).
- Conversation history is HR conversation text held in the bot state blob container outside local
  Development.
- Sends the generated response plus only the controlled citations returned by the service.
- Returns a generic reply when processing fails and does not log prompt or response text.

The user authorization flow is durable across App Service instances. General conversation ordering
and exactly-once model execution are separate concerns.

### User commands

| Command | What it clears | Personal chat | Group chat / channel |
| --- | --- | --- | --- |
| `/reset` | Conversation history only. The user stays signed in. | History cleared | No effect (no history is kept) |
| `/signout` | Delegated token in the Azure Bot Token Service (when `Teams:SsoEnabled` is `true`), SDK per-user sign-in state, and the sender's user and private conversation state. | OAuth flow and all conversation state also cleared (history, message count, last draft/objective) | Shared conversation and OAuth flow state are kept; only the sender's per-user state is cleared |

`/reset` only forgets the chat. `/signout` returns the user to the same state as a brand-new user.
The delegated token is not held in blob storage: it lives in the Azure Bot Token Service and is
removed by a sign-out call to that service. Blob storage only holds the SDK sign-in/flow records and
bot state. `/signout` deletes the sender's records; in groups/channels the SDK's conversation-wide
OAuth flow record is retained because it may belong to another member. For shared chats the bot
calls the Token Service client directly: the SDK's higher-level sign-out would also delete that
shared flow record. If the Token Service call fails or times out, the bot still clears its state,
logs a warning without tokens or user identifiers, and tells the user sign-out may be incomplete.
Each run logs `Teams user signed out` with `ConversationType`, `TokenServiceSignOut`
(`succeeded`, `failed` or `skipped`) and `StateCleared`.

For testing, `/signout` lets a tester repeat the first-run experience: the next message follows the
new-user silent SSO path (token exchange) instead of reusing a stored token. While silent SSO is
not working, that next message will hit the same silent-failure path a new user sees, until SSO is
fixed or a manual sign-in fallback is available.

`/signout` is handled before the SDK's automatic sign-in and pending-flow continuation, so it also
works without a token or while sign-in is stuck. All other activities retain the SDK's normal
authentication pipeline and configured `AutoSignIn` behaviour.

## Environment verification

Before enabling an environment:

1. Confirm the App Service has the same user-assigned identity configured on the HR Azure Bot.
2. Confirm APIM forwards the Bot Connector `Authorization` header unchanged.
3. Confirm an unauthenticated `/api/messages` request returns `401`.
4. Confirm an activity for another bot application ID is rejected.
5. Confirm APIM can reach the internal Agents - HR App Service.
6. Send a DEV Teams message and verify one response is delivered through the Connector service.
7. Verify silent token exchange, SharePoint permission trimming, and the ServiceNow employee UPN.
8. Revoke consent and verify one safe sign-in prompt with no repeated sign-in loop.
9. Verify no Connector token, delegated token, prompt body, or response body is emitted to logs.
