# Orb.Automate - Agents - HR (Assist | HR)

Production-oriented ASP.NET Core chatbot scaffold using Microsoft Agent Framework, Microsoft 365
Agents SDK, Azure AI Foundry, managed identity, and Azure App Service.

The API hosts the Microsoft Teams Bot Connector endpoint behind the enterprise APIM gateway. See
the [Teams channel integration contract](docs/teams-channel-integration.md) for authentication,
routing, managed-identity, and deployment requirements.

## Architecture

```text
Aspire AppHost -> ASP.NET Core API -> Microsoft Agent Framework -> Azure AI Foundry model
      |                      \-> OpenTelemetry -> Aspire dashboard / Application Insights
      \-> DevUI aggregator (Aspire.Hosting.AgentFramework.DevUI)
```

- `POST /api/chat` returns a complete response, including a structured `emailDraft` when the agent drafts an email, or a structured `smartObjective` when the agent writes a SMART performance objective.
- `POST /api/chat/stream` emits Server-Sent Events (SSE). The final event includes `emailDraft` or `smartObjective` when present; the visible `text` has the corresponding `email-draft`/`smart-objective` fence removed.
- To refine a previous draft or objective, resend the request with `previousDraft` **or** `previousObjective` (never both) plus a `regenerateInstruction`, e.g. `"Make it more measurable."` or `"Add a timeframe."`.
- `POST /api/messages` receives authenticated Bot Connector activities when Teams is enabled.
- `GET /health/live` is a liveness probe.
- `GET /health/ready` validates application readiness.
- `POST /v1/responses` and `/v1/conversations` expose the OpenAI-compatible agent surface in Development only; the Aspire DevUI resource aggregates them.
- Conversations are stateless in this starter. Persist Agent Framework sessions before relying on multi-turn server-side history.

## Projects

| Project | Purpose |
|---------|---------|
| `src/Orb.Automate.Agents.HR.Api` | The deployed HR agent API. |
| `src/Orb.Automate.Agents.HR.AppHost` | Aspire AppHost: orchestrates the API and hosts the Agent Framework DevUI resource. Local development only. |
| `src/Orb.Automate.Agents.HR.ServiceDefaults` | Shared OpenTelemetry, service discovery and resilience defaults. |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Aspire CLI](https://aspire.dev/get-started/install-cli/)
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli)
- An Azure AI Foundry/Azure AI Services resource with a deployed chat model
- The `Cognitive Services OpenAI User` role on that resource
- A ServiceNow OAuth client with access to the AI Search API when ServiceNow retrieval is enabled

If the .NET 10 SDK is installed per-user under `%LOCALAPPDATA%\Microsoft\dotnet`, configure the current PowerShell session before building or running Aspire:

```powershell
$dotnetRoot = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:PATH = "$dotnetRoot;$env:PATH"
$env:DOTNET_ROOT = $dotnetRoot
$env:DOTNET_ROOT_X64 = $dotnetRoot
dotnet --list-sdks
```

Setting `PATH` selects the SDK for `dotnet` commands. Setting `DOTNET_ROOT` and `DOTNET_ROOT_X64` also ensures that AppHost and child application executables locate the matching .NET 10 runtime instead of a system-wide older runtime.

## Quick start with Aspire

Run these steps from the repository root. Replace every `<...>` placeholder with a real value; do not save the placeholder text as a user secret.

1. Configure this PowerShell session to use the per-user .NET 10 installation when applicable:

      ```powershell
      $dotnetRoot = "$env:LOCALAPPDATA\Microsoft\dotnet"
      $env:PATH = "$dotnetRoot;$env:PATH"
      $env:DOTNET_ROOT = $dotnetRoot
      $env:DOTNET_ROOT_X64 = $dotnetRoot
      dotnet --list-sdks
      ```

      Confirm that the output includes a .NET 10 SDK.

2. Authenticate and select the required Azure subscription:

      ```powershell
      az login
      az account set --subscription '<subscription-id>'
      ```

3. Store the required Foundry configuration in the API project's user-secrets store:

      ```powershell
      dotnet user-secrets --project src/Orb.Automate.Agents.HR.Api set "Foundry:Endpoint" "https://<account-name>.openai.azure.com/"
      dotnet user-secrets --project src/Orb.Automate.Agents.HR.Api set "Foundry:DeploymentName" "<deployment-name>"
      ```

