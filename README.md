# Azure Kyoshi — Azure Certification Study Studio

An AI-powered `.NET 10` Blazor WebAssembly + serverless Azure Functions study studio for Microsoft Azure certifications.

## Solution Architecture (Static Web App + Serverless Functions)

- **`AzureKyoshi.Client`** — `.NET 10` Blazor WebAssembly standalone SPA (includes `wwwroot/staticwebapp.config.json` and MSAL Microsoft Entra ID auth) for **Azure Static Web Apps (`Free`)**.
- **`AzureKyoshi.Api`** — `.NET 10` **Azure Functions (Isolated Worker)** app (Flex Consumption serverless plan — no App Service required!) hosting:
  - **Microsoft Learn MCP Client** (`https://learn.microsoft.com/api/mcp`) via the official `ModelContextProtocol` C# SDK (with Learn Catalog API fallback).
  - **Azure AI Foundry Service** via `Azure.AI.OpenAI` + `Microsoft.Extensions.AI` (`IChatClient`), with a local Demo AI fallback when no endpoint is configured yet.
  - **EF Core 10 + SQL Server** (`Azure SQL` / `LocalDB`, with automatic `InMemory` fallback in Development if LocalDB is unavailable).
  - **Microsoft Entra ID** JWT Bearer middleware (`EntraJwtAuthenticationMiddleware`) with a local Dev Mode toggle.
- **`AzureKyoshi.Shared`** — Shared DTO contracts between the Blazor WASM client and Functions API.
- **`infra/provision-azure.ps1`** — Azure CLI provisioning script creating the Resource Group, Azure SQL Server/DB, Azure AI Foundry + model deployment, Flex Consumption Function App + Managed Identity, Azure Static Web App, and Entra ID App Registrations.

## Running Locally in Visual Studio

1. Open `AzureKyoshi/AzureKyoshi.slnx` in Visual Studio.
2. Right-click the Solution → **Configure Startup Projects** → select **Multiple startup projects** and set both **`AzureKyoshi.Api`** and **`AzureKyoshi.Client`** to **Start**.
3. Press **F5**:
   - `AzureKyoshi.Api` (Azure Functions) starts on `http://localhost:7071`
   - `AzureKyoshi.Client` (Blazor WASM) opens on `https://localhost:7221` and automatically calls `http://localhost:7071/api/...` in Local Dev Mode!

## Provisioning Azure Resources

```powershell
az login
az account set --subscription "<your-subscription-id>"
.\infra\provision-azure.ps1 -Location "eastus2" -Suffix "yourinitials01"
```

## Connecting Your Azure Services Locally

### 1. Azure AI Foundry Model
Edit `AzureKyoshi.Api/local.settings.json`:

```json
"AzureFoundry:Endpoint": "https://<your-foundry-resource>.openai.azure.com/",
"AzureFoundry:ApiKey": "",
"AzureFoundry:DeploymentName": "gpt-6-luna"
```
*(Leaving `ApiKey` blank authenticates automatically via `DefaultAzureCredential` using your Visual Studio / `az login` Entra ID account.)*

### 2. Azure SQL Server
Update `ConnectionStrings:DefaultConnection` and set `"Database:Provider": "SqlServer"` in `AzureKyoshi.Api/local.settings.json`. Tables are created automatically via EF Core (`EnsureCreatedAsync`).

### 3. Microsoft Entra ID Authentication
1. Set `"Authentication:UseDevMode": "false"` in `AzureKyoshi.Api/local.settings.json` (and add `AzureAd:TenantId`, `AzureAd:ClientId`, `AzureAd:Audience`).
2. Set `"Authentication": { "UseDevMode": false }` in `AzureKyoshi.Client/wwwroot/appsettings.Development.json` and paste the Entra ID values printed by `provision-azure.ps1` into `AzureKyoshi.Client/wwwroot/appsettings.json`.
