using System.Globalization;
using System.Text.Json;

namespace TodoExit.Cli.Auth;

/// <summary>Where and as whom to sign in. The endpoints are injectable for tests.</summary>
internal sealed record SignInOptions(string ClientId, string Tenant = "common", string AuthorityBase = SignInOptions.DefaultAuthorityBase)
{
    public const string DefaultAuthorityBase = "https://login.microsoftonline.com/";

    /// <summary>The todo-exit app registration (multi-tenant plus personal accounts, delegated Tasks.Read).</summary>
    public const string DefaultClientId = "a9d144de-2371-43eb-b285-c98db87814cb";

    public const string Scope = "https://graph.microsoft.com/Tasks.Read offline_access";

    public Uri DeviceCodeUri => new($"{AuthorityBase.TrimEnd('/')}/{Uri.EscapeDataString(Tenant)}/oauth2/v2.0/devicecode");

    public Uri TokenUri => new($"{AuthorityBase.TrimEnd('/')}/{Uri.EscapeDataString(Tenant)}/oauth2/v2.0/token");
}

/// <summary>Tokens kept in memory only. Never written to disk or printed.</summary>
internal sealed record TokenSet(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt)
{
    /// <summary>Avoid leaking tokens through logging or debugging output.</summary>
    public override string ToString() => $"TokenSet(expires {ExpiresAt:u})";
}

/// <summary>Sign-in failed. The message is safe to print: it never contains a token or a device code.</summary>
internal sealed class SignInException(string message) : Exception(message);

/// <summary>
/// OAuth 2.0 device authorization grant against the Microsoft identity platform, with plain HttpClient.
/// The user opens a URL, enters a code and signs in; the CLI polls the token endpoint meanwhile.
/// </summary>
internal sealed class DeviceCodeSignIn(
    HttpClient http,
    SignInOptions options,
    Func<TimeSpan, CancellationToken, Task> delay,
    Func<DateTimeOffset> clock)
{
    public const string PublicClientHint =
        "This app registration does not allow sign-in from a command-line tool. In Microsoft Entra, open the registration, " +
        "go to Authentication and set 'Allow public client flows' to Yes.";

    private const string DeviceCodeGrant = "urn:ietf:params:oauth:grant-type:device_code";

    /// <param name="showMessage">Receives Microsoft's instruction text (URL and code) for the user.</param>
    public async Task<TokenSet> SignInAsync(Action<string> showMessage, CancellationToken cancellationToken)
    {
        var (status, body) = await PostAsync(options.DeviceCodeUri, new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId,
            ["scope"] = SignInOptions.Scope,
        }, cancellationToken).ConfigureAwait(false);

        if (status is < 200 or >= 300 || body is null || Str(body.Value, "device_code") is not { } deviceCode)
        {
            throw Failure(body, "Microsoft did not start the sign-in");
        }

        var root = body.Value;
        showMessage(Str(root, "message")
            ?? $"To sign in, open {Str(root, "verification_uri")} and enter the code {Str(root, "user_code")}.");

        var interval = TimeSpan.FromSeconds(Math.Max(1, Int(root, "interval") ?? 5));
        var expiresAt = clock() + TimeSpan.FromSeconds(Int(root, "expires_in") ?? 900);

        while (true)
        {
            await delay(interval, cancellationToken).ConfigureAwait(false);
            if (clock() >= expiresAt)
            {
                throw new SignInException("The sign-in code expired before it was used. Run the command again.");
            }

            var (tokenStatus, token) = await PostAsync(options.TokenUri, new Dictionary<string, string>
            {
                ["grant_type"] = DeviceCodeGrant,
                ["client_id"] = options.ClientId,
                ["device_code"] = deviceCode,
            }, cancellationToken).ConfigureAwait(false);

            if (tokenStatus is >= 200 and < 300 && token is { } ok && ReadTokens(ok, previousRefreshToken: null) is { } tokens)
            {
                return tokens;
            }

            switch (token is { } t ? Str(t, "error") : null)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "authorization_declined":
                    throw new SignInException("Sign-in was declined. Run the command again to try once more.");
                case "expired_token":
                    throw new SignInException("The sign-in code expired before it was used. Run the command again.");
                case "bad_verification_code":
                    throw new SignInException("Microsoft did not recognize the sign-in code. Run the command again.");
                default:
                    throw Failure(token, "Sign-in failed");
            }
        }
    }

    /// <summary>Gets a new access token with the refresh token. Throws <see cref="SignInException"/> when that is refused.</summary>
    public async Task<TokenSet> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var (status, body) = await PostAsync(options.TokenUri, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = options.ClientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = SignInOptions.Scope,
        }, cancellationToken).ConfigureAwait(false);

        if (status is >= 200 and < 300 && body is { } ok && ReadTokens(ok, refreshToken) is { } tokens)
        {
            return tokens;
        }

        throw Failure(body, "Refreshing the sign-in failed");
    }

    private TokenSet? ReadTokens(JsonElement root, string? previousRefreshToken)
    {
        if (Str(root, "access_token") is not { Length: > 0 } access)
        {
            return null;
        }

        var lifetime = TimeSpan.FromSeconds(Int(root, "expires_in") ?? 3600);
        return new TokenSet(access, Str(root, "refresh_token") ?? previousRefreshToken, clock() + lifetime);
    }

    /// <summary>The public-client hint for invalid_client / AADSTS70002 / AADSTS7000218, else error and first description line.</summary>
    internal static SignInException Failure(JsonElement? body, string what)
    {
        if (body is not { } root || root.ValueKind != JsonValueKind.Object)
        {
            return new SignInException($"{what}: Microsoft returned an unexpected answer.");
        }

        var error = Str(root, "error");
        var description = Str(root, "error_description");
        var codes = new List<long>();
        if (root.TryGetProperty("error_codes", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var code))
                {
                    codes.Add(code);
                }
            }
        }

        if (error == "invalid_client"
            || codes.Contains(70002) || codes.Contains(7000218)
            || (description?.Contains("AADSTS70002", StringComparison.Ordinal) ?? false)
            || (description?.Contains("AADSTS7000218", StringComparison.Ordinal) ?? false))
        {
            return new SignInException(PublicClientHint);
        }

        var firstLine = description?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var detail = string.Join(": ", new[] { error, firstLine }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return new SignInException(detail.Length > 0 ? $"{what}: {detail}" : $"{what}.");
    }

    private async Task<(int Status, JsonElement? Body)> PostAsync(
        Uri uri, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(form) };
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(text);
            return ((int)response.StatusCode, doc.RootElement.Clone());
        }
        catch (JsonException)
        {
            return ((int)response.StatusCode, null);
        }
    }

    private static string? Str(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? Int(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var v))
        {
            return null;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
        {
            return n;
        }

        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null;
    }
}
