# SharePoint AI Search tool prerequisites

## Purpose

This document defines only the platform, security, configuration, and operational prerequisites for implementing the Assist | HR `search_hr_policy_documents_in_sharepoint` tool.

The tool queries the Azure AI Search index built from the HR SharePoint site and returns bounded, normalized, citable results to the HR agent. It is separate from the ServiceNow `get_scoped_knowledge_articles_in_servicenow` tool, the Azure Bot identity, Teams publishing, and the unimplemented `Get Employee Country` tool.

Both knowledge sources are required together for Performance Review and MYCD answers. Neither tool may be treated as a substitute for the other.

## Target integration



### Search request

```text
POST https://orbauto-shared-srch-xx-dev.search.windows.net/indexes/rag-1786623004139/docs/search?api-version=<version>
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
  "semanticConfiguration": "rag-1786623004139-semantic-configuration",
  "select": "chunk_id,parent_id,title,chunk,metadata_spo_item_path",
  "top": 12
}
```

`select` must be explicit. `text_vector` is marked retrievable and holds 3072 floats per chunk; returning it would exhaust the payload budget.

## 1. Azure AI Search index prerequisites

> **Implementation status:** the application now rejects anonymous chat requests, obtains its OBO
> assertion only from the validated bearer-authentication ticket, and sends every selected passage
> to Foundry through a controlled grounding ledger. The Azure resource definitions below remain a
> platform-owned deployment dependency: do not set `SharePointSearch__Enabled=true` until the
> schema and live acceptance checks in this document have been completed.



### Confirmed resources


| Value                       | Observed                                                       |
| --------------------------- | -------------------------------------------------------------- |
| Search service              | `orbauto-shared-srch-xx-dev`                                   |
| Private endpoint            | `orbauto-shared-pep-srch-xx-gb-dev`                            |
| Index                       | `rag-1786623004139`                                            |
| Indexer                     | `rag-1786623004139-indexer`                                    |
| Skillset                    | `rag-1786623004139-skillset`                                   |
| Data source                 | `rag-1786623004139-datasource`                                 |
| SharePoint site             | `https://snclgroup.sharepoint.com/sites/intranet-dept-hr`      |
| SharePoint app registration | `orb.automate.ai.search.dev`, `Sites.Read.All, Files.Read.All` |




### Confirmed index schema


| Field                    | Type                                                   | Role in the tool                                   |
| ------------------------ | ------------------------------------------------------ | -------------------------------------------------- |
| `chunk_id`               | `Edm.String` (key)                                     | Result identity                                    |
| `parent_id`              | `Edm.String`, filterable                               | Deduplication across chunks of one document        |
| `chunk`                  | `Edm.String`, searchable                               | Result content                                     |
| `title`                  | `Edm.String`, searchable                               | Result title, mapped from `metadata_spo_item_name` |
| `text_vector`            | `Collection(Edm.Single)`, 3072 dimensions              | Vector search only, never selected                 |
| `UserIds`                | `Collection(Edm.String)`, `permissionFilter: userIds`  | Document-level trimming                            |
| `GroupIds`               | `Collection(Edm.String)`, `permissionFilter: groupIds` | Document-level trimming                            |
| `metadata_spo_item_path` | `Edm.String`, retrievable                              | Graph drive path converted through the trusted canonical library base URL |


Index-level `permissionFilterOption` is `enabled`.

Semantic configuration `rag-1786623004139-semantic-configuration` is the default, with `title` as the title field and `chunk` as prioritized content.

Vector profile `rag-1786623004139-cu-text-profile` uses HNSW with cosine distance and has vectorizer `rag-1786623004139-cu-text-vectorizer` attached, targeting `https://orbauto-shared-ais-xx-dev.openai.azure.com` with deployment `text-embedding-3-large`. No compression is configured.

Because a vectorizer is attached, the application sends query text and the search service performs embedding. The application requires no embedding deployment, no embedding model configuration, and no Azure OpenAI role assignment for embeddings.

### Known schema gaps


