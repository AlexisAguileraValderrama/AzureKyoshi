using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzureKyoshi.Api.Options;
using AzureKyoshi.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AzureKyoshi.Api.Services;

public sealed record MicrosoftLearnModuleContent(
    string ResolvedTitle,
    string ContentText,
    List<StudyModuleSourceDto> Sources,
    bool RetrievedViaMcp);

public interface IMicrosoftLearnService
{
    Task<MicrosoftLearnModuleContent> FetchModuleContentAsync(
        string moduleName,
        string? certificationCode,
        CancellationToken cancellationToken = default);
}

public sealed partial class MicrosoftLearnMcpService(
    IHttpClientFactory httpClientFactory,
    IOptions<MicrosoftLearnMcpOptions> options,
    ILogger<MicrosoftLearnMcpService> logger)
    : IMicrosoftLearnService
{
    private const int MaxContentCharacters = 28_000;

    public async Task<MicrosoftLearnModuleContent> FetchModuleContentAsync(
        string moduleName,
        string? certificationCode,
        CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        var trimmedModule = moduleName.Trim();
        var searchQuery = string.IsNullOrWhiteSpace(certificationCode)
            ? $"Azure {trimmedModule}"
            : $"Azure {certificationCode.Trim()} {trimmedModule}";

        // 1. Primary path: Official Microsoft Learn MCP Server (Streamable HTTP)
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(opts.TimeoutSeconds));

            var mcpResult = await FetchViaMcpAsync(trimmedModule, searchQuery, opts, timeoutCts.Token);
            if (mcpResult is not null && !string.IsNullOrWhiteSpace(mcpResult.ContentText))
            {
                return mcpResult;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Microsoft Learn MCP request failed for '{Module}'. Falling back to Learn Catalog API.", trimmedModule);
        }

        // 2. Secondary fallback: Microsoft Learn public Catalog API
        try
        {
            var catalogResult = await FetchViaLearnCatalogAsync(trimmedModule, certificationCode, cancellationToken);
            if (catalogResult is not null)
            {
                return catalogResult;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Microsoft Learn Catalog fallback failed for '{Module}'.", trimmedModule);
        }

        // 3. Offline / sandboxed graceful fallback
        var fallbackUrl = $"https://learn.microsoft.com/en-us/search/?terms={Uri.EscapeDataString(searchQuery)}";
        var fallbackText = $"""
            Module Topic: {trimmedModule}
            Certification Focus: {(string.IsNullOrWhiteSpace(certificationCode) ? "Microsoft Azure" : certificationCode)}

            Note: Live Microsoft Learn MCP content could not be retrieved right now (check outbound internet access to {opts.Endpoint}).
            Study guide generated from requested Azure module topic. Review official Microsoft Learn documentation at {fallbackUrl}.
            """;

        return new MicrosoftLearnModuleContent(
            ResolvedTitle: trimmedModule,
            ContentText: fallbackText,
            Sources: [new StudyModuleSourceDto($"Microsoft Learn Search: {trimmedModule}", fallbackUrl)],
            RetrievedViaMcp: false);
    }

    private async Task<MicrosoftLearnModuleContent?> FetchViaMcpAsync(
        string moduleName,
        string searchQuery,
        MicrosoftLearnMcpOptions opts,
        CancellationToken ct)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(opts.Endpoint),
            Name = "MicrosoftLearnMcp"
        });

        await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);

        var searchTool = tools.FirstOrDefault(t =>
            t.Name.Equals("microsoft_docs_search", StringComparison.OrdinalIgnoreCase))
            ?? tools.FirstOrDefault(t =>
                t.Name.Contains("search", StringComparison.OrdinalIgnoreCase) &&
                t.Name.Contains("docs", StringComparison.OrdinalIgnoreCase));

        var fetchTool = tools.FirstOrDefault(t =>
            t.Name.Equals("microsoft_docs_fetch", StringComparison.OrdinalIgnoreCase))
            ?? tools.FirstOrDefault(t =>
                t.Name.Contains("fetch", StringComparison.OrdinalIgnoreCase));

        var sources = new List<StudyModuleSourceDto>();
        var contentBuilder = new StringBuilder();
        var resolvedTitle = moduleName;

        // If user directly pasted a Microsoft Learn HTTPS URL, fetch it first
        if (TryValidateLearnUrl(moduleName, out var directUri) &&
            fetchTool is not null)
        {
            var fetched = await CallMcpToolAsync(client, fetchTool.Name, ResolveSingleParamArg(fetchTool, directUri.ToString(), "url"), ct);
            if (!string.IsNullOrWhiteSpace(fetched))
            {
                sources.Add(new StudyModuleSourceDto(ExtractTitleFromMarkdown(fetched) ?? moduleName, directUri.ToString()));
                resolvedTitle = ExtractTitleFromMarkdown(fetched) ?? moduleName;
                contentBuilder.AppendLine(fetched);
                return new MicrosoftLearnModuleContent(
                    resolvedTitle,
                    Truncate(contentBuilder.ToString(), MaxContentCharacters),
                    sources,
                    RetrievedViaMcp: true);
            }
        }

        if (searchTool is null)
        {
            logger.LogWarning("Microsoft Learn MCP server did not advertise a docs search tool.");
            return null;
        }

        var searchArgs = ResolveSearchArgs(searchTool, searchQuery);
        var searchRaw = await CallMcpToolAsync(client, searchTool.Name, searchArgs, ct);
        if (string.IsNullOrWhiteSpace(searchRaw))
        {
            return null;
        }

        var discoveredLinks = ExtractLearnSources(searchRaw);
        if (discoveredLinks.Count > 0)
        {
            sources.AddRange(discoveredLinks.Take(6));
            if (!string.IsNullOrWhiteSpace(discoveredLinks[0].Title) &&
                discoveredLinks[0].Title.Length >= 5 &&
                !discoveredLinks[0].Title.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                resolvedTitle = discoveredLinks[0].Title;
            }
        }

        contentBuilder.AppendLine("## Microsoft Learn Search Excerpts");
        contentBuilder.AppendLine(searchRaw);
        contentBuilder.AppendLine();

        // Fetch full page content for top official docs via microsoft_docs_fetch
        if (fetchTool is not null && discoveredLinks.Count > 0)
        {
            foreach (var source in discoveredLinks.Take(Math.Clamp(opts.MaxFetchedDocs, 1, 4)))
            {
                if (contentBuilder.Length >= MaxContentCharacters)
                {
                    break;
                }

                try
                {
                    var pageText = await CallMcpToolAsync(
                        client,
                        fetchTool.Name,
                        ResolveSingleParamArg(fetchTool, source.Url, "url"),
                        ct);

                    if (!string.IsNullOrWhiteSpace(pageText))
                    {
                        contentBuilder.AppendLine($"## Official Documentation: {source.Title}");
                        contentBuilder.AppendLine($"Source: {source.Url}");
                        contentBuilder.AppendLine(pageText);
                        contentBuilder.AppendLine();
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not fetch full doc page {Url} via MCP.", source.Url);
                }
            }
        }

        if (sources.Count == 0)
        {
            sources.Add(new StudyModuleSourceDto(
                $"Microsoft Learn: {moduleName}",
                $"https://learn.microsoft.com/en-us/search/?terms={Uri.EscapeDataString(searchQuery)}"));
        }

        return new MicrosoftLearnModuleContent(
            ResolvedTitle: resolvedTitle,
            ContentText: Truncate(contentBuilder.ToString(), MaxContentCharacters),
            Sources: sources,
            RetrievedViaMcp: true);
    }

    private static Dictionary<string, object?> ResolveSearchArgs(McpClientTool tool, string query)
    {
        var paramName = FindFirstSchemaProperty(tool.JsonSchema, "query", "question", "search", "terms") ?? "query";
        return new Dictionary<string, object?> { [paramName] = query };
    }

    private static Dictionary<string, object?> ResolveSingleParamArg(McpClientTool tool, string value, string preferred)
    {
        var paramName = FindFirstSchemaProperty(tool.JsonSchema, preferred, "uri", "link") ?? preferred;
        return new Dictionary<string, object?> { [paramName] = value };
    }

    private static string? FindFirstSchemaProperty(JsonElement schema, params string[] preferredNames)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var props) ||
            props.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var available = props.EnumerateObject().Select(p => p.Name).ToList();
        foreach (var preferred in preferredNames)
        {
            var match = available.FirstOrDefault(a => a.Equals(preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return available.FirstOrDefault();
    }

    private static async Task<string> CallMcpToolAsync(
        McpClient client,
        string toolName,
        Dictionary<string, object?> args,
        CancellationToken ct)
    {
        var result = await client.CallToolAsync(toolName, args, cancellationToken: ct);
        var parts = result.Content
            .OfType<TextContentBlock>()
            .Select(b => b.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t));

        return string.Join("\n\n", parts);
    }

    private async Task<MicrosoftLearnModuleContent?> FetchViaLearnCatalogAsync(
        string moduleName,
        string? certificationCode,
        CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient("MicrosoftLearnCatalog");
        using var response = await http.GetAsync(
            "https://learn.microsoft.com/api/catalog/?locale=en-us&type=modules",
            ct);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("modules", out var modules) || modules.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var keywords = Tokenize(moduleName + " " + certificationCode);
        var bestMatches = new List<(int Score, string Title, string Summary, string Url, List<string> Products, List<string> Levels)>();

        foreach (var item in modules.EnumerateArray())
        {
            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var summary = item.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
            var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            var combined = $"{title} {summary}".ToLowerInvariant();

            var score = keywords.Count(k => combined.Contains(k, StringComparison.Ordinal));
            if (title.Contains(moduleName, StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }

            if (score > 0 && !string.IsNullOrWhiteSpace(url))
            {
                var products = item.TryGetProperty("products", out var p) && p.ValueKind == JsonValueKind.Array
                    ? p.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                    : [];
                var levels = item.TryGetProperty("levels", out var l) && l.ValueKind == JsonValueKind.Array
                    ? l.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                    : [];

                bestMatches.Add((score, title, summary, url, products, levels));
            }
        }

        var top = bestMatches.OrderByDescending(m => m.Score).Take(4).ToList();
        if (top.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        var sources = new List<StudyModuleSourceDto>();
        foreach (var match in top)
        {
            sources.Add(new StudyModuleSourceDto(match.Title, match.Url));
            sb.AppendLine($"### {match.Title}");
            sb.AppendLine($"URL: {match.Url}");
            if (match.Levels.Count > 0) sb.AppendLine($"Level: {string.Join(", ", match.Levels)}");
            if (match.Products.Count > 0) sb.AppendLine($"Products: {string.Join(", ", match.Products)}");
            sb.AppendLine($"Summary: {match.Summary}");
            sb.AppendLine();
        }

        return new MicrosoftLearnModuleContent(
            ResolvedTitle: top[0].Title,
            ContentText: Truncate(sb.ToString(), MaxContentCharacters),
            Sources: sources,
            RetrievedViaMcp: false);
    }

    private static List<StudyModuleSourceDto> ExtractLearnSources(string raw)
    {
        var results = new List<StudyModuleSourceDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Try structured JSON parsing first (Microsoft Learn MCP often returns JSON arrays/objects)
        try
        {
            using var doc = JsonDocument.Parse(raw);
            CollectJsonSources(doc.RootElement, results, seen);
        }
        catch (JsonException)
        {
            // Not pure JSON; parse markdown links and raw URLs
        }

        foreach (Match match in MarkdownLinkRegex().Matches(raw))
        {
            var title = match.Groups[1].Value.Trim();
            var url = match.Groups[2].Value.Trim();
            if (TryValidateLearnUrl(url, out _) && seen.Add(url))
            {
                results.Add(new StudyModuleSourceDto(string.IsNullOrWhiteSpace(title) ? url : title, url));
            }
        }

        foreach (Match match in LearnUrlRegex().Matches(raw))
        {
            var url = match.Value.TrimEnd('.', ',', ')', ']', '"', '\'');
            if (TryValidateLearnUrl(url, out _) && seen.Add(url))
            {
                results.Add(new StudyModuleSourceDto(HumanizeUrlTitle(url), url));
            }
        }

        return results;
    }

    private static bool TryValidateLearnUrl(string? candidate, out Uri validUri)
    {
        if (Uri.TryCreate(candidate?.Trim(), UriKind.Absolute, out var parsed) &&
            parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            parsed.Host.Equals("learn.microsoft.com", StringComparison.OrdinalIgnoreCase))
        {
            validUri = parsed;
            return true;
        }

        validUri = null!;
        return false;
    }

    private static void CollectJsonSources(JsonElement element, List<StudyModuleSourceDto> results, HashSet<string> seen)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectJsonSources(item, results, seen);
            }
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string? url = null;
        string? title = null;

        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var val = prop.Value.GetString();
                if (string.IsNullOrWhiteSpace(val)) continue;

                if ((prop.Name.Equals("contentUrl", StringComparison.OrdinalIgnoreCase) ||
                     prop.Name.Equals("url", StringComparison.OrdinalIgnoreCase) ||
                     prop.Name.Equals("link", StringComparison.OrdinalIgnoreCase)) &&
                    TryValidateLearnUrl(val, out _))
                {
                    url = val;
                }
                else if (prop.Name.Equals("title", StringComparison.OrdinalIgnoreCase) ||
                         prop.Name.Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    title = val;
                }
            }
            else if (prop.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            {
                CollectJsonSources(prop.Value, results, seen);
            }
        }

        if (!string.IsNullOrWhiteSpace(url) && seen.Add(url))
        {
            results.Add(new StudyModuleSourceDto(string.IsNullOrWhiteSpace(title) ? HumanizeUrlTitle(url) : title, url));
        }
    }

    private static string? ExtractTitleFromMarkdown(string markdown)
    {
        var firstHeading = markdown
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal));
        return firstHeading is null ? null : firstHeading[2..].Trim();
    }

    private static string HumanizeUrlTitle(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var segment = uri.Segments.LastOrDefault(s => s != "/")?.Trim('/') ?? "Microsoft Learn Doc";
        var cleaned = segment.Replace('-', ' ').Replace('_', ' ');
        return char.ToUpperInvariant(cleaned[0]) + cleaned[1..];
    }

    private static List<string> Tokenize(string input) =>
        WordRegex().Matches(input.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(w => w.Length >= 3 && w is not ("the" or "and" or "for" or "with" or "azure"))
            .Distinct()
            .ToList();

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + "\n\n[Content truncated for prompt window]";

    [GeneratedRegex(@"\[([^\]]+)\]\((https://learn\.microsoft\.com[^\s\)]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"https://learn\.microsoft\.com/[^\s""'<>)\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex LearnUrlRegex();

    [GeneratedRegex(@"[a-z0-9\-]+")]
    private static partial Regex WordRegex();
}
