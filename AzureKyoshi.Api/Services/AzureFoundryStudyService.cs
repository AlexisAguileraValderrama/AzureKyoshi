using System.ClientModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using AzureKyoshi.Api.Options;
using AzureKyoshi.Shared.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureKyoshi.Api.Services;

public sealed record GeneratedQuestionModel(
    string QuestionText,
    List<string> Options,
    int CorrectOptionIndex,
    string Explanation);

public sealed record SummaryGenerationResult(
    string SummaryMarkdown,
    bool GeneratedWithFoundryAi);

public sealed record QuizGenerationResult(
    string Title,
    List<GeneratedQuestionModel> Questions,
    bool GeneratedWithFoundryAi);

public interface IAiStudyService
{
    bool IsFoundryConfigured { get; }
    string DeploymentName { get; }

    Task<SummaryGenerationResult> GenerateSummaryAsync(
        string moduleTitle,
        string? certificationCode,
        string learnContent,
        string? customFocus,
        CancellationToken cancellationToken = default);

    Task<QuizGenerationResult> GenerateQuizAsync(
        string moduleTitle,
        string? certificationCode,
        string summaryMarkdown,
        string learnContent,
        int questionCount,
        string difficulty,
        string? focusArea,
        IReadOnlyList<string> existingQuestionTexts,
        int quizNumber,
        CancellationToken cancellationToken = default);

    Task<string> AnswerQuestionAsync(
        string moduleTitle,
        string summaryMarkdown,
        string learnContent,
        IReadOnlyList<QaMessageDto> history,
        string userQuestion,
        CancellationToken cancellationToken = default);
}

