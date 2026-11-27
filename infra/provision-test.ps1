<#
.SYNOPSIS
    Provisions the complete TEST (and local-dev shared) Azure environment for Azure Kyoshi (rg-azurekyoshi-test):
    - Registers required Azure Resource Providers automatically
    - Resource Group (rg-azurekyoshi-test)
    - Azure SQL Server (Entra ID-only auth) + Azure SQL Database (Free Offer Serverless)
    - Azure AI Foundry (AIServices, custom domain) + gpt-6-luna deployment (shared with local Visual Studio dev)
    - Storage Account + User-Assigned Managed Identity + .NET 10 Flex Consumption Function App
    - Azure Static Web App (Free SKU)
    - Microsoft Entra ID App Registrations (API + SPA with BOTH local Visual Studio & TEST SWA redirect URIs)

.EXAMPLE
    ./provision-test.ps1 -Location "eastus2" -SqlLocation "westus3"
#>

param(
    [string]$Location        = "eastus2",
    [string]$SqlLocation     = "westus3",          # Region that accepts Azure SQL Server on your subscription
    [string]$ResourceGroup   = "rg-azurekyoshi-test",
    [string]$Suffix          = "test$(Get-Random -Minimum 1000 -Maximum 9999)",
    [string]$SqlDbName       = "AzureKyoshiDb",
    [string]$ModelName       = "gpt-6-luna",
    [string]$ModelVersion    = "",                 # Auto-resolved to latest GA version in $Location when blank
    [string]$DeploymentName  = "gpt-6-luna",
    [string]$StaticWebAppSku = "Free"
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($SqlLocation)) { $SqlLocation = $Location }

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
Write-Host "Suffix Used  : $CleanSuffix"

# ------------------------------------------------------------------------------
Write-Host "`n=== 1b. Ensuring required Azure Resource Providers are registered ===" -ForegroundColor Cyan
$requiredProviders = @(
    "Microsoft.Sql",
    "Microsoft.CognitiveServices",
    "Microsoft.Storage",
    "Microsoft.ManagedIdentity",
    "Microsoft.Web",
    "Microsoft.Insights",
    "Microsoft.OperationalInsights"
)

foreach ($ns in $requiredProviders) {
    $state = az provider show --namespace $ns --query registrationState -o tsv
    if ($state -ne "Registered") {
        Write-Host "Registering resource provider '$ns' (currently: $state)..."
        az provider register --namespace $ns --wait --output none
        Write-Host "Registered '$ns'."
    }
}

# ------------------------------------------------------------------------------
Write-Host "`n=== 2. Creating TEST Resource Group '$ResourceGroup' ===" -ForegroundColor Cyan
az group create `
    --name $ResourceGroup `
    --location $Location `
    --tags Environment=Test Application=AzureKyoshi `
    --output none

# ------------------------------------------------------------------------------
Write-Host "`n=== 3. Creating Azure SQL Server (Entra ID Auth) & Free Serverless DB in '$SqlLocation' ===" -ForegroundColor Cyan
az sql server create `
    --name $SqlServerName `
    --resource-group $ResourceGroup `
    --location $SqlLocation `
    --enable-ad-only-auth `
    --external-admin-principal-type User `
    --external-admin-name $adminUpn `
    --external-admin-sid $adminOid `
    --output none

az sql server firewall-rule create `
    --resource-group $ResourceGroup `
    --server $SqlServerName `
    --name "AllowAzureServices" `
    --start-ip-address 0.0.0.0 `
    --end-ip-address 0.0.0.0 `
    --output none

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

$SqlConnectionString = "Server=tcp:$SqlServerName.database.windows.net,1433;Initial Catalog=$SqlDbName;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;Authentication=Active Directory Default;"

# ------------------------------------------------------------------------------
Write-Host "`n=== 4. Creating Azure AI Foundry (AIServices) & '$ModelName' Deployment ===" -ForegroundColor Cyan
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
        throw "Model '$ModelName' was not found in '$Location'. Run 'az cognitiveservices model list --location $Location -o table' to inspect available models."
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

Write-Host "Deploying model '$ModelName' (version: $ModelVersion, format: $modelFormat)..."
az cognitiveservices account deployment create `
    --resource-group $ResourceGroup `
    --name $FoundryName `
    --deployment-name $DeploymentName `
    --model-name $ModelName `
    --model-version $ModelVersion `
    --model-format $modelFormat `
    --sku-name "GlobalStandard" `
    --sku-capacity 10 `
    --output none

# Grant your developer Entra account OpenAI User for local Visual Studio dev testing
az role assignment create `
    --assignee-object-id $adminOid `
    --assignee-principal-type User `
    --role "Cognitive Services OpenAI User" `
    --scope $foundryId `
    --output none

