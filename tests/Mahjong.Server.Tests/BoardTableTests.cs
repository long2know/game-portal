using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Mahjong.Server.Tests;

public sealed class BoardTableTests
{
    private const string Api = "/portal/api/board";

    [Fact]
    public async Task PrivateInviteAndRevisionCommitSurviveRestartWithoutReplacingOtherTables()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var catalog = await Catalog(new User(server));
        Assert.Equal(new[] { "checkers", "chess", "chinese-checkers", "go", "reversi" },
            catalog.Select(g => g.Text("id")).Order(StringComparer.Ordinal));
        var reversi = Definition(catalog, "reversi");
        var host = await Guest(server, "Host");
        var peer = await Guest(server, "Friend");
        var stranger = await Guest(server, "Stranger");

        var waiting = await Create(host, reversi, 2);
        var id = waiting.Text("id");
        var invite = waiting.Text("invite");
        var table = Api + "/tables/" + id;
        Assert.Equal("waiting", waiting.Text("status"));
        Assert.Equal(0, Revision(waiting));
        Assert.Equal(JsonValueKind.Null, waiting.GetProperty("position").ValueKind);
        Assert.Matches("^[a-f0-9]{32}$", invite);
        Assert.NotEqual(id, invite);
        Assert.Equal("Host", waiting.GetProperty("seats")[0].Text("name"));
        Assert.False(waiting.GetProperty("seats")[1].GetProperty("occupied").GetBoolean());

        await Error(await new User(server).Request(HttpMethod.Get, table), HttpStatusCode.Unauthorized, "not_authenticated");
        await Error(await stranger.Request(HttpMethod.Get, table), HttpStatusCode.NotFound, "room_not_found");
        await Error(await stranger.Request(HttpMethod.Post, table + "/moves", new { revision = 0, moveId = "guess" }),
            HttpStatusCode.NotFound, "room_not_found");
        await Error(await peer.Request(HttpMethod.Post, Api + "/join", new { invite = id }),
            HttpStatusCode.NotFound, "room_not_found");
        Assert.Null(await Resume(stranger, "reversi"));

        var request = new { invite };
        var joined = await Ok(await peer.Request(HttpMethod.Post, Api + "/join", request));
        Assert.Equal("playing", joined.Text("status"));
        Assert.Equal(1, Revision(joined));
        Assert.Equal(1, joined.GetProperty("seat").GetInt32());
        Assert.Equal("Friend", joined.GetProperty("seats")[1].Text("name"));
        Assert.Null(joined.Text("invite"));
        Assert.Equal(joined.GetRawText(), (await Ok(await peer.Request(HttpMethod.Post, Api + "/join", request))).GetRawText());
        await Error(await stranger.Request(HttpMethod.Post, Api + "/join", request), HttpStatusCode.NotFound, "room_not_found");
        Assert.Equal(id, await Resume(peer, "reversi"));

        var moves = Position(joined).GetProperty("moves").EnumerateArray().Take(2).Select(move => move.Text("id")).ToArray();
        Assert.Equal(2, moves.Length);
        await Error(await peer.Request(HttpMethod.Post, table + "/moves", new { revision = 1, moveId = moves[0] }),
            HttpStatusCode.BadRequest, "not_your_turn");
        await Error(await host.Request(HttpMethod.Post, table + "/moves", new { revision = 1, moveId = "illegal" }),
            HttpStatusCode.BadRequest, "illegal_move");
        Assert.Equal(Position(joined).GetRawText(), Position(await Ok(await host.Request(HttpMethod.Get, table))).GetRawText());

