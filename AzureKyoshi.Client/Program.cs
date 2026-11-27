using AzureKyoshi.Client;
using AzureKyoshi.Client.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var useDevMode = builder.Configuration.GetValue<bool>("Authentication:UseDevMode");
var configuredApiBase = builder.Configuration["ApiBaseUrl"];
var apiBaseAddress = string.IsNullOrWhiteSpace(configuredApiBase)
    ? new Uri(builder.HostEnvironment.BaseAddress)
    : new Uri(configuredApiBase);

builder.Services.AddSingleton(new ClientAuthConfig(useDevMode));
builder.Services.AddSingleton<MarkdownRenderer>();

if (useDevMode)
{
    builder.Services.AddAuthorizationCore();
    builder.Services.AddScoped<AuthenticationStateProvider, DevAuthStateProvider>();
    builder.Services.AddHttpClient<StudyApiClient>(client =>
    {
        client.BaseAddress = apiBaseAddress;
        client.Timeout = TimeSpan.FromMinutes(2);
    });
}
else
{
    var apiScopes = builder.Configuration.GetSection("ApiScopes").Get<string[]>() ?? [];

    builder.Services.AddMsalAuthentication(options =>
    {
        builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
        foreach (var scope in apiScopes)
        {
            options.ProviderOptions.DefaultAccessTokenScopes.Add(scope);
        }
    });

    builder.Services.AddTransient<CustomApiAuthorizationMessageHandler>();
    builder.Services.AddHttpClient<StudyApiClient>(client =>
    {
        client.BaseAddress = apiBaseAddress;
        client.Timeout = TimeSpan.FromMinutes(4);
    }).AddHttpMessageHandler<CustomApiAuthorizationMessageHandler>();
}

await builder.Build().RunAsync();
