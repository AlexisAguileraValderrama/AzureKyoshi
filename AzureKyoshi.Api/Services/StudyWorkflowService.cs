using System.Text.Json;
using AzureKyoshi.Api.Data;
using AzureKyoshi.Api.Data.Entities;
using AzureKyoshi.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureKyoshi.Api.Services;

public interface IStudyWorkflowService
{
    Task<List<FolderDto>> GetFoldersAsync(string userId, CancellationToken ct);
    Task<FolderDto> CreateFolderAsync(string userId, CreateFolderRequest request, CancellationToken ct);
    Task<FolderDto?> UpdateFolderAsync(string userId, Guid folderId, UpdateFolderRequest request, CancellationToken ct);
    Task<bool> DeleteFolderAsync(string userId, Guid folderId, CancellationToken ct);

    Task<List<StudyModuleSummaryDto>> GetModulesAsync(string userId, CancellationToken ct);
    Task<StudyModuleDetailDto?> GetModuleDetailAsync(string userId, Guid moduleId, CancellationToken ct);
    Task<StudyModuleDetailDto> CreateStudyModuleAsync(string userId, CreateStudyModuleRequest request, CancellationToken ct);
    Task<StudyModuleSummaryDto?> MoveModuleAsync(string userId, Guid moduleId, Guid? folderId, CancellationToken ct);
    Task<StudyModuleDetailDto?> RegenerateSummaryAsync(string userId, Guid moduleId, RegenerateSummaryRequest request, CancellationToken ct);
    Task<bool> DeleteModuleAsync(string userId, Guid moduleId, CancellationToken ct);

    Task<QuizDto?> GenerateQuizAsync(string userId, Guid moduleId, GenerateQuizRequest request, CancellationToken ct);
    Task<QuizAttemptDto?> SubmitQuizAttemptAsync(string userId, Guid quizId, SubmitQuizAttemptRequest request, CancellationToken ct);
    Task<AskQuestionResponse?> AskQuestionAsync(string userId, Guid moduleId, AskQuestionRequest request, CancellationToken ct);
}

