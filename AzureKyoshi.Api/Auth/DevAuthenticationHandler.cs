using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AzureKyoshi.Api.Auth;

public sealed class AuthConfigurationOptions
{
    public const string SectionName = "Authentication";

    public bool UseDevMode { get; set; } = false;
    public string DevUserId { get; set; } = "dev-local-user";
    public string DevUserName { get; set; } = "Local Azure Learner";
    public string DevUserEmail { get; set; } = "learner@azurekyoshi.local";
}

public interface ICurrentUserService
{
    string UserId { get; }
    string DisplayName { get; }
    string Email { get; }
    void SetUser(ClaimsPrincipal principal);
}

public sealed class CurrentUserService(
    IOptions<AuthConfigurationOptions> authOptions,
    IHostEnvironment hostEnvironment) : ICurrentUserService
{
    private ClaimsPrincipal? _principal;

    public void SetUser(ClaimsPrincipal principal) => _principal = principal;

    public string UserId
    {
        get
        {
            var id = _principal?.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier")
                ?? _principal?.FindFirstValue("oid")
                ?? _principal?.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? _principal?.FindFirstValue("sub");

            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }

            if (hostEnvironment.IsDevelopment() && authOptions.Value.UseDevMode)
            {
                return authOptions.Value.DevUserId;
            }

            throw new UnauthorizedAccessException("Authenticated user identity claim is missing.");
        }
    }

    public string DisplayName =>
        _principal?.FindFirstValue("name")
        ?? _principal?.FindFirstValue(ClaimTypes.Name)
        ?? (hostEnvironment.IsDevelopment() ? authOptions.Value.DevUserName : "Azure User");

    public string Email =>
        _principal?.FindFirstValue("preferred_username")
        ?? _principal?.FindFirstValue(ClaimTypes.Email)
        ?? (hostEnvironment.IsDevelopment() ? authOptions.Value.DevUserEmail : string.Empty);
}

