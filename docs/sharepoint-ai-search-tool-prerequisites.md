# SharePoint AI Search tool prerequisites

## Purpose

This document defines only the platform, security, configuration, and operational prerequisites for the SharePoint knowledge source consumed by the Assist | HR `search_hr_knowledge` tool.

For the version-controlled Azure AI Search templates, deployment parameters, pipeline flow, and validation commands, see [Azure AI Search infrastructure](azure-ai-search-infrastructure.md).

The `search_hr_knowledge` tool fans out to SharePoint and ServiceNow simultaneously during the model turn, normalises and merges the results, and returns one ranked list to the agent. This document covers only the SharePoint (Azure AI Search) leg. See [ServiceNow AI Search tool prerequisites](servicenow-ai-search-tool-prerequisites.md) for the ServiceNow leg.

Both knowledge sources are required together for Performance Review and MYCD answers. Neither source may be treated as a substitute for the other.

## Target integration

### Search request

```text
POST https://<search-service-name>.search.windows.net/indexes/<index-name>/docs/search?api-version=<api-version>
Authorization: Bearer <app-managed-identity-token>
x-ms-query-source-authorization: <raw-end-user-token>
Content-Type: application/json
```

Two tokens are required and serve different purposes. The `Authorization` token is the application's managed identity token and authorizes access to the index. The `x-ms-query-source-authorization` token is the end user's token and is used only to evaluate document-level permissions. The end user requires no role assignment on the search service.

### Query shape

Hybrid retrieval (BM25 and vector) with the semantic ranker, using integrated vectorization:

```json
{
  "search": "<generated keywords>",
  "vectorQueries": [
    { "kind": "text", "text": "<generated keywords>", "fields": "text_vector", "k": 12 }
  ],
  "queryType": "semantic",
  "semanticConfiguration": "<semantic-configuration-name>",
  "select": "chunk_id,parent_id,title,chunk,metadata_spo_item_path",
  "top": 12
}
```

`select` must be explicit. `text_vector` is neither retrievable nor stored (3072 floats per chunk), so it must never be requested.

## 1. Azure AI Search index prerequisites

> **Implementation status:** the application now rejects anonymous chat requests, obtains its OBO
> assertion only from the validated bearer-authentication ticket, and sends every selected passage
> to Foundry through a controlled grounding ledger. Version-controlled definitions for the data
> source, index, skillset, and indexer are under `infra/search/` and are deployed by the release
> pipeline. Do not set `SharePointSearch__Enabled=true` until the identity prerequisites and live
> acceptance checks in this document have been completed.

### Confirmed resources

| Value                       | Observed                                                       |
| --------------------------- | -------------------------------------------------------------- |
| Search service              | `<search-service-name>`                                        |
| Private endpoint            | `<search-private-endpoint-name>`                              |
| Index                       | `<index-name>`                                                 |
| Indexer                     | `<index-name>-indexer`                                         |
| Skillset                    | `<index-name>-skillset`                                        |
| Data source                 | `<index-name>-datasource`                                      |
| SharePoint site             | `<sharepoint-site-url>`                                        |
| SharePoint app registration | `<sharepoint-ingestion-app-registration>`, `<required-sharepoint-permissions>` |

### Confirmed index schema

| Field                    | Type                                                   | Role in the tool                                   |
| ------------------------ | ------------------------------------------------------ | -------------------------------------------------- |
| `chunk_id`               | `Edm.String` (key)                                     | Result identity                                    |
| `parent_id`              | `Edm.String`, filterable                               | Deduplication across chunks of one document        |
| `chunk`                  | `Edm.String`, searchable                               | Result content                                     |
| `title`                  | `Edm.String`, searchable                               | Result title, mapped from `metadata_spo_item_name` |
| `text_vector`            | `Collection(Edm.Single)`, 3072 dimensions, not retrievable or stored | Vector search only, never selected   |
| `UserIds`                | `Collection(Edm.String)`, `permissionFilter: userIds`, not retrievable  | Document-level trimming           |
| `GroupIds`               | `Collection(Edm.String)`, `permissionFilter: groupIds`, not retrievable | Document-level trimming           |
| `SharePointSiteUrl`      | `Edm.String`, `sharepointSiteUrl: true`                | Site used by the service to resolve SharePoint permissions |
| `metadata_spo_item_path` | `Edm.String`, retrievable                              | Graph drive path converted through the trusted canonical library base URL |
| `weburi`                 | `Edm.String`, retrievable                              | SharePoint web URL of the item                     |
| `last_modified_date`     | `Edm.DateTimeOffset`, filterable, sortable             | Source recency                                     |
| `created_date_sp`        | `Edm.DateTimeOffset`, filterable, sortable             | Source creation date                               |

