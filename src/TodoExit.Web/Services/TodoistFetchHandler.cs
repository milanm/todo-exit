using Microsoft.AspNetCore.Components.WebAssembly.Http;
using TodoExit.Core.Todoist;

namespace TodoExit.Web.Services;

/// <summary>
/// The only handler in the Todoist HttpClient, which has no Microsoft sign-in handler. It refuses every address
/// except https://api.todoist.com and sets the browser fetch options: no cookies or other credentials, no
/// redirects followed (a redirect fails the request), no Referer, no HTTP cache.
/// </summary>
public sealed class TodoistFetchHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TodoistClient.IsTodoist(request.RequestUri))
        {
            throw new InvalidOperationException("The Todoist client sends requests to https://api.todoist.com only.");
        }

        request.SetBrowserRequestMode(BrowserRequestMode.Cors);
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Omit);
        request.SetBrowserRequestCache(BrowserRequestCache.NoStore);
        request.SetBrowserRequestOption("redirect", "error");
        request.SetBrowserRequestOption("referrerPolicy", "no-referrer");
        return base.SendAsync(request, cancellationToken);
    }
}
