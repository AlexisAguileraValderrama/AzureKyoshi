<#
.SYNOPSIS
    Swaps the Azure AI Foundry model deployment on existing Azure Kyoshi resources
    (e.g., replaces gpt-5-mini with gpt-6-luna) and updates the Azure Function App setting.

.EXAMPLE
    .\infra\switch-foundry-model.ps1
    .\infra\switch-foundry-model.ps1 -ResourceGroup "rg-azurekyoshi-dev" -NewModelName "gpt-6-luna"
    .\infra\switch-foundry-model.ps1 -KeepOldDeployment
#>

param(
    [string]$ResourceGroup     = "rg-azurekyoshi-dev",
    [string]$FoundryName       = "",                # Auto-detected from $ResourceGroup if left blank
    [string]$FuncAppName       = "",                # Auto-detected from $ResourceGroup if left blank
    [string]$OldDeploymentName = "gpt-5-mini",
    [string]$NewModelName      = "gpt-6-luna",
    [string]$NewModelVersion   = "",                # Auto-detected latest version in your region if left blank
    [string]$NewDeploymentName = "gpt-6-luna",
    [string]$SkuName           = "GlobalStandard",
    [int]$SkuCapacity          = 10,
    [switch]$KeepOldDeployment
)

$ErrorActionPreference = "Stop"

Write-Host "=== 1. Locating existing Azure AI Foundry & Function App resources ===" -ForegroundColor Cyan

if ([string]::IsNullOrWhiteSpace($FoundryName)) {
    $foundryAccount = az cognitiveservices account list `
        --resource-group $ResourceGroup `
        --query "[?kind=='AIServices'] | [0]" `
        --output json | ConvertFrom-Json

    if (-not $foundryAccount) {
        throw "No AIServices (Azure AI Foundry) account found in resource group '$ResourceGroup'. Pass -FoundryName explicitly."
    }
    $FoundryName = $foundryAccount.name
    $foundryLocation = $foundryAccount.location
} else {
    $foundryLocation = az cognitiveservices account show `
        --resource-group $ResourceGroup `
        --name $FoundryName `
        --query location -o tsv
}

if ([string]::IsNullOrWhiteSpace($FuncAppName)) {
    $FuncAppName = az functionapp list `
        --resource-group $ResourceGroup `
        --query "[0].name" -o tsv
}

Write-Host "Resource Group   : $ResourceGroup"
Write-Host "Foundry Account  : $FoundryName ($foundryLocation)"
Write-Host "Function App     : $(if ($FuncAppName) { $FuncAppName } else { '(none found yet)' })"

# ------------------------------------------------------------------------------
Write-Host "`n=== 2. Resolving '$NewModelName' version in '$foundryLocation' ===" -ForegroundColor Cyan

if ([string]::IsNullOrWhiteSpace($NewModelVersion)) {
    $models = az cognitiveservices model list `
        --location $foundryLocation `
        --query "[?model.name=='$NewModelName'].{Version:model.version, Format:model.format, Status:model.lifecycleStatus}" `
        --output json | ConvertFrom-Json

    if (-not $models -or $models.Count -eq 0) {
        Write-Host "Available OpenAI models in '$foundryLocation':" -ForegroundColor Yellow
        az cognitiveservices model list `
            --location $foundryLocation `
            --query "[?model.format=='OpenAI'].{Name:model.name, Version:model.version, Status:model.lifecycleStatus}" `
            --output table
        throw "Model '$NewModelName' was not found in region '$foundryLocation'. Check the table above and re-run with -NewModelName / -NewModelVersion."
    }

    # Prefer GenerallyAvailable, otherwise newest version string
    $chosen = $models | Where-Object { $_.Status -eq "GenerallyAvailable" } | Sort-Object Version -Descending | Select-Object -First 1
    if (-not $chosen) {
        $chosen = $models | Sort-Object Version -Descending | Select-Object -First 1
    }

    $NewModelVersion = $chosen.Version
    $modelFormat = if ($chosen.Format) { $chosen.Format } else { "OpenAI" }
} else {
    $modelFormat = "OpenAI"
}

Write-Host "Deploying: $NewModelName (Version: $NewModelVersion, Format: $modelFormat, SKU: $SkuName, Capacity: $SkuCapacity)"

# ------------------------------------------------------------------------------
Write-Host "`n=== 3. Creating new deployment '$NewDeploymentName' ===" -ForegroundColor Cyan
az cognitiveservices account deployment create `
    --resource-group $ResourceGroup `
    --name $FoundryName `
    --deployment-name $NewDeploymentName `
    --model-name $NewModelName `
    --model-version $NewModelVersion `
    --model-format $modelFormat `
    --sku-name $SkuName `
    --sku-capacity $SkuCapacity `
    --output none

Write-Host "Deployment '$NewDeploymentName' created successfully." -ForegroundColor Green

# ------------------------------------------------------------------------------
if (-not [string]::IsNullOrWhiteSpace($FuncAppName)) {
    Write-Host "`n=== 4. Updating Function App setting 'AzureFoundry__DeploymentName' ===" -ForegroundColor Cyan
    az functionapp config appsettings set `
        --resource-group $ResourceGroup `
        --name $FuncAppName `
        --settings "AzureFoundry__DeploymentName=$NewDeploymentName" `
        --output none
    Write-Host "Updated '$FuncAppName' to use '$NewDeploymentName'." -ForegroundColor Green
}

# ------------------------------------------------------------------------------
if (-not $KeepOldDeployment -and $OldDeploymentName -ne $NewDeploymentName) {
    Write-Host "`n=== 5. Removing old deployment '$OldDeploymentName' ===" -ForegroundColor Cyan
    $oldExists = az cognitiveservices account deployment list `
        --resource-group $ResourceGroup `
        --name $FoundryName `
        --query "[?name=='$OldDeploymentName'].name" -o tsv

    if ($oldExists) {
        az cognitiveservices account deployment delete `
            --resource-group $ResourceGroup `
            --name $FoundryName `
            --deployment-name $OldDeploymentName
        Write-Host "Deleted old deployment '$OldDeploymentName'." -ForegroundColor Green
    } else {
        Write-Host "Old deployment '$OldDeploymentName' not found (already removed)."
    }
}

# ------------------------------------------------------------------------------
Write-Host "`n=== Active deployments on '$FoundryName' ===" -ForegroundColor Cyan
az cognitiveservices account deployment list `
    --resource-group $ResourceGroup `
    --name $FoundryName `
    --query "[].{Deployment:name, Model:properties.model.name, Version:properties.model.version, Sku:sku.name, Capacity:sku.capacity}" `
    --output table

Write-Host "`nReminder for local Visual Studio testing:" -ForegroundColor Yellow
Write-Host "Update 'AzureFoundry:DeploymentName' to '$NewDeploymentName' in AzureKyoshi.Api/local.settings.json"
