using Microsoft.Extensions.Configuration;

namespace TodoExit.Web;

public sealed class AppSettings
{
    public const string GraphScope = "https://graph.microsoft.com/Tasks.Read";
    public const string GraphClientName = "Graph";
    public const string RepositoryUrl = "https://github.com/milanm/todo-exit";

    /// <summary>False while appsettings.json still holds the placeholder client id.</summary>
    public bool SignInConfigured { get; init; }

    public static AppSettings From(IConfiguration configuration)
    {
        var clientId = configuration["AzureAd:ClientId"];
        var configured = Guid.TryParse(clientId, out var id) && id != Guid.Empty;
        return new AppSettings { SignInConfigured = configured };
    }
}
