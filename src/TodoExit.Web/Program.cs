using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Logging;
using TodoExit.Web;
using TodoExit.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The default level (Information) prints every Graph request URL, with list and task IDs, to the browser console.
builder.Logging.SetMinimumLevel(LogLevel.Warning);

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

// Todoist client: a separate client without AuthorizationMessageHandler, so the Microsoft token never reaches Todoist.
// TodoistClient adds the Todoist token itself, for api.todoist.com only. One request of 100 commands takes 12 to 19 s.
builder.Services.AddTransient<TodoistFetchHandler>();
builder.Services.AddHttpClient(AppSettings.TodoistClientName, client => client.Timeout = TimeSpan.FromMinutes(3))
    .AddHttpMessageHandler<TodoistFetchHandler>();

await builder.Build().RunAsync();