Index-level `permissionFilterOption` is `disabled`, so queries are not trimmed per user. The `UserIds` and `GroupIds` ACL fields remain populated so trimming can be re-enabled later. `sharePointConnectorAppRegistration` identifies `sharePointConnectorAppRegistration` identifies the ingestion app, the federated credential, and the tenant.

The semantic configuration `<semantic-configuration-name>` is the default, with `title` as the title field and `chunk` as prioritized content.

The vector profile `<vector-profile-name>` uses HNSW with cosine distance and has vectorizer `<vectorizer-name>` attached, targeting `<embedding-endpoint>` with deployment `<embedding-deployment-name>`. No compression is configured.

Because a vectorizer is attached, the application sends query text and the search service performs embedding. The application requires no embedding deployment, no embedding model configuration, and no Azure OpenAI role assignment for embeddings.

### Known schema gaps

| Gap                                     | Consequence                                                                            | Resolution                                                          |
| --------------------------------------- | -------------------------------------------------------------------------------------- | ------------------------------------------------------------------- |
| Last-modified field is not aligned with the API response | Source-priority rule 2, "most recent article", cannot be applied reliably to SharePoint results | Select the indexed `last_modified_date` field and align the API response mapping |
| `title` holds a file name               | Citation labels will read as file names rather than actionable names                   | Clean in projection, or index a document title                      |
| No country or audience field            | Country-specific LOA prioritisation relies on keyword matching only, not filtering     | Add and populate a country field if country correctness is required |
| `metadata_spo_item_path` not filterable | Queries cannot be scoped by document library or path                                   | Requires index rebuild                                              |
| `metadata_spo_item_path` is a Graph drive path | It isn't directly usable as an employee citation URL | Configure `CanonicalUrlBase`, or switch citations to the indexed `weburi` field |
| No vector compression                   | Higher storage and memory for 3072-dimension vectors                                   | Requires index rebuild                                              |

The index owner must confirm:

1. Which SharePoint document libraries are in indexing scope.
2. That no employee-specific, restricted, or personal-data content is in scope.
3. Whether the skillset projects `title`, `metadata_spo_item_path`, `UserIds`, and `GroupIds` onto every chunk, not only onto parent documents.
4. That `title`, `metadata_spo_item_path`, `UserIds`, `GroupIds`, and `SharePointSiteUrl` are populated on every chunk. The skillset projects them; verify against indexed
5. The expected result and chunk volume, for threshold and cost planning.
6. Whether the index will be recreated with an environment-specific name.
The environment-specific index name. For example, DEV uses `index-agents-hr-knowledge`.

## 2. Identity and permission-trimming prerequisites

Document-level trimming is enforced by Azure AI Search, not by the application. The indexer copies SharePoint permissions into `UserIds` and `GroupIds`; the search service evaluates them against the token supplied in `x-ms-query-source-authorization`. The application never constructs a permission filter.

A query that omits `x-ms-query-source-authorization` must be treated as unsafe until its behaviour is verified.

Required before implementation:

1. Verify empirically whether a query without `x-ms-query-source-authorization` fails closed or returns untrimmed results. Record the result.
2. If the behaviour is fail-open, the header must be enforced structurally so that no code path can issue an untrimmed query.
3. Confirm that `metadata_user_ids` and `metadata_group_ids` contain Entra object identifiers that correspond to the claims available in end-user tokens.
4. Confirm expected behaviour for guest users, disabled accounts, and users with no matching permissions.
5. Confirm the acceptable staleness window for permission data. See section 6.

### Trusted user identity

The tool must obtain the end user's token from a trusted authenticated channel. The model must not supply it, and callers must not be able to provide an arbitrary identity in the chat message or tool arguments.

The `/api/chat` and `/api/chat/stream` endpoints require a tenant-specific Entra access token with
the configured audience and delegated scope (normally `access_as_user`). The API uses that inbound
token as the user assertion for the on-behalf-of exchange; requests without a verified token fail
before the chat handler runs. The tool uses only the token from the validated authentication ticket;
without a verified user token, it must not query the index.

