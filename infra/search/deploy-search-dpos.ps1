[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^https://[a-z0-9-]+\.search\.windows\.net/?$')]
    [string] $SearchEndpoint,

    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z0-9][a-z0-9-]{1,127}$')]
    [string] $IndexName,

    [Parameter(Mandatory)]
    [ValidatePattern('^https://[a-z0-9-]+\.sharepoint\.com/.+$')]
    [string] $SharePointSiteUrl,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F-]{36}$')]
    [string] $TenantId,

    # Ingestion app registration's Application (client) ID.
    # Used for ApplicationId (data source) and applicationId (index).
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F-]{36}$')]
    [string] $IngestionApplicationId,

    # The search service managed identity's Application ID
    # (Entra ID > Enterprise applications > <search service> > Properties > Application ID,
    #  or the Client ID of a user-assigned identity).
    # Used for BOTH FederatedCredentialApplicationId (data source) and federatedCredentialId (index).
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F-]{36}$')]
    [string] $FederatedCredentialApplicationId,

    [Parameter(Mandatory)]
    [ValidatePattern('^https://[a-z0-9.-]+\.(openai\.azure\.com|services\.ai\.azure\.com|cognitiveservices\.azure\.com)/?$')]
    [string] $EmbeddingEndpoint,

    [Parameter()]
    [string] $SkillsetName = "$IndexName-skillset",

    [Parameter()]
    [string] $DataSourceName = "$IndexName-datasource",

    [Parameter()]
    [string] $IndexerName = "$IndexName-indexer",

    [Parameter(Mandatory)]
    [ValidatePattern('^https://[a-z0-9-]+\.services\.ai\.azure\.com/?$')]
    [string] $AiServicesEndpoint,

    [Parameter(Mandatory)]
    [ValidatePattern('^https://[a-z0-9-]+\.sharepoint\.com/.+$')]
    [string] $SharePointLibraryUrl,

    [ValidatePattern('^20\d{2}-\d{2}-\d{2}(-preview)?$')]
    [string] $ApiVersion = '2026-08-01-preview',

    # Where definitions of pre-existing objects are saved before they are changed.
    [Parameter()]
    [string] $BackupDirectory,

    # Leave the service as-is on failure (useful for inspecting a failed state).
    [switch] $NoRollback
)

$ErrorActionPreference = 'Stop'
$SearchEndpoint = $SearchEndpoint.TrimEnd('/')
$SharePointSiteUrl = $SharePointSiteUrl.TrimEnd('/')
$EmbeddingEndpoint = $EmbeddingEndpoint.TrimEnd('/')
$AiServicesEndpoint = $AiServicesEndpoint.TrimEnd('/')
$SharePointLibraryUrl = $SharePointLibraryUrl.TrimEnd('/')
if (-not $BackupDirectory) { $BackupDirectory = Join-Path $PSScriptRoot 'backups' }

if ($FederatedCredentialApplicationId -eq $IngestionApplicationId) {
    throw "FederatedCredentialApplicationId must be the managed identity's Application ID, not the ingestion app's client ID."
}

$names = [ordered]@{
    DataSource = $DataSourceName
    Skillset = $SkillsetName
    Indexer = $IndexerName
    SemanticConfiguration = "$SkillsetName-semantic-configuration"
    VectorAlgorithm = "$SkillsetName-hnsw-algorithm"
    VectorProfile = "$SkillsetName-text-profile"
    Vectorizer = "$SkillsetName-text-vectorizer"
}

$replacements = [ordered]@{
    '__INDEX_NAME__' = $IndexName
    '__DATA_SOURCE_NAME__' = $names.DataSource
    '__SKILLSET_NAME__' = $names.Skillset
    '__INDEXER_NAME__' = $names.Indexer
    '__SEMANTIC_CONFIGURATION_NAME__' = $names.SemanticConfiguration
    '__VECTOR_ALGORITHM_NAME__' = $names.VectorAlgorithm
    '__VECTOR_PROFILE_NAME__' = $names.VectorProfile
    '__VECTORIZER_NAME__' = $names.Vectorizer
    '__SHAREPOINT_SITE_URL__' = $SharePointSiteUrl
    '__TENANT_ID__' = $TenantId
    '__INGESTION_APPLICATION_ID__' = $IngestionApplicationId
    # Both tokens intentionally receive the same value.
    '__FEDERATED_CREDENTIAL_APPLICATION_ID__' = $FederatedCredentialApplicationId
    '__FEDERATED_CREDENTIAL_ID__' = $FederatedCredentialApplicationId
    '__EMBEDDING_ENDPOINT__' = $EmbeddingEndpoint
    '__AI_SERVICES_ENDPOINT__' = $AiServicesEndpoint
    '__SHAREPOINT_LIBRARY_URL__' = $SharePointLibraryUrl
}

#region Helpers

