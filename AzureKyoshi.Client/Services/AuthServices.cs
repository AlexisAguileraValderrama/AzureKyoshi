using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace AzureKyoshi.Client.Services;

public sealed record ClientAuthConfig(bool UseDevMode);

public sealed class DevAuthStateProvider : AuthenticationStateProvider
{
    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "Local Azure Learner"),
            new Claim(ClaimTypes.Email, "learner@azurekyoshi.local")
        ], "DevMode");

        return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
    }
}

public sealed class CustomApiAuthorizationMessageHandler : AuthorizationMessageHandler
{
    public CustomApiAuthorizationMessageHandler(
        IAccessTokenProvider provider,
        NavigationManager navigation,
        IConfiguration configuration)
        : base(provider, navigation)
    {
        var apiBase = configuration["ApiBaseUrl"];
        var authorizedUrl = string.IsNullOrWhiteSpace(apiBase) ? navigation.BaseUri : apiBase;
        var scopes = configuration.GetSection("ApiScopes").Get<string[]>() ?? [];

        ConfigureHandler(
            authorizedUrls: [authorizedUrl],
            scopes: scopes);
    }
}
