namespace AzureKyoshi.Api.Data.Entities;

public sealed class StudyFolder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerUserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#0078D4";
    public string? CertificationCode { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<StudyModule> Modules { get; set; } = [];
}

public sealed class StudyModule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerUserId { get; set; } = string.Empty;
    public Guid? FolderId { get; set; }
    public StudyFolder? Folder { get; set; }

    public string Title { get; set; } = string.Empty;
    public string RequestedTopic { get; set; } = string.Empty;
    public string? CertificationCode { get; set; }
    public string SummaryMarkdown { get; set; } = string.Empty;
    public string SourceContent { get; set; } = string.Empty;
    public string SourcesJson { get; set; } = "[]";
    public bool GeneratedWithFoundryAi { get; set; }
    public bool RetrievedViaMcp { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<StudyQuiz> Quizzes { get; set; } = [];
    public ICollection<QaMessage> QaMessages { get; set; } = [];
}

public sealed class StudyQuiz
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudyModuleId { get; set; }
    public StudyModule StudyModule { get; set; } = null!;

    public string Title { get; set; } = string.Empty;
    public string Difficulty { get; set; } = "Associate";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<QuizQuestion> Questions { get; set; } = [];
    public ICollection<QuizAttempt> Attempts { get; set; } = [];
}

public sealed class QuizQuestion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudyQuizId { get; set; }
    public StudyQuiz StudyQuiz { get; set; } = null!;

    public int OrderIndex { get; set; }
    public string QuestionText { get; set; } = string.Empty;
    public string OptionsJson { get; set; } = "[]";
    public int CorrectOptionIndex { get; set; }
    public string Explanation { get; set; } = string.Empty;
}

public sealed class QuizAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudyQuizId { get; set; }
    public StudyQuiz StudyQuiz { get; set; } = null!;

    public string UserId { get; set; } = string.Empty;
    public int Score { get; set; }
    public int TotalQuestions { get; set; }
    public string SelectedOptionIndicesJson { get; set; } = "[]";
    public DateTime CompletedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class QaMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudyModuleId { get; set; }
    public StudyModule StudyModule { get; set; } = null!;

    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