For Teams traffic, the trusted identity originates at the shared bot handler after Bot Connector
authentication. Teams SSO must provide a token for the Agents - HR API audience and required
delegated scope. The handler forwards that token to `/api/chat`; arbitrary identity or token values
from message text, Adaptive Card data, or other client-controlled activity fields must be ignored.

The implementation owner must document:

- The authoritative claim or activity property carrying user identity.
- The on-behalf-of exchange used to obtain a token for the search data plane.
- Behaviour when no verified user token is available.
- Treatment of guest users and alternate email addresses.

The bot-only Entra client identity does not provide an end-user token. A bot registration must not be treated as user authentication for this tool.

## 3. Azure RBAC prerequisites

This integration requires no secrets and no Key Vault. All access is Entra-based.

| Hop                                      | Identity                             | Required role or permission                                        | Scope                        |
| ---------------------------------------- | ------------------------------------ | ------------------------------------------------------------------ | ---------------------------- |
| API queries the index                    | API system-assigned managed identity | `Search Index Data Reader`                                         | `<search-service-name>`      |
| API obtains a user token for Search      | API Entra app registration           | Delegated `user_impersonation` on Azure AI Search, admin consented | Tenant                       |
| Search performs query-time vectorization | Search service managed identity      | `Cognitive Services OpenAI User`                                   | `<ai-services-resource-name>` |
| Reading index or indexer definitions     | Operator or pipeline identity        | `Search Service Contributor`                                       | `<search-service-name>`       |

`Search Index Data Reader` permits querying documents only. It does not permit reading index definitions.

The on-behalf-of exchange requires the API app registration to act as a confidential client. To remain secretless, configure a federated identity credential on the app registration trusting the App Service managed identity, and supply a client assertion from that managed identity rather than storing a client secret. This approach must be approved by the Azure platform owner before implementation.

The search service's role assignment for Azure OpenAI must be verified rather than assumed. Query-time vectorization uses the vectorizer, which has never executed; the successful indexing run exercised the skillset's connection, which is configured separately.

The search service, private endpoint, and AI Services resource are in `<shared-resource-group-name>`. Role assignments targeting them must be deployed at that resource group's scope, not inline in `infra/rg-agents-hr.bicep`.

## 4. Non-secret application configuration

Configure these values independently for DEV, UAT, and PROD:

| Setting                                           | DEV value                                               |
| ------------------------------------------------- | ------------------------------------------------------- |
| `SharePointSearch__Enabled`                       | `false` until prerequisites are satisfied               |
| `SharePointSearch__Endpoint`                      | `https://<search-service-name>.search.windows.net`      |
| `SharePointSearch__IndexName`                     | `<index-name>`                                          |
| `SharePointSearch__SemanticConfigurationName`     | `<semantic-configuration-name>`                        |
| `SharePointSearch__EnforceDocumentPermissions`    | `false`. Queries run as the API managed identity and return every indexed document to every authenticated user. `true` requires the index `permissionFilterOption` to be `enabled` and admin consent for the on-behalf-of exchange |
| `SharePointSearch__CanonicalUrlBase`              | `<sharepoint-library-url>`                              |
| `SharePointSearch__VectorFieldName`               | `text_vector`                                           |
| `SharePointSearch__RequestTimeoutSeconds`         | `30`                                                    |
| `SharePointSearch__MaxResults`                    | Retrieved result count, product-approved                |
| `SharePointSearch__MaxLlmResults`                 | Results returned to the model, product-approved         |
| `SharePointSearch__MaxLlmResultContentCharacters` | Product-approved limit                                  |
| `SharePointSearch__MaxLlmPayloadCharacters`       | Product-approved limit                                  |
| `SharePointSearch__MaxSearchTermCharacters`       | `100`                                                   |
| `SharePointSearch__MaxTitleCharacters`            | `500`                                                   |
| `SharePointSearch__MinimumRerankerScore`          | Calibrated, see section 8                               |
| `OnBehalfOf__TenantId`                            | The Entra tenant identifier                             |
| `OnBehalfOf__ClientId`                            | The API app registration's client identifier            |
| `EntraAuthentication__TenantId`                   | The Entra tenant identifier                             |
| `EntraAuthentication__Audience`                   | API app ID URI or client ID accepted by this API        |
| `EntraAuthentication__RequiredScope`              | Delegated chat scope, normally `access_as_user`         |

