using System.ComponentModel.DataAnnotations;

namespace AzureKyoshi.Shared.Models;

public sealed record StudyModuleSummaryDto(
    Guid Id,
    string Title,
    string RequestedTopic,
    string? CertificationCode,
    Guid? FolderId,
    string? FolderName,
    DateTime CreatedAtUtc,
    int QuizCount,
    int? BestQuizScorePercent,
    int QaMessageCount);

public sealed record StudyModuleSourceDto(
    string Title,
    string Url);

public sealed record StudyModuleDetailDto(
    Guid Id,
    string Title,
    string RequestedTopic,
    string? CertificationCode,
    Guid? FolderId,
    string? FolderName,
    string SummaryMarkdown,
    List<StudyModuleSourceDto> Sources,
    bool GeneratedWithFoundryAi,
    bool RetrievedViaMcp,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    List<QuizDto> Quizzes,
    List<QaMessageDto> QaMessages);

public sealed class CreateStudyModuleRequest
{
    [Required, StringLength(200, MinimumLength = 2)]
    public string ModuleName { get; set; } = string.Empty;

    [StringLength(20)]
    public string? CertificationCode { get; set; }

    public Guid? FolderId { get; set; }

    [Range(3, 15)]
    public int InitialQuestionCount { get; set; } = 5;
}

public sealed class MoveModuleRequest
{
    public Guid? FolderId { get; set; }
}

public sealed class RegenerateSummaryRequest
{
    [StringLength(300)]
    public string? FocusInstructions { get; set; }
}
