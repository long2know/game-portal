using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mahjong.Server.Tests;

public sealed class PortalMountTests
{
    private const string Mount = "/portal";
    private const string Authority = "games.example.test:8443";
    private const string Edge = "https://" + Authority;

    [Theory]
    [InlineData(Mount)]
    [InlineData("/game-portal")]
    public async Task ServesBothBuiltShellsOnlyAtTheirCanonicalMountedRoutes(string mount)
    {
        await using var server = BoardTableTests.PortalServer().WithSetting("BASE_URL", mount);
        await server.StartAsync();
        using var http = Http(server);
        var portal = await ReadShell(mount + "/");
        var mahjong = await ReadShell(mount + "/mahjong/");
        Assert.NotEqual(portal, mahjong);
        Assert.Contains("Changsha Mahjong</title>", mahjong);
        Assert.Equal(mahjong, await ReadShell(mount + "/mahjong/play/reconnect"));
        foreach (var game in new[] { "", "mahjong", "chess", "go", "reversi", "checkers", "chinese-checkers",
            "holdem", "solitaire", "sudoku", "word-tiles", "farkle", "backgammon", "lantern-run", "xiangqi", "gomoku",
            "mahjong-solitaire", "big-two", "daily-court" })
        {
            var bare = game.Length == 0 ? mount : mount + "/" + game;
            const string query = "?next=invite%2Ftoken&keep=1";
            using var redirect = await http.GetAsync(bare + query + "#invite");
            Assert.Equal(HttpStatusCode.PermanentRedirect, redirect.StatusCode);
            Assert.Equal(bare + "/" + query, redirect.Headers.Location?.OriginalString);
            Assert.Equal(game == "mahjong" ? mahjong : portal, await ReadShell(bare + "/"));
        }
        foreach (var html in new[] { portal, mahjong })
        {
            var assets = Regex.Matches(html, $@"(?:src|href)=""({Regex.Escape(mount)}/assets/[^""]+)""")
                .Select(match => match.Groups[1].Value).ToArray();
            Assert.Contains(assets, asset => asset.EndsWith(".js", StringComparison.Ordinal));
            Assert.Contains(assets, asset => asset.EndsWith(".css", StringComparison.Ordinal));
            foreach (var asset in assets)
            {
                using var response = await http.GetAsync(asset);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains(response.Headers.CacheControl!.Extensions, extension => extension.Name == "immutable");
                Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
            }
        }
        foreach (var path in new[] { "/big-two/", "/daily-court/" })
        {
            using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, mount + path));
            Assert.Equal("text/html", head.Content.Headers.ContentType?.MediaType);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        }

        foreach (var path in new[] { "/", "/mahjong/", mount + "-extra/chess/", mount + "/portal.html", mount + "/index.html",
            mount + "/Chess/", mount + "/chess/unknown", mount + "/chess-extra/", mount + "/unknown", mount + "/mahjong-extra/",
            mount + "/big-two/unknown", mount + "/big-two-extra/", mount + "/daily-court/unknown", mount + "/api/daily-court" })
        {
            using var missing = await http.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.NotEqual("text/html", missing.Content.Headers.ContentType?.MediaType);
        }
        Assert.Equal("ready", (await http.GetFromJsonAsync<JsonElement>(mount + "/health/ready")).Text("status"));
        Assert.Equal(1, (await http.GetFromJsonAsync<JsonElement>(mount + "/api/version")).GetProperty("protocolVersion").GetInt32());

        async Task<string> ReadShell(string path)
        {
            using var response = await http.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.True(response.Headers.CacheControl?.NoCache);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains($"data-base=\"{mount}/\"", html);
            Assert.DoesNotContain("data-base=\"/\"", html);
            Assert.DoesNotContain("\"./assets/", html);
            return html;
        }
    }

    [Fact]
    public async Task BoardWritesHonorTheExistingRawHostAndOriginPolicyInPathPreservingProxyMode()
    {
        await using var server = BoardTableTests.PortalServer().InProduction()
            .WithSetting("Mahjong__BehindHttpsProxy", "false").WithSetting("GamePortal__BehindHttpsProxy", "true")
            .WithSetting("Mahjong__PublicOrigin", Edge);
        var portalData = Path.Combine(server.DataDirectory, "portal");
        server.WithSetting("GamePortal__DataDirectory", portalData);
        await server.StartAsync();
        using var http = Http(server);
        using var enter = new HttpRequestMessage(HttpMethod.Post, Mount + "/api/guest") { Content = JsonContent.Create(new { }) };
        enter.Headers.Host = Authority;
        enter.Headers.Add("Origin", Edge);
        using var guest = await http.SendAsync(enter);
        Assert.Equal(HttpStatusCode.OK, guest.StatusCode);
        var cookieParts = Assert.Single(guest.Headers.GetValues("Set-Cookie")).Split(';');
        var cookie = cookieParts[0];
        Assert.StartsWith("mahjong_guest=", cookie, StringComparison.Ordinal);
        var attributes = cookieParts.Skip(1).Select(a => a.Trim().ToLowerInvariant()).ToArray();
        Assert.Contains("path=/portal", attributes);
        Assert.Contains("secure", attributes);
        Assert.Contains("httponly", attributes);
        Assert.Contains("samesite=lax", attributes);
        Assert.DoesNotContain(attributes, a => a.StartsWith("domain=", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(portalData, "mahjong.db")), "The portal database must use GamePortal__DataDirectory.");
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(portalData, "keys"), "key-*.xml"));
        Assert.False(File.Exists(Path.Combine(server.DataDirectory, "mahjong.db")));
        Assert.False(Directory.Exists(Path.Combine(server.DataDirectory, "keys")));

        const string create = Mount + "/api/board/tables";
        var chess = new { game = "chess", variant = "standard", players = 2, bots = false };
        var table = await BoardTableTests.Ok(await Write(create, Authority, Edge, chess));
        var invite = table.Text("invite");
        var moves = create + "/" + table.Text("id") + "/moves";
        foreach (var (path, body) in new (string, object)[]
        {
            (create, chess), (Mount + "/api/board/join", new { invite }), (moves, new { revision = 0, moveId = "e2e4" })
        })
        {
            await BoardTableTests.Error(await Write(path, "alias.example.test:8443", Edge, body),
                HttpStatusCode.Forbidden, "forbidden");
            await BoardTableTests.Error(await Write(path, Authority, "https://alias.example.test:8443", body),
                HttpStatusCode.Forbidden, "forbidden");
        }
        using var stripped = await Write("/api/board/tables", Authority, Edge, chess);
        Assert.Equal(HttpStatusCode.NotFound, stripped.StatusCode);

        var ws = new ClientWebSocket();
        using var socket = new Client(ws);
        ws.Options.Proxy = null;
        ws.Options.SetRequestHeader("Host", Authority);
        ws.Options.SetRequestHeader("Origin", Edge);
        ws.Options.SetRequestHeader("Cookie", cookie);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await ws.ConnectAsync(new UriBuilder(server.Address) { Scheme = "ws", Path = Mount + "/ws" }.Uri, timeout.Token);
        Assert.NotEmpty((await socket.Take("lease")).Text("lease"));

        async Task<HttpResponseMessage> Write(string path, string host, string origin, object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            request.Headers.Host = host;
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Cookie", cookie);
            request.Headers.Add("X-Forwarded-Host", Authority);
            request.Headers.Add("X-Forwarded-Proto", "https");
            request.Headers.Add("X-Forwarded-Prefix", Mount);
            return await http.SendAsync(request);
        }
    }

    private static HttpClient Http(Kestrel server) => new(
        new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false })
    {
        BaseAddress = server.Address, Timeout = TimeSpan.FromSeconds(10)
    };
}