Store these in the existing environment-specific Azure DevOps variable groups (`Agents-HR-DEV`, `Agents-HR-UAT`, `Agents-HR-PROD`) and pass them through `apiAdditionalAppSettings` in `pipelines/deploy.pipeline.yml`. Do not hard-code environment URLs or index names in application code.

Production configuration must require HTTPS URLs and reject missing or invalid settings during startup.

The release pipeline passes these values from each `Agents-HR-*` variable group. The variable
groups must define every listed variable, including a deliberate `sharePointSearchEnabled=false`
until the live checks are signed off. None of these values is a secret; do not put a user token,
Search key, or client secret in an app setting.

## 5. Network prerequisites

Public network access to the search service is disabled. The service is reachable only through its private endpoint.

The HR App Service must have outbound HTTPS and DNS access to:

- The search service private endpoint.
- Microsoft Entra token endpoints.
- Existing Azure AI Foundry dependencies.

Confirm with the network team:

1. That the `privatelink.search.windows.net` private DNS zone is linked to the App Service Environment's virtual network. `WEBSITE_VNET_ROUTE_ALL` is already `1`, so traffic routes into the network correctly; unresolved DNS presents as a timeout rather than an authentication failure.
2. That name resolution from the App Service returns a private address. Verify with `nameresolver <search-service-name>.search.windows.net` from the SCM console.
3. That the shared private link from the search service to `<ai-services-resource-name>` is approved and healthy. Query-time vectorization fails without it.
4. Developer access to the private endpoint for local development, including DNS resolution over VPN.
5. TLS inspection compatibility and certificate trust.

The indexer runs with `executionEnvironment: private`, which is consistent with this configuration and must not be changed to public.

## 6. Indexer and freshness prerequisites

The indexer configuration has operational settings that affect both answer correctness and access control.

| Observed                         | Consequence                                                                                                      | Required decision                                   |
| -------------------------------- | ---------------------------------------------------------------------------------------------------------------- | --------------------------------------------------- |
| `schedule.interval: PT8H`        | Permission and content changes can remain stale for up to 8 hours, until the next scheduled run.           | Agree whether the 8-hour interval meets the staleness SLA |
| `cache: null`                    | Every changed document is reprocessed and re-embedded.                                                        | Retain this setting unless preview API compatibility changes |
| `maxFailedItems: 50` and `maxFailedItemsPerBatch: 30` | A run can complete with failed documents; failures require monitoring and remediation. | Agree failure allowance and monitoring |

Permission data is a snapshot. Built-in trimming enforces whatever the last indexer run recorded, so the schedule bounds the access-revocation window but never eliminates it. The content owner must accept this window explicitly. A permissions-only refresh can be triggered between runs with `POST /indexers/<name>/resync` and `{"options":["permissions"]}`.

`outputFieldMappings` is empty, which is expected when the skillset writes chunks through index projections. The indexer's `fieldMappings` for `metadata_user_ids` and `metadata_group_ids` are retained so the portal reports SharePoint ACL support as enabled. With `skipIndexingParentDocuments`, the projections, not these mappings, populate chunk ACLs. Chunk-level field availability must therefore be verified directly against indexed documents.
## 7. Application implementation requirements

The implementation must include:

1. A callable Agent Framework function named `search_hr_knowledge` that fans out to SharePoint (and optionally ServiceNow) during the model turn.
2. One model-visible argument, `q`, containing one to three generated search keywords.
3. Server-side acquisition of the end user's search-scoped token. The model must never influence it.
4. Hybrid retrieval combining the text query and a vectorizable text query against `text_vector`.
5. The semantic ranker, using the configured semantic configuration.
6. An explicit `select` list that excludes `text_vector`.
7. Bounded request timeouts and cancellation.
8. Controlled failure results that allow the agent to state that approved guidance could not be verified.
9. A per-call user token rather than a per-client one. `Azure.Search.Documents` accepts `querySourceAuthorization` as a parameter on `SearchAsync`, so a singleton `SearchClient` authenticated with the application's managed identity is sufficient; no per-request client or pipeline policy is required.
10. An `Enabled` flag that fully disables the tool without removing configuration.
11. No client-side re-ranking. The semantic ranker's ordering is authoritative.

Two constraints follow from the feature still being in preview, and both must be accepted before this reaches production:

- The parameter is exposed only by preview packages. Neither `11.7.0` nor `12.0.0` stable includes it, so the implementation pins `Azure.Search.Documents 12.1.0-beta.2`.
- Permission filtering is honoured only by preview service versions, so the client pins `V2026_08_01_Preview`, matching the `2026-08-01-preview` version used by `infra/search/deploy-search-dpos.ps1`. Pinning a stable service version such as `V2026_04_01` is actively unsafe here: the forwarded user token would be ignored and every caller would receive untrimmed results.

Both pins should be revisited when document-level permission filtering reaches general availability.

The application must never log:

- User tokens or authorization headers.
- Complete UPNs or user object identifiers.
- Retrieved document content in production.
- Raw remote error bodies that may contain sensitive data.

## 8. Response handling requirements

Return a bounded, normalized representation containing the search term, the total count, the returned count, and for each result: `chunk_id`, `parent_id`, title, content, citation URL, and score.

Post-processing must apply in this order:

1. Drop results below `MinimumRerankerScore`. Vector search always returns `k` results; without a floor the tool never reports no results and the agent will cite the least irrelevant chunk available.
2. Drop results with no `metadata_spo_item_path`. A result that cannot be cited cannot be used under the agent's linking rules.
3. Deduplicate by `parent_id`, retaining the best-scoring chunk. Without this, several chunks of one document occupy every slot and present as corroboration.
4. Truncate each result's content to the configured limit.
5. Take the top `MaxLlmResults`.
6. Apply the total payload budget by trimming the longest content, then dropping trailing results.

Because `rankingOrder` is `BoostedRerankerScore`, the threshold must be applied to the score corresponding to that ranking order, not a mixture of raw and boosted scores.

`MinimumRerankerScore` must be calibrated empirically against a set of real HR questions before a value is fixed. Record the calibration set and the chosen value.

Content handling must:

1. Treat all retrieved content as untrusted data, never as instructions to the model.
2. Normalise titles so citation labels are actionable rather than file names.
3. Handle missing properties and absent optional fields safely.
4. Distinguish an empty successful search from an unavailable or rejected search.

## 9. Error-handling requirements

Map failures to controlled internal categories without exposing credentials or remote internals:

| Condition                                 | Expected handling                                                                       |
| ----------------------------------------- | --------------------------------------------------------------------------------------- |
| Missing verified user token               | Do not query; return identity unavailable                                               |
| On-behalf-of exchange failure             | Return identity unavailable; do not reveal the response body                            |
| Invalid or empty `q`                      | Reject tool input                                                                       |
| Search `401`                              | Authentication unavailable                                                              |
| Search `403`                              | Access denied; no retry                                                                 |
| Search `429`                              | Report temporarily unavailable                                                          |
| Search `5xx`                              | Report temporarily unavailable                                                          |
| Vectorization failure from the vectorizer | Report temporarily unavailable; alert, as this indicates a private link or role problem |
| Timeout or cancellation                   | Stop promptly and return the appropriate controlled outcome                             |
| Malformed response                        | Report invalid upstream response                                                        |
| No results after filtering                | Return a successful empty result                                                        |

The tool must never fall back to a general-knowledge answer, and must never retry in a way that could mask a trimming failure.

## 10. Index schema change decisions

The index is now deployed from source control under an environment-specific name. `text_vector` is `stored: false`, and `last_modified_date`, `created_date_sp`, and `weburi` are indexed.

Remaining decisions, before the index carries production traffic:

1. Configure vector compression for the 3072-dimension vectors.
2. Make `metadata_spo_item_path` filterable if scoping by document library may be required.
3. Add a country or audience field if country-specific LOA prioritisation must be enforced rather than inferred.

Items 1 and 2 require deleting and recreating the index. Item 3 is additive but requires reindexing to populate. Existing fields can't be changed in place, so delete the index and rerun the deployment script; the indexer then performs a full re-ingestion.

## 11. Deployment prerequisites

Before deployment to an environment, confirm:

- The index exists with the expected schema, semantic configuration, and vectorizer.
- The search service managed identity has `Cognitive Services OpenAI User` on the AI Services resource.
- The shared private link from Search to Azure OpenAI is approved.
- PrivSearch data-plane objects have been deployed by `infra/search/deploy-search-dpos.ps1` (release pipeline), and the indexer has completed a run with ACL fields populated.
- The SharePoint ingestion app registration has admin-consented SharePoint/Graph application permissions and a federated credential trusting the Search managed identity.
- The API managed identity has `Search Index Data Reader`.
- The API app registration exists, has admin-consented delegated permission to Azure AI Search, and has a working federated identity credential.
- Entra ID authentication is enabled on the API.
- The no-header trimming behaviour has been verified and recorded.
- Non-secret `SharePointSearch__*` settings are populated for the environment.
- The indexer schedule, cache, and failure allowance are agreed.
- Application Insights and other telemetry are configured to exclude sensitive data.

