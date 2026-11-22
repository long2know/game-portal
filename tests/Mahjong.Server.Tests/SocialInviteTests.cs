using System.Net;
using System.Text;
using System.Text.Json;
using Mahjong.Server.Social;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using static Mahjong.Server.Tests.BoardTableTests;

namespace Mahjong.Server.Tests;

public sealed class SocialInviteTests
{
    private const string Api = "/portal/api";
    private const string Social = Api + "/social";
    private const string Invites = Social + "/invites";
    private const string Presence = Social + "/presence";
    private const string Board = Api + "/board";
    private const string Ws = "/portal/ws";
    private const string Unknown = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task SocialIsPortalOnlyAndUsesTheMountedProtectedPostContract()
    {
        await using var disabled = new Kestrel().WithSetting("BASE_URL", "/portal");
        await disabled.StartAsync();
        await Fault(new User(disabled).Request(HttpMethod.Post, Presence, new { }), HttpStatusCode.NotFound, "invalid_request");
        await using var server = PortalServer();
        await server.StartAsync();
        var guest = await Guest(server, "Guest");
        var pulse = new { tabId = "visible", active = true, busy = false };
        await Fault(new User(server).Request(HttpMethod.Post, Presence, pulse), HttpStatusCode.Unauthorized, "not_authenticated");
        await Fault(guest.Request(HttpMethod.Post, Presence, pulse, "https://elsewhere.example"),
            HttpStatusCode.Forbidden, "forbidden");
        await Fault(guest.Request(HttpMethod.Post, Presence, pulse, includeOrigin: false), HttpStatusCode.Forbidden, "forbidden");
        using (var naked = await guest.Request(HttpMethod.Post, "/api/social/presence", pulse))
            Assert.Equal(HttpStatusCode.NotFound, naked.StatusCode);
        var initial = await Pulse(guest);
        Fields(initial, "available", "incoming", "outgoing", "players");
        Assert.False(initial.GetProperty("available").GetBoolean());
        foreach (var array in new[] { "players", "incoming", "outgoing" }) Assert.Empty(List(initial, array));
        foreach (var body in new[]
        {
            "{\"tabId\":\"one\",\"tabId\":\"two\",\"active\":true,\"busy\":false}",
            "{\"tabId\":\"one\",\"active\":true,\"busy\":false,\"available\":null}",
            "{\"tabId\":\"" + new string('a', 4096) + "\",\"active\":true,\"busy\":false}"
        })
            await Fault(guest.Request(HttpMethod.Post, Presence, new StringContent(body, Encoding.UTF8, "application/json")),
                HttpStatusCode.BadRequest, "invalid_request");
        await Fault(guest.Request(HttpMethod.Post, Presence, new StringContent("{}", Encoding.UTF8, "text/plain")),
            HttpStatusCode.BadRequest, "invalid_request");
        foreach (var game in new[] { "Chess", "poker", "words", "sudoku", "solitaire", "runner", "local-runner", "anything" })
            await Fault(guest.Request(HttpMethod.Post, Invites, new { playerId = Unknown, game, tableId = Unknown }),
                HttpStatusCode.BadRequest, "invalid_request");
        await Fault(guest.Request(HttpMethod.Post, InvitePath(Unknown, "accept"), new { tableId = Unknown }),
            HttpStatusCode.BadRequest, "invalid_request");
        foreach (var op in new[] { "accept", "decline", "revoke", "block" })
        {
            await Unavailable(guest.Request(HttpMethod.Post, InvitePath(Unknown, op), new { }));
            await Unavailable(guest.Request(HttpMethod.Post, InvitePath("not-a-token", op), new { }));
        }
    }

    [Fact]
    public void PresenceDoesNotRenewHiddenBusyTabsOrExposeExpiredAndOtherViewersHandles()
    {
        var clock = new ManualClock();
        var presence = new SocialPresence(clock);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        void Ping(Guid id, string tab, bool active = true, bool busy = false) => presence.Apply(id, new(tab, active, busy, null), true);
        Ping(first, "sender", busy: true);
        Ping(second, "window");
        Ping(third, "window");
        var old = presence.Players(first)[second];
        Assert.Equal(second, presence.FindPlayer(first, old));
        Assert.Null(presence.FindPlayer(third, old));
        Assert.DoesNotContain(first, presence.Players(second).Keys);

        Ping(second, "game", busy: true);
        clock.Advance(30);
        Ping(second, "game", active: false);
        Ping(second, "window");
        Ping(first, "sender", busy: true);
        Assert.DoesNotContain(second, presence.Players(first).Keys);
        Assert.Null(presence.FindPlayer(first, old));
        clock.Advance(15);
        var fresh = presence.Players(first)[second];
        Assert.NotEqual(old, fresh);
        Assert.Equal(second, presence.FindPlayer(first, fresh));
        clock.Advance(29);
        Ping(first, "sender", busy: true);
        Ping(second, "window");
        Assert.Equal(fresh, presence.Players(first)[second]);
        clock.Advance(16);
        Assert.Null(presence.FindPlayer(first, fresh));
        Assert.NotEqual(fresh, presence.Players(first)[second]);
        clock.Advance(30);
        Ping(first, "sender", busy: true);
        Assert.Empty(presence.Players(first));
    }