function Expand-Template {
    param([Parameter(Mandatory)][string] $Path)

    $content = Get-Content -LiteralPath $Path -Raw
    foreach ($replacement in $replacements.GetEnumerator()) {
        $content = $content.Replace($replacement.Key, $replacement.Value)
    }

    if ($content -match '__[A-Z0-9_]+__') {
        throw "Template '$Path' contains an unresolved token: $($Matches[0])"
    }

    $null = $content | ConvertFrom-Json
    return $content
}

$script:SearchToken = $null
function Get-SearchToken {
    if (-not $script:SearchToken) {
        $token = & az account get-access-token --resource 'https://search.azure.com' --query accessToken -o tsv --only-show-errors 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Could not get an Entra token for Azure AI Search. Run 'az login' first.`n$token"
        }
        $script:SearchToken = ($token | Out-String).Trim()
    }
    return $script:SearchToken
}

# Calls the Search data plane and returns the HTTP status instead of throwing,
# so the caller can tell "not found" apart from real failures.
function Invoke-Search {
    param(
        [Parameter(Mandatory)][ValidateSet('GET', 'PUT', 'DELETE')][string] $Method,
        [Parameter(Mandatory)][string] $Kind,
        [Parameter(Mandatory)][string] $Name,
        [string] $Body
    )

    $request = @{
        Method          = $Method
        Uri             = "$SearchEndpoint/$Kind/$([Uri]::EscapeDataString($Name))?api-version=$ApiVersion"
        Headers         = @{ Authorization = "Bearer $(Get-SearchToken)" }
        UseBasicParsing = $true
    }
    if ($Body) {
        $request.Body = [System.Text.Encoding]::UTF8.GetBytes($Body)
        $request.ContentType = 'application/json; charset=utf-8'
    }

    try {
        $response = Invoke-WebRequest @request
        return [pscustomobject]@{ Status = [int]$response.StatusCode; Content = $response.Content; Error = $null }
    }
    catch {
        $status = 0
        if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        $detail = if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $_.ErrorDetails.Message } else { $_.Exception.Message }
        return [pscustomobject]@{ Status = $status; Content = $null; Error = $detail }
    }
}

# Turns a GET response into a body that can be PUT back.
function ConvertTo-RestorableBody {
    param(
        [Parameter(Mandatory)]
        [string] $Json,

        [Parameter(Mandatory)]
        [string] $Kind
    )

    $obj = $Json | ConvertFrom-Json

    # Remove service-generated properties
    $obj.PSObject.Properties.Remove('@odata.context')
    $obj.PSObject.Properties.Remove('@odata.etag')

    if ($Kind -eq 'datasources' -and $null -eq $obj.connectionString) {
        # The service never returns the connection string.
        # '<unchanged>' keeps whatever is currently set.
        $obj.connectionString = '<unchanged>'
    }

    return ($obj | ConvertTo-Json)
}
function Get-DisabledIndexerBody {
    param([Parameter(Mandatory)][string] $Json)

    $pattern = [regex]'"disabled"\s*:\s*(true|false|null)'
    if ($pattern.IsMatch($Json)) { return $pattern.Replace($Json, '"disabled": true', 1) }
    return ([regex]'^\s*\{').Replace($Json, '{ "disabled": true,', 1)
}

function Test-Success([int] $Status, [int[]] $Allowed = @(200, 201, 204)) {
    return $Allowed -contains $Status
}

function Restore-SearchObject {
    param($Step, $Problems)

    Write-Host "  Restoring previous $($Step.Kind)/$($Step.Name)"
    if ($Step.Kind -eq 'datasources') {
        Write-Warning "  The previous connection string can't be read back from the service; $($Step.Name) keeps the new one."
    }
    $r = Invoke-Search -Method PUT -Kind $Step.Kind -Name $Step.Name -Body $Step.Backup
    if (-not (Test-Success $r.Status)) {
        $hint = if ($Step.Kind -eq 'indexes') { ' (fields added in place cannot be removed; the index keeps them, which the old pipeline ignores)' } else { '' }
        $Problems.Add("restore $($Step.Kind)/$($Step.Name) failed (HTTP $($r.Status))$hint`: $($r.Error)")
    }
}

function Remove-NewSearchObject {
    param($Step, $Problems)

    Write-Host "  Deleting $($Step.Kind)/$($Step.Name) (created by this run)"
    $r = Invoke-Search -Method DELETE -Kind $Step.Kind -Name $Step.Name
    if (-not (Test-Success $r.Status @(200, 204, 404))) {
        $Problems.Add("delete $($Step.Kind)/$($Step.Name) failed (HTTP $($r.Status)): $($r.Error)")
    }
}

