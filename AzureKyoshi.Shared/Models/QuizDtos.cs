using System.ComponentModel.DataAnnotations;

namespace AzureKyoshi.Shared.Models;

public sealed record QuizQuestionDto(
    Guid Id,
    int OrderIndex,
    string QuestionText,
    List<string> Options,
    int CorrectOptionIndex,
    string Explanation);

public sealed record QuizAttemptDto(
    Guid Id,
    Guid QuizId,
    int Score,
    int TotalQuestions,
    int ScorePercent,
    DateTime CompletedAtUtc,
    List<int> SelectedOptionIndices);

public sealed record QuizDto(
    Guid Id,
    Guid StudyModuleId,
    string Title,
    string Difficulty,
    int QuestionCount,
    DateTime CreatedAtUtc,
    List<QuizQuestionDto> Questions,
    List<QuizAttemptDto> Attempts);

public sealed class GenerateQuizRequest
{
    [Range(3, 20)]
    public int QuestionCount { get; set; } = 5;

    [Required, StringLength(30)]
    public string Difficulty { get; set; } = "Associate";

    [StringLength(200)]
    public string? FocusArea { get; set; }
}

public sealed class SubmitQuizAttemptRequest
{
    [Required]
    public List<int> SelectedOptionIndices { get; set; } = [];
}