    [Fact]
    public async Task BusyAndHiddenTabsCanNeverUseAnOldHandleAndOptOutCancelsBothDirections()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var host = await Guest(server, "Host");
        var recipient = await Guest(server, "Receiver");
        var target = (await CreateBoard(host, "chess", "standard")).Text("id");
        var reverseTarget = (await CreateBoard(recipient, "go", "9x9")).Text("id");
        await Pulse(recipient, available: true);
        var old = Player(await Pulse(host, busy: true, available: true), "Receiver");
        Assert.Empty(List(await Pulse(recipient), "players"));
        await Pulse(recipient, tab: "game", busy: true);
        var hidden = await Pulse(recipient, tab: "game", active: false);
        Assert.True(hidden.GetProperty("available").GetBoolean());
        Assert.Empty(List(hidden, "players"));
        Assert.Empty(List(await Pulse(host, busy: true), "players"));
        await Unavailable(Send(host, old, "chess", target));
        await Pulse(recipient, tab: "game", busy: false);
        var fresh = Player(await Pulse(host), "Receiver");
        Assert.NotEqual(old, fresh);
        var hostHandle = Player(await Pulse(recipient), "Host");
        var toRecipient = await Offer(host, fresh, "chess", target);
        var toHost = await Offer(recipient, hostHandle, "go", reverseTarget);
        await Pulse(recipient, tab: "main", active: false);
        await Pulse(recipient, tab: "game", active: false);
        await Unavailable(Act(recipient, toRecipient, "accept"));
        // A second visible tab doesn't override another fresh busy tab.
        await Pulse(recipient, tab: "game", busy: true);
        await Pulse(recipient, tab: "main");
        await Fault(Act(recipient, toRecipient, "accept"), HttpStatusCode.Conflict, "already_seated");
        Assert.Equal(2L, await Number(server, "SELECT count(*) FROM SocialInvites WHERE Accepted = 0"));
        var off = await Pulse(recipient, tab: "settings", active: false, available: false);
        Assert.False(off.GetProperty("available").GetBoolean());
        Assert.False((await Pulse(recipient, tab: "main")).GetProperty("available").GetBoolean());
        Assert.Empty(List(await Pulse(host), "outgoing"));
        Assert.Empty(List(await Pulse(host), "incoming"));
        await Unavailable(Act(recipient, toRecipient, "accept"));
        await Unavailable(Act(host, toHost, "accept"));
        Assert.Equal(0L, await Number(server, "SELECT count(*) FROM SocialInvites"));

