using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using static Mahjong.Server.Tests.BoardTableTests;

namespace Mahjong.Server.Tests;

public sealed class SaveDiscoveryTests
{
    private const string Saves = "/api/saves";
    private const string Boards = "/api/board/tables";

    private sealed record Save(string Game, string? TableId = null, string? Variant = null, int? Players = null,
        string? BotDifficulty = null, bool CanStartFresh = false);

    [Theory]
    [InlineData("/portal", true)]
    [InlineData("/game-portal", true)]
    [InlineData("/mahjong", false)]
    public async Task DiscoveryIsPortalOnlyAtTheExactMountAndDoesNotCreateGuestOrCookie(string mount, bool portal)
    {
        await using var server = Mounted(mount, portal);
        await server.StartAsync();
        var anonymous = new User(server, mount);
        if (portal)
        {
            await AssertSaves(anonymous);
            anonymous.Cookies.Add(server.Address, new Cookie("mahjong_guest", "not-a-protected-cookie", mount));
            await AssertSaves(anonymous);
            foreach (var other in new[] { "", mount == "/portal" ? "/game-portal" : "/portal", mount + "-extra" })
            {
                using var misplaced = await new User(server, other).Request(HttpMethod.Get, Saves);
                Assert.Equal(HttpStatusCode.NotFound, misplaced.StatusCode);
                Assert.False(misplaced.Headers.Contains("Set-Cookie"));
            }
        }
        else
        {
            var response = await anonymous.Request(HttpMethod.Get, Saves);
            Assert.False(response.Headers.Contains("Set-Cookie"));
            await Error(response, HttpStatusCode.NotFound, "invalid_request");
        }
        Assert.Equal(0L, await Sql(server, "SELECT count(*) FROM Guests"));
    }

    [Fact]
    public async Task DiscoveryListsOnlyMemberSetupForCatalogGamesAndKeepsOlderBotSaves()
    {
        const string mount = "/portal";
        await using var server = Mounted(mount);
        await server.StartAsync();
        var owner = await Enter(server, mount, "Owner");
        var peer = await Enter(server, mount, "Peer");
        var observer = await Enter(server, mount, "Observer");
        var priorChess = await Board(owner, "chess", bots: true, difficulty: "easy");
        var latestChess = await Board(owner, "chess", bots: true, difficulty: "hard");
        var chineseCheckers = await Board(owner, "chinese-checkers", bots: false, players: 3);
        var go = await Board(owner, "go", bots: false, variant: "9x9");
        var backgammon = await Seated(owner, "backgammon", 2, true);
        var poker = await Seated(owner, "poker", 3, true);
        var words = await Seated(owner, "words", 2, true);
        var farkle = await Seated(owner, "farkle", 4, true);
        var bigTwo = await Seated(peer, "big-two", 4, false);
        var joined = await Post(owner, "/api/big-two/join", new { invite = bigTwo.Text("invite") });
        Assert.Equal(1, joined.GetProperty("seat").GetInt32());
        var sudoku = await Post(owner, "/api/solo/sudoku", new { variant = "hard" });
        await Post(owner, "/api/solo/solitaire", new { variant = "draw-one" });

        await AssertSaves(owner,
            new("backgammon", backgammon.Text("id"), Players: 2, CanStartFresh: true),
            new("big-two", bigTwo.Text("id"), Players: 4),
            new("chess", latestChess.Text("id"), "standard", 2, "hard", true),
            new("chinese-checkers", chineseCheckers.Text("id"), "standard", 3),
            new("farkle", farkle.Text("id"), Players: 4, CanStartFresh: true),
            new("go", go.Text("id"), "9x9", 2),
            new("holdem", poker.Text("id"), Players: 3, CanStartFresh: true),
            new("solitaire", Variant: "draw-one", CanStartFresh: true),
            new("sudoku", Variant: "hard", CanStartFresh: true),
            new("word-tiles", words.Text("id"), Players: 2, CanStartFresh: true));
        await AssertSaves(peer, new Save("big-two", bigTwo.Text("id"), Players: 4));
        await AssertSaves(observer);
        await AssertSaves(new User(server, mount));
        Assert.True(JsonElement.DeepEquals(priorChess, await Get(owner, Boards + "/" + priorChess.Text("id"))));
        Assert.True(JsonElement.DeepEquals(sudoku, (await Get(owner, "/api/solo/sudoku")).GetProperty("table")));
    }

