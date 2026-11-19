using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TodoExit.Core.Graph;

namespace TodoExit.Cli.Auth;

/// <summary>
/// Attaches the bearer token to requests for https://graph.microsoft.com/ only. Refreshes the token shortly before
/// it expires, and once more after a Graph 401 that is a token problem, retrying that request once.
/// Tokens stay in memory.
/// </summary>
internal sealed class GraphAuthHandler : DelegatingHandler
{
    /// <summary>Refresh this long before the access token expires.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly Func<string, CancellationToken, Task<TokenSet>> _refresh;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TokenSet _tokens;

    public GraphAuthHandler(TokenSet tokens, Func<string, CancellationToken, Task<TokenSet>> refresh, Func<DateTimeOffset> clock)
    {
        _tokens = tokens;
        _refresh = refresh;
        _clock = clock;
    }

    /// <summary>Set when a refresh was refused; the next Graph 401 then ends the read.</summary>
    public string? RefreshFailure { get; private set; }

    public int Refreshes { get; private set; }

    public static bool IsGraph(Uri? uri) =>
        uri is not null
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, "graph.microsoft.com", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!IsGraph(request.RequestUri))
        {
            request.Headers.Authorization = null; // never send the token anywhere else
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (_tokens.ExpiresAt - _clock() <= RefreshMargin)
        {
            await TryRefreshAsync(_tokens.AccessToken, cancellationToken).ConfigureAwait(false);
        }

        // Keep the body so the request can be sent a second time after a refresh.
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var used = _tokens.AccessToken;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", used);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized || !await IsTokenProblemAsync(response, cancellationToken).ConfigureAwait(false))
        {
            return response;
        }

        if (!await TryRefreshAsync(used, cancellationToken).ConfigureAwait(false))
        {
            return response; // the reader treats this 401 as fatal and saves a partial archive
        }

        response.Dispose();
        using var retry = Clone(request, body);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens.AccessToken);
        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Refreshes unless another request already did so after <paramref name="staleToken"/> was used.</summary>
    private async Task<bool> TryRefreshAsync(string staleToken, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tokens.AccessToken != staleToken)
            {
                return true;
            }

            if (_tokens.RefreshToken is not { } refreshToken || RefreshFailure is not null)
            {
                return false;
            }

            try
            {
                _tokens = await _refresh(refreshToken, cancellationToken).ConfigureAwait(false);
                Refreshes++;
                return true;
            }
            catch (Exception ex) when (ex is SignInException or HttpRequestException or IOException)
            {
                RefreshFailure = ex.Message;
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<bool> IsTokenProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        string? code = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
            {
                code = c.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return GraphRequestException.IsTokenProblemFor(response.StatusCode, code);
    }

    private static HttpRequestMessage Clone(HttpRequestMessage original, byte[]? body)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri) { Version = original.Version };
        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (body is not null)
        {
            clone.Content = new ByteArrayContent(body);
            if (original.Content is not null)
            {
                foreach (var header in original.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        return clone;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _gate.Dispose();
        }

        base.Dispose(disposing);
    }
}