4. Start the AppHost and leave the terminal running:

      ```powershell
      cd src/Orb.Automate.Agents.HR.AppHost
      aspire run
      ```

      Alternatively, start it in the background and return the dashboard details as JSON:

      ```powershell
      aspire run --detach --non-interactive --format Json
      ```

5. Open the dashboard URL printed by Aspire, wait for `hr-api` to become **Healthy**, and then open the `devui` endpoint.

After changing user secrets, restart only `hr-api` from the Aspire dashboard; rebuilding is unnecessary. Stop a detached AppHost before rebuilding it, otherwise Windows may report that the AppHost executable is locked.

For SharePoint knowledge retrieval you also need the Azure AI Search index built from the HR SharePoint site, with `permissionFilterOption` disabled (results are not trimmed per user; the `UserIds`/`GroupIds` ACL fields are still populated by the indexer), plus the `Search Index Data Reader` role on the search service for both your own identity and the App Service managed identity. See the [Azure AI Search infrastructure guide](docs/azure-ai-search-infrastructure.md) and [SharePoint AI Search tool prerequisites](docs/sharepoint-ai-search-tool-prerequisites.md).

## Local configuration

Authenticate without storing a secret:

```powershell
az login
az account set --subscription '<subscription-id>'
$env:Foundry__Endpoint='https://<account-name>.openai.azure.com/'
$env:Foundry__DeploymentName='<model-deployment-name>'
dotnet run --project src/Orb.Automate.Agents.HR.Api --launch-profile http
```

The environment variables above apply only to the current PowerShell process. For persistent local configuration, store the values once with .NET user secrets from the repository root:

```powershell
dotnet user-secrets set "Foundry:Endpoint" "https://<account-name>.openai.azure.com/" --project src/Orb.Automate.Agents.HR.Api
dotnet user-secrets set "Foundry:DeploymentName" "<model-deployment-name>" --project src/Orb.Automate.Agents.HR.Api
dotnet run --project src/Orb.Automate.Agents.HR.Api --launch-profile http
```

User secrets are stored in the current Windows user profile, outside the repository, and are loaded automatically in Development. No terminal script is required. Use `dotnet user-secrets list --project src/Orb.Automate.Agents.HR.Api` to verify that both keys exist without editing tracked configuration files.

## Foundry Responses compatibility

The main agent uses Azure OpenAI Responses; the telemetry classifier still uses Chat Completions
without tools. Responses is recommended for reasoning-enabled tool loops, but the restriction on
combining reasoning and tools in Chat Completions is model-dependent, not universal.

With Azure.AI.OpenAI 2.9.0-beta.1, the provider endpoint is
`/openai/responses?api-version=2025-04-01-preview`. This is separate from the application's
Development-only `/v1/responses` endpoint used by DevUI. The offline contract tests pin this request
shape; they do not establish availability for an actual Azure model deployment or region.

`Foundry:ReasoningEffort` accepts `none`, `low`, `medium`, `high`, `xhigh`, or the alias `extrahigh`
(case-insensitive, surrounding whitespace ignored). Blank values use the model default. Unsupported
values, including numeric strings and `max`, fail startup. The current Microsoft.Extensions.AI
adapter cannot represent `max`. Provider support for accepted values still depends on the model.

Before rollout, use a non-sensitive question against the target deployment that invokes the
`search_hr_knowledge` tool. Verify tool execution and a final answer, then repeat through the
streaming endpoint. Check provider-call telemetry for the tool loop, token usage, and successful
completion. Record the model version, Azure region, and effective effort in the PR. Simulated
provider tests are not a substitute for this live compatibility check.

Response-storage policy is a separate outstanding decision; this migration does not explicitly
disable provider storage. Confirm the retention policy for HR payloads before production rollout.

## Run with Aspire

Start the AppHost instead of the API to get the Aspire dashboard, structured telemetry and the DevUI resource:

```powershell
cd src/Orb.Automate.Agents.HR.AppHost
aspire run
```

The dashboard lists the `hr-api` and `devui` resources. Open the `devui` endpoint to chat with the `Assist | HR` agent; the aggregator forwards requests to the API's `/v1/responses` and `/v1/conversations` endpoints. Foundry settings continue to come from the API's user secrets.

To start the AppHost in the background and return its dashboard URL as JSON:

```powershell
aspire run --detach --non-interactive --format Json
```

The AppHost requires the Aspire CLI bundle because it supplies the DCP orchestration executable and dashboard binaries. Local builds enable this bundle automatically. Azure DevOps builds disable it because build agents only compile the AppHost and do not run it.