    [Fact]
    public async Task DiscoveryAgreesWithResumeOrderingAndSkipsClosedAndExpiredTables()
    {
        const string mount = "/game-portal";
        await using var server = Mounted(mount);
        await server.StartAsync();
        var owner = await Enter(server, mount, "Owner");
        var peer = await Enter(server, mount, "Peer");
        var chess = await Board(owner, "chess", bots: false);
        var chessId = chess.Text("id");
        var playing = await Post(peer, "/api/board/join", new { invite = chess.Text("invite") });
        foreach (var (actor, moveId) in new[] { (owner, "f2f3"), (peer, "e7e5"), (owner, "g2g4"), (peer, "d8h4") })
            playing = await Post(actor, Boards + "/" + chessId + "/moves", new { revision = Revision(playing), moveId });
        Assert.Equal("finished", playing.Text("status"));
        Assert.Equal(1, Assert.Single(playing.At("position", "winners").EnumerateArray()).GetInt32());
        await Post(owner, "/api/solo/solitaire", new { variant = "draw-one" });

        var firstBoard = (await Board(owner, "reversi", bots: true)).Text("id");
        var secondBoard = (await Board(owner, "reversi", bots: true)).Text("id");
        var firstSeat = (await Seated(owner, "farkle", 2, true)).Text("id");
        var secondSeat = (await Seated(owner, "farkle", 3, true)).Text("id");
        await AssertSelection(secondBoard, true, secondSeat, 3, true);
        await Sql(server, "UPDATE BoardMemberships SET JoinedAtTicks=1 WHERE TableId IN ($first,$second)",
            ("$first", firstBoard), ("$second", secondBoard));
        await Sql(server, "UPDATE SeatedMemberships SET JoinedAtTicks=1 WHERE TableId IN ($first,$second)",
            ("$first", firstSeat), ("$second", secondSeat));
        var tiedBoard = string.CompareOrdinal(firstBoard, secondBoard) > 0 ? firstBoard : secondBoard;
        var tiedSeat = string.CompareOrdinal(firstSeat, secondSeat) > 0 ? firstSeat : secondSeat;
        var tiedPlayers = tiedSeat == firstSeat ? 2 : 3;
        await AssertSelection(tiedBoard, true, tiedSeat, tiedPlayers, true);

        var closedBoard = (await Board(owner, "reversi", bots: false)).Text("id");
        var closedSeat = (await Seated(owner, "farkle", 4, false)).Text("id");
        await AssertSelection(closedBoard, false, closedSeat, 4, false);
        await Post(owner, Boards + "/" + closedBoard + "/leave", new { revision = 0 });
        await Post(owner, "/api/farkle/tables/" + closedSeat + "/leave", new { revision = 0 });
        await AssertSelection(tiedBoard, true, tiedSeat, tiedPlayers, true);

        var expiringBoard = (await Board(owner, "reversi", bots: false)).Text("id");
        var expiringSeat = (await Seated(owner, "farkle", 5, false)).Text("id");
        await AssertSelection(expiringBoard, false, expiringSeat, 5, false);
        await server.StopAsync();
        server.SetClockOffset(90_000);
        await server.StartAsync();
        await AssertSelection(tiedBoard, true, tiedSeat, tiedPlayers, true);
        Assert.Equal(chessId, (await Get(peer, "/api/board/resume?game=chess")).Text("tableId"));
        await AssertSaves(peer, new Save("chess", chessId, "standard", 2));
        await Error(await owner.Request(HttpMethod.Get, Boards + "/" + expiringBoard), HttpStatusCode.NotFound, "room_not_found");
        await Error(await owner.Request(HttpMethod.Get, "/api/farkle/tables/" + expiringSeat), HttpStatusCode.NotFound, "room_not_found");

        async Task AssertSelection(string board, bool boardFresh, string seated, int players, bool seatedFresh)
        {
            Assert.Equal(board, (await Get(owner, "/api/board/resume?game=reversi")).Text("tableId"));
            Assert.Equal(seated, (await Get(owner, "/api/farkle/resume")).Text("tableId"));
            await AssertSaves(owner, new("chess", chessId, "standard", 2),
                new("farkle", seated, Players: players, CanStartFresh: seatedFresh),
                new("reversi", board, "standard", 2, CanStartFresh: boardFresh),
                new("solitaire", Variant: "draw-one", CanStartFresh: true));
        }
    }

