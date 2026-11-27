<#
.SYNOPSIS
    Provisions a 100% ISOLATED PRODUCTION environment for Azure Kyoshi (rg-azurekyoshi-prod):
    - Dedicated Resource Group (rg-azurekyoshi-prod)
    - Dedicated Azure SQL Server (Entra ID-only auth, NO local dev firewall rule) + Azure SQL Database
    - Dedicated Azure AI Foundry (AIServices, custom domain) + gpt-6-luna deployment
    - Dedicated Storage Account + User-Assigned Managed Identity + .NET 10 Flex Consumption Function App
    - Dedicated Azure Static Web App (Free or Standard)
    - Dedicated Microsoft Entra ID App Registrations (strict prod-only redirect URIs & CORS — NO localhost)

.EXAMPLE
    .\infra\provision-prod.ps1 -Location "eastus2" -SqlLocation "westus3" -Suffix "prod2115"
#>

param(
    [string]$Location        = "eastus2",
    [string]$SqlLocation     = "westus3",          # Region that accepts Azure SQL Server on your subscription
    [string]$ResourceGroup   = "rg-azurekyoshi-prod",
    [string]$Suffix          = "prod$(Get-Random -Minimum 1000 -Maximum 9999)",
    [string]$SqlDbName       = "AzureKyoshiDb",
    [string]$ModelName       = "gpt-6-luna",
    [string]$ModelVersion    = "",                 # Auto-resolved to latest GA version in $Location when blank
    [string]$DeploymentName  = "gpt-6-luna",
    [string]$StaticWebAppSku = "Free",             # Use "Standard" if you want custom SLA / linked backend
    [switch]$UsePaidBasicSql                       # Default uses Azure SQL Free Offer; pass -UsePaidBasicSql for flat ~$5/mo Basic tier
)

$ErrorActionPreference = "Stop"

$CleanSuffix   = ($Suffix -replace '[^a-zA-Z0-9]', '').ToLower()
$SqlServerName = "sql-azurekyoshi-$CleanSuffix"
$FoundryName   = "aif-azurekyoshi-$CleanSuffix"
$StorageName   = "stakfunc$CleanSuffix"
if ($StorageName.Length -gt 24) { $StorageName = $StorageName.Substring(0, 24) }
$IdentityName  = "id-azurekyoshi-func-$CleanSuffix"
$FuncAppName   = "func-azurekyoshi-$CleanSuffix"
$StaticAppName = "swa-azurekyoshi-$CleanSuffix"

Write-Host "=== 1. Checking Azure CLI session & signed-in user ===" -ForegroundColor Cyan
$account      = az account show --output json | ConvertFrom-Json
$tenantId     = $account.tenantId
$signedInUser = az ad signed-in-user show --output json | ConvertFrom-Json
$adminUpn     = $signedInUser.userPrincipalName
$adminOid     = $signedInUser.id

Write-Host "Subscription : $($account.name) ($($account.id))"
Write-Host "Tenant ID    : $tenantId"
Write-Host "Entra Admin  : $adminUpn"

# ------------------------------------------------------------------------------
Write-Host "`n=== 2. Creating PRODUCTION Resource Group '$ResourceGroup' ===" -ForegroundColor Cyan
az group create `
    --name $ResourceGroup `
    --location $Location `
    --tags Environment=Production Application=AzureKyoshi `
    --output none

# ------------------------------------------------------------------------------
Write-Host "`n=== 3. Creating Dedicated PROD Azure SQL Server & Database in '$SqlLocation' ===" -ForegroundColor Cyan
az sql server create `
    --name $SqlServerName `
    --resource-group $ResourceGroup `
    --location $SqlLocation `
    --enable-ad-only-auth `
    --external-admin-principal-type User `
    --external-admin-name $adminUpn `
    --external-admin-sid $adminOid `
    --output none

# Production firewall: allow Azure services only (GitHub Actions opens/closes a temporary runner IP rule during migrations)
az sql server firewall-rule create `
    --resource-group $ResourceGroup `
    --server $SqlServerName `
    --name "AllowAzureServices" `
    --start-ip-address 0.0.0.0 `
    --end-ip-address 0.0.0.0 `
    --output none