public sealed class EntraJwtAuthenticationMiddleware(
    IOptions<AuthConfigurationOptions> authOptions,
    IHostEnvironment hostEnvironment,
    IConfiguration configuration,
    ILogger<EntraJwtAuthenticationMiddleware> logger)
    : IFunctionsWorkerMiddleware
{
    private const string DefaultRequiredScope = "access_as_user";
    private readonly JsonWebTokenHandler _tokenHandler = new();
    private ConfigurationManager<OpenIdConnectConfiguration>? _oidcConfigManager;
    private readonly object _configLock = new();

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext is null)
        {
            await next(context);
            return;
        }

        // Allow CORS preflight requests through untouched
        if (HttpMethods.IsOptions(httpContext.Request.Method))
        {
            await next(context);
            return;
        }

        var currentUser = context.InstanceServices.GetService(typeof(ICurrentUserService)) as ICurrentUserService;
        var cfg = authOptions.Value;

        if (cfg.UseDevMode)
        {
            if (hostEnvironment.IsDevelopment())
            {
                var devIdentity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, cfg.DevUserId),
                    new Claim("oid", cfg.DevUserId),
                    new Claim(ClaimTypes.Name, cfg.DevUserName),
                    new Claim("name", cfg.DevUserName),
                    new Claim(ClaimTypes.Email, cfg.DevUserEmail),
                    new Claim("preferred_username", cfg.DevUserEmail)
                ], "DevMode");

                var devPrincipal = new ClaimsPrincipal(devIdentity);
                httpContext.User = devPrincipal;
                currentUser?.SetUser(devPrincipal);

                await next(context);
                return;
            }

            // Fail closed: never allow DevMode bypass outside Development
            logger.LogCritical(
                "Authentication:UseDevMode is true in environment '{Environment}'. Ignoring DevMode and enforcing Microsoft Entra ID authentication.",
                hostEnvironment.EnvironmentName);
        }

        // Validate Microsoft Entra ID Bearer token
        var authHeader = httpContext.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await httpContext.Response.WriteAsync("Missing Bearer authorization token.");
            return;
        }

        var token = authHeader["Bearer ".Length..].Trim();
        var validation = await ValidateEntraTokenAsync(token);
        if (validation.Principal is null)
        {
            httpContext.Response.StatusCode = validation.StatusCode;
            await httpContext.Response.WriteAsync(validation.ErrorMessage);
            return;
        }

        httpContext.User = validation.Principal;
        currentUser?.SetUser(validation.Principal);

        await ExecuteWithErrorHandlingAsync(httpContext, context, next);
    }

    private async Task ExecuteWithErrorHandlingAsync(
        HttpContext httpContext,
        FunctionContext context,
        FunctionExecutionDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception executing function '{FunctionName}'.", context.FunctionDefinition.Name);
            if (!httpContext.Response.HasStarted)
            {
                httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
                httpContext.Response.ContentType = "text/plain; charset=utf-8";
                var detail = hostEnvironment.IsDevelopment()
                    ? $"{ex.GetType().Name}: {ex.Message}"
                    : "An internal server error occurred. Check Application Insights logs for details.";
                await httpContext.Response.WriteAsync(detail);
            }
        }
    }

    private async Task<TokenValidationOutcome> ValidateEntraTokenAsync(string token)
    {
        var tenantId = configuration["AzureAd:TenantId"];
        var clientId = configuration["AzureAd:ClientId"];
        var audience = configuration["AzureAd:Audience"];
        var requiredScope = configuration["AzureAd:RequiredScope"] ?? DefaultRequiredScope;
        var instance = (configuration["AzureAd:Instance"] ?? "https://login.microsoftonline.com/").TrimEnd('/');

        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId))
        {
            logger.LogError("AzureAd:TenantId or AzureAd:ClientId is not configured while Authentication:UseDevMode is false.");
            return TokenValidationOutcome.Unauthorized("Server authentication is not configured.");
        }

        try
        {
            var configManager = GetOrCreateOidcManager($"{instance}/{tenantId}/v2.0/.well-known/openid-configuration");
            var oidcConfig = await configManager.GetConfigurationAsync(CancellationToken.None);

            var validAudiences = new List<string> { clientId };
            if (!string.IsNullOrWhiteSpace(audience))
            {
                validAudiences.Add(audience);
            }

            var validationParams = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuers =
                [
                    $"{instance}/{tenantId}/v2.0",
                    $"https://sts.windows.net/{tenantId}/"
                ],
                ValidateAudience = true,
                ValidAudiences = validAudiences,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = oidcConfig.SigningKeys,
                ValidateLifetime = true
            };

            var result = await _tokenHandler.ValidateTokenAsync(token, validationParams);
            if (!result.IsValid || result.ClaimsIdentity is null)
            {
                logger.LogWarning(result.Exception, "Entra ID JWT validation failed.");
                return TokenValidationOutcome.Unauthorized("Invalid or expired Microsoft Entra ID token.");
            }

            var principal = new ClaimsPrincipal(result.ClaimsIdentity);

            // Enforce non-empty user subject / object identifier claim
            var userId = principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier")
                         ?? principal.FindFirstValue("oid")
                         ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
                         ?? principal.FindFirstValue("sub");

            if (string.IsNullOrWhiteSpace(userId))
            {
                logger.LogWarning("Entra ID JWT is missing user identity claim (oid/sub).");
                return TokenValidationOutcome.Unauthorized("Token is missing a user identity claim.");
            }

            // Enforce delegated API scope (scp)
            if (!string.IsNullOrWhiteSpace(requiredScope))
            {
                var scopeClaim = principal.FindFirstValue("scp")
                                 ?? principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/scope")
                                 ?? string.Empty;

                var scopes = scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (!scopes.Contains(requiredScope, StringComparer.OrdinalIgnoreCase))
                {
                    logger.LogWarning("Entra ID JWT missing required scope '{Scope}'.", requiredScope);
                    return TokenValidationOutcome.Forbidden($"Token is missing required scope '{requiredScope}'.");
                }
            }

            return TokenValidationOutcome.Success(principal);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error validating Entra ID JWT token.");
            return TokenValidationOutcome.Unauthorized("Invalid or expired Microsoft Entra ID token.");
        }
    }

    private ConfigurationManager<OpenIdConnectConfiguration> GetOrCreateOidcManager(string metadataAddress)
    {
        if (_oidcConfigManager is not null)
        {
            return _oidcConfigManager;
        }

        lock (_configLock)
        {
            _oidcConfigManager ??= new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever());
            return _oidcConfigManager;
        }
    }

    private sealed record TokenValidationOutcome(ClaimsPrincipal? Principal, int StatusCode, string ErrorMessage)
    {
        public static TokenValidationOutcome Success(ClaimsPrincipal principal) => new(principal, StatusCodes.Status200OK, string.Empty);
        public static TokenValidationOutcome Unauthorized(string message) => new(null, StatusCodes.Status401Unauthorized, message);
        public static TokenValidationOutcome Forbidden(string message) => new(null, StatusCodes.Status403Forbidden, message);
    }
}
