using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TodoExit.Cli;
using TodoExit.Cli.Auth;

namespace TodoExit.Core.Tests;

/// <summary>A fake Microsoft identity platform in front of FakeGraph.</summary>
internal sealed class FakeLogin : DelegatingHandler
{
    public FakeLogin(HttpMessageHandler graph)
    {
        InnerHandler = graph;
    }

    public (HttpStatusCode Status, string Json) DeviceCode { get; set; } = (HttpStatusCode.OK, """
        { "device_code": "DEVICE-CODE-SECRET", "user_code": "ABCD-1234", "verification_uri": "https://microsoft.com/devicelogin",
          "expires_in": 900, "interval": 5,
          "message": "To sign in, use a web browser to open the page https://microsoft.com/devicelogin and enter the code ABCD-1234 to authenticate." }
        """);

    /// <summary>Answers to device_code polls, in order; the last one repeats.</summary>
    public Queue<(HttpStatusCode Status, string Json)> Polls { get; } = new();

    /// <summary>Answers to refresh_token requests, in order; the last one repeats.</summary>
    public Queue<(HttpStatusCode Status, string Json)> Refreshes { get; } = new();

    public List<(string Url, Dictionary<string, string> Form, string? Authorization)> Requests { get; } = new();

    public static string Token(string access, string refresh, int expiresIn = 3600) =>
        $$"""{ "token_type": "Bearer", "access_token": "{{access}}", "refresh_token": "{{refresh}}", "expires_in": {{expiresIn}} }""";

    public static string Error(string error, string description = "description", string codes = "[]") =>
        $$"""{ "error": "{{error}}", "error_description": "{{description}}", "error_codes": {{codes}} }""";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.Host != "login.microsoftonline.com")
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var form = (await request.Content!.ReadAsStringAsync(cancellationToken))
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
        Requests.Add((request.RequestUri.ToString(), form, request.Headers.Authorization?.ToString()));

        var (status, json) = request.RequestUri.AbsolutePath.EndsWith("/devicecode", StringComparison.Ordinal)
            ? DeviceCode
            : form["grant_type"] == "refresh_token" ? Next(Refreshes) : Next(Polls);
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static (HttpStatusCode, string) Next(Queue<(HttpStatusCode, string)> queue) =>
        queue.Count > 1 ? queue.Dequeue() : queue.Peek();
}

public class SignInTests
{
    private const string Access1 = "eyJ-access-token-one-SECRET";
    private const string Refresh1 = "refresh-token-one-SECRET";
    private const string Access2 = "eyJ-access-token-two-SECRET";
    private const string Refresh2 = "refresh-token-two-SECRET";

    private static readonly (HttpStatusCode, string) Pending = (HttpStatusCode.BadRequest, FakeLogin.Error("authorization_pending"));

    private static string NewDir() => Path.Combine(AppContext.BaseDirectory, "signin-tests", Guid.NewGuid().ToString("N"));