if ($UsePaidBasicSql) {
    az sql db create `
        --resource-group $ResourceGroup `
        --server $SqlServerName `
        --name $SqlDbName `
        --edition Basic `
        --service-objective Basic `
        --max-size 2GB `
        --output none
} else {
    az sql db create `
        --resource-group $ResourceGroup `
        --server $SqlServerName `
        --name $SqlDbName `
        --edition GeneralPurpose `
        --compute-model Serverless `
        --family Gen5 `
        --capacity 2 `
        --use-free-limit true `
        --free-limit-exhaustion-behavior AutoPause `
        --output none
}

$SqlConnectionString = "Server=tcp:$SqlServerName.database.windows.net,1433;Initial Catalog=$SqlDbName;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;Authentication=Active Directory Default;"

# ------------------------------------------------------------------------------
Write-Host "`n=== 4. Creating Dedicated PROD Azure AI Foundry & '$ModelName' Deployment ===" -ForegroundColor Cyan
$foundryAccount = az cognitiveservices account create `
    --name $FoundryName `
    --resource-group $ResourceGroup `
    --kind AIServices `
    --sku S0 `
    --location $Location `
    --custom-domain $FoundryName `
    --yes `
    --output json | ConvertFrom-Json

$foundryId       = $foundryAccount.id
$foundryEndpoint = "https://$FoundryName.openai.azure.com/"

if ([string]::IsNullOrWhiteSpace($ModelVersion)) {
    $availableModels = az cognitiveservices model list `
        --location $Location `
        --query "[?model.name=='$ModelName'].{Version:model.version, Format:model.format, Status:model.lifecycleStatus}" `
        --output json | ConvertFrom-Json

    if (-not $availableModels -or $availableModels.Count -eq 0) {
        throw "Model '$ModelName' not found in '$Location'. Run 'az cognitiveservices model list --location $Location -o table'."
    }

    $chosenModel = $availableModels | Where-Object { $_.Status -eq "GenerallyAvailable" } | Sort-Object Version -Descending | Select-Object -First 1
    if (-not $chosenModel) {
        $chosenModel = $availableModels | Sort-Object Version -Descending | Select-Object -First 1
    }
    $ModelVersion = $chosenModel.Version
    $modelFormat  = if ($chosenModel.Format) { $chosenModel.Format } else { "OpenAI" }
} else {
    $modelFormat = "OpenAI"
}

Write-Host "Deploying PROD model '$ModelName' (version: $ModelVersion, format: $modelFormat)..."
az cognitiveservices account deployment create `
    --resource-group $ResourceGroup `
    --name $FoundryName `
    --deployment-name $DeploymentName `
    --model-name $ModelName `
    --model-version $ModelVersion `
    --model-format $modelFormat `
    --sku-name "GlobalStandard" `
    --sku-capacity 20 `
    --output none

# ------------------------------------------------------------------------------
Write-Host "`n=== 5. Creating Dedicated PROD Azure Function App (.NET 10 Flex Consumption) ===" -ForegroundColor Cyan
$storage = az storage account create `
    --name $StorageName `
    --resource-group $ResourceGroup `
    --location $Location `
    --sku Standard_LRS `
    --allow-blob-public-access false `
    --output json | ConvertFrom-Json

$identity = az identity create `
    --name $IdentityName `
    --resource-group $ResourceGroup `
    --location $Location `
    --output json | ConvertFrom-Json

$identityId          = $identity.id
$identityPrincipalId = $identity.principalId
$identityClientId    = $identity.clientId

az role assignment create `
    --assignee-object-id $identityPrincipalId `
    --assignee-principal-type ServicePrincipal `
    --role "Storage Blob Data Owner" `
    --scope $storage.id `
    --output none

az role assignment create `
    --assignee-object-id $identityPrincipalId `
    --assignee-principal-type ServicePrincipal `
    --role "Cognitive Services OpenAI User" `
    --scope $foundryId `
    --output none

Write-Host "Waiting 15 seconds for Managed Identity RBAC propagation..."
Start-Sleep -Seconds 15

$funcApp = az functionapp create `
    --name $FuncAppName `
    --resource-group $ResourceGroup `
    --storage-account $StorageName `
    --flexconsumption-location $Location `
    --runtime "dotnet-isolated" `
    --runtime-version "10.0" `
    --deployment-storage-auth-type "UserAssignedIdentity" `
    --deployment-storage-auth-value $IdentityName `
    --output json | ConvertFrom-Json

az functionapp identity assign `
    --name $FuncAppName `
    --resource-group $ResourceGroup `
    --identities $identityId `
    --output none

$funcHost = if ($funcApp.properties.defaultHostName) { $funcApp.properties.defaultHostName } elseif ($funcApp.defaultHostName) { $funcApp.defaultHostName } else { "$FuncAppName.azurewebsites.net" }
$funcUrl  = "https://$funcHost"

# ------------------------------------------------------------------------------
Write-Host "`n=== 6. Creating Dedicated PROD Azure Static Web App ($StaticWebAppSku) ===" -ForegroundColor Cyan
$swa = az staticwebapp create `
    --name $StaticAppName `
    --resource-group $ResourceGroup `
    --location $Location `
    --sku $StaticWebAppSku `
    --output json | ConvertFrom-Json

$swaUrl = "https://$($swa.defaultHostname)"

# ------------------------------------------------------------------------------
Write-Host "`n=== 7. Registering Dedicated PROD Microsoft Entra ID Apps (Strict PROD Redirect URI Only) ===" -ForegroundColor Cyan
$scopeGuid = [guid]::NewGuid().ToString()
$apiApp = az ad app create `
    --display-name "AzureKyoshi-Api-$CleanSuffix" `
    --sign-in-audience "AzureADMyOrg" `
    --output json | ConvertFrom-Json

$apiClientId      = $apiApp.appId
$apiObjectId      = $apiApp.id
$apiIdentifierUri = "api://$apiClientId"

$apiPatchBody = @{
    identifierUris = @($apiIdentifierUri)
    api = @{
        requestedAccessTokenVersion = 2
        oauth2PermissionScopes = @(
            @{
                id = $scopeGuid
                adminConsentDescription = "Allow the app to access Azure Kyoshi Production API on behalf of the signed-in user."
                adminConsentDisplayName = "Access Azure Kyoshi Production API"
                isEnabled = $true
                type = "User"
                userConsentDescription = "Allow the app to access Azure Kyoshi Production API on your behalf."
                userConsentDisplayName = "Access Azure Kyoshi Production API"
                value = "access_as_user"
            }
        )
    }
} | ConvertTo-Json -Depth 6

$apiPatchFile = [System.IO.Path]::GetTempFileName()
Set-Content -Path $apiPatchFile -Value $apiPatchBody -Encoding UTF8
az rest --method PATCH `
    --uri "https://graph.microsoft.com/v1.0/applications/$apiObjectId" `
    --headers "Content-Type=application/json" `
    --body "@$apiPatchFile"
Remove-Item $apiPatchFile -Force

az ad sp create --id $apiClientId --output none

# Strict PROD SPA app (NO localhost redirect URIs)
$spaApp = az ad app create `
    --display-name "AzureKyoshi-Client-$CleanSuffix" `
    --sign-in-audience "AzureADMyOrg" `
    --output json | ConvertFrom-Json

$spaClientId = $spaApp.appId
$spaObjectId = $spaApp.id

$spaPatchBody = @{
    spa = @{
        redirectUris = @(
            "$swaUrl/authentication/login-callback"
        )
    }
    requiredResourceAccess = @(
        @{
            resourceAppId = $apiClientId
            resourceAccess = @(
                @{
                    id = $scopeGuid
                    type = "Scope"
                }
            )
        }
    )
} | ConvertTo-Json -Depth 6

$spaPatchFile = [System.IO.Path]::GetTempFileName()
Set-Content -Path $spaPatchFile -Value $spaPatchBody -Encoding UTF8
az rest --method PATCH `
    --uri "https://graph.microsoft.com/v1.0/applications/$spaObjectId" `
    --headers "Content-Type=application/json" `
    --body "@$spaPatchFile"
Remove-Item $spaPatchFile -Force

az ad sp create --id $spaClientId --output none

$preAuthBody = @{
    api = @{
        preAuthorizedApplications = @(
            @{
                appId = $spaClientId
                delegatedPermissionIds = @($scopeGuid)
            }
        )
    }
} | ConvertTo-Json -Depth 5

$preAuthFile = [System.IO.Path]::GetTempFileName()
Set-Content -Path $preAuthFile -Value $preAuthBody -Encoding UTF8
az rest --method PATCH `
    --uri "https://graph.microsoft.com/v1.0/applications/$apiObjectId" `
    --headers "Content-Type=application/json" `
    --body "@$preAuthFile"
Remove-Item $preAuthFile -Force

# ------------------------------------------------------------------------------
Write-Host "`n=== 8. Configuring PROD Function App Settings & Strict CORS (PROD SWA Only) ===" -ForegroundColor Cyan
az functionapp cors add `
    --name $FuncAppName `
    --resource-group $ResourceGroup `
    --allowed-origins $swaUrl `
    --output none

az functionapp config appsettings set `
    --name $FuncAppName `
    --resource-group $ResourceGroup `
    --settings `
        "AZURE_CLIENT_ID=$identityClientId" `
        "ConnectionStrings__DefaultConnection=$SqlConnectionString" `
        "Database__Provider=SqlServer" `
        "Authentication__UseDevMode=false" `
        "AzureAd__Instance=https://login.microsoftonline.com/" `
        "AzureAd__TenantId=$tenantId" `
        "AzureAd__ClientId=$apiClientId" `
        "AzureAd__Audience=$apiIdentifierUri" `
        "AzureFoundry__Endpoint=$foundryEndpoint" `
        "AzureFoundry__DeploymentName=$DeploymentName" `
        "AzureFoundry__AllowDemoFallback=false" `
        "MicrosoftLearnMcp__Endpoint=https://learn.microsoft.com/api/mcp" `
    --output none

# ------------------------------------------------------------------------------
Write-Host "`n==================================================================" -ForegroundColor Green
Write-Host "  AZURE KYOSHI PRODUCTION ENVIRONMENT PROVISIONED!" -ForegroundColor Green
Write-Host "==================================================================" -ForegroundColor Green
Write-Host "Resource Group          : $ResourceGroup"
Write-Host "Suffix Used             : $CleanSuffix"
Write-Host "PROD Static Web App URL : $swaUrl"
Write-Host "PROD Functions API URL  : $funcUrl"
Write-Host "PROD Managed Identity   : $IdentityName (ClientId: $identityClientId)"
Write-Host "PROD AI Foundry URL     : $foundryEndpoint (Deployment: $DeploymentName)"
Write-Host "PROD Azure SQL Server   : $SqlServerName.database.windows.net / DB: $SqlDbName"
Write-Host "PROD Entra API ClientId : $apiClientId"
Write-Host "PROD Entra SPA ClientId : $spaClientId"

Write-Host "`n--- ACTION REQUIRED: Grant PROD Managed Identity least-privilege SQL access (NO db_ddladmin!) ---" -ForegroundColor Yellow
Write-Host "Open Azure Portal -> SQL Database '$SqlDbName' (on '$SqlServerName') -> Query Editor and run:"
Write-Host @"
CREATE USER [$IdentityName] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [$IdentityName];
ALTER ROLE db_datawriter ADD MEMBER [$IdentityName];
"@ -ForegroundColor White

Write-Host "`n--- NEXT: Wire GitHub OIDC for PROD (with Required Reviewer approval in GitHub Environments) ---" -ForegroundColor Yellow
Write-Host ".\infra\setup-github-oidc.ps1 -GitHubOrg '<your-github-user>' -GitHubRepo '<your-repo>' -EnvName 'prod' -ResourceGroup '$ResourceGroup'"
