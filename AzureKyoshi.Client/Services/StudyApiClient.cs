using System.Net.Http.Json;
using AzureKyoshi.Shared.Models;

namespace AzureKyoshi.Client.Services;

public sealed class StudyApiClient(HttpClient http)
{
    public async Task<AppRuntimeStatusDto?> GetStatusAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<AppRuntimeStatusDto>("api/status", ct);

    public async Task<List<FolderDto>> GetFoldersAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<FolderDto>>("api/folders", ct) ?? [];

    public async Task<FolderDto> CreateFolderAsync(CreateFolderRequest request, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/folders", request, ct);
        await EnsureSuccessWithMessageAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<FolderDto>(ct))!;
    }

    public async Task<FolderDto> UpdateFolderAsync(Guid id, UpdateFolderRequest request, CancellationToken ct = default)
    {
        var response = await http.PutAsJsonAsync($"api/folders/{id}", request, ct);
        await EnsureSuccessWithMessageAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<FolderDto>(ct))!;
    }

    public async Task DeleteFolderAsync(Guid id, CancellationToken ct = default)
    {
        var response = await http.DeleteAsync($"api/folders/{id}", ct);
        await EnsureSuccessWithMessageAsync(response, ct);
    }

    public async Task<List<StudyModuleSummaryDto>> GetModulesAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<StudyModuleSummaryDto>>("api/modules", ct) ?? [];

    public async Task<StudyModuleDetailDto?> GetModuleDetailAsync(Guid id, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<StudyModuleDetailDto>($"api/modules/{id}", ct);

    public async Task<StudyModuleDetailDto> CreateModuleAsync(CreateStudyModuleRequest request, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/modules", request, ct);
        await EnsureSuccessWithMessageAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<StudyModuleDetailDto>(ct))!;
    }

    public async Task<StudyModuleSummaryDto> MoveModuleAsync(Guid moduleId, Guid? folderId, CancellationToken ct = default)
    {
        var response = await http.PatchAsJsonAsync($"api/modules/{moduleId}/folder", new MoveModuleRequest { FolderId = folderId }, ct);
        await EnsureSuccessWithMessageAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<StudyModuleSummaryDto>(ct))!;
    }

    public async Task<StudyModuleDetailDto> RegenerateSummaryAsync(Guid moduleId, string? focusInstructions, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync(
            $"api/modules/{moduleId}/summary/regenerate",
            new RegenerateSummaryRequest { FocusInstructions = focusInstructions },
            ct);
        await EnsureSuccessWithMessageAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<StudyModuleDetailDto>(ct))!;
    }

    public async Task DeleteModuleAsync(Guid moduleId, CancellationToken ct = default)
    {
        var response = await http.DeleteAsync($"api/modules/{moduleId}", ct);
        await EnsureSuccessWithMessageAsync(response, ct);
    }

    public async Task<QuizDto> GenerateQuizAsync(Guid moduleId, GenerateQuizRequest request, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync($"api/modules/{moduleId}/quizzes", request, ct);
        await EnsureSuccessWithMessageAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<QuizDto>(ct))!;
    }

    public async Task<QuizAttemptDto> SubmitQuizAttemptAsync(Guid quizId, List<int> selectedIndices, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync(
            $"api/quizzes/{quizId}/attempts",
            new SubmitQuizAttemptRequest { SelectedOptionIndices = selectedIndices },
            ct);
        await EnsureSuccessWithMessageAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<QuizAttemptDto>(ct))!;
    }

    public async Task<AskQuestionResponse> AskQuestionAsync(Guid moduleId, string question, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync(
            $"api/modules/{moduleId}/qa",
            new AskQuestionRequest { Question = question },
            ct);
        await EnsureSuccessWithMessageAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<AskQuestionResponse>(ct))!;
    }

    private static async Task EnsureSuccessWithMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        var message = string.IsNullOrWhiteSpace(body)
            ? $"Request failed ({(int)response.StatusCode} {response.ReasonPhrase})"
            : body;

        throw new InvalidOperationException(message);
    }
}