### SharePoint knowledge retrieval

SharePoint retrieval is off by default. Enable it locally with:

```powershell
dotnet user-secrets set "SharePointSearch:Enabled" "true" --project src/Orb.Automate.Agents.HR.Api
dotnet user-secrets set "SharePointSearch:Endpoint" "https://<search-service>.search.windows.net" --project src/Orb.Automate.Agents.HR.Api
dotnet user-secrets set "SharePointSearch:IndexName" "<index-name>" --project src/Orb.Automate.Agents.HR.Api
dotnet user-secrets set "SharePointSearch:SemanticConfigurationName" "<semantic-configuration-name>" --project src/Orb.Automate.Agents.HR.Api
```

In Development the tool sends your own Azure CLI identity to the search service as the query source authorization, so results are trimmed to the documents *you* are permitted to read. Outside Development the inbound bearer token is exchanged on behalf of the user, which additionally requires `OnBehalfOf:TenantId` and `OnBehalfOf:ClientId`.

Retrieval limits (`MaxResults`, `MaxLlmResults`, `MaxLlmPayloadCharacters`, `MinimumRerankerScore`, and related keys) have conservative defaults in `appsettings.json`. `MinimumRerankerScore` in particular is an unvalidated starting point and must be calibrated against representative HR questions before the answers are trusted.

The app uses `AzureCliCredential` locally and `DefaultAzureCredential` for the App Service managed identity in Azure. API-key authentication is not supported. Configuration keys map to environment variables by replacing `:` with `__`.

Use the Azure OpenAI-compatible resource endpoint shown in the Foundry portal for the deployed model, not the Foundry project URL or the deployment REST URL. The deployment name is the custom deployment name, not necessarily the underlying model name.

Optional settings:

| Setting                                 | Default                       | Purpose                |
|-----------------------------------------|-------------------------------|------------------------|
| `Agent__Name`                           | `Assist \| HR`                | Service display name   |
| `Agent__Instructions`                   | Built-in HR policy prompt      | System instructions    |
| `Agent__MaxInputCharacters`             | `12000`                       | Request size guardrail |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | empty locally                 | Azure Monitor export   |

Never commit credentials. For local overrides, use environment variables or .NET user secrets.

### Local ServiceNow AI Search

The `search_hr_knowledge` tool fans out to both SharePoint and ServiceNow in parallel during the model turn. When only ServiceNow is enabled, it calls ServiceNow AI Search with generated keywords. Local Scalar/API requests do not provide an authenticated Teams identity, so Development may use an explicit test UPN. Configure secrets with .NET user secrets:

```powershell
dotnet user-secrets --project src/Orb.Automate.Agents.HR.Api set "ServiceNow:Enabled" "true"
dotnet user-secrets --project src/Orb.Automate.Agents.HR.Api set "ServiceNow:Endpoint" "https://atkinsdev.service-now.com"
dotnet user-secrets --project src/Orb.Automate.Agents.HR.Api set "ServiceNow:TokenEndpoint" "https://atkinsdev.service-now.com/oauth_token.do"
dotnet user-secrets --project src/Orb.Automate.Agents.HR.Api set "ServiceNow:ClientId" "<client-id>"
# Single quotes preserve literal backticks that may occur in the client secret.
dotnet user-secrets --project src/Orb.Automate.Agents.HR.Api set "ServiceNow:ClientSecret" '<client-secret>'
dotnet user-secrets --project src/Orb.Automate.Agents.HR.Api set "ServiceNow:DevelopmentUserUpn" "<test-user-upn>"
```

`ServiceNow:DevelopmentUserUpn` is rejected during startup outside the `Development` environment. In Teams, the UPN must come from authenticated channel identity or an approved server-side `aadObjectId` lookup; it is never a model-visible tool argument.

The OAuth implementation assumes client credentials are posted to `/oauth_token.do` as form fields with no scope or audience. Confirm that contract with the ServiceNow owner before production use.
Automatic HTTP redirects are disabled for both OAuth and search requests so credentials and bearer tokens cannot be forwarded to an unexpected host.

