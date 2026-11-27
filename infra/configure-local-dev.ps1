<#
.SYNOPSIS
    Discovers your provisioned Azure Kyoshi resources (Azure SQL, Azure AI Foundry,
    Azure Functions, Static Web App, and Entra ID App Registrations) and automatically
    configures local Visual Studio dev settings so you can press F5 against real Azure resources.

.EXAMPLE
    .\infra\configure-local-dev.ps1
    .\infra\configure-local-dev.ps1 -ResourceGroup "rg-azurekyoshi-dev"
    .\infra\configure-local-dev.ps1 -UseDevAuth   # Keeps real Azure SQL + Foundry, skips Entra login screen locally
#>

param(
    [string]$ResourceGroup = "rg-azurekyoshi-test",
    [string]$SqlDbName     = "AzureKyoshiDb",
    [switch]$UseDevAuth
)

$ErrorActionPreference = "Stop"

$solutionRoot = Split-Path -Parent $PSScriptRoot
$apiLocalSettingsPath = Join-Path $solutionRoot "AzureKyoshi.Api\local.settings.json"
$clientAppSettingsPath = Join-Path $solutionRoot "AzureKyoshi.Client\wwwroot\appsettings.json"
$clientDevSettingsPath = Join-Path $solutionRoot "AzureKyoshi.Client\wwwroot\appsettings.Development.json"

Write-Host "=== 1. Reading Azure session & resources in '$ResourceGroup' ===" -ForegroundColor Cyan
$account = az account show --output json | ConvertFrom-Json
$tenantId = $account.tenantId

# SQL Server
$sqlServer = az sql server list --resource-group $ResourceGroup --query "[0]" --output json | ConvertFrom-Json
if (-not $sqlServer) {
    throw "No Azure SQL Server found in '$ResourceGroup'."
}
$sqlFqdn = $sqlServer.fullyQualifiedDomainName
$sqlConnectionString = "Server=tcp:$sqlFqdn,1433;Initial Catalog=$SqlDbName;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;Authentication=Active Directory Default;"

# Refresh local IP firewall rule on Azure SQL so Visual Studio can connect
try {
    $myIp = (Invoke-RestMethod -Uri "https://api.ipify.org" -TimeoutSec 5).Trim()
    az sql server firewall-rule create `
        --resource-group $ResourceGroup `
        --server $sqlServer.name `
        --name "AllowLocalDevMachine" `
        --start-ip-address $myIp `
        --end-ip-address $myIp `
        --output none
    Write-Host "SQL Firewall   : Allowed local IP $myIp on $($sqlServer.name)"
} catch {
    Write-Warning "Could not auto-update SQL firewall rule for your current IP."
}

# Azure AI Foundry
$foundry = az cognitiveservices account list --resource-group $ResourceGroup --query "[?kind=='AIServices'] | [0]" --output json | ConvertFrom-Json
if (-not $foundry) {
    throw "No Azure AI Foundry (AIServices) resource found in '$ResourceGroup'."
}
$foundryEndpoint = "https://$($foundry.name).openai.azure.com/"

