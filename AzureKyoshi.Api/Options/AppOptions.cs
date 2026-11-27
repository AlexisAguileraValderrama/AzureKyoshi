namespace AzureKyoshi.Api.Options;

public sealed class AzureFoundryOptions
{
    public const string SectionName = "AzureFoundry";

    /// <summary>
    /// Azure AI Foundry / Azure OpenAI resource endpoint, e.g.
    /// https://&lt;your-resource&gt;.openai.azure.com/ or https://&lt;your-resource&gt;.services.ai.azure.com/
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Optional API Key. Leave empty to authenticate using Microsoft Entra ID (DefaultAzureCredential).
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Model deployment name created in Azure AI Foundry (e.g., gpt-4o, gpt-4o-mini, gpt-4.1).
    /// </summary>
    public string DeploymentName { get; set; } = "gpt-6-luna";

    /// <summary>
    /// When true and Endpoint is not configured, falls back to local deterministic study generation
    /// using the fetched Microsoft Learn content so Visual Studio UI testing works immediately.
    /// </summary>
    public bool AllowDemoFallback { get; set; } = true;
}

public sealed class MicrosoftLearnMcpOptions
{
    public const string SectionName = "MicrosoftLearnMcp";

    public string Endpoint { get; set; } = "https://learn.microsoft.com/api/mcp";
    public int MaxFetchedDocs { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 25;
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// SqlServer | InMemory | Auto (tries SqlServer with quick timeout, falls back to InMemory in Development).
    /// </summary>
    public string Provider { get; set; } = "SqlServer";
}