## 12. Test and acceptance requirements

Automated tests must cover:

- Query construction, including the semantic configuration, vector query, and explicit `select` list.
- Absence of `text_vector` from the projection.
- Reranker score thresholding, including the boundary case.
- Deduplication by `parent_id`.
- Exclusion of results with no citation URL.
- Title normalisation.
- Content truncation, payload budgeting, and empty results.
- `401`, `403`, `429`, `5xx`, timeout, cancellation, and malformed responses.
- Missing user token and failed on-behalf-of exchange.
- Tool registration under `search_hr_knowledge`.
- Safe tool outcomes for success, no results, identity unavailable, and unavailable service.
- That the `Enabled` flag fully disables the tool.

Environment acceptance must verify:

1. A query from an authenticated employee returns results trimmed to that employee's permissions.
2. A user without access to a document cannot retrieve it, directly or through paraphrase.
3. The model cannot override or supply identity through tool arguments or conversation text.
4. Query-time vectorization succeeds, proving the vectorizer path and shared private link.
5. Every returned result carries a working SharePoint URL, and the agent's citations use only those URLs.
6. Deduplication prevents one document from occupying every result slot.
7. No user token, authorization header, or complete user identifier appears in logs or telemetry.
8. A search outage produces the approved guidance-unavailable response rather than a general-knowledge answer.
9. SharePoint and ServiceNow knowledge are combined for Performance Review and MYCD questions, and a failure of one source is reported rather than silently ignored.

## 13. Required ownership decisions

| Decision or value                                                                 | Owner                                   |
| --------------------------------------------------------------------------------- | --------------------------------------- |
| SharePoint libraries in indexing scope, and confirmation of no restricted content | HR content owner                        |
| Acceptable permission staleness window and indexer schedule                       | HR content owner and security           |
| Index rebuild scope and environment-specific naming                               | Azure platform owner and HR engineering |
| Federated identity credential approach for the on-behalf-of exchange              | Azure platform owner                    |
| App registration creation, delegated permission, and admin consent                | Identity engineering                    |
| Trusted Teams user-to-token mechanism                                             | Teams and identity engineering          |
| Shared private link approval and private DNS linkage                              | Network and security team               |
| Role assignments on the shared search and AI Services resources                   | Azure platform owner                    |
| Result limits, content limits, and the reranker score floor                       | HR product and engineering              |
| Telemetry redaction validation                                                    | Security and engineering                |

The tool must not be enabled in an environment until these dependencies are satisfied and recorded.

## 14. Production enablement evidence

Record one signed result per environment before enabling retrieval:

| Gate | Required evidence | Failure action |
| --- | --- | --- |
| API authentication | Anonymous `POST /api/chat/` returns `401`; a valid tenant token with the required scope is accepted; expired, wrong-audience, wrong-tenant, and missing-scope tokens are rejected. | Keep retrieval disabled. |
| Direct ACL | A user with direct SharePoint access retrieves the expected chunk and canonical URL. | Block release and inspect projected `userIds`. |
| Group ACL | A user with group-only access retrieves the expected chunk. | Block release and inspect projected `groupIds`. |
| Deny case | A user with neither permission receives no passage, citation, telemetry source, or answer content for the protected document. | Treat as security incident; keep disabled. |
| Query-source token | Missing, malformed, expired, disabled, and guest identities fail closed. | Keep disabled; never substitute client-side ACL filtering. |
| Network and vectorizer | App Service DNS resolves the Search host to a private address and a hybrid query succeeds through the approved Search-to-Azure OpenAI shared private link. | Escalate to network/platform; do not enable a public fallback. |
| Freshness | A SharePoint permission revocation disappears within the agreed indexer and ACL-refresh SLA. | Define and approve the schedule/SLA before release. |

The API returns a structured citation ledger containing `chunk_id`, `parent_id`, canonical URL,
title, score, version, and modification time. A response citation is returned only when its exact
canonical URL occurs in that response's retrieved ledger. When a selected ledger has no citation
in the model output, the API replaces the answer with a safe insufficient-approved-source response.