$deployments = az cognitiveservices account deployment list `
    --resource-group $ResourceGroup `
    --name $foundry.name `
    --query "[].name" --output json | ConvertFrom-Json

$deploymentName = if ($deployments -contains "gpt-6-luna") {
    "gpt-6-luna"
} elseif ($deployments -and $deployments.Count -gt 0) {
    $deployments[0]
} else {
    throw "No model deployment found on Foundry account '$($foundry.name)'."
}

# Function App (for production appsettings.json)
$funcApp = az functionapp list --resource-group $ResourceGroup --query "[0]" --output json | ConvertFrom-Json
$funcUrl = if ($funcApp) { "https://$($funcApp.defaultHostName)/" } else { "" }

# Entra ID App Registrations
$apiApp = az ad app list --filter "startswith(displayName, 'AzureKyoshi-Api-')" --query "[0]" --output json | ConvertFrom-Json
$spaApp = az ad app list --filter "startswith(displayName, 'AzureKyoshi-Client-')" --query "[0]" --output json | ConvertFrom-Json

if (-not $UseDevAuth -and (-not $apiApp -or -not $spaApp)) {
    throw "Could not find AzureKyoshi-Api-* and AzureKyoshi-Client-* Entra ID app registrations. Run Step 7 of provision-azure.ps1 first, or pass -UseDevAuth."
}

$apiClientId = if ($apiApp) { $apiApp.appId } else { "" }
$spaClientId = if ($spaApp) { $spaApp.appId } else { "" }
$apiAudience = if ($apiClientId) { "api://$apiClientId" } else { "" }
$useDevModeBool = [bool]$UseDevAuth
$useDevModeStr  = if ($useDevModeBool) { "true" } else { "false" }

Write-Host "Tenant ID      : $tenantId"
Write-Host "SQL Server     : $sqlFqdn (DB: $SqlDbName)"
Write-Host "Foundry        : $foundryEndpoint (Deployment: $deploymentName)"
Write-Host "Function App   : $(if ($funcUrl) { $funcUrl } else { '(not found)' })"
Write-Host "Entra API App  : $(if ($apiClientId) { $apiClientId } else { '(not found)' })"
Write-Host "Entra SPA App  : $(if ($spaClientId) { $spaClientId } else { '(not found)' })"
Write-Host "Local Auth     : $(if ($useDevModeBool) { 'Local Dev Mode' } else { 'Microsoft Entra ID (Live)' })"

# ------------------------------------------------------------------------------
Write-Host "`n=== 2. Writing AzureKyoshi.Api/local.settings.json ===" -ForegroundColor Cyan
$localSettings = [ordered]@{
    IsEncrypted = $false
    Values = [ordered]@{
        AzureWebJobsStorage                    = "UseDevelopmentStorage=true"
        FUNCTIONS_WORKER_RUNTIME               = "dotnet-isolated"
        ASPNETCORE_ENVIRONMENT                 = "Development"
        "ConnectionStrings:DefaultConnection"  = $sqlConnectionString
        "Database:Provider"                    = "SqlServer"
        "Authentication:UseDevMode"            = $useDevModeStr
        "Authentication:DevUserId"             = "dev-local-user"
        "Authentication:DevUserName"           = "Local Azure Learner"
        "Authentication:DevUserEmail"          = "learner@azurekyoshi.local"
        "AzureAd:Instance"                     = "https://login.microsoftonline.com/"
        "AzureAd:TenantId"                     = $tenantId
        "AzureAd:ClientId"                     = $apiClientId
        "AzureAd:Audience"                     = $apiAudience
        "AzureFoundry:Endpoint"                = $foundryEndpoint
        "AzureFoundry:ApiKey"                  = ""
        "AzureFoundry:DeploymentName"          = $deploymentName
        "AzureFoundry:AllowDemoFallback"       = "false"
        "MicrosoftLearnMcp:Endpoint"           = "https://learn.microsoft.com/api/mcp"
    }
    Host = [ordered]@{
        LocalHttpPort   = 7071
        CORS            = "https://localhost:7221,http://localhost:5221"
        CORSCredentials = $false
    }
}
$localSettings | ConvertTo-Json -Depth 4 | Set-Content -Path $apiLocalSettingsPath -Encoding UTF8

# ------------------------------------------------------------------------------
Write-Host "=== 3. Writing AzureKyoshi.Client/wwwroot/appsettings.json ===" -ForegroundColor Cyan
$clientProdSettings = [ordered]@{
    ApiBaseUrl     = $funcUrl
    Authentication = [ordered]@{
        UseDevMode = $false
    }
    AzureAd        = [ordered]@{
        Authority         = "https://login.microsoftonline.com/$tenantId"
        ClientId          = $spaClientId
        ValidateAuthority = $true
    }
    ApiScopes      = @(
        "$apiAudience/access_as_user"
    )
}
$clientProdSettings | ConvertTo-Json -Depth 4 | Set-Content -Path $clientAppSettingsPath -Encoding UTF8

# ------------------------------------------------------------------------------
Write-Host "=== 4. Writing AzureKyoshi.Client/wwwroot/appsettings.Development.json ===" -ForegroundColor Cyan
$clientDevSettings = [ordered]@{
    ApiBaseUrl     = "http://localhost:7071/"
    Authentication = [ordered]@{
        UseDevMode = $useDevModeBool
    }
}
$clientDevSettings | ConvertTo-Json -Depth 4 | Set-Content -Path $clientDevSettingsPath -Encoding UTF8

Write-Host "`n==================================================================" -ForegroundColor Green
Write-Host "  LOCAL VISUAL STUDIO CONFIG READY!" -ForegroundColor Green
Write-Host "==================================================================" -ForegroundColor Green
Write-Host "Next steps:"
Write-Host "  1. Open AzureKyoshi.slnx in Visual Studio"
Write-Host "  2. Set Multiple Startup Projects: AzureKyoshi.Api (Start) + AzureKyoshi.Client (Start)"
Write-Host "  3. Press F5 (make sure you're signed into Visual Studio with your Azure account)"
Write-Host "     Note: First request may take ~30-45s if your Free Serverless Azure SQL DB is waking from auto-pause."

SQL SERVER
sql-azurekyoshi-2115.database.windows.net

<FOUNDRY_ENDPOINT> & <DEPLOYMENT_NAME>
https://aif-azurekyoshi-2115.services.ai.azure.com/
gpt-6-luna

<FUNC_URL>
func-azurekyoshi-2115.azurewebsites.net

<TENANT_ID> & <API_CLIENT_ID
92a465c3-371d-4b6e-8782-c0bfc4edb69e
77f0070b-20c8-405c-acc8-291c6c7c3781

<SPA_CLIENT_ID>
e003a204-50de-44d7-a3fa-dbdd7e80b28c