public sealed class AzureFoundryStudyService(
    IOptions<AzureFoundryOptions> options,
    IConfiguration configuration,
    IHostEnvironment hostEnvironment,
    ILogger<AzureFoundryStudyService> logger)
    : IAiStudyService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private IChatClient? _chatClient;
    private readonly object _clientLock = new();

    public bool IsFoundryConfigured => !string.IsNullOrWhiteSpace(options.Value.Endpoint);
    public string DeploymentName => options.Value.DeploymentName;

    public async Task<SummaryGenerationResult> GenerateSummaryAsync(
        string moduleTitle,
        string? certificationCode,
        string learnContent,
        string? customFocus,
        CancellationToken cancellationToken = default)
    {
        var client = GetOrCreateChatClient();
        if (client is null)
        {
            return new SummaryGenerationResult(
                BuildDemoSummary(moduleTitle, certificationCode, learnContent, customFocus),
                GeneratedWithFoundryAi: false);
        }

        try
        {
            var certLine = string.IsNullOrWhiteSpace(certificationCode)
                ? "Microsoft Azure Certification"
                : $"Microsoft Azure Certification ({certificationCode})";

            var systemPrompt = """
                You are Azure Kyoshi, an expert Microsoft Certified Trainer helping engineers prepare for Azure certification exams.
                Produce a crisp, structured, exam-focused Markdown study guide grounded strictly in the provided Microsoft Learn documentation.
                Use clear sections:
                ## Overview
                ## Core Concepts & Architecture
                ## Key Configuration & Operational Details
                ## Exam Tips & Common Pitfalls
                ## Quick Revision Checklist
                Include concise bullet points, comparison tables where helpful, and Azure CLI/Portal concepts mentioned in the source.
                """;

            var userPrompt = new StringBuilder();
            userPrompt.AppendLine($"Create a comprehensive study summary for the Azure module: \"{moduleTitle}\".");
            userPrompt.AppendLine($"Target certification context: {certLine}.");
            if (!string.IsNullOrWhiteSpace(customFocus))
            {
                userPrompt.AppendLine($"Additional learner focus instructions: {customFocus}");
            }
            userPrompt.AppendLine();
            userPrompt.AppendLine("=== MICROSOFT LEARN SOURCE CONTENT ===");
            userPrompt.AppendLine(learnContent);

            var response = await client.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, userPrompt.ToString())
                ],
                cancellationToken: cancellationToken);

            var markdown = response.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(markdown))
            {
                return new SummaryGenerationResult(markdown, GeneratedWithFoundryAi: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Azure AI Foundry summary generation failed for '{Module}'.", moduleTitle);
            if (!options.Value.AllowDemoFallback)
            {
                throw;
            }
        }

        return new SummaryGenerationResult(
            BuildDemoSummary(moduleTitle, certificationCode, learnContent, customFocus),
            GeneratedWithFoundryAi: false);
    }

    public async Task<QuizGenerationResult> GenerateQuizAsync(
        string moduleTitle,
        string? certificationCode,
        string summaryMarkdown,
        string learnContent,
        int questionCount,
        string difficulty,
        string? focusArea,
        IReadOnlyList<string> existingQuestionTexts,
        int quizNumber,
        CancellationToken cancellationToken = default)
    {
        var clampedCount = Math.Clamp(questionCount, 3, 20);
        var defaultTitle = string.IsNullOrWhiteSpace(focusArea)
            ? $"Quiz #{quizNumber} · {difficulty}"
            : $"Quiz #{quizNumber} · {focusArea}";

        var client = GetOrCreateChatClient();
        if (client is null)
        {
            return new QuizGenerationResult(
                defaultTitle,
                BuildDemoQuizQuestions(moduleTitle, certificationCode, learnContent, clampedCount, difficulty, focusArea, quizNumber),
                GeneratedWithFoundryAi: false);
        }

        try
        {
            var systemPrompt = """
                You are an expert Microsoft Azure certification exam author. Generate realistic multiple-choice practice questions grounded in the provided Microsoft Learn module.
                Return ONLY valid JSON with no surrounding markdown fences or commentary, matching this exact schema:
                {
                  "title": "Short quiz title",
                  "questions": [
                    {
                      "questionText": "Scenario or technical question?",
                      "options": ["Choice text 1", "Choice text 2", "Choice text 3", "Choice text 4"],
                      "correctOptionIndex": 2,
                      "explanation": "Clear explanation naming the correct Azure service/setting and why the distractors are wrong."
                    }
                  ]
                }

                CRITICAL EXAM QUALITY RULES:
                1. Every question MUST have exactly 4 options and correctOptionIndex between 0 and 3.
                2. STRICT LENGTH & DETAIL PARITY: All 4 options MUST be roughly equal in word count (within ~20% of each other), grammatical structure, and technical specificity. NEVER make the correct answer noticeably longer, more detailed, or more qualified than the 3 distractors.
                3. PLAUSIBLE DISTRACTORS: Every incorrect option must be a realistic, specific Azure service, SKU, role, or configuration that tests genuine exam understanding (never obviously silly or overly vague).
                4. STANDALONE OPTIONS: Never use "All of the above", "None of the above", or references to option letters (like "Option A" or "Choice B") in option text or explanations—always refer to the Azure concepts by name.
                5. Distribute correctOptionIndex across 0, 1, 2, and 3 evenly.
                """;

            var userPrompt = new StringBuilder();
            userPrompt.AppendLine($"Generate {clampedCount} unique {difficulty}-level Azure certification multiple-choice questions for module: \"{moduleTitle}\".");
            if (!string.IsNullOrWhiteSpace(certificationCode))
            {
                userPrompt.AppendLine($"Exam alignment: {certificationCode}.");
            }
            if (!string.IsNullOrWhiteSpace(focusArea))
            {
                userPrompt.AppendLine($"Focus area: {focusArea}.");
            }
            if (existingQuestionTexts.Count > 0)
            {
                userPrompt.AppendLine("Do NOT repeat or closely paraphrase any of these already-asked questions:");
                foreach (var q in existingQuestionTexts.Take(20))
                {
                    userPrompt.AppendLine($"- {q}");
                }
            }

            userPrompt.AppendLine();
            userPrompt.AppendLine("=== MODULE SUMMARY ===");
            userPrompt.AppendLine(summaryMarkdown);
            userPrompt.AppendLine();
            userPrompt.AppendLine("=== MICROSOFT LEARN EXCERPTS ===");
            userPrompt.AppendLine(learnContent.Length > 12_000 ? learnContent[..12_000] : learnContent);

            var response = await client.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, userPrompt.ToString())
                ],
                cancellationToken: cancellationToken);

            var parsed = ParseQuizPayload(response.Text, defaultTitle);
            if (parsed is not null && parsed.Questions.Count > 0)
            {
                return new QuizGenerationResult(parsed.Title, parsed.Questions, GeneratedWithFoundryAi: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Azure AI Foundry quiz generation failed for '{Module}'.", moduleTitle);
            if (!options.Value.AllowDemoFallback)
            {
                throw;
            }
        }

        return new QuizGenerationResult(
            defaultTitle,
            BuildDemoQuizQuestions(moduleTitle, certificationCode, learnContent, clampedCount, difficulty, focusArea, quizNumber),
            GeneratedWithFoundryAi: false);
    }

    public async Task<string> AnswerQuestionAsync(
        string moduleTitle,
        string summaryMarkdown,
        string learnContent,
        IReadOnlyList<QaMessageDto> history,
        string userQuestion,
        CancellationToken cancellationToken = default)
    {
        var client = GetOrCreateChatClient();
        if (client is null)
        {
            return BuildDemoAnswer(moduleTitle, summaryMarkdown, learnContent, userQuestion);
        }

        try
        {
            var systemPrompt = $"""
                You are Azure Kyoshi, an Azure Certification Study Tutor answering learner questions about the Microsoft Learn module "{moduleTitle}".
                Ground your answers accurately in the provided module summary and Microsoft Learn content.
                Be clear, concise, and practical—highlight Azure exam nuances, best practices, or CLI/Portal steps when helpful.
                Format your response in clean Markdown.

                === MODULE SUMMARY ===
                {summaryMarkdown}

                === MICROSOFT LEARN REFERENCE ===
                {(learnContent.Length > 10_000 ? learnContent[..10_000] : learnContent)}
                """;

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt)
            };

            foreach (var msg in history.TakeLast(10))
            {
                var role = msg.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
                    ? ChatRole.Assistant
                    : ChatRole.User;
                messages.Add(new ChatMessage(role, msg.Content));
            }

            messages.Add(new ChatMessage(ChatRole.User, userQuestion));

            var response = await client.GetResponseAsync(messages, cancellationToken: cancellationToken);
            if (!string.IsNullOrWhiteSpace(response.Text))
            {
                return response.Text.Trim();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Azure AI Foundry Q&A failed for '{Module}'.", moduleTitle);
            if (!options.Value.AllowDemoFallback)
            {
                throw;
            }
        }

        return BuildDemoAnswer(moduleTitle, summaryMarkdown, learnContent, userQuestion);
    }

    private IChatClient? GetOrCreateChatClient()
    {
        var cfg = options.Value;
        if (string.IsNullOrWhiteSpace(cfg.Endpoint))
        {
            return null;
        }

        if (_chatClient is not null)
        {
            return _chatClient;
        }

        lock (_clientLock)
        {
            if (_chatClient is not null)
            {
                return _chatClient;
            }

            var endpoint = new Uri(cfg.Endpoint.Trim());
            AzureOpenAIClient azureClient;
            if (string.IsNullOrWhiteSpace(cfg.ApiKey))
            {
                var tenantId = configuration["AzureAd:TenantId"];
                var credentialOptions = new DefaultAzureCredentialOptions
                {
                    TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim(),
                    // In local Development (e.g. without az CLI), allow 1-time interactive browser sign-in if VS credential fails
                    ExcludeInteractiveBrowserCredential = !hostEnvironment.IsDevelopment()
                };
                azureClient = new AzureOpenAIClient(endpoint, new DefaultAzureCredential(credentialOptions));
            }
            else
            {
                azureClient = new AzureOpenAIClient(endpoint, new ApiKeyCredential(cfg.ApiKey.Trim()));
            }

            _chatClient = azureClient
                .GetChatClient(cfg.DeploymentName.Trim())
                .AsIChatClient();

            return _chatClient;
        }
    }

    private static QuizPayload? ParseQuizPayload(string? rawText, string fallbackTitle)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        var cleaned = rawText.Trim();
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = cleaned.IndexOf('\n');
            var lastFence = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine > 0 && lastFence > firstNewLine)
            {
                cleaned = cleaned[(firstNewLine + 1)..lastFence].Trim();
            }
        }

        var firstBrace = cleaned.IndexOf('{');
        var lastBrace = cleaned.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            cleaned = cleaned[firstBrace..(lastBrace + 1)];
        }

        var dto = JsonSerializer.Deserialize<QuizPayloadDto>(cleaned, JsonOptions);
        if (dto?.Questions is null || dto.Questions.Count == 0)
        {
            return null;
        }

        var validQuestions = new List<GeneratedQuestionModel>();
        foreach (var q in dto.Questions)
        {
            if (string.IsNullOrWhiteSpace(q.QuestionText) || q.Options is null || q.Options.Count < 2)
            {
                continue;
            }

            var normalizedOptions = q.Options.Take(4).Select(o => o.Trim()).ToList();
            while (normalizedOptions.Count < 4)
            {
                normalizedOptions.Add("None of the above");
            }

            var idx = Math.Clamp(q.CorrectOptionIndex, 0, normalizedOptions.Count - 1);
            var explanation = string.IsNullOrWhiteSpace(q.Explanation)
                ? "Review the official Microsoft Learn documentation for this capability."
                : q.Explanation.Trim();

            validQuestions.Add(new GeneratedQuestionModel(q.QuestionText.Trim(), normalizedOptions, idx, explanation));
        }

        return new QuizPayload(
            string.IsNullOrWhiteSpace(dto.Title) ? fallbackTitle : dto.Title.Trim(),
            ShuffleAndBalanceQuestionOptions(validQuestions));
    }

    /// <summary>
    /// Eliminates LLM answer-position bias (e.g., always A or B) by shuffling each question's 4 options
    /// so correct answers are evenly and randomly distributed across A, B, C, and D.
    /// </summary>
    private static List<GeneratedQuestionModel> ShuffleAndBalanceQuestionOptions(List<GeneratedQuestionModel> questions)
    {
        if (questions.Count == 0)
        {
            return questions;
        }

        // Build a balanced target-slot deck (shuffled blocks of 0..3) so A/B/C/D appear evenly across the quiz
        var targetSlots = new List<int>(questions.Count);
        while (targetSlots.Count < questions.Count)
        {
            int[] block = [0, 1, 2, 3];
            Random.Shared.Shuffle(block);
            targetSlots.AddRange(block);
        }

        var balanced = new List<GeneratedQuestionModel>(questions.Count);
        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            if (q.Options.Count != 4 ||
                q.Options.Any(o => o.Contains("of the above", StringComparison.OrdinalIgnoreCase)))
            {
                balanced.Add(q);
                continue;
            }

            var correctText = q.Options[q.CorrectOptionIndex];
            var distractors = q.Options
                .Where((_, idx) => idx != q.CorrectOptionIndex)
                .ToArray();
            Random.Shared.Shuffle(distractors);

            var targetIdx = targetSlots[i];
            var newOptions = new string[4];
            var dCursor = 0;
            for (var slot = 0; slot < 4; slot++)
            {
                newOptions[slot] = slot == targetIdx ? correctText : distractors[dCursor++];
            }

            balanced.Add(q with
            {
                Options = [.. newOptions],
                CorrectOptionIndex = targetIdx
            });
        }

        return balanced;
    }

    private static string BuildDemoSummary(
        string moduleTitle,
        string? certificationCode,
        string learnContent,
        string? customFocus)
    {
        var extractedHeadings = ExtractKeyLines(learnContent, isHeading: true).Take(6).ToList();
        var extractedPoints = ExtractKeyLines(learnContent, isHeading: false).Take(6).ToList();
        var certLabel = string.IsNullOrWhiteSpace(certificationCode) ? "Microsoft Azure" : certificationCode;

        var sb = new StringBuilder();
        sb.AppendLine($"## Overview");
        sb.AppendLine($"This study guide synthesizes **{moduleTitle}** for **{certLabel}** certification preparation using content retrieved from Microsoft Learn.");
        if (!string.IsNullOrWhiteSpace(customFocus))
        {
            sb.AppendLine();
            sb.AppendLine($"> **Study Focus:** {customFocus}");
        }

        sb.AppendLine();
        sb.AppendLine("## Core Concepts & Architecture");
        if (extractedHeadings.Count > 0)
        {
            foreach (var h in extractedHeadings)
            {
                sb.AppendLine($"- **{h}**: Core topic covered in the official Microsoft Learn documentation for this module.");
            }
        }
        else
        {
            sb.AppendLine($"- **Service Architecture**: Understand how **{moduleTitle}** integrates with Azure Resource Manager (ARM), identity governance, and networking boundaries.");
            sb.AppendLine($"- **Security & Access Control**: Apply Microsoft Entra ID RBAC least-privilege principles and managed identities wherever applicable.");
            sb.AppendLine($"- **Resilience & Scaling**: Evaluate SLA tiers, redundancy options, and cost trade-offs for production workloads.");
        }

        sb.AppendLine();
        sb.AppendLine("## Key Highlights from Microsoft Learn");
        if (extractedPoints.Count > 0)
        {
            foreach (var p in extractedPoints)
            {
                sb.AppendLine($"- {p}");
            }
        }
        else
        {
            sb.AppendLine($"- Review official portal workflows, Azure CLI commands (`az`), and Bicep/ARM deployment properties for **{moduleTitle}**.");
            sb.AppendLine("- Pay close attention to SKU/tier feature boundaries—Azure certification questions frequently test which tier unlocks specific capabilities.");
        }

        sb.AppendLine();
        sb.AppendLine("## Exam Tips & Common Pitfalls");
        sb.AppendLine($"- **Least Privilege First**: When an exam scenario asks for the *most secure* or *least administrative effort* configuration in **{moduleTitle}**, prefer built-in RBAC roles and Managed Identities over shared keys.");
        sb.AppendLine("- **Scope Hierarchy**: Remember Azure governance inheritance: Management Group → Subscription → Resource Group → Resource.");
        sb.AppendLine("- **Monitoring & Diagnostics**: Distinguish between Activity Logs (control plane) and Resource Logs / Metrics (data plane).");

        sb.AppendLine();
        sb.AppendLine("## Quick Revision Checklist");
        sb.AppendLine($"- [ ] Explain the primary use cases and architecture of **{moduleTitle}**");
        sb.AppendLine("- [ ] Compare pricing/SKU tiers and regional availability constraints");
        sb.AppendLine("- [ ] Configure access control, diagnostics, and high availability");
        sb.AppendLine("- [ ] Complete the practice quizzes in the **Quizzes** tab (aim for 80%+)");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("*Running in **Local Demo AI Mode** because `AzureFoundry:Endpoint` is not configured yet. Add your Azure AI Foundry endpoint and deployment name in `AzureKyoshi.Api/appsettings.Development.json` to switch to live LLM synthesis.*");

        return sb.ToString();
    }

    private static List<GeneratedQuestionModel> BuildDemoQuizQuestions(
        string moduleTitle,
        string? certificationCode,
        string learnContent,
        int count,
        string difficulty,
        string? focusArea,
        int quizNumber)
    {
        var certTag = string.IsNullOrWhiteSpace(certificationCode) ? "Azure" : certificationCode;
        var focusNote = string.IsNullOrWhiteSpace(focusArea) ? moduleTitle : $"{moduleTitle} ({focusArea})";
        var keyHeadings = ExtractKeyLines(learnContent, isHeading: true);
        var topicHighlight = keyHeadings.Count > 0 ? keyHeadings[(quizNumber - 1) % keyHeadings.Count] : moduleTitle;

        var bank = new List<GeneratedQuestionModel>
        {
            new(
                $"[{difficulty} · {certTag}] Your team is implementing {focusNote} in production and needs to follow Azure security best practices with minimal secret management overhead. Which approach should you recommend?",
                [
                    "Use Microsoft Entra ID Managed Identities with least-privilege RBAC roles",
                    "Store shared access keys in application source control for fast rotation",
                    "Grant the Owner role at the root Management Group to all developers",
                    "Disable HTTPS and rely exclusively on subnet IP filtering"
                ],
                0,
                $"Microsoft Learn recommends Managed Identities paired with least-privilege Azure RBAC roles for {moduleTitle} because Azure automatically manages credential lifecycle without hardcoded secrets."),
            new(
                $"[{difficulty} · {certTag}] When organizing resources for {moduleTitle} across development, staging, and production environments, which Azure governance hierarchy order is applied from highest to lowest scope?",
                [
                    "Subscription → Management Group → Resource → Resource Group",
                    "Management Group → Subscription → Resource Group → Resource",
                    "Resource Group → Subscription → Management Group → Resource",
                    "Tenant → Resource → Subscription → Management Group"
                ],
                1,
                "Azure evaluates policies and RBAC inheritance from Management Groups down to Subscriptions, Resource Groups, and individual Resources."),
            new(
                $"[{difficulty} · {certTag}] You need to audit who modified configuration settings related to \"{topicHighlight}\" over the last 30 days. Which Azure telemetry source records control-plane operations?",
                [
                    "Azure Activity Log",
                    "Guest OS Performance Counters",
                    "Azure Advisor Cost Recommendations",
                    "DNS Resolver Cache"
                ],
                0,
                "Azure Activity Log captures subscription-level control-plane events (create, update, delete, and RBAC changes), whereas resource logs capture data-plane operations."),
            new(
                $"[{difficulty} · {certTag}] An architecture review for {focusNote} requires preventing accidental deletion of critical production resources even by users with Contributor permissions. What should you configure?",
                [
                    "An Azure Resource Lock set to CanNotDelete",
                    "A resource tag named DoNotDelete=true",
                    "An Azure Cost Management budget alert",
                    "A custom Azure Monitor action group"
                ],
                0,
                "Resource Locks (CanNotDelete or ReadOnly) override Contributor permissions and must be explicitly removed by an Owner or User Access Administrator before deletion."),
            new(
                $"[{difficulty} · {certTag}] Your organization wants repeatable, version-controlled deployments of {moduleTitle} across multiple Azure regions with drift detection. Which approach aligns best with Microsoft Learn guidance?",
                [
                    "Manually recreate resources in the Azure Portal for each region",
                    "Use Infrastructure as Code (Bicep or ARM templates) deployed through CI/CD pipelines",
                    "Export billing invoices and replay them via Azure Cloud Shell",
                    "Copy VM disks manually between storage accounts"
                ],
                1,
                "Declarative Infrastructure as Code (Bicep/ARM/Terraform) ensures idempotent, auditable, and repeatable deployments across environments."),
            new(
                $"[{difficulty} · {certTag}] While troubleshooting latency and availability for {focusNote}, you want to ensure resilience against a single datacenter failure within a region. Which capability provides datacenter-level isolation?",
                [
                    "Availability Zones",
                    "Resource Tags",
                    "Azure Lighthouse",
                    "Azure Policy Exemptions"
                ],
                0,
                "Availability Zones are physically separate datacenters within an Azure region with independent power, cooling, and networking."),
            new(
                $"[{difficulty} · {certTag}] You need to enforce that all newly deployed resources for {moduleTitle} are restricted to approved Azure regions automatically. Which service enforces this guardrail?",
                [
                    "Azure Policy with an Allowed Locations assignment",
                    "Azure Service Health notifications",
                    "Microsoft Entra ID Self-Service Password Reset",
                    "Azure Pricing Calculator"
                ],
                0,
                "Azure Policy evaluates resource properties at creation/update time and can deny non-compliant deployments outside approved regions."),
            new(
                $"[{difficulty} · {certTag}] To optimize operational costs for {focusNote} while maintaining visibility into unused or oversized resources, which native Azure tool provides automated recommendations?",
                [
                    "Azure Advisor",
                    "Azure Bastion",
                    "Azure Private Link",
                    "Azure DDoS Protection"
                ],
                0,
                "Azure Advisor analyzes telemetry and configuration to provide personalized recommendations across Cost, Security, Reliability, Operational Excellence, and Performance.")
        };

        // Rotate bank by quizNumber so generating additional quizzes yields fresh ordering & scenarios
        var offset = ((quizNumber - 1) * 3) % bank.Count;
        var selected = new List<GeneratedQuestionModel>(count);
        for (var i = 0; i < count; i++)
        {
            var baseQ = bank[(offset + i) % bank.Count];
            if (i >= bank.Count)
            {
                selected.Add(baseQ with { QuestionText = $"(Scenario Variant {i + 1}) {baseQ.QuestionText}" });
            }
            else
            {
                selected.Add(baseQ);
            }
        }

        return ShuffleAndBalanceQuestionOptions(selected);
    }

    private static string BuildDemoAnswer(
        string moduleTitle,
        string summaryMarkdown,
        string learnContent,
        string userQuestion)
    {
        var keywords = Regex.Matches(userQuestion.ToLowerInvariant(), @"[a-z0-9\-]{3,}")
            .Select(m => m.Value)
            .Where(w => w is not ("what" or "how" or "does" or "when" or "where" or "why" or "the" or "and" or "for" or "with" or "about" or "this" or "that"))
            .Distinct()
            .ToList();

        var matchingLines = (summaryMarkdown + "\n" + learnContent)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length is >= 25 and <= 360 &&
                        !l.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                        !l.StartsWith('{') &&
                        !l.StartsWith('[') &&
                        !l.Contains("\"contentUrl\"", StringComparison.OrdinalIgnoreCase))
            .Select(l => (Line: l, Score: keywords.Count(k => l.Contains(k, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(3)
            .Select(x => x.Line)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"### Answer regarding **{moduleTitle}**");
        sb.AppendLine();

        if (matchingLines.Count > 0)
        {
            sb.AppendLine("Based on the Microsoft Learn module content retrieved for this topic:");
            sb.AppendLine();
            foreach (var line in matchingLines)
            {
                sb.AppendLine($"> {line.TrimStart('#', '-', '*', ' ')}");
            }
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine($"For **{moduleTitle}**, focus on how Azure manages security (Entra ID / RBAC), reliability (Availability Zones / redundancy), and configuration tiers for your scenario: *\"{userQuestion}\"*");
            sb.AppendLine();
        }

        sb.AppendLine("**Certification Exam Tip:** Always verify whether the requirement asks for *least administrative effort*, *lowest cost*, or *highest security*, as those constraints determine the exact Azure service SKU or configuration.");
        sb.AppendLine();
        sb.AppendLine("*(Demo AI Mode active — configure `AzureFoundry:Endpoint` and `DeploymentName` to get full conversational answers from your Azure AI Foundry model.)*");

        return sb.ToString();
    }

    private static List<string> ExtractKeyLines(string content, bool isHeading)
    {
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .ToList();

        if (isHeading)
        {
            return lines
                .Where(l => l.StartsWith("##", StringComparison.Ordinal) && !l.Contains("Search Excerpts", StringComparison.OrdinalIgnoreCase))
                .Select(l => l.TrimStart('#', ' '))
                .Where(l => l.Length is >= 4 and <= 100)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return lines
            .Where(l => !l.StartsWith('#') &&
                        !l.StartsWith("URL:", StringComparison.OrdinalIgnoreCase) &&
                        !l.StartsWith("Source:", StringComparison.OrdinalIgnoreCase) &&
                        !l.StartsWith('{') &&
                        !l.StartsWith('[') &&
                        l.Length is >= 45 and <= 240)
            .Select(l => l.TrimStart('-', '*', ' '))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed record QuizPayload(string Title, List<GeneratedQuestionModel> Questions);

    private sealed class QuizPayloadDto
    {
        public string? Title { get; set; }
        public List<QuizQuestionPayloadDto>? Questions { get; set; }
    }

    private sealed class QuizQuestionPayloadDto
    {
        public string? QuestionText { get; set; }
        public List<string>? Options { get; set; }
        public int CorrectOptionIndex { get; set; }
        public string? Explanation { get; set; }
    }
}
