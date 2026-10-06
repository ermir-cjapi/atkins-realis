# Azure AI Search infrastructure

This document describes the Azure AI Search data-plane configuration used by the HR SharePoint retrieval tool. The definitions are kept in [infra/search](../infra/search/) and are applied after the Azure resources have been provisioned.

For application prerequisites, identity design, permission trimming, and acceptance tests, see [SharePoint AI Search tool prerequisites](sharepoint-ai-search-tool-prerequisites.md).

## Architecture

The deployment creates four Azure AI Search objects:

```text
SharePoint document library
        |
        v
SharePoint data source -> indexer -> Content Understanding -> embeddings -> HR index (chunks only)
                                                        |
                                                        v
                         ACL fields (UserIds, GroupIds, SharePointSiteUrl)
```

The definitions mirror the configuration produced by the Azure portal "Import and vectorize data" wizard for SharePoint with ACL support, so that code-deployed objects behave the same as wizard-created ones.

At query time, the application uses hybrid text/vector retrieval and semantic ranking. The index's permission filter evaluates the end-user token supplied in `x-ms-query-source-authorization`; the application identity separately authorizes access to the search service.

## Repository layout

| File | Purpose |
| --- | --- |
| [index.json](../infra/search/index.json) | Index schema, ACL fields, semantic ranking, vector profile, and integrated vectorizer. |
| [datasource.json](../infra/search/datasource.json) | SharePoint connector, site/library scope, and ACL ingestion options. |
| [skillset.json](../infra/search/skillset.json) | Content Understanding chunking, embedding generation, and index projections. |
| [indexer.json](../infra/search/indexer.json) | Indexer schedule, private execution, file exclusions, ACL field mappings, and failure limits. |
| [deploy-search-dpos.ps1](../infra/search/deploy-search-dpos.ps1) | Token substitution, validation, backup, deployment, and best-effort rollback for all four objects. |

The files are JSON templates. Tokens are replaced at deployment time; do not replace them with environment-specific values in source control.

Local runs write backups of the previous definitions to `infra/search/backups/`. That folder is git-ignored and is not included in the build artifact.

## Resource configuration

### Index

The index is configured with:

- `sharePointConnectorAppRegistration`, containing the ingestion application ID, federated credential ID, and tenant ID used by the service to resolve SharePoint permissions at query time.
- `chunk_id` as the key and `parent_id` for document-level deduplication.
- Searchable `chunk` and `title` fields.
- A non-retrievable, non-stored, 3,072-dimensional `text_vector` field using `text-embedding-3-large`.
- Non-retrievable `UserIds` and `GroupIds` ACL collections with `permissionFilter` values `userIds` and `groupIds`.
- `SharePointSiteUrl` with `sharepointSiteUrl: true`, as created by the wizard.
- `metadata_spo_item_path` and `weburi` for citations.
- `last_modified_date` and `created_date_sp` for source metadata.
- A semantic configuration that prioritizes `title` and `chunk` and uses `BoostedRerankerScore`.
- An HNSW cosine vector profile with an Azure OpenAI vectorizer.

The default object names are derived from the index name:

| Object | Default name |
| --- | --- |
| Index | `<IndexName>` |
| Data source | `<IndexName>-datasource` |
| Skillset | `<IndexName>-skillset` |
| Indexer | `<IndexName>-indexer` |
| Semantic configuration | `<SkillsetName>-semantic-configuration` |
| Vector algorithm | `<SkillsetName>-hnsw-algorithm` |
| Vector profile | `<SkillsetName>-text-profile` |
| Vectorizer | `<SkillsetName>-text-vectorizer` |

### Data source

The data source uses the Azure AI Search SharePoint connector. Its scope is defined by:

- `SharePointSiteUrl`: the SharePoint site URL.
- `indexerPermissionOptions: ["userIds", "groupIds"]`: permission metadata copied from SharePoint.

The connection string is secretless:

```text
SharePointOnlineEndpoint=<site-url>;ApplicationId=<ingestion-app-id>;FederatedCredentialApplicationId=<search-managed-identity-app-id>;TenantId=<tenant-id>
```

`FederatedCredentialApplicationId` and `TenantId` are required. Without them, the connector falls back to interactive device-code sign-in and indexer creation hangs.

The ingestion app registration requires:

- A federated identity credential that trusts the Search service managed identity.
- Admin-consented application permissions for SharePoint and Microsoft Graph, such as `Files.Read.All`, `Sites.Read.All` or `Sites.Selected`, and `User.Read.All` or `GroupMember.Read.All` for ACL resolution.

No Search admin key or client secret is used.

### Skillset

The skillset:

1. Reads file data through Content Understanding.
2. Splits document content into sections.
3. Generates embeddings for each section.
4. Projects each section into the target index together with `title`, citation metadata, dates, and ACL fields.
5. Skips parent documents with `skipIndexingParentDocuments`, so the index contains chunk documents only.

The projection mappings populate `metadata_user_ids` as `UserIds`, `metadata_group_ids` as `GroupIds`, and `metadata_spo_site_url` as `SharePointSiteUrl` on every chunk. Because only chunks are written, projection mappings, rather than ordinary indexer field mappings, populate the chunk ACL fields.

The Search service managed identity is used for the AI Services connection. It requires `Cognitive Services OpenAI User` on the AI Services resource and approved private connectivity.

### Indexer

The indexer runs:

- Once per day, using `P1D`. The interval is authoritative even if the JSON description says hourly.
- In the private execution environment.
- With `allowSkillsetToReadFileData: true`.
- With `maxFailedItems: 50` and `maxFailedItemsPerBatch: 30`.
- Without a cache or output field mappings.

The indexer retains field mappings for `metadata_user_ids` to `UserIds`, `metadata_group_ids` to `GroupIds`, and `metadata_spo_item_path` to `metadata_spo_item_path`. With `skipIndexingParentDocuments`, these mappings do not populate chunks; skillset projections do. The mappings are retained because the portal uses them to show **Enable SharePoint ACL support** as selected.

The indexer excludes video, calendar, font, stylesheet, JavaScript, map, and download file extensions. A manual run may be started after deployment when initial ingestion is required.

The service may enable folder propagation automatically and report a warning on each run. This warning is expected and can be ignored.

## Identity and RBAC

The following identities and permissions are required:

| Identity | Permission | Scope |
| --- | --- | --- |
| Deployment service connection or operator | `Search Service Contributor` | Search service |
| HR API managed identity | `Search Index Data Reader` | Search service or target index |
| Search service managed identity | `Cognitive Services OpenAI User` | AI Services resource |
| SharePoint ingestion app registration | SharePoint application permissions required by the connector | HR SharePoint site/tenant |

The managed identity application ID used for the Search connector's federated credential is not the ingestion application's client ID. The deployment script rejects those values being equal.

The API also needs a valid end-user token for permission trimming at query time. A successful deployment does not prove that query-time ACL trimming or on-behalf-of authentication is working.

## Deployment script

Run the script from [infra/search](../infra/search/) after authenticating with Azure CLI. The authenticated identity must be able to write Search data-plane objects.

```powershell
az login
az account set --subscription '<subscription-id>'
az account get-access-token --scope https://search.azure.com/.default --query accessToken -o tsv

.\deploy-search-dpos.ps1 `
  -SearchEndpoint 'https://<search-service>.search.windows.net' `
  -IndexName '<index-name>' `
  -SharePointSiteUrl 'https://<tenant>.sharepoint.com/sites/<site>' `
  -SharePointLibraryUrl 'https://<tenant>.sharepoint.com/sites/<site>/Shared Documents' `
  -TenantId '<tenant-id>' `
  -IngestionApplicationId '<sharepoint-ingestion-app-client-id>' `
  -FederatedCredentialApplicationId '<search-managed-identity-app-id>' `
  -AiServicesEndpoint 'https://<ai-services>.services.ai.azure.com/' `
  -EmbeddingEndpoint 'https://<ai-services>.openai.azure.com'