## Build and test

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
```

The current machine must have the .NET 10 SDK. Runtime-only installations cannot build the solution.

## Verify OpenTelemetry locally

The custom AI meter is `Orb.Automate.Agents.HR.ChatAI`. Aspire automatically displays its metrics in the dashboard. To verify against another local OTLP collector, set the endpoint before starting the API:

```powershell
$env:OTEL_EXPORTER_OTLP_ENDPOINT='http://localhost:4317'
$env:OTEL_EXPORTER_OTLP_PROTOCOL='grpc'
dotnet run --project src/Orb.Automate.Agents.HR.Api --launch-profile http
```

Exercise both complete and SSE endpoints, then inspect `chat.turn.*`, `ai.provider.*`, `ai.provider.token.count`, `chat.turn.token.count`, `chat.grounding.*`, `chat.tool.*` and `chat.citation.*`. A complete delivered response has one `AgentResponseSent` custom event and one terminal turn measurement; a cancelled or disconnected stream has only the terminal measurement. Do not add `ai.provider.token.count` to `chat.turn.token.count`; they describe the same tokens at different scopes.

For Azure Monitor verification, set `APPLICATIONINSIGHTS_CONNECTION_STRING`, restart the API, and query `customEvents` for `AgentResponseSent` as shown in [docs/assist-hr-business-telemetry.md](docs/assist-hr-business-telemetry.md). Never place connection strings in tracked settings or command history; prefer user secrets or a temporary process environment variable.

The on-behalf-of client secret used for local Graph enrichment is never stored in `appsettings.json`. Set it only through user secrets:

```powershell
dotnet user-secrets --project src/Orb.Automate.Agents.HR.Api set "OnBehalfOf:ClientSecret" '<client-secret>'
```

Outside Development the API uses a federated credential and no secret is required.

## API examples

Complete response:

```powershell
$accessToken = '<Entra access token with the configured access_as_user scope>'
$headers = @{ Authorization = "Bearer $accessToken" }
$body = @{ message = 'Explain my benefits in one paragraph.' } | ConvertTo-Json
Invoke-RestMethod http://localhost:5080/api/chat -Method Post -Headers $headers -ContentType 'application/json' -Body $body
```

The HTTP launch profile listens on `http://localhost:5080`. In Development, open `http://localhost:5080/devui` to test the agent interactively or `http://localhost:5080/scalar` to exercise the API. DevUI is restricted to loopback callers by default and must not be used as a production interface. A successful API request returns JSON containing `message`, `responseId`, and `finishReason`.

SSE stream:

```powershell
curl.exe -N -X POST http://localhost:5080/api/chat/stream -H "Authorization: Bearer $accessToken" -H "Content-Type: application/json" -d '{"message":"Write a short SMART objective."}'
```

Both chat routes require a valid Entra bearer token whose `scp` claim contains the configured
`EntraAuthentication:RequiredScope` value. Development tooling routes remain local-only and are
not a substitute for the authenticated API.

OpenAPI is available at `/openapi/v1.json`, with Scalar at `/scalar`, in Development. DevUI also exposes OpenAI-compatible Responses and Conversations endpoints under `/v1` while running in Development.

## Azure runtime requirements

Infrastructure is managed in a separate repository. The provisioned Azure App Service must provide:

- A Windows App Service using the .NET 10 runtime and HTTPS-only access.
- A Windows App Service Plan, such as the existing internal App Service Environment plan.
- A system- or user-assigned managed identity with `Cognitive Services OpenAI User` on the Foundry resource.
- `Foundry__Endpoint` and `Foundry__DeploymentName` application settings.
- ServiceNow endpoint settings when the search tool is enabled.
- Versionless Key Vault references for the ServiceNow OAuth client ID and client secret.
- `Key Vault Secrets User` on the shared environment Key Vault for the App Service system-assigned identity.
- For SharePoint retrieval: `Search Index Data Reader` on the search service for the managed identity, an app registration whose federated credential trusts that managed identity, and the `SharePointSearch__*` and `OnBehalfOf__*` application settings.
- `APPLICATIONINSIGHTS_CONNECTION_STRING` when Application Insights export is required.
- `/health/live` as the App Service health-check path.

## Azure DevOps pipelines

The repository contains separate build and deployment pipelines under `pipelines/`:

- `build.pipeline.yml` restores, builds, tests, publishes coverage, and creates the `Apps` pipeline artifact.
- `deploy.pipeline.yml` deploys `main` builds to DEV. Tagged builds deploy sequentially to DEV, UAT, and PROD.
- `templates/deploy-env.pipeline.yml` contains the shared App Service deployment job.

Create an Azure DevOps pipeline named `Agents - HR - Build` from `pipelines/build.pipeline.yml`, then create the deployment pipeline from `pipelines/deploy.pipeline.yml`.

