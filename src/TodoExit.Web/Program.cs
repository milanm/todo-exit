using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using TodoExit.Web;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Plain client for files served with the app (the sample archive).
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.Services.AddSingleton<AppSettings>(_ => AppSettings.From(builder.Configuration));

builder.Services.AddMsalAuthentication(options =>
{
    // Read values explicitly instead of reflection-based binding, which does not survive trimming well.
    var auth = options.ProviderOptions.Authentication;
    auth.Authority = builder.Configuration["AzureAd:Authority"] ?? "https://login.microsoftonline.com/common";
    auth.ClientId = builder.Configuration["AzureAd:ClientId"] ?? string.Empty;
    auth.ValidateAuthority = !bool.TryParse(builder.Configuration["AzureAd:ValidateAuthority"], out var validate) || validate;

    options.ProviderOptions.DefaultAccessTokenScopes.Add(AppSettings.GraphScope);
    options.ProviderOptions.LoginMode = "redirect";
});

// Graph client: AuthorizationMessageHandler attaches the token for graph.microsoft.com only.
builder.Services.AddHttpClient(AppSettings.GraphClientName, client => client.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"))
    .AddHttpMessageHandler(sp => sp.GetRequiredService<AuthorizationMessageHandler>()
        .ConfigureHandler(
            authorizedUrls: ["https://graph.microsoft.com"],
            scopes: [AppSettings.GraphScope]));

await builder.Build().RunAsync();