public sealed class StudyWorkflowService(
    AzureKyoshiDbContext db,
    IMicrosoftLearnService learnService,
    IAiStudyService aiStudyService)
    : IStudyWorkflowService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<List<FolderDto>> GetFoldersAsync(string userId, CancellationToken ct)
    {
        return await db.StudyFolders
            .AsNoTracking()
            .Where(f => f.OwnerUserId == userId)
            .OrderBy(f => f.Name)
            .Select(f => new FolderDto(
                f.Id,
                f.Name,
                f.ColorHex,
                f.CertificationCode,
                f.CreatedAtUtc,
                f.Modules.Count))
            .ToListAsync(ct);
    }

    public async Task<FolderDto> CreateFolderAsync(string userId, CreateFolderRequest request, CancellationToken ct)
    {
        var folder = new StudyFolder
        {
            OwnerUserId = userId,
            Name = request.Name.Trim(),
            ColorHex = string.IsNullOrWhiteSpace(request.ColorHex) ? "#0078D4" : request.ColorHex.Trim(),
            CertificationCode = NormalizeOptional(request.CertificationCode),
            CreatedAtUtc = DateTime.UtcNow
        };

        db.StudyFolders.Add(folder);
        await db.SaveChangesAsync(ct);

        return new FolderDto(folder.Id, folder.Name, folder.ColorHex, folder.CertificationCode, folder.CreatedAtUtc, 0);
    }

    public async Task<FolderDto?> UpdateFolderAsync(string userId, Guid folderId, UpdateFolderRequest request, CancellationToken ct)
    {
        var folder = await db.StudyFolders
            .Include(f => f.Modules)
            .FirstOrDefaultAsync(f => f.Id == folderId && f.OwnerUserId == userId, ct);

        if (folder is null)
        {
            return null;
        }

        folder.Name = request.Name.Trim();
        folder.ColorHex = string.IsNullOrWhiteSpace(request.ColorHex) ? folder.ColorHex : request.ColorHex.Trim();
        folder.CertificationCode = NormalizeOptional(request.CertificationCode);

        await db.SaveChangesAsync(ct);

        return new FolderDto(folder.Id, folder.Name, folder.ColorHex, folder.CertificationCode, folder.CreatedAtUtc, folder.Modules.Count);
    }

    public async Task<bool> DeleteFolderAsync(string userId, Guid folderId, CancellationToken ct)
    {
        var folder = await db.StudyFolders
            .Include(f => f.Modules)
            .FirstOrDefaultAsync(f => f.Id == folderId && f.OwnerUserId == userId, ct);

        if (folder is null)
        {
            return false;
        }

        // Preserve all studied modules by unassigning them from the deleted folder
        foreach (var module in folder.Modules)
        {
            module.FolderId = null;
        }

        db.StudyFolders.Remove(folder);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<List<StudyModuleSummaryDto>> GetModulesAsync(string userId, CancellationToken ct)
    {
        var modules = await db.StudyModules
            .AsNoTracking()
            .Where(m => m.OwnerUserId == userId)
            .Include(m => m.Folder)
            .Include(m => m.Quizzes)
                .ThenInclude(q => q.Attempts)
            .Include(m => m.QaMessages)
            .OrderByDescending(m => m.CreatedAtUtc)
            .ToListAsync(ct);

        return modules.Select(MapSummaryDto).ToList();
    }

    public async Task<StudyModuleDetailDto?> GetModuleDetailAsync(string userId, Guid moduleId, CancellationToken ct)
    {
        var module = await db.StudyModules
            .AsNoTracking()
            .Where(m => m.Id == moduleId && m.OwnerUserId == userId)
            .Include(m => m.Folder)
            .Include(m => m.Quizzes.OrderBy(q => q.CreatedAtUtc))
                .ThenInclude(q => q.Questions.OrderBy(qq => qq.OrderIndex))
            .Include(m => m.Quizzes)
                .ThenInclude(q => q.Attempts.OrderByDescending(a => a.CompletedAtUtc))
            .Include(m => m.QaMessages.OrderBy(msg => msg.CreatedAtUtc))
            .FirstOrDefaultAsync(ct);

        return module is null ? null : MapDetailDto(module);
    }

    public async Task<StudyModuleDetailDto> CreateStudyModuleAsync(string userId, CreateStudyModuleRequest request, CancellationToken ct)
    {
        Guid? validFolderId = null;
        if (request.FolderId.HasValue)
        {
            var exists = await db.StudyFolders.AnyAsync(f => f.Id == request.FolderId.Value && f.OwnerUserId == userId, ct);
            if (exists)
            {
                validFolderId = request.FolderId.Value;
            }
        }

        var certCode = NormalizeOptional(request.CertificationCode);

        // 1. Fetch official Microsoft Learn module content via MCP
        var learnContent = await learnService.FetchModuleContentAsync(request.ModuleName, certCode, ct);

        // 2. Synthesize structured study summary via Azure AI Foundry (or Demo fallback)
        var summaryResult = await aiStudyService.GenerateSummaryAsync(
            learnContent.ResolvedTitle,
            certCode,
            learnContent.ContentText,
            customFocus: null,
            ct);

        // 3. Automatically generate the initial 5-question quiz
        var initialQuestionCount = Math.Clamp(request.InitialQuestionCount, 3, 15);
        var quizResult = await aiStudyService.GenerateQuizAsync(
            learnContent.ResolvedTitle,
            certCode,
            summaryResult.SummaryMarkdown,
            learnContent.ContentText,
            initialQuestionCount,
            difficulty: "Associate",
            focusArea: "Initial Module Assessment",
            existingQuestionTexts: [],
            quizNumber: 1,
            ct);

        var now = DateTime.UtcNow;
        var entity = new StudyModule
        {
            OwnerUserId = userId,
            FolderId = validFolderId,
            Title = learnContent.ResolvedTitle,
            RequestedTopic = request.ModuleName.Trim(),
            CertificationCode = certCode,
            SummaryMarkdown = summaryResult.SummaryMarkdown,
            SourceContent = learnContent.ContentText,
            SourcesJson = JsonSerializer.Serialize(learnContent.Sources, JsonOptions),
            GeneratedWithFoundryAi = summaryResult.GeneratedWithFoundryAi,
            RetrievedViaMcp = learnContent.RetrievedViaMcp,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        var initialQuiz = new StudyQuiz
        {
            StudyModule = entity,
            Title = quizResult.Title,
            Difficulty = "Associate",
            CreatedAtUtc = now,
            Questions = quizResult.Questions
                .Select((q, index) => new QuizQuestion
                {
                    OrderIndex = index + 1,
                    QuestionText = q.QuestionText,
                    OptionsJson = JsonSerializer.Serialize(q.Options, JsonOptions),
                    CorrectOptionIndex = q.CorrectOptionIndex,
                    Explanation = q.Explanation
                })
                .ToList()
        };

        entity.Quizzes.Add(initialQuiz);

        db.StudyModules.Add(entity);
        await db.SaveChangesAsync(ct);

        return (await GetModuleDetailAsync(userId, entity.Id, ct))!;
    }

    public async Task<StudyModuleSummaryDto?> MoveModuleAsync(string userId, Guid moduleId, Guid? folderId, CancellationToken ct)
    {
        var module = await db.StudyModules
            .Include(m => m.Folder)
            .Include(m => m.Quizzes)
                .ThenInclude(q => q.Attempts)
            .Include(m => m.QaMessages)
            .FirstOrDefaultAsync(m => m.Id == moduleId && m.OwnerUserId == userId, ct);

        if (module is null)
        {
            return null;
        }

        if (folderId.HasValue)
        {
            var folder = await db.StudyFolders.FirstOrDefaultAsync(f => f.Id == folderId.Value && f.OwnerUserId == userId, ct);
            if (folder is null)
            {
                return null;
            }
            module.FolderId = folder.Id;
            module.Folder = folder;
        }
        else
        {
            module.FolderId = null;
            module.Folder = null;
        }

        module.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return MapSummaryDto(module);
    }

    public async Task<StudyModuleDetailDto?> RegenerateSummaryAsync(
        string userId,
        Guid moduleId,
        RegenerateSummaryRequest request,
        CancellationToken ct)
    {
        var module = await db.StudyModules
            .FirstOrDefaultAsync(m => m.Id == moduleId && m.OwnerUserId == userId, ct);

        if (module is null)
        {
            return null;
        }

        var result = await aiStudyService.GenerateSummaryAsync(
            module.Title,
            module.CertificationCode,
            module.SourceContent,
            request.FocusInstructions,
            ct);

        module.SummaryMarkdown = result.SummaryMarkdown;
        module.GeneratedWithFoundryAi = result.GeneratedWithFoundryAi;
        module.UpdatedAtUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return await GetModuleDetailAsync(userId, module.Id, ct);
    }

    public async Task<bool> DeleteModuleAsync(string userId, Guid moduleId, CancellationToken ct)
    {
        var module = await db.StudyModules.FirstOrDefaultAsync(m => m.Id == moduleId && m.OwnerUserId == userId, ct);
        if (module is null)
        {
            return false;
        }

        db.StudyModules.Remove(module);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<QuizDto?> GenerateQuizAsync(
        string userId,
        Guid moduleId,
        GenerateQuizRequest request,
        CancellationToken ct)
    {
        var module = await db.StudyModules
            .Include(m => m.Quizzes)
                .ThenInclude(q => q.Questions)
            .FirstOrDefaultAsync(m => m.Id == moduleId && m.OwnerUserId == userId, ct);

        if (module is null)
        {
            return null;
        }

        var existingQuestions = module.Quizzes
            .SelectMany(q => q.Questions)
            .Select(q => q.QuestionText)
            .ToList();

        var quizNumber = module.Quizzes.Count + 1;
        var generated = await aiStudyService.GenerateQuizAsync(
            module.Title,
            module.CertificationCode,
            module.SummaryMarkdown,
            module.SourceContent,
            request.QuestionCount,
            request.Difficulty,
            request.FocusArea,
            existingQuestions,
            quizNumber,
            ct);

        var quiz = new StudyQuiz
        {
            StudyModuleId = module.Id,
            Title = generated.Title,
            Difficulty = request.Difficulty.Trim(),
            CreatedAtUtc = DateTime.UtcNow,
            Questions = generated.Questions
                .Select((q, index) => new QuizQuestion
                {
                    OrderIndex = index + 1,
                    QuestionText = q.QuestionText,
                    OptionsJson = JsonSerializer.Serialize(q.Options, JsonOptions),
                    CorrectOptionIndex = q.CorrectOptionIndex,
                    Explanation = q.Explanation
                })
                .ToList()
        };

        db.StudyQuizzes.Add(quiz);
        await db.SaveChangesAsync(ct);

        return MapQuizDto(quiz);
    }

    public async Task<QuizAttemptDto?> SubmitQuizAttemptAsync(
        string userId,
        Guid quizId,
        SubmitQuizAttemptRequest request,
        CancellationToken ct)
    {
        var quiz = await db.StudyQuizzes
            .Include(q => q.StudyModule)
            .Include(q => q.Questions.OrderBy(qq => qq.OrderIndex))
            .FirstOrDefaultAsync(q => q.Id == quizId && q.StudyModule.OwnerUserId == userId, ct);

        if (quiz is null)
        {
            return null;
        }

        var orderedQuestions = quiz.Questions.OrderBy(q => q.OrderIndex).ToList();
        var score = 0;

        for (var i = 0; i < orderedQuestions.Count; i++)
        {
            var selected = i < request.SelectedOptionIndices.Count ? request.SelectedOptionIndices[i] : -1;
            if (selected == orderedQuestions[i].CorrectOptionIndex)
            {
                score++;
            }
        }

        var attempt = new QuizAttempt
        {
            StudyQuizId = quiz.Id,
            UserId = userId,
            Score = score,
            TotalQuestions = orderedQuestions.Count,
            SelectedOptionIndicesJson = JsonSerializer.Serialize(request.SelectedOptionIndices, JsonOptions),
            CompletedAtUtc = DateTime.UtcNow
        };

        db.QuizAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);

        return MapAttemptDto(attempt);
    }

    public async Task<AskQuestionResponse?> AskQuestionAsync(
        string userId,
        Guid moduleId,
        AskQuestionRequest request,
        CancellationToken ct)
    {
        var module = await db.StudyModules
            .Include(m => m.QaMessages.OrderBy(msg => msg.CreatedAtUtc))
            .FirstOrDefaultAsync(m => m.Id == moduleId && m.OwnerUserId == userId, ct);

        if (module is null)
        {
            return null;
        }

        var historyDtos = module.QaMessages
            .OrderBy(m => m.CreatedAtUtc)
            .Select(m => new QaMessageDto(m.Id, m.StudyModuleId, m.Role, m.Content, m.CreatedAtUtc))
            .ToList();

        var answer = await aiStudyService.AnswerQuestionAsync(
            module.Title,
            module.SummaryMarkdown,
            module.SourceContent,
            historyDtos,
            request.Question.Trim(),
            ct);

        var now = DateTime.UtcNow;
        var userEntity = new QaMessage
        {
            StudyModuleId = module.Id,
            Role = "user",
            Content = request.Question.Trim(),
            CreatedAtUtc = now
        };

        var assistantEntity = new QaMessage
        {
            StudyModuleId = module.Id,
            Role = "assistant",
            Content = answer,
            CreatedAtUtc = now.AddMilliseconds(5)
        };

        db.QaMessages.AddRange(userEntity, assistantEntity);
        module.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return new AskQuestionResponse(
            new QaMessageDto(userEntity.Id, userEntity.StudyModuleId, userEntity.Role, userEntity.Content, userEntity.CreatedAtUtc),
            new QaMessageDto(assistantEntity.Id, assistantEntity.StudyModuleId, assistantEntity.Role, assistantEntity.Content, assistantEntity.CreatedAtUtc));
    }

    private static StudyModuleSummaryDto MapSummaryDto(StudyModule m)
    {
        int? bestScore = null;
        var attempts = m.Quizzes.SelectMany(q => q.Attempts).ToList();
        if (attempts.Count > 0)
        {
            bestScore = attempts.Max(a => a.TotalQuestions > 0 ? (int)Math.Round(100.0 * a.Score / a.TotalQuestions) : 0);
        }

        return new StudyModuleSummaryDto(
            m.Id,
            m.Title,
            m.RequestedTopic,
            m.CertificationCode,
            m.FolderId,
            m.Folder?.Name,
            m.CreatedAtUtc,
            m.Quizzes.Count,
            bestScore,
            m.QaMessages.Count(msg => msg.Role == "user"));
    }

    private static StudyModuleDetailDto MapDetailDto(StudyModule m)
    {
        var sources = DeserializeOrDefault<List<StudyModuleSourceDto>>(m.SourcesJson) ?? [];
        var quizzes = m.Quizzes
            .OrderBy(q => q.CreatedAtUtc)
            .Select(MapQuizDto)
            .ToList();

        var qa = m.QaMessages
            .OrderBy(msg => msg.CreatedAtUtc)
            .Select(msg => new QaMessageDto(msg.Id, msg.StudyModuleId, msg.Role, msg.Content, msg.CreatedAtUtc))
            .ToList();

        return new StudyModuleDetailDto(
            m.Id,
            m.Title,
            m.RequestedTopic,
            m.CertificationCode,
            m.FolderId,
            m.Folder?.Name,
            m.SummaryMarkdown,
            sources,
            m.GeneratedWithFoundryAi,
            m.RetrievedViaMcp,
            m.CreatedAtUtc,
            m.UpdatedAtUtc,
            quizzes,
            qa);
    }

    private static QuizDto MapQuizDto(StudyQuiz q)
    {
        var questions = q.Questions
            .OrderBy(qq => qq.OrderIndex)
            .Select(qq => new QuizQuestionDto(
                qq.Id,
                qq.OrderIndex,
                qq.QuestionText,
                DeserializeOrDefault<List<string>>(qq.OptionsJson) ?? [],
                qq.CorrectOptionIndex,
                qq.Explanation))
            .ToList();

        var attempts = q.Attempts
            .OrderByDescending(a => a.CompletedAtUtc)
            .Select(MapAttemptDto)
            .ToList();

        return new QuizDto(
            q.Id,
            q.StudyModuleId,
            q.Title,
            q.Difficulty,
            questions.Count,
            q.CreatedAtUtc,
            questions,
            attempts);
    }

    private static QuizAttemptDto MapAttemptDto(QuizAttempt a)
    {
        var pct = a.TotalQuestions > 0 ? (int)Math.Round(100.0 * a.Score / a.TotalQuestions) : 0;
        var selected = DeserializeOrDefault<List<int>>(a.SelectedOptionIndicesJson) ?? [];
        return new QuizAttemptDto(a.Id, a.StudyQuizId, a.Score, a.TotalQuestions, pct, a.CompletedAtUtc, selected);
    }

    private static T? DeserializeOrDefault<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch
        {
            return default;
        }
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
}