    [Theory]
    [InlineData(1, "complete")]
    [InlineData(4, "betweenHands")]
    public async Task OnlyStartedSoleBotHostCanRestartAcrossRecoveryAndMustSyncFirst(int hands, string settledStatus)
    {
        const string mount = "/portal";
        await using var server = Mounted(mount, true, ("TurnSeconds", 30), ("ClaimSeconds", 2), ("BotDelayMilliseconds", 5000));
        await server.StartAsync();
        var user = await Enter(server, mount, "Host");
        string roomId;
        using (var host = await user.Connect())
        {
            (await host.Command("createRoom", new { visibility = "invite", matchHands = hands, fillBots = true })).Ok();
            var waiting = await host.Snapshot();
            roomId = waiting.Text("roomId");
            const string expirySql = "SELECT ExpiresAtTicks FROM Rooms WHERE Id=$id";
            var expiry = await Sql(server, expirySql, ("$id", roomId));
            await AssertSaves(user, new Save("mahjong", roomId));
            Assert.Equal(expiry, await Sql(server, expirySql, ("$id", roomId)));
            var playing = await StartRoom(host, waiting);
            Assert.Equal("playing", playing.Status());
            await AssertSaves(user, new Save("mahjong", roomId, CanStartFresh: true));
        }

        await server.StopAsync();
        server.SetClockOffset(43_200);
        await server.StartAsync();
        await AssertSaves(user, new Save("mahjong", roomId, CanStartFresh: true));
        using var resumed = await user.Connect();
        Assert.Equal("invalid_state", (await resumed.Command("restartMatch", new { roomId, expectedRoomRevision = 0 })).Error());
        (await resumed.Sync(roomId)).Ok();
        var settled = await resumed.Snapshot();
        Assert.Equal(settledStatus, settled.Status());
        var expectedRoomRevision = settled.RoomRevision();
        (await resumed.Command("restartMatch", new { roomId, expectedRoomRevision })).Ok();
        var fresh = await resumed.Snapshot(s => s.Status() == "playing");
        Assert.Equal(roomId, fresh.Text("roomId"));
        Assert.Equal(expectedRoomRevision + 1, fresh.RoomRevision());
        Assert.Equal(1, fresh.Table().GetProperty("handNumber").GetInt32());
        (await resumed.Command("leaveRoom", new { roomId, expectedRoomRevision = fresh.RoomRevision(), abandon = true })).Ok();
        await AssertSaves(user);
    }

