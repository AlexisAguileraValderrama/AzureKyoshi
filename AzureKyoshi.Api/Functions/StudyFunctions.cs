using System.Text.Json;
using AzureKyoshi.Api.Auth;
using AzureKyoshi.Api.Options;
using AzureKyoshi.Api.Services;
using AzureKyoshi.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Options;

namespace AzureKyoshi.Api.Functions;

public sealed record RuntimeDatabaseInfo(string ProviderLabel);

public sealed class StudyFunctions(
    ICurrentUserService currentUser,
    IStudyWorkflowService workflowService,
    IAiStudyService aiService,
    IOptions<AuthConfigurationOptions> authOptions,
    IOptions<MicrosoftLearnMcpOptions> mcpOptions,
    RuntimeDatabaseInfo dbInfo)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetStatus")]
    public IActionResult GetStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "status")] HttpRequest req)
    {
        var status = new AppRuntimeStatusDto(
            AiMode: aiService.IsFoundryConfigured ? "Azure AI Foundry" : "Local Demo AI",
            AiDeploymentName: aiService.IsFoundryConfigured ? aiService.DeploymentName : "Demo Generator",
            DatabaseProvider: dbInfo.ProviderLabel,
            AuthMode: authOptions.Value.UseDevMode ? "Local Dev Mode" : "Microsoft Entra ID",
            CurrentUserName: currentUser.DisplayName,
            CurrentUserEmail: currentUser.Email,
            McpEndpoint: mcpOptions.Value.Endpoint);

        return new OkObjectResult(status);
    }

    // --- Folders ---

    [Function("GetFolders")]
    public async Task<IActionResult> GetFolders(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "folders")] HttpRequest req,
        CancellationToken ct) =>
        new OkObjectResult(await workflowService.GetFoldersAsync(currentUser.UserId, ct));

    [Function("CreateFolder")]
    public async Task<IActionResult> CreateFolder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "folders")] HttpRequest req,
        CancellationToken ct)
    {
        var body = await ReadJsonAsync<CreateFolderRequest>(req, ct);
        if (body is null || string.IsNullOrWhiteSpace(body.Name))
        {
            return new BadRequestObjectResult("Folder name is required.");
        }

        var created = await workflowService.CreateFolderAsync(currentUser.UserId, body, ct);
        return new CreatedResult($"/api/folders/{created.Id}", created);
    }

    [Function("UpdateFolder")]
    public async Task<IActionResult> UpdateFolder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "folders/{id:guid}")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        var body = await ReadJsonAsync<UpdateFolderRequest>(req, ct);
        if (body is null || string.IsNullOrWhiteSpace(body.Name))
        {
            return new BadRequestObjectResult("Folder name is required.");
        }

        var updated = await workflowService.UpdateFolderAsync(currentUser.UserId, id, body, ct);
        return updated is null ? new NotFoundResult() : new OkObjectResult(updated);
    }

    [Function("DeleteFolder")]
    public async Task<IActionResult> DeleteFolder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "folders/{id:guid}")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        var deleted = await workflowService.DeleteFolderAsync(currentUser.UserId, id, ct);
        return deleted ? new NoContentResult() : new NotFoundResult();
    }

    // --- Study Modules ---

    [Function("GetModules")]
    public async Task<IActionResult> GetModules(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "modules")] HttpRequest req,
        CancellationToken ct) =>
        new OkObjectResult(await workflowService.GetModulesAsync(currentUser.UserId, ct));

    [Function("GetModuleDetail")]
    public async Task<IActionResult> GetModuleDetail(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "modules/{id:guid}")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        var detail = await workflowService.GetModuleDetailAsync(currentUser.UserId, id, ct);
        return detail is null ? new NotFoundResult() : new OkObjectResult(detail);
    }

    [Function("CreateModule")]
    public async Task<IActionResult> CreateModule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "modules")] HttpRequest req,
        CancellationToken ct)
    {
        var body = await ReadJsonAsync<CreateStudyModuleRequest>(req, ct);
        if (body is null || string.IsNullOrWhiteSpace(body.ModuleName))
        {
            return new BadRequestObjectResult("Please specify an Azure module name to study.");
        }

        var created = await workflowService.CreateStudyModuleAsync(currentUser.UserId, body, ct);
        return new CreatedResult($"/api/modules/{created.Id}", created);
    }

    [Function("MoveModule")]
    public async Task<IActionResult> MoveModule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "modules/{id:guid}/folder")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        var body = await ReadJsonAsync<MoveModuleRequest>(req, ct) ?? new MoveModuleRequest();
        var updated = await workflowService.MoveModuleAsync(currentUser.UserId, id, body.FolderId, ct);
        return updated is null ? new NotFoundResult() : new OkObjectResult(updated);
    }

    [Function("RegenerateSummary")]
    public async Task<IActionResult> RegenerateSummary(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "modules/{id:guid}/summary/regenerate")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        var body = await ReadJsonAsync<RegenerateSummaryRequest>(req, ct) ?? new RegenerateSummaryRequest();
        var updated = await workflowService.RegenerateSummaryAsync(currentUser.UserId, id, body, ct);
        return updated is null ? new NotFoundResult() : new OkObjectResult(updated);
    }

    [Function("DeleteModule")]
    public async Task<IActionResult> DeleteModule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "modules/{id:guid}")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        var deleted = await workflowService.DeleteModuleAsync(currentUser.UserId, id, ct);
        return deleted ? new NoContentResult() : new NotFoundResult();
    }

    // --- Quizzes ---

    [Function("GenerateQuiz")]
    public async Task<IActionResult> GenerateQuiz(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "modules/{id:guid}/quizzes")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        var body = await ReadJsonAsync<GenerateQuizRequest>(req, ct) ?? new GenerateQuizRequest();
        var quiz = await workflowService.GenerateQuizAsync(currentUser.UserId, id, body, ct);
        return quiz is null ? new NotFoundResult() : new CreatedResult($"/api/quizzes/{quiz.Id}", quiz);
    }

    [Function("SubmitQuizAttempt")]
    public async Task<IActionResult> SubmitQuizAttempt(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "quizzes/{quizId:guid}/attempts")] HttpRequest req,
        Guid quizId,
        CancellationToken ct)
    {
        var body = await ReadJsonAsync<SubmitQuizAttemptRequest>(req, ct);
        if (body is null)
        {
            return new BadRequestObjectResult("Selected option indices are required.");
        }

        var attempt = await workflowService.SubmitQuizAttemptAsync(currentUser.UserId, quizId, body, ct);
        return attempt is null ? new NotFoundResult() : new OkObjectResult(attempt);
    }

    // --- Q&A Tutor ---

    [Function("AskQuestion")]
    public async Task<IActionResult> AskQuestion(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "modules/{id:guid}/qa")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        var body = await ReadJsonAsync<AskQuestionRequest>(req, ct);
        if (body is null || string.IsNullOrWhiteSpace(body.Question))
        {
            return new BadRequestObjectResult("Please enter a question.");
        }

        var response = await workflowService.AskQuestionAsync(currentUser.UserId, id, body, ct);
        return response is null ? new NotFoundResult() : new OkObjectResult(response);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpRequest req, CancellationToken ct)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(req.Body, JsonOptions, ct);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