# ------------------------------------------------------------------------------
Write-Host "`n=== 5. Creating TEST Azure Function App (.NET 10 Flex Consumption) ===" -ForegroundColor Cyan
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
Write-Host "`n=== 6. Creating TEST Azure Static Web App ($StaticWebAppSku) ===" -ForegroundColor Cyan
$swa = az staticwebapp create `
    --name $StaticAppName `
    --resource-group $ResourceGroup `
    --location $Location `
    --sku $StaticWebAppSku `
    --output json | ConvertFrom-Json

$swaUrl = "https://$($swa.defaultHostname)"

# ------------------------------------------------------------------------------
Write-Host "`n=== 7. Registering Microsoft Entra ID Apps (Shared by Local Dev & TEST) ===" -ForegroundColor Cyan
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
                adminConsentDescription = "Allow the app to access Azure Kyoshi API on behalf of the signed-in user."
                adminConsentDisplayName = "Access Azure Kyoshi API"
                isEnabled = $true
                type = "User"
                userConsentDescription = "Allow the app to access Azure Kyoshi API on your behalf."
                userConsentDisplayName = "Access Azure Kyoshi API"
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

$spaApp = az ad app create `
    --display-name "AzureKyoshi-Client-$CleanSuffix" `
    --sign-in-audience "AzureADMyOrg" `
    --output json | ConvertFrom-Json

$spaClientId = $spaApp.appId
$spaObjectId = $spaApp.id

$spaPatchBody = @{
    spa = @{
        redirectUris = @(
            "https://localhost:7221/authentication/login-callback",
            "http://localhost:5221/authentication/login-callback",
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
Write-Host "`n=== 8. Configuring TEST Function App Settings & CORS ===" -ForegroundColor Cyan
az functionapp cors add `
    --name $FuncAppName `
    --resource-group $ResourceGroup `
    --allowed-origins $swaUrl "https://localhost:7221" "http://localhost:5221" `
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
Write-Host "  AZURE KYOSHI TEST ENVIRONMENT PROVISIONED!" -ForegroundColor Green
Write-Host "==================================================================" -ForegroundColor Green
Write-Host "Resource Group          : $ResourceGroup"
Write-Host "Suffix Used             : $CleanSuffix"
Write-Host "TEST Static Web App URL : $swaUrl"
Write-Host "TEST Functions API URL  : $funcUrl"
Write-Host "TEST Managed Identity   : $IdentityName (ClientId: $identityClientId)"
Write-Host "AI Foundry URL          : $foundryEndpoint (Deployment: $DeploymentName)"
Write-Host "Azure SQL Server        : $SqlServerName.database.windows.net / DB: $SqlDbName"
Write-Host "Entra Tenant ID         : $tenantId"
Write-Host "Entra API Client ID     : $apiClientId"
Write-Host "Entra SPA Client ID     : $spaClientId"

Write-Host "`n--- ACTION 1: Grant TEST Managed Identity SQL access ---" -ForegroundColor Yellow
Write-Host "Open Azure Portal -> SQL Database '$SqlDbName' (on '$SqlServerName') -> Query Editor and run:"
Write-Host @"
CREATE USER [$IdentityName] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [$IdentityName];
ALTER ROLE db_datawriter ADD MEMBER [$IdentityName];
"@ -ForegroundColor White

Write-Host "`n--- ACTION 2: Local Visual Studio AzureKyoshi.Api/local.settings.json ---" -ForegroundColor Yellow
Write-Host @"
  "ConnectionStrings:DefaultConnection": "$SqlConnectionString",
  "Database:Provider": "SqlServer",
  "Authentication:UseDevMode": "false",
  "AzureAd:TenantId": "$tenantId",
  "AzureAd:ClientId": "$apiClientId",
  "AzureAd:Audience": "$apiIdentifierUri",
  "AzureFoundry:Endpoint": "$foundryEndpoint",
  "AzureFoundry:DeploymentName": "$DeploymentName"
"@ -ForegroundColor White
Write-Host "(Remember to add your PC's IP in Azure Portal -> SQL Server '$SqlServerName' -> Networking -> + Add your client IPv4 address)"

Write-Host "`n--- ACTION 3: AzureKyoshi.Client/wwwroot/appsettings.json ---" -ForegroundColor Yellow
Write-Host @"
{
  "ApiBaseUrl": "$funcUrl/",
  "Authentication": {
    "UseDevMode": false
  },
  "AzureAd": {
    "Authority": "https://login.microsoftonline.com/$tenantId",
    "ClientId": "$spaClientId",
    "ValidateAuthority": true
  },
  "ApiScopes": [
    "$apiIdentifierUri/access_as_user"
  ]
}
"@ -ForegroundColor White

Write-Host "`n--- ACTION 4: Wire GitHub OIDC for TEST ---" -ForegroundColor Yellow
Write-Host "./setup-github-oidc.ps1 -GitHubOrg '<your-github-user>' -GitHubRepo '<your-repo>' -EnvName 'test' -ResourceGroup '$ResourceGroup'"
