<#
.SYNOPSIS
    Creates a passwordless Microsoft Entra ID App Registration + GitHub OIDC Federated Credential
    for a specific GitHub Environment (dev or prod), scoped strictly to that environment's Resource Group.

.EXAMPLE
    # Run in Azure Cloud Shell (or any machine with Azure CLI):
    .\infra\setup-github-oidc.ps1 -GitHubOrg "your-github-username" -GitHubRepo "AzureKyoshi" -EnvName "dev" -ResourceGroup "rg-azurekyoshi-dev"
    .\infra\setup-github-oidc.ps1 -GitHubOrg "your-github-username" -GitHubRepo "AzureKyoshi" -EnvName "prod" -ResourceGroup "rg-azurekyoshi-prod"
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$GitHubOrg,

    [Parameter(Mandatory = $true)]
    [string]$GitHubRepo,

    [ValidateSet("test", "prod")]
    [string]$EnvName = "test",

    [string]$ResourceGroup = ""
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($ResourceGroup)) {
    $ResourceGroup = "rg-azurekyoshi-$EnvName"
}

$account = az account show --output json | ConvertFrom-Json
$tenantId = $account.tenantId
$subscriptionId = $account.id
$rgId = az group show --name $ResourceGroup --query id -o tsv

$displayName = "sp-azurekyoshi-github-$EnvName"
Write-Host "=== 1. Creating Entra ID App & Service Principal '$displayName' ===" -ForegroundColor Cyan

$app = az ad app create --display-name $displayName --output json | ConvertFrom-Json
$appId = $app.appId
$appObjectId = $app.id

$sp = az ad sp create --id $appId --output json | ConvertFrom-Json
$spObjectId = $sp.id

# ------------------------------------------------------------------------------
Write-Host "`n=== 2. Adding GitHub OIDC Federated Credential for environment '$EnvName' ===" -ForegroundColor Cyan
$fedBody = @{
    name        = "github-$EnvName-env"
    issuer      = "https://token.actions.githubusercontent.com"
    subject     = "repo:${GitHubOrg}/${GitHubRepo}:environment:${EnvName}"
    description = "Passwordless GitHub Actions OIDC deploy for $EnvName"
    audiences   = @("api://AzureADTokenExchange")
} | ConvertTo-Json -Depth 4

$fedFile = [System.IO.Path]::GetTempFileName()
Set-Content -Path $fedFile -Value $fedBody -Encoding UTF8
az ad app federated-credential create --id $appObjectId --parameters "@$fedFile" --output none
Remove-Item $fedFile -Force

# ------------------------------------------------------------------------------
Write-Host "`n=== 3. Granting Contributor RBAC scoped ONLY to '$ResourceGroup' ===" -ForegroundColor Cyan
az role assignment create `
    --assignee-object-id $spObjectId `
    --assignee-principal-type ServicePrincipal `
    --role "Contributor" `
    --scope $rgId `
    --output none

# Discover existing resource names in this RG so we can print exact GitHub Variables
$sqlServer = az sql server list --resource-group $ResourceGroup --query "[0].name" -o tsv
$funcApp   = az functionapp list --resource-group $ResourceGroup --query "[0].name" -o tsv
$funcHost  = az functionapp list --resource-group $ResourceGroup --query "[0].properties.defaultHostName || [0].defaultHostName" -o tsv
if ([string]::IsNullOrWhiteSpace($funcHost) -and $funcApp) { $funcHost = "$funcApp.azurewebsites.net" }
$swaName   = az staticwebapp list --resource-group $ResourceGroup --query "[0].name" -o tsv
$swaToken  = if ($swaName) { az staticwebapp secrets list --name $swaName --resource-group $ResourceGroup --query properties.apiKey -o tsv } else { "<run-provision-first>" }

Write-Host "`n==================================================================" -ForegroundColor Green
Write-Host "  GITHUB OIDC SETUP COMPLETE FOR ENVIRONMENT: $EnvName" -ForegroundColor Green
Write-Host "==================================================================" -ForegroundColor Green

Write-Host "`n--- STEP A: Grant GitHub Deployer SQL Migration Access ---" -ForegroundColor Yellow
Write-Host "Run once in Azure Portal -> SQL Database 'AzureKyoshiDb' -> Query Editor:"
Write-Host @"
CREATE USER [$displayName] FROM EXTERNAL PROVIDER;
ALTER ROLE db_ddladmin   ADD MEMBER [$displayName];
ALTER ROLE db_datareader ADD MEMBER [$displayName];
ALTER ROLE db_datawriter ADD MEMBER [$displayName];
"@ -ForegroundColor White

Write-Host "`n--- STEP B: Add these GitHub Environment Variables (Settings -> Environments -> $EnvName) ---" -ForegroundColor Yellow
Write-Host "AZURE_TENANT_ID        = $tenantId"
Write-Host "AZURE_SUBSCRIPTION_ID  = $subscriptionId"
Write-Host "AZURE_CLIENT_ID        = $appId"
Write-Host "AZURE_RESOURCE_GROUP   = $ResourceGroup"
Write-Host "AZURE_FUNCTIONAPP_NAME = $funcApp"
Write-Host "AZURE_SQL_SERVER       = $sqlServer"
Write-Host "AZURE_SQL_DATABASE     = AzureKyoshiDb"
Write-Host "CLIENT_API_BASE_URL    = https://$funcHost/"
Write-Host "CLIENT_SPA_CLIENT_ID   = <your AzureKyoshi-Client-* App ID>"
Write-Host "CLIENT_API_SCOPE       = api://<your AzureKyoshi-Api-* App ID>/access_as_user"

Write-Host "`n--- STEP C: Add this GitHub Environment Secret (Settings -> Environments -> $EnvName) ---" -ForegroundColor Yellow
Write-Host "AZURE_STATIC_WEB_APPS_API_TOKEN = $swaToken"