```

The script:

- Validates endpoint and GUID formats.
- Derives names for omitted data source, skillset, and indexer parameters.
- Replaces tokens and rejects unresolved tokens.
- Parses each expanded document as JSON before sending it.
- Backs up current index, data source, skillset, and indexer definitions to `-BackupDirectory`, which defaults to `infra/search/backups/<index>-<timestamp>`.
- Disables an existing indexer during the update so it cannot run against a partly updated pipeline. A run already in progress is not interrupted.
- Creates or updates the objects in this order: index, data source, skillset, indexer. PUT requests use an Azure CLI Entra token for `https://search.azure.com`.
- If a PUT fails, restores definitions that existed before deployment and deletes objects created by the failed deployment. Use `-NoRollback` to leave the failed state for inspection.

A new indexer starts its first run when it is created. The data source connection string cannot be read back from the service, so a restored data source keeps the new connection string.

Azure AI Search data-plane operations are not transactional. Rollback is best effort. It restores object definitions, but cannot restore indexed document content, indexer execution history, or external effects from an indexer that already ran. Review rollback errors and validate all objects before retrying.

The deployment order is intentional: the index is created before the skillset because the skillset projection targets that index.

### Optional object names

Supply these options when an environment does not use the defaults:

```powershell
-SkillsetName '<skillset-name>'
-DataSourceName '<data-source-name>'
-IndexerName '<indexer-name>'
-ApiVersion '2026-08-01-preview'
-BackupDirectory '<folder>'
-NoRollback
```

Use the same object names in the API's `SharePointSearch` configuration and in the pipeline variable group. The API needs the index and semantic configuration names at runtime; it does not need the data source, skillset, or indexer names.

## Pipeline deployment

The release pipeline calls the same script from [templates/deploy-env.pipeline.yml](../pipelines/templates/deploy-env.pipeline.yml). Each environment's variable group must provide:

| Pipeline parameter | Variable group value |
| --- | --- |
| `searchEndpoint` | `sharePointSearchEndpoint` |
| `searchIndexName` | `sharePointSearchIndexName` |
| `searchSkillsetName` | `sharePointSearchSkillsetName` |
| `searchDataSourceName` | `sharePointSearchDataSourceName` |
| `searchIndexerName` | `sharePointSearchIndexerName` |
| `sharePointSiteUrl` | `sharePointSiteUrl` |
| `sharePointLibraryUrl` | `sharePointLibraryUrl` |
| `tenantId` | `entraTenantId` |
| `searchIngestionApplicationId` | `searchIngestionApplicationId` |
| `searchFederatedCredentialApplicationId` | `searchFederatedCredentialApplicationId` |
| `searchEmbeddingEndpoint` | `searchEmbeddingEndpoint` |
| `searchAiServicesEndpoint` | `searchAiServicesEndpoint` |

`Agents-HR-DEV`, `Agents-HR-UAT`, and `Agents-HR-PROD` should each use environment-specific values. These values are configuration, not secrets; never add Search admin keys, user tokens, or client secrets to the variable groups for this deployment.

The build pipeline publishes the JSON templates and the script (not local backups) in the `Infrastructure` artifact under `search/`.

The deploy pipeline runs infrastructure deployment first, then Search data-plane deployment, then the API deployment. The Search step writes backups to the agent's temp directory. It then publishes them as the pipeline artifact `search-backups-<environment>-<attempt>`, even when the step fails, so previous definitions are available for manual recovery.

The service connection's identity needs `Search Service Contributor` on each environment's Search service. The Search objects can be updated independently by rerunning the deployment stage.

## Validation

After deployment, verify all four objects:

```powershell
$endpoint = 'https://<search-service>.search.windows.net'
$apiVersion = '2026-08-01-preview'
$index = '<index-name>'

az rest --method get --url "$endpoint/indexes/$index`?api-version=$apiVersion" --resource https://search.azure.com
az rest --method get --url "$endpoint/datasources/$index-datasource`?api-version=$apiVersion" --resource https://search.azure.com
az rest --method get --url "$endpoint/skillsets/$index-skillset`?api-version=$apiVersion" --resource https://search.azure.com
az rest --method get --url "$endpoint/indexers/$index-indexer`?api-version=$apiVersion" --resource https://search.azure.com
az rest --method post --url "$endpoint/indexers/$index-indexer/run`?api-version=$apiVersion" --resource https://search.azure.com
az rest --method get --url "$endpoint/indexers/$index-indexer/status`?api-version=$apiVersion" --resource https://search.azure.com
```

Confirm that:

- The index includes `sharePointConnectorAppRegistration`.
- The vector field has 3,072 dimensions and is not retrievable or stored.
- `UserIds` and `GroupIds` are present and marked for permission filtering.
- The skillset projection targets the expected index.
- The indexer is enabled, private, and has the expected schedule.
- The portal shows **Enable SharePoint ACL support** as selected for the indexer.
- A completed run indexed documents and has no unexpected failed items. The folder-propagation warning is expected.
- Returned documents contain a usable `weburi` or citation path.
- ACL fields are populated.

ACL fields are not normally retrievable. Verify them only in DEV by temporarily setting `retrievable: true` on `UserIds` and `GroupIds`, redeploying, and querying a few chunks. SharePoint group IDs appear with an `spg:` prefix. Revert the temporary change afterward.

If documents were indexed before ACL ingestion was configured, refresh their permissions without reprocessing content:

```powershell
az rest --method post --url "$endpoint/indexers/$index-indexer/resync`?api-version=$apiVersion" --resource https://search.azure.com --body '{"options":["permissions"]}'
```

Do not enable `SharePointSearch__Enabled` in the API until an authenticated acceptance test proves both retrieval and document-level permission trimming.

## Troubleshooting

| Symptom | Checks |
| --- | --- |
| `az rest` returns `401` or `403` | Confirm `az login`, subscription context, Search data-plane RBAC, and the `https://search.azure.com/.default` token resource. |
| Script reports an unresolved token | Check the template token spelling and confirm all mandatory parameters were supplied. Do not bypass validation. |
| Data source or indexer creation hangs | Confirm the connection string contains `FederatedCredentialApplicationId` and `TenantId`, and check the ingestion app's federated credential. Missing values can cause device-code sign-in fallback. |
| Indexer shows **never run** and fails immediately | Compare the definitions with a wizard-created SharePoint ACL index. Do not add `propagateFolderChanges` to the data source query. If the index previously used a different schema, delete it and redeploy. |
| All items fail with `Projection.IndexProjections ... Bad Request` | The target index may be in a bad state. Delete the index, redeploy, and rerun the indexer. |
| Portal does not show SharePoint ACL support as enabled | Confirm the indexer `fieldMappings` include `metadata_user_ids` and `metadata_group_ids`. |
| Warning that folder propagation was enabled automatically | Expected; no action is required. |
| Search cannot reach AI Services | Check Search managed identity RBAC, shared private link approval, private DNS, and the AI Services endpoint. |
| SharePoint connector rejects the data source | Check site and library URL formats, ingestion app permissions, tenant ID, and admin consent. |
| Indexer has no documents | Inspect indexer status, excluded file types, source library scope, and the first error details. |
| Results are not permission-trimmed | Confirm `x-ms-query-source-authorization` is present, contains the verified user's token, and the indexer populated `UserIds` and `GroupIds`. |
| Existing objects use unexpected names | Pass explicit `-SkillsetName`, `-DataSourceName`, and `-IndexerName`, then align the API and pipeline configuration. |

The JSON files use preview Search features. Keep the API version in the deployment script aligned with the service features and retest after changing it.
