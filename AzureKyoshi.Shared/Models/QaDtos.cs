using System.ComponentModel.DataAnnotations;

namespace AzureKyoshi.Shared.Models;

public sealed record QaMessageDto(
    Guid Id,
    Guid StudyModuleId,
    string Role,
    string Content,
    DateTime CreatedAtUtc);

public sealed class AskQuestionRequest
{
    [Required, StringLength(1500, MinimumLength = 2)]
    public string Question { get; set; } = string.Empty;
}

public sealed record AskQuestionResponse(
    QaMessageDto UserMessage,
    QaMessageDto AssistantMessage);

public sealed record AppRuntimeStatusDto(
    string AiMode,
    string AiDeploymentName,
    string DatabaseProvider,
    string AuthMode,
    string CurrentUserName,
    string CurrentUserEmail,
    string McpEndpoint);