        // Off heartbeats don't allocate tabs or consume the opted-in tab allowance.
        for (var i = 0; i < 10; i++) await Pulse(recipient, tab: "before" + i);
        for (var i = 0; i < 8; i++) await Pulse(recipient, tab: "visible" + i, available: i == 0 ? true : null);
        await Fault(recipient.Request(HttpMethod.Post, Presence, new { tabId = "excess", active = true, busy = false }),
            HttpStatusCode.TooManyRequests, "busy");
        await Pulse(recipient, tab: "visible0", active: false);
        Assert.True((await Pulse(recipient, tab: "excess")).GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task PendingRequestsArePersonalizedActorScopedDedupedAndBlockedDurably()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var sender = await Guest(server, "Sender");
        var recipient = await Guest(server, "Receiver");
        var third = await Guest(server, "Third");
        var table = await CreateBoard(sender, "chess", "standard");
        var id = table.Text("id");
        await Pulse(recipient, available: true);
        var handle = Player(await Pulse(sender, available: true), "Receiver");
        var otherHandle = Player(await Pulse(third, available: true), "Receiver");
        Assert.NotEqual(handle, otherHandle);
        await Unavailable(Send(third, handle, "chess", id));
        await Unavailable(Send(third, otherHandle, "chess", id));
        await Unavailable(Send(sender, handle, "go", id));
        await Unavailable(Send(sender, "Receiver", "chess", id));
        var invite = await Offer(sender, handle, "chess", id);
        Assert.Equal(invite, await Offer(sender, handle, "chess", id));
        var firstExpiry = Assert.Single(List(await Pulse(recipient), "incoming")).Text("expiresAtUtc");
        Assert.EndsWith("Z", firstExpiry, StringComparison.Ordinal);
        Assert.InRange(DateTimeOffset.Parse(firstExpiry) - DateTimeOffset.UtcNow, TimeSpan.FromSeconds(110), TimeSpan.FromMinutes(2));
        var senderHandle = Player(await Pulse(recipient), "Sender");
        var second = (await CreateBoard(recipient, "checkers", "english")).Text("id");
        var reply = await Offer(recipient, senderHandle, "checkers", second);
        var thirdRequest = await Offer(sender, handle, "chess", (await CreateBoard(sender, "chess", "standard")).Text("id"));
        var fourth = (await CreateBoard(sender, "go", "9x9")).Text("id");
        await Fault(Send(sender, handle, "go", fourth), HttpStatusCode.TooManyRequests, "busy");
        Assert.Equal(3L, await Number(server, "SELECT count(*) FROM SocialInvites WHERE Accepted = 0"));

        await Ok(await sender.Request(HttpMethod.Patch, Api + "/profile", new { displayName = "Renamed Sender" }));
        await Ok(await recipient.Request(HttpMethod.Patch, Api + "/profile", new { displayName = "Renamed Receiver" }));
        var outgoing = List(await Pulse(sender), "outgoing").Single(i => i.Text("id") == invite);
        var incoming = List(await Pulse(recipient), "incoming").Single(i => i.Text("id") == invite);
        Fields(outgoing, "displayName", "expiresAtUtc", "game", "id");
        Fields(incoming, "displayName", "expiresAtUtc", "game", "id");
        Assert.Equal("Renamed Receiver", outgoing.Text("displayName"));
        Assert.Equal("Renamed Sender", incoming.Text("displayName"));
        Assert.Equal("chess", incoming.Text("game"));
        Assert.Equal(firstExpiry, incoming.Text("expiresAtUtc"));
        Assert.DoesNotContain(id, incoming.GetRawText());
        Assert.DoesNotContain(table.Text("invite"), outgoing.GetRawText());
        foreach (var action in new[] { "accept", "decline", "block", "revoke" })
            await Unavailable(Act(third, invite, action));
        foreach (var action in new[] { "accept", "decline", "block" })
            await Unavailable(Act(sender, invite, action));
        await Unavailable(Act(recipient, invite, "revoke"));
        await Confirm(Act(recipient, thirdRequest, "decline"));
        await Unavailable(Act(recipient, thirdRequest, "accept"));
        var revoked = await Offer(sender, handle, "go", fourth);
        await Confirm(Act(sender, revoked, "revoke"));
        await Unavailable(Act(recipient, revoked, "accept"));
        await Confirm(Act(recipient, invite, "block"));
        await Unavailable(Act(sender, reply, "accept"));
        await Unavailable(Send(sender, handle, "chess", id));
        await Unavailable(Send(recipient, senderHandle, "checkers", second));
        Assert.Equal(0L, await Number(server, "SELECT count(*) FROM SocialInvites"));
        Assert.Equal(1L, await Number(server, "SELECT count(*) FROM SocialBlocks"));

        await server.StopAsync();
        await server.StartAsync();
        Assert.True((await Pulse(sender)).GetProperty("available").GetBoolean());
        var receiver = await Pulse(recipient);
        Assert.True(receiver.GetProperty("available").GetBoolean());
        Assert.Empty(List(receiver, "players"));
        Assert.Empty(List(await Pulse(sender), "players"));
        await Pulse(third);
        Assert.NotEqual(handle, Player(await Pulse(third), "Renamed Receiver"));
        Assert.DoesNotContain("Renamed Receiver", List(await Pulse(sender), "players").Select(p => p.Text("displayName")));
    }