function Invoke-Rollback {
    param([Parameter(Mandatory)] $Plan)

    Write-Host 'Rolling back...'
    $problems = [System.Collections.Generic.List[string]]::new()
    $indexer = $Plan | Where-Object Kind -eq 'indexers'
    $others = @($Plan | Where-Object { $_.Kind -ne 'indexers' -and $_.Touched })
    [array]::Reverse($others)

    # 1. Remove a new indexer first, so nothing runs against a half-restored pipeline.
    if ($indexer.Touched -and -not $indexer.Existed) { Remove-NewSearchObject $indexer $problems }

    # 2. Undo skillset, data source and index in reverse deployment order.
    foreach ($step in $others) {
        if ($step.Existed) { Restore-SearchObject $step $problems }
        else { Remove-NewSearchObject $step $problems }
    }

    # 3. Restore a pre-existing indexer last, with its original enabled/disabled state.
    if ($indexer.Touched -and $indexer.Existed) { Restore-SearchObject $indexer $problems }

    return $problems.ToArray()
}

#endregion

$plan = @(
    @{ Kind = 'indexes';     Name = $IndexName;        Template = 'index.json' }       # must precede the skillset (projection target)
    @{ Kind = 'datasources'; Name = $names.DataSource; Template = 'datasource.json' }
    @{ Kind = 'skillsets';   Name = $names.Skillset;   Template = 'skillset.json' }
    @{ Kind = 'indexers';    Name = $names.Indexer;    Template = 'indexer.json' }
) | ForEach-Object {
    [pscustomobject]@{
        Kind = $_.Kind; Name = $_.Name; Template = $_.Template
        Body = $null; Existed = $false; Backup = $null; Touched = $false
    }
}

# 1. Expand and validate every template before touching the service.
foreach ($step in $plan) {
    $step.Body = Expand-Template -Path (Join-Path $PSScriptRoot $step.Template)
}

# 2. Snapshot existing objects (in memory and on disk).
$backupPath = Join-Path $BackupDirectory ("{0}-{1}" -f $IndexName, (Get-Date -Format 'yyyyMMdd-HHmmss'))
foreach ($step in $plan) {
    $r = Invoke-Search -Method GET -Kind $step.Kind -Name $step.Name
    if ($r.Status -eq 200) {
        $step.Existed = $true
        $step.Backup = ConvertTo-RestorableBody -Json $r.Content -Kind $step.Kind
        $null = New-Item -ItemType Directory -Path $backupPath -Force
        [System.IO.File]::WriteAllText((Join-Path $backupPath "$($step.Kind)-$($step.Name).json"), $step.Backup, [System.Text.UTF8Encoding]::new($false))
    }
    elseif ($r.Status -ne 404) {
        throw "Could not read $($step.Kind)/$($step.Name) (HTTP $($r.Status)). Nothing was changed.`n$($r.Error)"
    }
}
if (Test-Path -LiteralPath $backupPath) { Write-Host "Backed up existing definitions to $backupPath" }

# 3. Pause an existing indexer so it can't run against a half-updated pipeline.
#    (A run already in progress is not interrupted.)
$indexerStep = $plan | Where-Object Kind -eq 'indexers'
if ($indexerStep.Existed) {
    Write-Host "Pausing existing indexer $($indexerStep.Name) during the update"
    $r = Invoke-Search -Method PUT -Kind 'indexers' -Name $indexerStep.Name -Body (Get-DisabledIndexerBody $indexerStep.Backup)
    if (-not (Test-Success $r.Status)) {
        throw "Could not pause indexer $($indexerStep.Name) (HTTP $($r.Status)). Nothing was changed.`n$($r.Error)"
    }
    $indexerStep.Touched = $true
}

# 4. Deploy. The indexer goes last, so a new pipeline only starts running once everything else succeeded.
$failure = $null
foreach ($step in $plan) {
    Write-Host "Deploying Azure AI Search $($step.Kind)/$($step.Name)"
    $step.Touched = $true   # include in rollback even when the outcome is ambiguous (timeouts, network errors)
    $r = Invoke-Search -Method PUT -Kind $step.Kind -Name $step.Name -Body $step.Body
    if (-not (Test-Success $r.Status)) {
        $failure = "Azure AI Search rejected $($step.Kind)/$($step.Name) (HTTP $($r.Status)).`n$($r.Error)"
        break
    }
}

if ($failure) {
    Write-Warning $failure
    if ($NoRollback) {
        throw "Deployment failed; rollback skipped (-NoRollback). Previous definitions: $backupPath"
    }
    $problems = @(Invoke-Rollback -Plan $plan)
    if ($problems.Count -gt 0) {
        throw "Deployment failed and rollback was incomplete:`n - $($problems -join "`n - ")`nPrevious definitions: $backupPath`n`nOriginal error: $failure"
    }
    throw "Deployment failed and was rolled back.`n$failure"
}

Write-Host "Azure AI Search data-plane deployment completed for index '$IndexName'."