    [Fact]
    public async Task SharedMahjongReservationCanContinueButNeverOffersRestartAndExpiresNormally()
    {
        const string mount = "/game-portal";
        await using var server = Mounted(mount, true, ("TurnSeconds", 30), ("ClaimSeconds", 8));
        await server.StartAsync();
        var owner = await Enter(server, mount, "Owner");
        var peer = await Enter(server, mount, "Peer");
        var observer = await Enter(server, mount, "Observer");
        using var host = await owner.Connect();
        using var member = await peer.Connect();
        (await host.Command("createRoom", new { visibility = "invite", matchHands = 1 })).Ok();
        var waiting = await host.Snapshot();
        var roomId = waiting.Text("roomId");
        (await member.Command("joinRoom", new { inviteCode = waiting.Private().Text("inviteCode") })).Ok();
        waiting = await host.Snapshot(s => s.Connected(1));
        await AssertSaves(owner, new Save("mahjong", roomId));
        await AssertSaves(peer, new Save("mahjong", roomId));
        for (var seat = 2; seat <= 3; seat++)
        {
            (await host.Command("setBot", new { roomId, expectedRoomRevision = waiting.RoomRevision(), seat, enabled = true })).Ok();
            waiting = await host.Snapshot();
        }
        (await member.Command("setReady", new { roomId, expectedRoomRevision = waiting.RoomRevision(), ready = true })).Ok();
        waiting = await host.Snapshot(s => s.Chair(1).GetProperty("ready").GetBoolean());
        var playing = await StartRoom(host, waiting);
        (await member.Command("leaveRoom", new { roomId, expectedRoomRevision = playing.RoomRevision() })).Ok();
        var reserved = await host.Snapshot(s => s.Status() == "playing" && !s.Connected(1));
        Assert.Equal("human", reserved.Chair(1).Text("kind"));
        await AssertSaves(owner, new Save("mahjong", roomId));
        await AssertSaves(peer, new Save("mahjong", roomId));
        await AssertSaves(observer);

        await server.StopAsync();
        server.SetClockOffset(90_000);
        await server.StartAsync();
        await AssertSaves(owner);
        await AssertSaves(peer);
    }

    private static Kestrel Mounted(string mount, bool portal = true, params (string Name, int Value)[] timers)
    {
        var server = new Kestrel(timers).WithSetting("BASE_URL", mount).WithSetting("Portal__Enabled", portal ? "true" : "false");
        return portal ? server.WithSetting("GamePortal__DataDirectory", server.DataDirectory) : server;
    }

    private static async Task<User> Enter(Kestrel server, string mount, string name)
    {
        var user = new User(server, mount);
        Assert.Equal(name, (await user.Guest(name)).Text("displayName"));
        return user;
    }

    private static async Task<JsonElement> StartRoom(Client host, JsonElement waiting)
    {
        var roomId = waiting.Text("roomId");
        (await host.Command("setReady", new { roomId, expectedRoomRevision = waiting.RoomRevision(), ready = true })).Ok();
        var ready = await host.Snapshot(s => s.Chair(0).GetProperty("ready").GetBoolean());
        (await host.Command("start", new { roomId, expectedRoomRevision = ready.RoomRevision() })).Ok();
        return await host.Snapshot(s => s.Status() == "playing");
    }

    private static async Task AssertSaves(User user, params Save[] saves)
    {
        var response = await user.Request(HttpMethod.Get, Saves, includeOrigin: false);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var actual = await Ok(response);
        var expected = JsonSerializer.SerializeToElement(new { saves }, JsonSerializerOptions.Web);
        Assert.True(JsonElement.DeepEquals(expected, actual), $"Expected {expected}, got {actual}.");
    }

    private static Task<JsonElement> Board(User user, string game, bool bots, string? difficulty = null,
        string variant = "standard", int players = 2) =>
        Post(user, Boards, difficulty is null ? new { game, variant, players, bots }
            : new { game, variant, players, bots, botDifficulty = difficulty });
    private static Task<JsonElement> Seated(User user, string game, int players, bool bots) =>
        Post(user, "/api/" + game + "/tables", new { players, bots });
    private static async Task<JsonElement> Post(User user, string path, object payload) => await Ok(await user.Request(HttpMethod.Post, path, payload));
    private static async Task<JsonElement> Get(User user, string path) => await Ok(await user.Request(HttpMethod.Get, path));
    private static long Revision(JsonElement table) => table.GetProperty("revision").GetInt64();

    private static async Task<object?> Sql(Kestrel server, string sql, params (string Key, string Value)[] parameters)
    {
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(server.DataDirectory, "mahjong.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value);
        return await command.ExecuteScalarAsync();
    }
}