        var attempts = await Task.WhenAll(moves.Select(moveId =>
            host.Request(HttpMethod.Post, table + "/moves", new { revision = 1, moveId })));
        var success = Assert.Single(attempts, response => response.StatusCode == HttpStatusCode.OK);
        var committed = await Ok(success);
        await Error(Assert.Single(attempts, response => response != success), HttpStatusCode.Conflict, "stale_room");
        Assert.Equal(2, Revision(committed));
        Assert.Equal(moves[Array.IndexOf(attempts, success)], Position(committed).At("lastMove", "id").GetString());
        Assert.Equal(new[] { "bot", "name", "occupied", "seat" },
            committed.GetProperty("seats")[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.False(committed.TryGetProperty("checkpoint", out _));

        var newer = await Create(host, reversi, 2);
        Assert.NotEqual(id, newer.Text("id"));
        Assert.Equal(newer.Text("id"), await Resume(host, "reversi"));
        Assert.Equal(id, await Resume(peer, "reversi"));
        await Ok(await host.Request(HttpMethod.Patch, "/portal/api/profile", new { displayName = "Renamed Host" }));

        await server.StopAsync();
        server.SetClockOffset(90_000);
        await server.StartAsync();
        var recovered = await Ok(await host.Request(HttpMethod.Get, table));
        Assert.Equal(Revision(committed), Revision(recovered));
        Assert.Equal(Position(committed).GetRawText(), Position(recovered).GetRawText());
        Assert.Equal("Renamed Host", recovered.GetProperty("seats")[0].Text("name"));
        Assert.Equal(id, await Resume(host, "reversi"));
        await Error(await host.Request(HttpMethod.Get, Api + "/tables/" + newer.Text("id")),
            HttpStatusCode.NotFound, "room_not_found");
        await Error(await host.Request(HttpMethod.Post, table + "/moves", new { revision = 1, moveId = moves[0] }),
            HttpStatusCode.Conflict, "stale_room");
    }

    [Fact]
    public async Task ChessBotCommitsAReplyWhileMalformedRequestsDoNotCreateTables()
    {
        await using var server = PortalServer()
            .WithSetting("Portal__StockfishPath", Path.Combine(AppContext.BaseDirectory, "absent-stockfish"));
        await server.StartAsync();
        var guest = await Guest(server, "White");
        const string start = """{"game":"chess","variant":"standard","players":2,"bots":true}""";
        using var oversized = new ChunkedJson(start + new string(' ', 4096));
        await Error(await guest.Request(HttpMethod.Post, Api + "/tables", oversized), HttpStatusCode.BadRequest, "invalid_request");
        await Error(await guest.Request(HttpMethod.Post, Api + "/tables", new
        {
            game = "chess", variant = "standard", players = 6, bots = true
        }), HttpStatusCode.BadRequest, "invalid_request");
        using var duplicate = new StringContent(start.Insert(start.Length - 1, ",\"bots\":false"), Encoding.UTF8, "application/json");
        await Error(await guest.Request(HttpMethod.Post, Api + "/tables", duplicate), HttpStatusCode.BadRequest, "invalid_request");
        Assert.Null(await Resume(guest, "chess"));

        var chess = Definition(await Catalog(guest), "chess");
        var solo = await Create(guest, chess, 2, true);
        Assert.Equal("playing", solo.Text("status"));
        Assert.Null(solo.Text("invite"));
        Assert.True(solo.GetProperty("seats")[1].GetProperty("bot").GetBoolean());
        Assert.True(solo.GetProperty("seats")[1].GetProperty("occupied").GetBoolean());
        Assert.Contains(Position(solo).GetProperty("moves").EnumerateArray(), move => move.Text("id") == "e2e4");

        var path = Api + "/tables/" + solo.Text("id");
        var played = await Ok(await guest.Request(HttpMethod.Post, path + "/moves", new { revision = 0, moveId = "e2e4" }));
        var position = Position(played);
        Assert.Equal(1, Revision(played));
        Assert.Equal(0, position.GetProperty("turn").GetInt32());
        var botMove = position.GetProperty("lastMove");
        Assert.NotEqual("e2e4", botMove.Text("id"));
        var pieces = position.GetProperty("cells").EnumerateArray().ToArray();
        Assert.Equal(0, Assert.Single(pieces, cell => cell.Text("id") == "e4").GetProperty("owner").GetInt32());
        Assert.Equal(1, Assert.Single(pieces, cell => cell.Text("id") == botMove.Text("to")).GetProperty("owner").GetInt32());
        Assert.Equal(position.GetRawText(), Position(await Ok(await guest.Request(HttpMethod.Get, path))).GetRawText());
    }

    [Fact]
    public async Task SixSeatsCanPollAtBrowserCadenceWithoutSharingTheProxyAddressQuota()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var guests = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Guest(server, $"Guest {i}")));
        var definition = Definition(await Catalog(guests[0]), "chinese-checkers");
        var created = await Create(guests[0], definition, 6);
        var invite = created.Text("invite");
        var joins = await Task.WhenAll(guests.Skip(1).Select(guest => Ok(guest.Request(HttpMethod.Post, Api + "/join", new { invite }))));
        Assert.Equal(Enumerable.Range(1, 5), joins.Select(t => t.GetProperty("seat").GetInt32()).Order());
        Assert.Single(joins, t => t.Text("status") == "playing");

        var path = Api + "/tables/" + created.Text("id");
        await Task.WhenAll(guests.Select(async (guest, index) =>
        {
            for (var poll = 0; poll < 30; poll++)
            {
                var table = await Ok(await guest.Request(HttpMethod.Get, path));
                Assert.Equal("playing", table.Text("status"));
                Assert.Equal(5, Revision(table));
                Assert.Equal($"Guest {index}", table.GetProperty("seats")[table.GetProperty("seat").GetInt32()].Text("name"));
            }
        }));
    }

    internal static Kestrel PortalServer() => new Kestrel().WithSetting("BASE_URL", "/portal")
        .WithSetting("Portal__Enabled", "true");

    private static async Task<User> Guest(Kestrel server, string name)
    {
        var guest = new User(server);
        var profile = await Ok(await guest.Request(HttpMethod.Post, "/portal/api/guest", new { displayName = name }));
        Assert.Equal(name, profile.Text("displayName"));
        return guest;
    }

    private static async Task<JsonElement[]> Catalog(User guest) =>
        (await Ok(await guest.Request(HttpMethod.Get, Api + "/games"))).GetProperty("games").EnumerateArray().ToArray();

    private static JsonElement Definition(JsonElement[] games, string id) => Assert.Single(games, game => game.Text("id") == id);

    private static Task<JsonElement> Create(User guest, JsonElement definition, int players, bool bots = false) =>
        Ok(guest.Request(HttpMethod.Post, Api + "/tables", new
        {
            game = definition.Text("id"), variant = definition.GetProperty("variants")[0].Text("id"), players, bots
        }));

    private static async Task<string?> Resume(User guest, string game) =>
        (await Ok(await guest.Request(HttpMethod.Get, Api + "/resume?game=" + game))).Text("tableId");

    private static JsonElement Position(JsonElement table) => table.GetProperty("position");
    private static long Revision(JsonElement table) => table.GetProperty("revision").GetInt64();

    private static async Task<JsonElement> Ok(Task<HttpResponseMessage> response) => await Ok(await response);

    internal static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {body}");
            Assert.True(response.Headers.CacheControl?.NoStore);
            return JsonSerializer.Deserialize<JsonElement>(body);
        }
    }

    internal static async Task Error(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).Error());
        }
    }

    private sealed class ChunkedJson : HttpContent
    {
        private readonly byte[] _content;

        public ChunkedJson(string json)
        {
            _content = Encoding.UTF8.GetBytes(json);
            Headers.ContentType = new("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_content).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