| Gap                                     | Consequence                                                                            | Resolution                                                          |
| --------------------------------------- | -------------------------------------------------------------------------------------- | ------------------------------------------------------------------- |
| No last-modified field                  | Source-priority rule 2, "most recent article", cannot be applied to SharePoint results | Map `metadata_spo_item_last_modified`, project to chunks, reindex   |
| `title` holds a file name               | Citation labels will read as file names rather than actionable names                   | Clean in projection, or index a document title                      |
| No country or audience field            | Country-specific LOA prioritisation relies on keyword matching only, not filtering     | Add and populate a country field if country correctness is required |
| `metadata_spo_item_path` not filterable | Queries cannot be scoped by document library or path                                   | Requires index rebuild                                              |
| `metadata_spo_item_path` is a Graph drive path | It isn't directly usable as an employee citation URL | Configure `CanonicalUrlBase`; replace it with an indexed immutable web URL in the production schema |
| `text_vector` is `stored: true`         | Unnecessary index storage for a field never returned                                   | Requires index rebuild                                              |
| No vector compression                   | Higher storage and memory for 3072-dimension vectors                                   | Requires index rebuild                                              |


The index owner must confirm:

1. Which SharePoint document libraries are in indexing scope.
2. That no employee-specific, restricted, or personal-data content is in scope.
3. Whether the skillset projects `title`, `metadata_spo_item_path`, `UserIds`, and `GroupIds` onto every chunk, not only onto parent documents.
4. Whether `metadata_spo_item_path` is populated and absolute for all chunks.
5. The expected result and chunk volume, for threshold and cost planning.
6. Whether the index will be recreated with an environment-specific name.



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

The API currently has no authentication. Until Entra ID authentication is added to `/api/chat`, no verified user token exists and the tool cannot query the index.

For Teams traffic, the trusted identity should originate from the Microsoft 365 Agents SDK after Bot Connector authentication, with Teams SSO providing the user token used for the on-behalf-of exchange.

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
| API queries the index                    | API system-assigned managed identity | `Search Index Data Reader`                                         | `orbauto-shared-srch-xx-dev` |
| API obtains a user token for Search      | API Entra app registration           | Delegated `user_impersonation` on Azure AI Search, admin consented | Tenant                       |
| Search performs query-time vectorization | Search service managed identity      | `Cognitive Services OpenAI User`                                   | `orbauto-shared-ais-xx-dev`  |
| Reading index or indexer definitions     | Operator or pipeline identity        | `Search Service Contributor`                                       | `orbauto-shared-srch-xx-dev` |


`Search Index Data Reader` permits querying documents only. It does not permit reading index definitions.

The on-behalf-of exchange requires the API app registration to act as a confidential client. To remain secretless, configure a federated identity credential on the app registration trusting the App Service managed identity, and supply a client assertion from that managed identity rather than storing a client secret. This approach must be approved by the Azure platform owner before implementation.

The search service's role assignment for Azure OpenAI must be verified rather than assumed. Query-time vectorization uses the vectorizer, which has never executed; the successful indexing run exercised the skillset's connection, which is configured separately.

The search service, private endpoint, and AI Services resource are in `orbauto-shared-rg-xx-<environment>`. Role assignments targeting them must be deployed at that resource group's scope, not inline in `infra/rg-agents-hr.bicep`.

## 4. Non-secret application configuration

Configure these values independently for DEV, UAT, and PROD:


| Setting                                           | DEV value                                               |
| ------------------------------------------------- | ------------------------------------------------------- |
| `SharePointSearch__Enabled`                       | `false` until prerequisites are satisfied               |
| `SharePointSearch__Endpoint`                      | `https://orbauto-shared-srch-xx-dev.search.windows.net` |
| `SharePointSearch__IndexName`                     | `rag-1786623004139`                                     |
| `SharePointSearch__SemanticConfigurationName`     | `rag-1786623004139-semantic-configuration`              |
| `SharePointSearch__EnforceDocumentPermissions`    | `true`. `false` uses elevated read and returns every indexed document to every authenticated user; it requires the `elevatedOperations/read` data-plane permission |
| `SharePointSearch__CanonicalUrlBase`              | `https://snclgroup.sharepoint.com/sites/intranet-dept-hr/Shared Documents` |
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
2. That name resolution from the App Service returns a private address. Verify with `nameresolver orbauto-shared-srch-xx-dev.search.windows.net` from the SCM console.
3. That the shared private link from the search service to `orbauto-shared-ais-xx-dev` is approved and healthy. Query-time vectorization fails without it.
4. Developer access to the private endpoint for local development, including DNS resolution over VPN.
5. TLS inspection compatibility and certificate trust.