    private static FakeGraph Account()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "Work", 5, i => new JsonObject { ["id"] = $"t{i}", ["title"] = $"Task {i}", ["hasAttachments"] = i == 2 });
        graph.Attachments["t2"] = new JsonArray((JsonNode)new JsonObject { ["id"] = "a", ["name"] = "f.pdf", ["size"] = 1 });
        return graph;
    }

    private sealed record Outcome(int Code, string Out, string Err, List<TimeSpan> Delays);

    private static async Task<Outcome> Run(HttpMessageHandler handler, Func<DateTimeOffset>? clock = null, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var delays = new List<TimeSpan>();
        var code = await Program.RunAsync(args, stdout, stderr, handler, CancellationToken.None,
            (d, _) => { delays.Add(d); return Task.CompletedTask; }, clock: clock);
        return new Outcome(code, stdout.ToString(), stderr.ToString(), delays);
    }

    private static void AssertNoSecrets(Outcome outcome)
    {
        foreach (var secret in new[] { Access1, Refresh1, Access2, Refresh2, "DEVICE-CODE-SECRET" })
        {
            Assert.DoesNotContain(secret, outcome.Out);
            Assert.DoesNotContain(secret, outcome.Err);
        }
    }

    [Fact]
    public async Task Signs_in_with_device_code_and_reads_with_the_token()
    {
        var graph = Account();
        var login = new FakeLogin(graph);
        login.Polls.Enqueue(Pending);
        login.Polls.Enqueue(Pending);
        login.Polls.Enqueue((HttpStatusCode.OK, FakeLogin.Token(Access1, Refresh1)));

        var outcome = await Run(login, null, "export", "--out", NewDir());

        Assert.Equal(Program.ExitOk, outcome.Code);
        Assert.Contains("https://microsoft.com/devicelogin and enter the code ABCD-1234", outcome.Err);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)], outcome.Delays);

        var deviceCode = login.Requests[0];
        Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/devicecode", deviceCode.Url);
        Assert.Equal(SignInOptions.DefaultClientId, deviceCode.Form["client_id"]);
        Assert.Equal("https://graph.microsoft.com/Tasks.Read offline_access", deviceCode.Form["scope"]);
        Assert.All(login.Requests.Skip(1), r =>
        {
            Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/token", r.Url);
            Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", r.Form["grant_type"]);
        });
        Assert.All(login.Requests, r => Assert.Null(r.Authorization));
        Assert.All(graph.Requests, r => Assert.Equal($"Bearer {Access1}", r.Authorization));
        AssertNoSecrets(outcome);
    }

    [Fact]
    public async Task Client_id_and_tenant_options_change_the_sign_in_request()
    {
        var login = new FakeLogin(Account());
        login.Polls.Enqueue((HttpStatusCode.OK, FakeLogin.Token(Access1, Refresh1)));

        var outcome = await Run(login, null, "inventory", "--client-id", "11111111-2222-3333-4444-555555555555", "--tenant", "consumers");

        Assert.Equal(Program.ExitOk, outcome.Code);
        Assert.Equal("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode", login.Requests[0].Url);
        Assert.Equal("11111111-2222-3333-4444-555555555555", login.Requests[0].Form["client_id"]);

        var bad = await Run(login, null, "inventory", "--client-id", "not-a-guid");
        Assert.Equal(Program.ExitUsage, bad.Code);
    }

    [Fact]
    public async Task Slow_down_adds_five_seconds_to_the_interval()
    {
        var login = new FakeLogin(Account());
        login.Polls.Enqueue((HttpStatusCode.BadRequest, FakeLogin.Error("slow_down")));
        login.Polls.Enqueue((HttpStatusCode.OK, FakeLogin.Token(Access1, Refresh1)));

        var outcome = await Run(login, null, "inventory");

        Assert.Equal(Program.ExitOk, outcome.Code);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)], outcome.Delays);
    }

    [Theory]
    [InlineData("authorization_declined", "declined")]
    [InlineData("expired_token", "expired")]
    [InlineData("bad_verification_code", "did not recognize")]
    public async Task Failed_sign_in_exits_2_with_one_clear_line(string error, string expected)
    {
        var login = new FakeLogin(Account());
        login.Polls.Enqueue((HttpStatusCode.BadRequest, FakeLogin.Error(error)));

        var outcome = await Run(login, null, "inventory");

        Assert.Equal(Program.ExitFailure, outcome.Code);
        Assert.Contains(expected, outcome.Err);
        Assert.Empty(((FakeGraph)login.InnerHandler!).Requests);
        AssertNoSecrets(outcome);
    }

    [Fact]
    public async Task Public_client_flows_off_on_devicecode_gives_the_entra_hint()
    {
        var login = new FakeLogin(Account())
        {
            DeviceCode = (HttpStatusCode.BadRequest, FakeLogin.Error("invalid_client",
                "AADSTS70002: The provided client is not supported for this feature. The client application must be marked as 'mobile.'", "[70002]")),
        };

        var outcome = await Run(login, null, "inventory");

        Assert.Equal(Program.ExitFailure, outcome.Code);
        Assert.Contains("set 'Allow public client flows' to Yes", outcome.Err);
        Assert.Single(login.Requests);
    }

    [Fact]
    public async Task AADSTS7000218_on_the_token_poll_gives_the_entra_hint()
    {
        var login = new FakeLogin(Account());
        login.Polls.Enqueue((HttpStatusCode.Unauthorized, FakeLogin.Error("unauthorized_client",
            "AADSTS7000218: The request body must contain the following parameter: 'client_assertion' or 'client_secret'.")));

        var outcome = await Run(login, null, "inventory");

        Assert.Equal(Program.ExitFailure, outcome.Code);
        Assert.Contains("Allow public client flows", outcome.Err);
    }

    [Fact]
    public async Task Other_devicecode_failure_prints_error_and_first_description_line()
    {
        var login = new FakeLogin(Account())
        {
            DeviceCode = (HttpStatusCode.BadRequest, FakeLogin.Error("invalid_scope",
                "AADSTS70011: The provided value for scope is not valid.\\r\\nTrace ID: 123\\r\\nCorrelation ID: 456")),
        };

        var outcome = await Run(login, null, "inventory");

        Assert.Equal(Program.ExitFailure, outcome.Code);
        Assert.Contains("invalid_scope: AADSTS70011: The provided value for scope is not valid.", outcome.Err);
        Assert.DoesNotContain("Trace ID", outcome.Err);
    }

    [Fact]
    public async Task Token_option_skips_sign_in()
    {
        var graph = Account();
        var login = new FakeLogin(graph);

        var outcome = await Run(login, null, "inventory", "--token", "given-token");

        Assert.Equal(Program.ExitOk, outcome.Code);
        Assert.Empty(login.Requests);
        Assert.All(graph.Requests, r => Assert.Equal("Bearer given-token", r.Authorization));
    }

    [Fact]
    public async Task Refresh_failure_interrupts_the_read_with_a_partial_archive_and_exit_3()
    {
        var graph = Account();
        graph.Intercept = (r, _) => r.Method == "POST"
            ? FakeGraph.Json("""{ "error": { "code": "InvalidAuthenticationToken", "message": "Lifetime validation failed, the token is expired." } }""", HttpStatusCode.Unauthorized)
            : null;
        var login = new FakeLogin(graph);
        login.Polls.Enqueue((HttpStatusCode.OK, FakeLogin.Token(Access1, Refresh1)));
        login.Refreshes.Enqueue((HttpStatusCode.BadRequest, FakeLogin.Error("invalid_grant", "AADSTS70043: The refresh token has expired.")));
        var outDir = NewDir();

        var outcome = await Run(login, null, "export", "--out", outDir);

        Assert.Equal(Program.ExitInterrupted, outcome.Code);
        Assert.True(File.Exists(Path.Combine(outDir, "archive.partial.json")));
        Assert.Contains("--resume", outcome.Err);
        Assert.Contains("it will ask you to sign in again", outcome.Err);
        Assert.Single(login.Requests, r => r.Form.GetValueOrDefault("grant_type") == "refresh_token");
        AssertNoSecrets(outcome);
    }

    private sealed class RecordingInner(Func<HttpRequestMessage, int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(string Url, string? Authorization)> Seen { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString()));
            return Task.FromResult(answer(request, Seen.Count));
        }
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent("{}") };

    [Fact]
    public async Task Bearer_token_goes_to_graph_only()
    {
        var inner = new RecordingInner((_, _) => Ok());
        var now = DateTimeOffset.UnixEpoch;
        var handler = new GraphAuthHandler(new TokenSet(Access1, Refresh1, now.AddHours(1)), (_, _) => throw new InvalidOperationException(), () => now)
        {
            InnerHandler = inner,
        };
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "should-be-removed");

        await http.GetAsync("https://graph.microsoft.com/v1.0/me/todo/lists");
        await http.GetAsync("https://example.com/other");
        await http.GetAsync("http://graph.microsoft.com/v1.0/me");
        await http.GetAsync("https://graph.microsoft.com.evil.example/v1.0/me");

        Assert.Equal($"Bearer {Access1}", inner.Seen[0].Authorization);
        Assert.All(inner.Seen.Skip(1), s => Assert.Null(s.Authorization));
    }

    [Fact]
    public async Task Token_is_refreshed_shortly_before_it_expires()
    {
        var inner = new RecordingInner((_, _) => Ok());
        var now = DateTimeOffset.UnixEpoch;
        var refreshCalls = 0;
        var handler = new GraphAuthHandler(new TokenSet(Access1, Refresh1, now.AddMinutes(30)), (refresh, _) =>
        {
            refreshCalls++;
            Assert.Equal(Refresh1, refresh);
            return Task.FromResult(new TokenSet(Access2, Refresh2, now.AddMinutes(90)));
        }, () => now)
        {
            InnerHandler = inner,
        };
        using var http = new HttpClient(handler);

        await http.GetAsync("https://graph.microsoft.com/v1.0/a");
        now = now.AddMinutes(20); // 10 minutes left: still fine
        await http.GetAsync("https://graph.microsoft.com/v1.0/b");
        now = now.AddMinutes(6); // 4 minutes left: inside the refresh margin
        await http.GetAsync("https://graph.microsoft.com/v1.0/c");

        Assert.Equal(1, refreshCalls);
        Assert.Equal([$"Bearer {Access1}", $"Bearer {Access1}", $"Bearer {Access2}"], inner.Seen.Select(s => s.Authorization));
    }

    [Fact]
    public async Task A_401_token_problem_refreshes_once_and_retries_the_request_once()
    {
        var inner = new RecordingInner((request, _) =>
            request.Headers.Authorization!.Parameter == Access1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("""{ "error": { "code": "InvalidAuthenticationToken", "message": "expired" } }"""),
                }
                : Ok());
        var now = DateTimeOffset.UnixEpoch;
        var refreshCalls = 0;
        var handler = new GraphAuthHandler(new TokenSet(Access1, Refresh1, now.AddHours(1)), (_, _) =>
        {
            refreshCalls++;
            return Task.FromResult(new TokenSet(Access2, Refresh2, now.AddHours(1)));
        }, () => now)
        {
            InnerHandler = inner,
        };
        using var http = new HttpClient(handler);

        var response = await http.PostAsync("https://graph.microsoft.com/v1.0/$batch", new StringContent("""{ "requests": [] }""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, refreshCalls);
        Assert.Equal([$"Bearer {Access1}", $"Bearer {Access2}"], inner.Seen.Select(s => s.Authorization));

        // Access denied is not a token problem: no refresh, no retry.
        var denied = new RecordingInner((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{ "error": { "code": "accessDenied", "message": "Access denied" } }"""),
        });
        var deniedHandler = new GraphAuthHandler(new TokenSet(Access1, Refresh1, now.AddHours(1)), (_, _) => throw new InvalidOperationException("must not refresh"), () => now)
        {
            InnerHandler = denied,
        };
        using var deniedHttp = new HttpClient(deniedHandler);
        var deniedResponse = await deniedHttp.GetAsync("https://graph.microsoft.com/v1.0/me/todo/lists/L/tasks/T/attachments");
        Assert.Equal(HttpStatusCode.Unauthorized, deniedResponse.StatusCode);
        Assert.Contains("accessDenied", await deniedResponse.Content.ReadAsStringAsync());
        Assert.Single(denied.Seen);
    }
}
