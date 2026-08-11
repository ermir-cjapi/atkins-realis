# Orb.Automate - Agents - HR (ARIA)

Production-oriented ASP.NET Core chatbot scaffold using Microsoft Agent Framework, Azure AI Foundry, managed identity, and Azure App Service.

## Architecture

```text
Client -> ASP.NET Core API -> Microsoft Agent Framework -> Azure AI Foundry model
						 \-> OpenTelemetry -> Application Insights
```

- `POST /api/chat` returns a complete response.
- `POST /api/chat/stream` emits Server-Sent Events (SSE).
- `GET /health/live` is a liveness probe.
- `GET /health/ready` validates application readiness.
- Conversations are stateless in this starter. Persist Agent Framework sessions before relying on multi-turn server-side history.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli)
- An Azure AI Foundry/Azure AI Services resource with a deployed chat model
- The `Cognitive Services OpenAI User` role on that resource

## Local configuration

Authenticate without storing a secret:

```powershell
az login
az account set --subscription '<subscription-id>'
$env:Foundry__Endpoint='https://<account-name>.openai.azure.com/'
$env:Foundry__DeploymentName='<model-deployment-name>'
dotnet run --project src/Chatbot.Api
```

The app uses `AzureCliCredential` locally and `DefaultAzureCredential` for the App Service managed identity in Azure. API-key authentication is not supported. Configuration keys map to environment variables by replacing `:` with `__`.

Use the Azure OpenAI-compatible resource endpoint shown in the Foundry portal for the deployed model, not the Foundry project URL or the deployment REST URL. The deployment name is the custom deployment name, not necessarily the underlying model name.

Optional settings:

| Setting                                 | Default                       | Purpose                |
|-----------------------------------------|-------------------------------|------------------------|
| `Agent__Name`                           | `ARIA`                        | Agent name             |
| `Agent__Instructions`                   | Built-in HR policy prompt      | System instructions    |
| `Agent__MaxInputCharacters`             | `12000`                       | Request size guardrail |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | empty locally                 | Azure Monitor export   |

Never commit credentials. For local overrides, use environment variables or .NET user secrets.

## Build and test

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
```

The current machine must have the .NET 10 SDK. Runtime-only installations cannot build the solution.

## API examples

Complete response:

```powershell
$body = @{ message = 'Explain managed identity in one paragraph.' } | ConvertTo-Json
Invoke-RestMethod http://localhost:5080/api/chat -Method Post -ContentType 'application/json' -Body $body
```

The HTTP launch profile listens on `http://localhost:5080`. In Development, open `http://localhost:5080/swagger` to test interactively. A successful request returns JSON containing `message`, `responseId`, and `finishReason`.

SSE stream:

```powershell
curl.exe -N -X POST http://localhost:5080/api/chat/stream -H "Content-Type: application/json" -d '{"message":"Write a short welcome message."}'
```

OpenAPI is available at `/swagger` in Development.

## Azure runtime requirements

Infrastructure is managed in a separate repository. The provisioned Azure App Service must provide:

- A Windows App Service using the .NET 10 runtime and HTTPS-only access.
- A Windows App Service Plan, such as the existing internal App Service Environment plan.
- A system- or user-assigned managed identity with `Cognitive Services OpenAI User` on the Foundry resource.
- `Foundry__Endpoint` and `Foundry__DeploymentName` application settings.
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
| `Agents-HR-DEV`  | `foundryEndpoint`, `foundryDeploymentName`           |
| `Agents-HR-UAT`  | `foundryEndpoint`, `foundryDeploymentName`           |
| `Agents-HR-PROD` | `foundryEndpoint`, `foundryDeploymentName`           |

Set `foundryEndpoint` to the Azure OpenAI-compatible resource endpoint and `foundryDeploymentName` to the custom model deployment name. The deployment pipeline writes them to App Service as `Foundry__Endpoint` and `Foundry__DeploymentName`; they are runtime configuration and are not compiled into the build artifact. Authorize the deployment pipeline to read each variable group. The Azure Resource Manager service connections are selected per environment in the deployment pipeline and should use workload identity federation; no publish profile or client secret is required.

## Troubleshooting

- **401/403 from Foundry:** verify the active identity has `Cognitive Services OpenAI User`; allow several minutes for role propagation.
- **401 with an API key:** verify the key belongs to the same resource as `Foundry:Endpoint`, then rotate it if it may have been exposed.
- **Deployment not found:** `Foundry__DeploymentName` is the deployment name, not necessarily the model family name.
- **Options validation failure:** set both required Foundry settings and ensure the endpoint is absolute HTTPS.
- **No telemetry:** set `APPLICATIONINSIGHTS_CONNECTION_STRING`; local console logging still works without it.
- **App fails after deployment:** inspect the App Service log stream and verify the Windows Web App is configured with the .NET 10 runtime stack.

## Security follow-ups

Before exposing the API publicly, add Entra ID authentication/authorization, rate limiting appropriate to expected traffic, durable conversation storage with retention controls, content-safety policy, and private networking where required.