Create the Azure DevOps environments `Agents - HR - DEV`, `Agents - HR - UAT`, and `Agents - HR - PROD`, adding approval checks where required. Also create these variable groups and authorize the deployment pipeline to use them:

| Variable group   | Required variables                                  |
|------------------|-----------------------------------------------------|
| `Agents-HR-DEV`  | `foundryEndpoint`, `foundryDeploymentName`, `serviceNowEndpoint`, `serviceNowTokenEndpoint` and `sharePointSearch*` values used by the DEV stage |
| `Agents-HR-UAT`  | `foundryEndpoint`, `foundryDeploymentName`, `serviceNowEndpoint`, `serviceNowTokenEndpoint` and `sharePointSearch*` values used by the UAT stage |
| `Agents-HR-PROD` | `foundryEndpoint`, `foundryDeploymentName`, `serviceNowEndpoint`, `serviceNowTokenEndpoint` and `sharePointSearch*` values used by the PROD stage |

Set `foundryEndpoint` to the Azure OpenAI-compatible resource endpoint and `foundryDeploymentName` to the custom model deployment name. The deployment pipeline writes them to App Service as `Foundry__Endpoint` and `Foundry__DeploymentName`; they are runtime configuration and are not compiled into the build artifact. Authorize the deployment pipeline to read each variable group. The Azure Resource Manager service connections are selected per environment in the deployment pipeline and should use workload identity federation; no publish profile or client secret is required.

Set `serviceNowEndpoint` to the environment's ServiceNow instance and `serviceNowTokenEndpoint` to its OAuth token endpoint. Every other non-secret `ServiceNow` setting (timeouts, retries, circuit breaker, rate limit, cache and result limits) can be overridden per environment by adding the matching `serviceNow<Name>` variable to the group; the pipeline defaults otherwise match `appsettings.json`. See [docs/servicenow-ai-search-tool-prerequisites.md](docs/servicenow-ai-search-tool-prerequisites.md) for the full list. The infrastructure expects these versionless secrets in the existing shared Key Vault named `orbautosharedkvxx<environment>`:

- `servicenow-ai-search-client-id`
- `servicenow-ai-search-client-secret`

The Bicep deployment grants the HR API system-assigned managed identity the `Key Vault Secrets User` role and configures App Service Key Vault references. Secret creation and rotation remain operational responsibilities and secret values never pass through the deployment pipeline.

## Troubleshooting

- **Missing DCP or dashboard paths:** run the AppHost with `aspire run`, not by launching the compiled AppHost executable directly. Confirm that the Aspire CLI is available with `aspire --version`, then rebuild the AppHost. Local builds must have `AspireUseCliBundle=true`.
- **AppHost uses an older system runtime:** when .NET 10 is installed per-user, set `PATH`, `DOTNET_ROOT`, and `DOTNET_ROOT_X64` as shown under Prerequisites before running Aspire.
- **`hr-api` exits while the dashboard remains available:** configure both `Foundry:Endpoint` and `Foundry:DeploymentName` as API user secrets, then restart the resource or AppHost.
- **401/403 from Foundry:** verify the active identity has `Cognitive Services OpenAI User`; allow several minutes for role propagation.
- **401 from the chat API:** send an Entra bearer token for the configured audience and verify its `scp` claim includes `EntraAuthentication:RequiredScope`.
- **Deployment not found:** `Foundry__DeploymentName` is the deployment name, not necessarily the model family name.
- **Options validation failure:** set both required Foundry settings and ensure the endpoint is absolute HTTPS.
- **No telemetry:** set `APPLICATIONINSIGHTS_CONNECTION_STRING`; local console logging still works without it.
- **`identity_unavailable` from the SharePoint tool:** locally, run `az login`; in Azure, confirm the caller sent a bearer token and that `OnBehalfOf:TenantId`/`OnBehalfOf:ClientId` and the federated credential are configured.
- **SharePoint search returns nothing for everyone:** confirm `Search Index Data Reader` is assigned, then check whether the indexer has ever populated `UserIds`/`GroupIds`; an index whose ACL fields are empty trims every document away.
- **App fails after deployment:** inspect the App Service log stream and verify the Windows Web App is configured with the .NET 10 runtime stack.

## Security follow-ups

Entra ID authentication and delegated-scope authorization are enforced on the chat routes. Before
exposing the API publicly, add rate limiting appropriate to expected traffic, durable conversation
storage with retention controls, content-safety policy, and private networking where required.