The indexer runs with `executionEnvironment: private`, which is consistent with this configuration and must not be changed to public.

## 6. Indexer and freshness prerequisites

The indexer configuration has three operational gaps that affect both answer correctness and access control.


| Observed               | Consequence                                                                                                                                      | Required decision                                   |
| ---------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------ | --------------------------------------------------- |
| `schedule: null`       | The index and its permission snapshot are frozen at the last manual run. Revoked SharePoint access continues to return results until reindexing. | Agree a schedule and an acceptable staleness window |
| `cache: null`          | Every run reprocesses and re-embeds all content, including image normalisation                                                                   | Enable the enrichment cache before scheduling       |
| `maxFailedItems: null` | Defaults to zero tolerance; one unparseable document fails the entire run                                                                        | Agree a failure allowance and monitoring            |


Permission data is a snapshot. Built-in trimming enforces whatever the last indexer run recorded, so the schedule bounds the access-revocation window but never eliminates it. The content owner must accept this window explicitly.

`outputFieldMappings` is empty, which is expected when the skillset writes chunks through index projections. This makes chunk-level field availability a property of the skillset rather than the indexer, and it must be verified directly against returned documents.

## 7. Application implementation requirements

The implementation must include:

1. A callable Agent Framework function named `search_hr_policy_documents_in_sharepoint`.
2. One model-visible argument, `q`, containing one to three generated search keywords.
3. Server-side acquisition of the end user's search-scoped token. The model must never influence it.
4. Hybrid retrieval combining the text query and a vectorizable text query against `text_vector`.
5. The semantic ranker, using the configured semantic configuration.
6. An explicit `select` list that excludes `text_vector`.
7. Bounded request timeouts and cancellation.
8. Controlled failure results that allow the agent to state that approved guidance could not be verified.
9. A per-call user token rather than a per-client one. `Azure.Search.Documents` accepts `querySourceAuthorization` as a parameter on `SearchAsync`, so a singleton `SearchClient` authenticated with the application's managed identity is sufficient; no per-request client or pipeline policy is required.

Two constraints follow from the feature still being in preview, and both must be accepted before this reaches production:

- The parameter is exposed only by preview packages. Neither `11.7.0` nor `12.0.0` stable includes it, so the implementation pins `Azure.Search.Documents 12.1.0-beta.1`.
- Permission filtering is honoured only by preview service versions, so the client pins `V2026_05_01_Preview`. Pinning a stable service version such as `V2026_04_01` is actively unsafe here: the forwarded user token would be ignored and every caller would receive untrimmed results.

Both pins should be revisited when document-level permission filtering reaches general availability.
10. An `Enabled` flag that fully disables the tool without removing configuration.
11. No client-side re-ranking. The semantic ranker's ordering is authoritative.

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

Several improvements require rebuilding the index rather than adding fields. The index currently holds a single unscheduled run of content and no production traffic depends on it, so this is the least disruptive point at which to make them.

Decide before the index carries production content and a scheduled indexer:

1. Set `text_vector` to `stored: false`. It is never returned.
2. Configure vector compression for the 3072-dimension vectors.
3. Make `metadata_spo_item_path` filterable if scoping by document library may be required.
4. Add a last-modified field, required by the agent's source-priority rule.
5. Add a country or audience field if country-specific LOA prioritisation must be enforced rather than inferred.
6. Rename the index to an environment-specific name. Indexes cannot be renamed, and each environment would otherwise receive its own opaque wizard-generated name.

Items 1 to 3 and 6 require a rebuild. Items 4 and 5 are additive but require reindexing to populate.

## 11. Deployment prerequisites

Before deployment to an environment, confirm:

- The index exists with the expected schema, semantic configuration, and vectorizer.
- The search service managed identity has `Cognitive Services OpenAI User` on the AI Services resource.
- The shared private link from Search to Azure OpenAI is approved.
- Private DNS resolution from the App Service returns a private address.
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
- Tool registration under `search_hr_policy_documents_in_sharepoint`.
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