    [Theory]
    [InlineData("chess", "standard", 2, "board")]
    [InlineData("go", "19x19", 2, "board")]
    [InlineData("reversi", "standard", 2, "board")]
    [InlineData("checkers", "english", 2, "board")]
    [InlineData("chinese-checkers", "standard", 3, "board")]
    [InlineData("holdem", null, 2, "poker")]
    [InlineData("word-tiles", null, 2, "words")]
    [InlineData("farkle", null, 2, "farkle")]
    [InlineData("backgammon", null, 2, "backgammon")]
    public async Task AcceptReallySeatsForEveryBoardAndSeatedRoute(string game, string? variant, int players, string endpoint)
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var host = await Guest(server, "Creator");
        var recipient = await Guest(server, "Recipient");
        var api = Api + "/" + endpoint;
        var table = variant is not null ? await CreateBoard(host, game, variant, players)
            : await Post(host, api + "/tables", new { players, bots = false });
        var id = table.Text("id");
        var sender = host;
        if (players == 3)
        {
            sender = await Guest(server, "Seated member");
            var member = await Post(sender, api + "/join", new { invite = table.Text("invite") });
            Assert.Equal(1, member.GetProperty("seat").GetInt32());
        }
        JsonElement unrelated = default;
        if (game == "holdem") unrelated = await Post(recipient, Api + "/farkle/tables", new { players = 2, bots = false });
        await Pulse(recipient, available: true);
        var token = await Offer(sender, Player(await Pulse(sender, busy: true, available: true), "Recipient"), game, id);
        await Fault(recipient.Request(HttpMethod.Get, api + "/tables/" + id), HttpStatusCode.NotFound, "room_not_found");
        await Joined(Act(recipient, token, "accept"), game, id);
        var joined = await Read(recipient, api + "/tables/" + id);
        Assert.Equal(players - 1, joined.GetProperty("seat").GetInt32());
        Assert.Equal(players - 1, joined.GetProperty("revision").GetInt32());
        Assert.Equal("playing", joined.Text("status"));
        Assert.Equal("Recipient", joined.GetProperty("seats")[players - 1].Text("name"));
        Assert.Equal(1L, await Number(server, "SELECT Accepted FROM SocialInvites WHERE Id = $id", token));
        var memberships = variant is not null ? "BoardMemberships" : "SeatedMemberships";
        Assert.Equal(players, await Number(server, $"SELECT count(*) FROM {memberships} WHERE TableId = $id", id));
        if (game == "holdem")
        {
            var owner = await Read(host, api + "/tables/" + id);
            var position = joined.GetProperty("position");
            var publicSeatKeys = new[] { "dealtIn", "folded", "handCommitted", "isAllIn", "seat", "stack", "streetCommitted" };
            Assert.Equal(1, position.GetProperty("viewerSeat").GetInt32());
            Assert.Equal(0, owner.At("position", "viewerSeat").GetInt32());
            var cards = List(position, "ownCards").Select(Card).ToArray();
            Assert.Equal(2, cards.Distinct().Count());
            Assert.Empty(cards.Intersect(List(owner.GetProperty("position"), "ownCards").Select(Card)));
            Assert.All(List(position, "seats"), seat => Fields(seat, publicSeatKeys));
            Assert.False(joined.TryGetProperty("checkpoint", out _));
            Assert.True(JsonElement.DeepEquals(unrelated, await Read(recipient, Api + "/farkle/tables/" + unrelated.Text("id"))));
        }
    }

    [Fact]
    public async Task ChessRollbackAndConcurrentRetriesLeaveOneSeatAndReceiptAcrossRestart()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var host = await Guest(server, "Host");
        var recipient = await Guest(server, "Recipient");
        var table = await CreateBoard(host, "chess", "standard");
        var id = table.Text("id");
        var next = await CreateBoard(host, "checkers", "english");
        var own = await CreateBoard(recipient, "go", "9x9");
        await Pulse(recipient, available: true);
        var handle = Player(await Pulse(host, busy: true, available: true), "Recipient");
        var token = await Offer(host, handle, "chess", id);
        var competing = await Offer(host, handle, "checkers", next.Text("id"));
        await PreventReceipt(server);
        await Fault(Act(recipient, token, "accept"), HttpStatusCode.TooManyRequests, "busy");
        Assert.Equal(0L, await Number(server, "SELECT Accepted FROM SocialInvites WHERE Id = $id", token));
        Assert.Equal(1L, await Number(server, "SELECT count(*) FROM BoardMemberships WHERE TableId = $id", id));
        Assert.True(JsonElement.DeepEquals(table, await Read(host, Board + "/tables/" + id)));
        await Sql(server, "DROP TRIGGER RejectTestReceipt");
        var responses = await Task.WhenAll(Act(recipient, token, "accept"), Act(recipient, token, "accept"));
        // Ignore the first response body to model a client losing its original acknowledgement.
        using (var ignored = responses[0]) Assert.Equal(HttpStatusCode.OK, ignored.StatusCode);
        await Joined(Task.FromResult(responses[1]), "chess", id);
        await Fault(Act(recipient, competing, "accept"), HttpStatusCode.Conflict, "already_seated");
        Assert.Equal(0L, await Number(server, "SELECT Accepted FROM SocialInvites WHERE Id = $id", competing));
        var before = await Read(recipient, Board + "/tables/" + id);
        Assert.Equal(1, before.GetProperty("revision").GetInt32());
        Assert.Null(before.GetProperty("invite").GetString());
        Assert.NotEmpty(List(before.GetProperty("position"), "moves"));
        Assert.Equal(2L, await Number(server, "SELECT count(*) FROM BoardMemberships WHERE TableId = $id", id));
        Assert.True(JsonElement.DeepEquals(own, await Read(recipient, Board + "/tables/" + own.Text("id"))));

        await server.StopAsync();
        await server.StartAsync();
        await Joined(Act(recipient, token, "accept"), "chess", id);
        Assert.True(JsonElement.DeepEquals(before, await Read(recipient, Board + "/tables/" + id)));
        Assert.Equal(id, (await Read(recipient, Board + "/resume?game=chess")).Text("tableId"));
        Assert.Equal(1L, await Number(server, "SELECT Accepted FROM SocialInvites WHERE Id = $id", token));
        await server.StopAsync();
        server.SetClockOffset(125);
        await server.StartAsync();
        await Unavailable(Act(recipient, competing, "accept"));
        await Joined(Act(recipient, token, "accept"), "chess", id);
        Assert.Equal(1L, await Number(server, "SELECT count(*) FROM SocialInvites"));
        await server.StopAsync();
        server.SetClockOffset(305);
        await server.StartAsync();
        await Unavailable(Act(recipient, token, "accept"));
        Assert.Equal(0L, await Number(server, "SELECT count(*) FROM SocialInvites"));
        Assert.Equal(1, (await Read(recipient, Board + "/tables/" + id)).GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task DifferentAdaptersCompetingForOneRecipientCommitOnlyOneHandoff()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var host = await Guest(server, "Host");
        var recipient = await Guest(server, "Receiver");
        await Pulse(recipient, available: true);
        var handle = Player(await Pulse(host, busy: true, available: true), "Receiver");
        var chess = (await CreateBoard(host, "chess", "standard")).Text("id");
        var poker = (await Post(host, Api + "/poker/tables", new { players = 2, bots = false })).Text("id");
        var boardInvite = await Offer(host, handle, "chess", chess);
        var pokerInvite = await Offer(host, handle, "holdem", poker);
        var results = await Task.WhenAll(Act(recipient, boardInvite, "accept"), Act(recipient, pokerInvite, "accept"));
        var firstWon = results[0].IsSuccessStatusCode;
        (string Token, string Game, string Id) won = firstWon ? (boardInvite, "chess", chess) : (pokerInvite, "holdem", poker);
        (string Token, string Game, string Id) waiting = firstWon ? (pokerInvite, "holdem", poker) : (boardInvite, "chess", chess);
        await Joined(Task.FromResult(results[firstWon ? 0 : 1]), won.Game, won.Id);
        await Fault(Task.FromResult(results[firstWon ? 1 : 0]), HttpStatusCode.Conflict, "already_seated");
        Assert.Equal(1L, await Number(server, "SELECT count(*) FROM SocialInvites WHERE Accepted = 1"));
        var boardPath = Board + "/tables/" + chess;
        var pokerPath = Api + "/poker/tables/" + poker;
        var board = await Read(host, boardPath);
        var seated = await Read(host, pokerPath);
        Assert.Equal(firstWon ? "playing" : "waiting", board.Text("status"));
        Assert.Equal(firstWon ? "waiting" : "playing", seated.Text("status"));
        Assert.Equal(firstWon ? 1 : 0, board.GetProperty("revision").GetInt32());
        Assert.Equal(firstWon ? 0 : 1, seated.GetProperty("revision").GetInt32());

        await server.StopAsync();
        // Allow for process startup latency while keeping the receipt inside the short handoff window.
        server.SetClockOffset(-10);
        await server.StartAsync();
        Assert.True((await Pulse(recipient)).GetProperty("available").GetBoolean());
        await Joined(Act(recipient, won.Token, "accept"), won.Game, won.Id);
        await Fault(Act(recipient, waiting.Token, "accept"), HttpStatusCode.Conflict, "already_seated");
        Assert.Equal(0L, await Number(server, "SELECT Accepted FROM SocialInvites WHERE Id = $id", waiting.Token));
        Assert.True(JsonElement.DeepEquals(board, await Read(host, boardPath)));
        Assert.True(JsonElement.DeepEquals(seated, await Read(host, pokerPath)));

        await server.StopAsync();
        server.SetClockOffset(20);
        await server.StartAsync();
        await Pulse(recipient);
        await Joined(Act(recipient, waiting.Token, "accept"), waiting.Game, waiting.Id);
        await Joined(Act(recipient, won.Token, "accept"), won.Game, won.Id);
        Assert.Equal(1, (await Read(recipient, boardPath)).GetProperty("revision").GetInt32());
        Assert.Equal(1, (await Read(recipient, pokerPath)).GetProperty("revision").GetInt32());
        Assert.Equal(2L, await Number(server, "SELECT count(*) FROM SocialInvites WHERE Accepted = 1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedReceiptSurvivesOptOutOrBlockWhilePendingInviteDoesNot(bool block)
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var host = await Guest(server, "Host");
        var recipient = await Guest(server, "Receiver");
        var chess = (await CreateBoard(host, "chess", "standard")).Text("id");
        var waiting = (await CreateBoard(host, "checkers", "english")).Text("id");
        await Pulse(recipient, available: true);
        var handle = Player(await Pulse(host, busy: true, available: true), "Receiver");
        var joined = await Offer(host, handle, "chess", chess);
        var pending = await Offer(host, handle, "checkers", waiting);
        using (var lost = await Act(recipient, joined, "accept")) Assert.Equal(HttpStatusCode.OK, lost.StatusCode);
        var path = Board + "/tables/" + chess;
        var original = await Read(recipient, path);
        Assert.Equal(1, original.GetProperty("revision").GetInt32());
        Assert.Equal(1, original.GetProperty("seat").GetInt32());
        var receiptExpiry = await Number(server, "SELECT ExpiresAtTicks FROM SocialInvites WHERE Id = $id", joined);

        if (block)
            await Confirm(Act(recipient, pending, "block"));
        else
            Assert.False((await Pulse(host, available: false)).GetProperty("available").GetBoolean());
        await Unavailable(Act(recipient, pending, "accept"));
        Assert.Equal(0L, await Number(server, "SELECT count(*) FROM SocialInvites WHERE Id = $id", pending));
        Assert.Equal(1L, await Number(server, "SELECT count(*) FROM BoardMemberships WHERE TableId = $id", waiting));
        await Unavailable(Act(host, joined, "accept"));
        await Joined(Act(recipient, joined, "accept"), "chess", chess);
        Assert.True(JsonElement.DeepEquals(original, await Read(recipient, path)));
        Assert.Equal(2L, await Number(server, "SELECT count(*) FROM BoardMemberships WHERE TableId = $id", chess));
        Assert.Equal(receiptExpiry, await Number(server, "SELECT ExpiresAtTicks FROM SocialInvites WHERE Id = $id", joined));
    }

    [Fact]
    public async Task SeatedLinkRaceCannotClaimItsSeatWasGrantedBySocialAccept()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var host = await Guest(server, "Host");
        var recipient = await Guest(server, "Receiver");
        await Pulse(recipient, available: true);
        var handle = Player(await Pulse(host, busy: true, available: true), "Receiver");
        var api = Api + "/words";
        var table = await Post(host, api + "/tables", new { players = 2, bots = false });
        var id = table.Text("id");
        var token = await Offer(host, handle, "word-tiles", id);
        var results = await Task.WhenAll(Act(recipient, token, "accept"),
            recipient.Request(HttpMethod.Post, api + "/join", new { invite = table.Text("invite") }));
        var linked = await Ok(results[1]);
        var accepted = results[0].IsSuccessStatusCode;
        if (accepted) await Joined(Task.FromResult(results[0]), "word-tiles", id);
        else await Fault(Task.FromResult(results[0]), HttpStatusCode.Conflict, "already_seated");
        Assert.Equal(accepted ? 1L : 0L, await Number(server, "SELECT Accepted FROM SocialInvites WHERE Id = $id", token));
        Assert.Equal(2L, await Number(server, "SELECT count(*) FROM SeatedMemberships WHERE TableId = $id", id));
        Assert.Equal(1, linked.GetProperty("seat").GetInt32());
        Assert.Equal(1, linked.GetProperty("revision").GetInt32());
        Assert.True(JsonElement.DeepEquals(linked, await Read(recipient, api + "/tables/" + id)));
        var closed = await Post(host, api + "/tables/" + id + "/leave", new { revision = 1 });
        Assert.Equal("closed", closed.Text("status"));
        await Unavailable(Act(recipient, token, "accept"));

        var waiting = await Guest(server, "Waiting");
        var linkedGuest = await Guest(server, "Linked");
        await Pulse(waiting, available: true);
        var next = await Post(host, api + "/tables", new { players = 2, bots = false });
        var nextId = next.Text("id");
        var pending = await Offer(host, Player(await Pulse(host, busy: true), "Waiting"), "word-tiles", nextId);
        Assert.Equal("playing", (await Post(linkedGuest, api + "/join", new { invite = next.Text("invite") })).Text("status"));
        await Unavailable(Act(waiting, pending, "accept"));
        Assert.Equal(0L, await Number(server, "SELECT Accepted FROM SocialInvites WHERE Id = $id", pending));
        Assert.Equal("Linked", (await Read(host, api + "/tables/" + nextId)).GetProperty("seats")[1].Text("name"));
    }

    [Fact]
    public async Task MahjongStagesExpiredMembershipAndPublishesACommittedRosterWithoutAttachingATab()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var host = await Guest(server, "Original host");
        var recipient = await Guest(server, "Invited");
        var peer = await Guest(server, "Linked peer");
        using var owner = await host.Connect(Ws);
        using var background = await recipient.Connect(Ws);
        using var linked = await peer.Connect(Ws);
        var room = await NewRoom(owner);
        var roomId = room.Text("roomId");
        (await linked.Command("joinRoom", new { inviteCode = room.Private().Text("inviteCode") })).Ok();
        var occupied = await owner.Snapshot(s => s.Chair(1).Text("displayName") == "Linked peer");
        Assert.Equal("room_not_found", (await background.Sync(roomId)).Error());
        var previous = (await NewRoom(background)).Text("roomId");
        await Pulse(recipient, available: true);
        var nonhost = Player(await Pulse(peer, busy: true, available: true), "Invited");
        await Unavailable(Send(peer, nonhost, "mahjong", roomId));
        var handle = Player(await Pulse(host, busy: true, available: true), "Invited");
        var token = await Offer(host, handle, "mahjong", roomId);
        await Fault(Act(recipient, token, "accept"), HttpStatusCode.Conflict, "already_seated");
        await Sql(server, "UPDATE Rooms SET ExpiresAtTicks = 1 WHERE Id = $id", previous);
        await PreventReceipt(server);
        await Fault(Act(recipient, token, "accept"), HttpStatusCode.TooManyRequests, "busy");
        Assert.Equal(1L, await Number(server, "SELECT count(*) FROM Rooms WHERE Id = $id", previous));
        Assert.Equal(1L, await Number(server, "SELECT count(*) FROM Memberships WHERE RoomId = $id", previous));
        Assert.Equal(2L, await Number(server, "SELECT OccupiedSeats FROM Rooms WHERE Id = $id", roomId));
        Assert.Equal(0L, await Number(server, "SELECT Accepted FROM SocialInvites WHERE Id = $id", token));
        await Sql(server, "DROP TRIGGER RejectTestReceipt");
        await Joined(Act(recipient, token, "accept"), "mahjong", roomId);
        var joined = await owner.Snapshot(s => s.Chair(2).Text("displayName") == "Invited");
        Assert.Equal(occupied.RoomRevision() + 1, joined.RoomRevision());
        Assert.True(joined.Connected(0));
        Assert.False(joined.Connected(2));
        Assert.Equal("room_not_found", (await background.Take("error")).Error());
        Assert.Equal(0L, await Number(server, "SELECT count(*) FROM Rooms WHERE Id = $id", previous));
        Assert.Equal(3L, await Number(server, "SELECT count(*) FROM Memberships WHERE RoomId = $id", roomId));
        Assert.Equal(roomId, (await Post(recipient, Api + "/guest", new { })).Text("resumeRoomId"));
        using (var arriving = await recipient.Connect(Ws))
        {
            (await arriving.Sync(roomId)).Ok();
            var own = await arriving.Snapshot();
            Assert.Equal(2, own.Private().GetProperty("seat").GetInt32());
            Assert.Equal(JsonValueKind.Null, own.Private().GetProperty("inviteCode").ValueKind);
            var visible = await owner.Snapshot(s => s.Connected(2));
            Assert.Equal(joined.RoomRevision(), visible.RoomRevision());
            await Joined(Act(recipient, token, "accept"), "mahjong", roomId);
            (await arriving.Command("leaveRoom", new { roomId, expectedRoomRevision = visible.RoomRevision() })).Ok();
        }
        await Unavailable(Act(recipient, token, "accept"));
        (await owner.Command("chat", new { roomId, text = "Still the host" })).Ok();
    }

    [Fact]
    public async Task PendingInviteCannotJoinAFormerHostsRoomOrAFullAndStartedRoom()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var host = await Guest(server, "Original");
        var successor = await Guest(server, "Successor");
        var recipient = await Guest(server, "Receiver");
        using var first = await host.Connect(Ws);
        using var second = await successor.Connect(Ws);
        var room = await NewRoom(first);
        var id = room.Text("roomId");
        (await second.Command("joinRoom", new { inviteCode = room.Private().Text("inviteCode") })).Ok();
        var joined = await first.Snapshot(s => s.Chair(1).Text("displayName") == "Successor");
        await Pulse(recipient, available: true);
        var token = await Offer(host, Player(await Pulse(host, busy: true, available: true), "Receiver"), "mahjong", id);
        (await first.Command("leaveRoom", new { roomId = id, expectedRoomRevision = joined.RoomRevision() })).Ok();
        var transfer = await second.Snapshot(s => s.RoomRevision() > joined.RoomRevision());
        await Unavailable(Act(recipient, token, "accept"));
        var other = await Offer(successor, Player(await Pulse(successor, busy: true, available: true), "Receiver"), "mahjong", id);
        foreach (var seat in new[] { 0, 2, 3 })
        {
            (await second.Command("setBot", new { roomId = id, expectedRoomRevision = transfer.RoomRevision(), seat, enabled = true })).Ok();
            transfer = await second.Snapshot(s => s.RoomRevision() > transfer.RoomRevision());
        }
        await Unavailable(Act(recipient, other, "accept"));
        (await second.Command("setReady", new { roomId = id, expectedRoomRevision = transfer.RoomRevision(), ready = true })).Ok();
        var ready = await second.Snapshot(s => s.Chair(1).GetProperty("ready").GetBoolean());
        (await second.Command("start", new { roomId = id, expectedRoomRevision = ready.RoomRevision() })).Ok();
        Assert.Equal("playing", (await second.Snapshot(s => s.Status() == "playing")).Status());
        await Unavailable(Act(recipient, other, "accept"));
        Assert.Equal(1L, await Number(server, "SELECT count(*) FROM Memberships WHERE RoomId = $id", id));
        Assert.Equal(0L, await Number(server, "SELECT Accepted FROM SocialInvites WHERE Id = $id", other));
    }

    [Fact]
    public async Task AdditiveMigrationPreservesActualBaselineSavesAndDefaultsExistingGuestsOff()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var old = await Guest(server, "Existing profile");
        var peer = await Guest(server, "Legacy peer");
        var board = await CreateBoard(old, "chess", "standard");
        await Post(peer, Board + "/join", new { invite = board.Text("invite") });
        var boardPath = Board + "/tables/" + board.Text("id");
        board = await Read(old, boardPath);
        var poker = await Post(old, Api + "/poker/tables", new { players = 2, bots = false });
        await Post(peer, Api + "/poker/join", new { invite = poker.Text("invite") });
        var pokerPath = Api + "/poker/tables/" + poker.Text("id");
        poker = await Read(old, pokerPath);
        await Post(old, Api + "/solo/solitaire", new { variant = "draw-one" });
        var solo = await Post(old, Api + "/solo/solitaire/moves", new { revision = 0, action = new { kind = "draw" } });
        string roomId;
        using (var client = await old.Connect(Ws)) roomId = (await NewRoom(client)).Text("roomId");
        await server.StopAsync();

        await using (var db = new MahjongDb(new DbContextOptionsBuilder<MahjongDb>()
            .UseSqlite(Connection(server)).Options))
            await db.GetService<IMigrator>().MigrateAsync("20261122022500_SoloSaves");
        Assert.Equal(0L, await Number(server, "SELECT count(*) FROM sqlite_schema WHERE name = 'SocialInvites'"));
        Assert.Equal(0L, await Number(server, "SELECT count(*) FROM pragma_table_info('Guests') WHERE name = 'SocialAvailable'"));

        await server.StartAsync();
        Assert.True(JsonElement.DeepEquals(board, await Read(old, boardPath)));
        Assert.True(JsonElement.DeepEquals(poker, await Read(old, pokerPath)));
        Assert.True(JsonElement.DeepEquals(solo, (await Read(old, Api + "/solo/solitaire")).GetProperty("table")));
        Assert.Equal("Existing profile", (await Post(old, Api + "/guest", new { })).Text("displayName"));
        Assert.False((await Pulse(old)).GetProperty("available").GetBoolean());
        using var restored = await old.Connect(Ws);
        (await restored.Sync(roomId)).Ok();
        var snapshot = await restored.Snapshot();
        Assert.Equal("Existing profile", snapshot.Chair(0).Text("displayName"));
        Assert.Equal(0, snapshot.Private().GetProperty("seat").GetInt32());
        Assert.Equal(0L, await Number(server, "SELECT count(*) FROM pragma_foreign_key_check"));
    }

    private static Task<JsonElement> Pulse(User user, string tab = "main", bool busy = false, bool active = true,
        bool? available = null) => Post(user, Presence, available is { } value ? new { tabId = tab, active, busy, available = value }
        : (object)new { tabId = tab, active, busy });

    private static Task<JsonElement> CreateBoard(User user, string game, string variant, int players = 2) =>
        Post(user, Board + "/tables", new { game, variant, players, bots = false });

    private static Task<HttpResponseMessage> Send(User user, string playerId, string game, string tableId) =>
        user.Request(HttpMethod.Post, Invites, new { playerId, game, tableId });

    private static string InvitePath(string token, string action) => Invites + "/" + token + "/" + action;
    private static Task<HttpResponseMessage> Act(User user, string token, string action) =>
        user.Request(HttpMethod.Post, InvitePath(token, action), new { });

    private static async Task<string> Offer(User user, string playerId, string game, string tableId)
    {
        var body = await SocialOk(Send(user, playerId, game, tableId));
        Fields(body, "id");
        return body.Text("id");
    }

    private static async Task Joined(Task<HttpResponseMessage> response, string game, string id)
    {
        var body = await SocialOk(response);
        Fields(body, "game", "tableId");
        Assert.Equal(game, body.Text("game"));
        Assert.Equal(id, body.Text("tableId"));
    }

    private static async Task Confirm(Task<HttpResponseMessage> response)
    {
        var body = await SocialOk(response);
        Fields(body, "ok");
        Assert.True(body.GetProperty("ok").GetBoolean());
    }

    private static async Task<JsonElement> SocialOk(Task<HttpResponseMessage> response)
    {
        using var result = await response;
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        return await Ok(result);
    }

    private static Task Unavailable(Task<HttpResponseMessage> response) => Fault(response, HttpStatusCode.NotFound, "invite_unavailable");

    private static async Task Fault(Task<HttpResponseMessage> response, HttpStatusCode status, string code)
    {
        using var result = await response;
        Assert.Equal(status, result.StatusCode);
        Assert.True(result.Headers.CacheControl?.NoStore);
        var json = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        using (json)
        {
            Fields(json.RootElement, "error");
            Fields(json.RootElement.GetProperty("error"), "code");
            Assert.Equal(code, json.RootElement.Error());
        }
    }

    private static string Player(JsonElement presence, string name)
    {
        var value = Assert.Single(List(presence, "players"), p => p.Text("displayName") == name);
        Fields(value, "displayName", "id");
        return value.Text("id");
    }

    private static JsonElement[] List(JsonElement body, string key) => body.GetProperty(key).EnumerateArray().ToArray();
    private static (int Rank, int Suit) Card(JsonElement value) =>
        (value.GetProperty("rank").GetInt32(), value.GetProperty("suit").GetInt32());
    private static void Fields(JsonElement json, params string[] fields) =>
        Assert.Equal(fields.Order(StringComparer.Ordinal), json.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

    private static async Task<JsonElement> NewRoom(Client client)
    {
        (await client.Command("createRoom", new { visibility = "invite", matchHands = 1 })).Ok();
        return await client.Snapshot();
    }

    private static Task<JsonElement> Read(User guest, string path) => SocialOk(guest.Request(HttpMethod.Get, path));
    private static Task<JsonElement> Post(User guest, string path, object body) => SocialOk(guest.Request(HttpMethod.Post, path, body));

    private static Task<object?> PreventReceipt(Kestrel server) => Sql(server, """
        CREATE TRIGGER RejectTestReceipt BEFORE UPDATE OF Accepted ON SocialInvites
        WHEN NEW.Accepted = 1 BEGIN SELECT RAISE(ABORT, 'synthetic receipt failure'); END
        """);

    private static async Task<long> Number(Kestrel server, string sql, string? id = null) =>
        Assert.IsType<long>(await Sql(server, sql, id));

    private static string Connection(Kestrel server) => new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(server.DataDirectory, "mahjong.db"), ForeignKeys = true, Pooling = false
    }.ConnectionString;

    private static async Task<object?> Sql(Kestrel server, string sql, string? id = null)
    {
        await using var db = new SqliteConnection(Connection(server));
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteScalarAsync();
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _utc = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _utc;
        public void Advance(int seconds) => _utc += TimeSpan.FromSeconds(seconds);
    }
}
