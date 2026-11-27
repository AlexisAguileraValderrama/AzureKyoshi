<#
.SYNOPSIS
    Inverse teardown script: removes all Azure resources and Microsoft Entra ID App Registrations
    associated with a given Azure Kyoshi suffix, prompting to press Enter before each resource deletion.

    Also handles:
    - Removing the Static Web App redirect URI from any shared Entra SPA app (if applicable)
    - Purging soft-deleted Azure AI Foundry (AIServices) so the subdomain can be reused immediately
    - Optionally deleting the Resource Group (-DeleteResourceGroup)

.EXAMPLE
    # Preview what would be deleted (safe dry run):
    .\infra\destroy-azure.ps1 -Suffix "2115" -ResourceGroup "rg-azurekyoshi-dev" -DryRun

    # Interactive per-resource teardown (press Enter to delete each, type 's' to skip):
    .\infra\destroy-azure.ps1 -Suffix "2115" -ResourceGroup "rg-azurekyoshi-dev" -DeleteResourceGroup
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$Suffix,

    [Parameter(Mandatory = $true)]
    [string]$ResourceGroup,

    [switch]$DeleteResourceGroup,
    [switch]$SkipFoundryPurge,
    [switch]$DryRun,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

function Confirm-ResourceDelete {
    param([string]$Description)
    if ($Force) { return $true }
    $answer = Read-Host "  -> Delete $Description? Press [Enter] to confirm, or type 's' to skip"
    if ($answer -match '^(s|skip|n|no)$') {
        Write-Host "     Skipped $Description." -ForegroundColor DarkGray
        return $false
    }
    return $true
}

$CleanSuffix   = ($Suffix -replace '[^a-zA-Z0-9]', '').ToLower()
$SqlServerName = "sql-azurekyoshi-$CleanSuffix"
$FoundryName   = "aif-azurekyoshi-$CleanSuffix"
$StorageName   = "stakfunc$CleanSuffix"
if ($StorageName.Length -gt 24) { $StorageName = $StorageName.Substring(0, 24) }
$IdentityName  = "id-azurekyoshi-func-$CleanSuffix"
$FuncAppName   = "func-azurekyoshi-$CleanSuffix"
$StaticAppName = "swa-azurekyoshi-$CleanSuffix"
$ApiAppName    = "AzureKyoshi-Api-$CleanSuffix"
$SpaAppName    = "AzureKyoshi-Client-$CleanSuffix"

Write-Host "==================================================================" -ForegroundColor Yellow
Write-Host "  AZURE KYOSHI TEARDOWN PLAN (Suffix: '$CleanSuffix')" -ForegroundColor Yellow
Write-Host "==================================================================" -ForegroundColor Yellow
Write-Host "Resource Group     : $ResourceGroup (Delete RG: $DeleteResourceGroup)"
Write-Host "Static Web App     : $StaticAppName"
Write-Host "Function App       : $FuncAppName"
Write-Host "Managed Identity   : $IdentityName"
Write-Host "Storage Account    : $StorageName"
Write-Host "SQL Server (+ DBs) : $SqlServerName"
Write-Host "AI Foundry (+Purge): $FoundryName (Purge: $(-not $SkipFoundryPurge))"
Write-Host "Entra ID Apps      : $ApiAppName, $SpaAppName"

if ($DryRun) {
    Write-Host "`n[DRY RUN] No resources were deleted." -ForegroundColor Cyan
    exit 0
}

if (-not $Force) {
    $confirm = Read-Host "`nType '$CleanSuffix' to start teardown (anything else cancels)"
    if ($confirm -ne $CleanSuffix) {
        Write-Host "Cancelled. Nothing was deleted." -ForegroundColor Cyan
        exit 0
    }
}

# ------------------------------------------------------------------------------
Write-Host "`n=== 1. Checking Static Web App URL (to clean up shared SPA redirect URI if any) ===" -ForegroundColor Cyan
$swaHost = az staticwebapp show --name $StaticAppName --resource-group $ResourceGroup --query defaultHostname -o tsv 2>$null
if ($swaHost) {
    $swaCallback = "https://$swaHost/authentication/login-callback"
    $allSpaApps = az ad app list --filter "startswith(displayName, 'AzureKyoshi-Client-')" --output json | ConvertFrom-Json
    foreach ($otherSpa in $allSpaApps) {
        if ($otherSpa.displayName -ne $SpaAppName -and $otherSpa.spa -and $otherSpa.spa.redirectUris -contains $swaCallback) {
            if (Confirm-ResourceDelete "redirect URI '$swaCallback' from shared SPA '$($otherSpa.displayName)'") {
                $remaining = @($otherSpa.spa.redirectUris | Where-Object { $_ -ne $swaCallback })
                $patchBody = @{ spa = @{ redirectUris = $remaining } } | ConvertTo-Json -Depth 4
                $patchFile = [System.IO.Path]::GetTempFileName()
                Set-Content -Path $patchFile -Value $patchBody -Encoding UTF8
                az rest --method PATCH `
                    --uri "https://graph.microsoft.com/v1.0/applications/$($otherSpa.id)" `
                    --headers "Content-Type=application/json" `
                    --body "@$patchFile"
                Remove-Item $patchFile -Force
                Write-Host "     Removed '$swaCallback' from shared SPA app '$($otherSpa.displayName)'." -ForegroundColor Green
            }
        }
    }
}

# ------------------------------------------------------------------------------
Write-Host "`n=== 2. Deleting Entra ID App Registrations for suffix '$CleanSuffix' ===" -ForegroundColor Cyan
foreach ($appDisplayName in @($ApiAppName, $SpaAppName)) {
    $matchingApps = az ad app list --display-name $appDisplayName --query "[].{id:id, appId:appId}" --output json | ConvertFrom-Json
    if ($matchingApps -and $matchingApps.Count -gt 0) {
        foreach ($item in $matchingApps) {
            if (Confirm-ResourceDelete "Entra ID App '$appDisplayName' ($($item.appId))") {
                az ad app delete --id $item.id
                Write-Host "     Deleted Entra ID App '$appDisplayName'." -ForegroundColor Green
            }
        }
    } else {
        Write-Host "Entra ID App '$appDisplayName' not found (skipping)."
    }
}

# ------------------------------------------------------------------------------
Write-Host "`n=== 3. Checking Azure AI Foundry location (for soft-delete purge) ===" -ForegroundColor Cyan
$foundryLocation = az cognitiveservices account show `
    --name $FoundryName `
    --resource-group $ResourceGroup `
    --query location -o tsv 2>$null
$foundryDeleted = $false

# ------------------------------------------------------------------------------
if ($DeleteResourceGroup) {
    Write-Host "`n=== 4. Deleting entire Resource Group '$ResourceGroup' ===" -ForegroundColor Cyan
    $rgExists = az group exists --name $ResourceGroup
    if ($rgExists -eq "true") {
        if (Confirm-ResourceDelete "entire Resource Group '$ResourceGroup' and ALL resources inside it") {
            az group delete --name $ResourceGroup --yes
            $foundryDeleted = [bool]$foundryLocation
            Write-Host "     Deleted Resource Group '$ResourceGroup'." -ForegroundColor Green
        }
    } else {
        Write-Host "Resource Group '$ResourceGroup' does not exist."
    }
} else {
    Write-Host "`n=== 4. Deleting suffix-matched Azure resources inside '$ResourceGroup' ===" -ForegroundColor Cyan

    if ($swaHost) {
        if (Confirm-ResourceDelete "Static Web App '$StaticAppName'") {
            az staticwebapp delete --name $StaticAppName --resource-group $ResourceGroup --yes
            Write-Host "     Deleted Static Web App '$StaticAppName'." -ForegroundColor Green
        }
    }

    $funcExists = az functionapp show --name $FuncAppName --resource-group $ResourceGroup --query name -o tsv 2>$null
    if ($funcExists) {
        if (Confirm-ResourceDelete "Function App '$FuncAppName'") {
            az functionapp delete --name $FuncAppName --resource-group $ResourceGroup
            Write-Host "     Deleted Function App '$FuncAppName'." -ForegroundColor Green
        }
    }

    $sqlExists = az sql server show --name $SqlServerName --resource-group $ResourceGroup --query name -o tsv 2>$null
    if ($sqlExists) {
        if (Confirm-ResourceDelete "SQL Server '$SqlServerName' (and all its databases)") {
            az sql server delete --name $SqlServerName --resource-group $ResourceGroup --yes
            Write-Host "     Deleted SQL Server '$SqlServerName'." -ForegroundColor Green
        }
    }

    if ($foundryLocation) {
        if (Confirm-ResourceDelete "Azure AI Foundry '$FoundryName'") {
            az cognitiveservices account delete --name $FoundryName --resource-group $ResourceGroup
            $foundryDeleted = $true
            Write-Host "     Deleted Azure AI Foundry '$FoundryName'." -ForegroundColor Green
        }
    }

    $storageExists = az storage account show --name $StorageName --resource-group $ResourceGroup --query name -o tsv 2>$null
    if ($storageExists) {
        if (Confirm-ResourceDelete "Storage Account '$StorageName'") {
            az storage account delete --name $StorageName --resource-group $ResourceGroup --yes
            Write-Host "     Deleted Storage Account '$StorageName'." -ForegroundColor Green
        }
    }

    $idExists = az identity show --name $IdentityName --resource-group $ResourceGroup --query name -o tsv 2>$null
    if ($idExists) {
        if (Confirm-ResourceDelete "Managed Identity '$IdentityName'") {
            az identity delete --name $IdentityName --resource-group $ResourceGroup
            Write-Host "     Deleted Managed Identity '$IdentityName'." -ForegroundColor Green
        }
    }
}

# ------------------------------------------------------------------------------
if (-not $SkipFoundryPurge -and $foundryDeleted -and $foundryLocation) {
    Write-Host "`n=== 5. Purging soft-deleted AI Foundry '$FoundryName' in '$foundryLocation' ===" -ForegroundColor Cyan
    if (Confirm-ResourceDelete "soft-deleted AI Foundry '$FoundryName' (permanent purge)") {
        try {
            az cognitiveservices account purge `
                --name $FoundryName `
                --resource-group $ResourceGroup `
                --location $foundryLocation
            Write-Host "     Purged soft-deleted AI Foundry '$FoundryName' (subdomain immediately reusable)." -ForegroundColor Green
        } catch {
            Write-Warning "Could not purge '$FoundryName' (it may already be purged). Check with: az cognitiveservices account list-deleted -o table"
        }
    }
}

Write-Host "`n==================================================================" -ForegroundColor Green
Write-Host "  TEARDOWN COMPLETE FOR SUFFIX '$CleanSuffix'!" -ForegroundColor Green
Write-Host "==================================================================" -ForegroundColor Green
