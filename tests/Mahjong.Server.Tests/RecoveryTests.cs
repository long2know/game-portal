using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mahjong.Server.Tests;

public sealed class RecoveryTests
{
    [Fact]
    public async Task PrivateViewsAndCommittedChoiceSurviveLostAckAndProcessRestart()
    {
        await using var server = new Kestrel(("TurnSeconds", 18), ("ClaimSeconds", 4), ("BotDelayMilliseconds", 5000));
        await server.StartAsync();
        var hostUser = new User(server);
        var otherUser = new User(server);
        await hostUser.Guest("Dealer");
        await otherUser.Guest("Other");
        using (var preferences = await hostUser.Request(HttpMethod.Patch, "/api/profile",
                   new { preferences = new { sound = false, music = true, volume = 0.3, reducedMotion = true } }))
            preferences.EnsureSuccessStatusCode();

        using var host = await hostUser.Connect();
        using var other = await otherUser.Connect();
        var turn = await DealerTurn(host, await Start(host, other));
        var observerTurn = await other.Snapshot(IsDealerTurn);
        var roomId = turn.Text("roomId");
        Assert.Equal(roomId, observerTurn.Text("roomId"));
        Assert.Equal(turn.Table().GetRawText(), observerTurn.Table().GetRawText());
        var ownHand = turn.Private().GetProperty("hand");
        var otherHand = observerTurn.Private().GetProperty("hand");
        Assert.Equal(14, ownHand.GetArrayLength());
        Assert.Equal(13, otherHand.GetArrayLength());
        Assert.Empty(ownHand.EnumerateArray().Select(tile => tile.Text("tileId"))
            .Intersect(otherHand.EnumerateArray().Select(tile => tile.Text("tileId"))));
        Assert.Empty(observerTurn.Private().GetProperty("legalOptions").EnumerateArray());
        Assert.Null(observerTurn.Private().Text("inviteCode"));
        AssertRedacted(turn);
        AssertRedacted(observerTurn);
        var drawn = turn.Private().Text("drawnTileId");
        var discard = Assert.Single(turn.Private().GetProperty("legalOptions").EnumerateArray(),
            option => option.Text("kind") == "discard" && option.GetProperty("tileIds")[0].GetString() == drawn);
        var face = Assert.Single(ownHand.EnumerateArray(), tile => tile.Text("tileId") == drawn).Text("face");
        var intent = Choice(turn, discard);
        var id = Guid.NewGuid();
        var originalLease = host.Lease;
        await host.Send(new { v = 1, type = "command", requestId = id, lease = originalLease, op = "choose", payload = intent });
        var committed = await other.Snapshot(s => s.GameRevision() > turn.GameRevision());
        Assert.Equal(turn.GameRevision() + 1, committed.GameRevision());
        Assert.Contains(committed.Table().GetProperty("seats")[0].GetProperty("discards").EnumerateArray(), tile => tile.GetString() == face);

        // The observer proves commit. Do not read the sender's ack before killing the process.
        await server.StopAsync();
        await server.StartAsync();
        var resumedGuest = await hostUser.Guest("Ignored rename");
        Assert.Equal("Dealer", resumedGuest.Text("displayName"));
        Assert.Equal(roomId, resumedGuest.Text("resumeRoomId"));
        Assert.True(resumedGuest.At("preferences", "music").GetBoolean());
        Assert.True(resumedGuest.At("preferences", "reducedMotion").GetBoolean());
        Assert.Equal(0.3, resumedGuest.At("preferences", "volume").GetDouble());

        using var reconnected = await hostUser.Connect();
        (await reconnected.Sync(roomId)).Ok();
        var recovered = await reconnected.Snapshot();
        Assert.Equal(committed.GameRevision(), recovered.GameRevision());
        Assert.Equal(committed.Table().GetRawText(), recovered.Table().GetRawText());
        Assert.Equal(13, recovered.Private().GetProperty("hand").GetArrayLength());
        Assert.False(recovered.Connected(1));
        Assert.True(recovered.Connected(0));
        var replay = await reconnected.Command("choose", new
        {
            optionId = intent.optionId, anchorGameRevision = intent.anchorGameRevision,
            windowId = intent.windowId, roomId = intent.roomId
        }, id, originalLease);
        replay.Ok();
        Assert.Equal(committed.GameRevision(), replay.GameRevision());
        Assert.Equal(committed.RoomRevision(), replay.RoomRevision());
        Assert.Equal(committed.GameRevision(), (await reconnected.Snapshot()).GameRevision());
        Assert.Equal("request_conflict", (await reconnected.Command("choose", intent with { optionId = "different" },
            id, originalLease)).Error());
        Assert.Equal("stale_window", (await reconnected.Command("choose", intent)).Error());
    }

    [Fact]
    public async Task RestartsThroughScheduledResumeAndExpiresTheOriginalWindowExactlyOnce()
    {
        await using var server = new Kestrel(("TurnSeconds", 6), ("ClaimSeconds", 4),
            ("BotDelayMilliseconds", 5000), ("PauseSeconds", 2));
        await server.StartAsync();
        var user = new User(server);
        await user.Guest();
        using var host = await user.Connect();
        var turn = await DealerTurn(host, await Start(host));
        var roomId = turn.Text("roomId");
        var originalDeadline = turn.Window().GetProperty("deadlineUtc").GetDateTimeOffset();
        var originalWindow = turn.Window().Text("windowId");
        (await host.Command("setPaused", new { roomId, expectedRoomRevision = turn.RoomRevision(), paused = true })).Ok();
        var paused = await host.Snapshot(s => s.At("room", "paused").GetBoolean());
        Assert.Equal(originalWindow, paused.Window().Text("windowId"));
        Assert.Null(paused.Window().Text("deadlineUtc"));
        Assert.Empty(paused.Private().GetProperty("legalOptions").EnumerateArray());
        Assert.Equal(turn.GameRevision(), paused.GameRevision());
        var scheduledResume = paused.At("room", "autoResumeAtUtc").GetDateTimeOffset();
        await server.StopAsync();
        await WaitUntil(scheduledResume.AddMilliseconds(100));
        await server.StartAsync();
        using var resumed = await user.Connect();
        (await resumed.Sync(roomId)).Ok();
        var unpaused = await resumed.Snapshot();
        Assert.False(unpaused.At("room", "paused").GetBoolean());
        Assert.Equal(paused.RoomRevision() + 1, unpaused.RoomRevision());
        Assert.Equal(paused.GameRevision(), unpaused.GameRevision());
        Assert.Equal(originalWindow, unpaused.Window().Text("windowId"));
        var reclaimedDeadline = unpaused.Window().GetProperty("deadlineUtc").GetDateTimeOffset();
        Assert.Equal(originalDeadline.AddSeconds(2), reclaimedDeadline);

        await server.StopAsync();
        await WaitUntil(reclaimedDeadline.AddMilliseconds(30));
        await server.StartAsync();
        using var expired = await user.Connect();
        (await expired.Sync(roomId)).Ok();
        var advanced = await expired.Snapshot();
        Assert.Equal(turn.GameRevision() + 1, advanced.GameRevision());
        Assert.NotEqual(originalWindow, advanced.Window().Text("windowId"));
        Assert.Single(advanced.Table().GetProperty("seats")[0].GetProperty("discards").EnumerateArray());
        var revision = advanced.GameRevision();
        (await expired.Sync(roomId)).Ok();
        Assert.Equal(revision, (await expired.Snapshot()).GameRevision());
    }

    [Fact]
    public async Task PrunedReceiptCannotRecreateARoomEvenAfterClockRollback()
    {
        await using var server = new Kestrel();
        await server.StartAsync();
        var user = new User(server);
        await user.Guest();
        using var first = await user.Connect();
        var create = new { visibility = "public", matchHands = 1 };
        var id = Guid.NewGuid();
        (await first.Command("createRoom", create, id)).Ok();
        var initial = await first.Snapshot();
        var result = await first.Command("leaveRoom", new { roomId = initial.Text("roomId"), expectedRoomRevision = initial.RoomRevision() });
        result.Ok();
        (await first.Command("createRoom", new { matchHands = 1, fillBots = false, visibility = "public" }, id)).Ok();
        Assert.Null((await user.Guest()).Text("resumeRoomId"));
        Assert.Equal(2L, await ReadScalar(server, "SELECT COUNT(*) FROM Receipts"));
        (await first.Command("createRoom", create)).Ok();
        Assert.NotEqual(initial.Text("roomId"), (await first.Snapshot()).Text("roomId"));
        await server.StopAsync();
        server.SetClockOffset(90_000);
        await server.StartAsync();
        // The real recovery/prune transaction drops the 24-hour room and expired receipts.
        Assert.Equal(0L, await ReadScalar(server, "SELECT COUNT(*) FROM Receipts"));
        Assert.True((long)(await ReadScalar(server, "SELECT LeasePruneTicks FROM Clock"))! >= first.LeaseExpiry.UtcTicks);
        Assert.Equal(0L, await ReadScalar(server, "SELECT COUNT(*) FROM Memberships"));
        await server.StopAsync();
        server.SetClockOffset(-90_000);
        await server.StartAsync();
        using var resumed = await user.Connect();
        Assert.True(resumed.LeaseExpiry > first.LeaseExpiry);
        Assert.Equal("request_expired", (await resumed.Command("createRoom", create, id, first.Lease)).Error());
        Assert.Null((await user.Guest()).Text("resumeRoomId"));
        Assert.Equal(0L, await ReadScalar(server, "SELECT COUNT(*) FROM Rooms"));
        (await resumed.Command("createRoom", create)).Ok();
    }

    [Fact]
    public async Task RealBotMatchSettlesFourHandsThenRematchesWithFreshWindows()
    {
        await using var server = new Kestrel(("TurnSeconds", 3), ("ClaimSeconds", 1), ("BotDelayMilliseconds", 15));
        await server.StartAsync();
        var user = new User(server);
        await user.Guest();
        using var host = await user.Connect();
        var snapshot = await Start(host, hands: 4);
        var firstWindow = snapshot.Window().Text("windowId");
        var roomId = snapshot.Text("roomId");
        var oldAnchor = snapshot.Window().GetProperty("anchorGameRevision").GetInt64();
        var totals = new int[4];
        var dealer = 0;
        for (var hand = 1; hand <= 4; hand++)
        {
            Assert.Equal(hand, snapshot.Table().GetProperty("handNumber").GetInt32());
            Assert.Equal(dealer, snapshot.Table().GetProperty("dealerSeat").GetInt32());
            Assert.Equal(JsonValueKind.Null, snapshot.Table().GetProperty("settlement").ValueKind);
            Assert.Equal(JsonValueKind.Null, snapshot.Table().GetProperty("kongRoll").ValueKind);
            var settled = await PlayHand(host, snapshot);
            Assert.Equal(hand == 4 ? "complete" : "betweenHands", settled.Status());
            Assert.Equal(JsonValueKind.Null, settled.Window().ValueKind);
            Assert.Equal(JsonValueKind.Null, settled.Table().GetProperty("kongRoll").ValueKind);
            AssertRedacted(settled);
            var settlement = settled.Table().GetProperty("settlement");
            var winners = settlement.GetProperty("winners").EnumerateArray().ToArray();
            Assert.Equal(settlement.Text("reason") == "draw", winners.Length == 0);
            foreach (var winner in winners)
            {
                var chair = settled.Table().GetProperty("seats")[winner.GetProperty("seat").GetInt32()];
                var winningHand = winner.GetProperty("winningHand");
                var nonFinalCount = chair.GetProperty("concealedCount").GetInt32() - (winner.Text("winBy") == "selfDraw" ? 1 : 0);
                Assert.Equal(nonFinalCount, winningHand.GetProperty("concealedTiles").GetArrayLength());
                Assert.Equal(chair.GetProperty("melds").GetArrayLength(), winningHand.GetProperty("melds").GetArrayLength());
                Assert.Single(winner.GetProperty("winningTiles").EnumerateArray());
            }
            var seatResults = settlement.GetProperty("seatResults").EnumerateArray().ToArray();
            Assert.Equal(0, seatResults.Sum(result => result.GetProperty("delta").GetInt32()));
            for (var seat = 0; seat < 4; seat++)
            {
                totals[seat] += seatResults[seat].GetProperty("delta").GetInt32();
                Assert.Equal(totals[seat], seatResults[seat].GetProperty("total").GetInt32());
                Assert.Equal(totals[seat], settled.Table().GetProperty("seats")[seat].GetProperty("total").GetInt32());
            }
            if (settlement.TryGetProperty("transfers", out var transfers))
            {
                var net = new int[4];
                foreach (var transfer in transfers.EnumerateArray())
                {
                    var points = transfer.GetProperty("points").GetInt32();
                    Assert.True(points > 0);
                    net[transfer.GetProperty("fromSeat").GetInt32()] -= points;
                    net[transfer.GetProperty("toSeat").GetInt32()] += points;
                }
                Assert.Equal(seatResults.Select(result => result.GetProperty("delta").GetInt32()).ToArray(), net);
            }
            dealer = settlement.GetProperty("nextDealerSeat").GetInt32();
            snapshot = settled;
            if (hand < 4)
            {
                (await host.Command("nextHand", new { roomId, expectedRoomRevision = settled.RoomRevision() })).Ok();
                snapshot = await host.Snapshot(s => s.Status() == "playing");
            }
        }

        (await host.Command("rematch", new { roomId, expectedRoomRevision = snapshot.RoomRevision() })).Ok();
        var waiting = await host.Snapshot(s => s.Status() == "waiting");
        Assert.Equal(snapshot.RoomRevision() + 1, waiting.RoomRevision());
        Assert.Equal(snapshot.GameRevision() + 1, waiting.GameRevision());
        Assert.Equal(JsonValueKind.Null, waiting.Table().ValueKind);
        Assert.False(waiting.Chair(0).GetProperty("ready").GetBoolean());
        Assert.Equal(snapshot.Private().Text("inviteCode"), waiting.Private().Text("inviteCode"));
        for (var seat = 1; seat < 4; seat++) Assert.Equal("bot", waiting.Chair(seat).Text("kind"));
        (await host.Command("setReady", new { roomId, expectedRoomRevision = waiting.RoomRevision(), ready = true })).Ok();
        waiting = await host.Snapshot();
        (await host.Command("start", new { roomId, expectedRoomRevision = waiting.RoomRevision() })).Ok();
        var rematch = await host.Snapshot(s => s.Status() == "playing");
        Assert.Equal(1, rematch.Table().GetProperty("handNumber").GetInt32());
        Assert.Equal(JsonValueKind.Null, rematch.Table().GetProperty("settlement").ValueKind);
        Assert.NotEqual(firstWindow, rematch.Window().Text("windowId"));
        Assert.All(rematch.Table().GetProperty("seats").EnumerateArray(), seat => Assert.Equal(0, seat.GetProperty("total").GetInt32()));
        Assert.Equal("stale_window", (await host.Command("choose",
            new ChoicePayload(roomId, firstWindow, oldAnchor, "cannot-reuse"))).Error());
    }

    [Fact]
    public async Task PausedBotMatchRestartsInPlaceAndRecoversItsFreshDecisionOnThePortalMount()
    {
        await using var server = new Kestrel(("TurnSeconds", 60), ("ClaimSeconds", 2), ("BotDelayMilliseconds", 5000))
            .WithSetting("BASE_URL", "/portal").WithSetting("Portal__Enabled", "true");
        server.WithSetting("GamePortal__DataDirectory", server.DataDirectory);
        await server.StartAsync();
        var user = new User(server, "/portal");
        await user.Guest("Host");
        using var host = await user.Connect();
        var turn = await DealerTurn(host, await Start(host, hands: 4));
        var roomId = turn.Text("roomId");
        var oldChoice = Choice(turn, turn.Private().GetProperty("legalOptions").EnumerateArray()
            .First(option => option.Text("kind") == "discard"));
        (await host.Command("chat", new { roomId, text = "<b>Keep this as text</b>" })).Ok();
        var chatted = await host.Snapshot();
        Assert.Equal("<b>Keep this as text</b>", chatted.At("room", "chat")[0].Text("text"));
        (await host.Command("setPaused", new { roomId, expectedRoomRevision = chatted.RoomRevision(), paused = true })).Ok();
        var paused = await host.Snapshot();
        Assert.True(paused.At("room", "paused").GetBoolean());
        Assert.Null(paused.Window().Text("deadlineUtc"));
        Assert.Equal(turn.Window().Text("windowId"), paused.Window().Text("windowId"));

        var expectedRoomRevision = paused.RoomRevision();
        var restart = new { roomId, expectedRoomRevision };
        Assert.Equal("stale_room", (await host.Command("restartMatch",
            new { roomId, expectedRoomRevision = expectedRoomRevision - 1 })).Error());
        Assert.Equal("stale_room", (await host.Command("leaveRoom",
            new { roomId, expectedRoomRevision = expectedRoomRevision - 1, abandon = true })).Error());
        Assert.Equal("invalid_request", (await host.Command("leaveRoom",
            new { roomId, expectedRoomRevision, abandon = "true" })).Error());
        Assert.Equal("invalid_request", (await host.Command("leaveRoom",
            new { roomId, expectedRoomRevision, abandon = (bool?)null })).Error());
        using (var unattached = await user.Connect())
        {
            foreach (var ack in new[]
            {
                await unattached.Command("restartMatch", restart),
                await unattached.Command("leaveRoom", new { roomId, expectedRoomRevision, abandon = true })
            })
            {
                Assert.Equal("invalid_state", ack.Error());
                Assert.Null(ack.Text("roomId"));
            }
        }

        var requestId = Guid.NewGuid();
        var lease = host.Lease;
        await host.Send(new { v = 1, type = "command", requestId, lease, op = "restartMatch", payload = restart });
        var committed = await host.Take();
        Assert.Equal("ack", committed.Text("type"));
        Assert.Equal(requestId, committed.GetProperty("requestId").GetGuid());
        committed.Ok();
        var fresh = await host.Take();
        Assert.Equal("snapshot", fresh.Text("type"));
        AssertFreshMatch(paused, fresh);
        Assert.Equal(fresh.RoomRevision(), committed.RoomRevision());
        Assert.Equal(fresh.GameRevision(), committed.GameRevision());
        Assert.NotEqual(oldChoice.windowId, fresh.Window().Text("windowId"));
        var freshLimit = fresh.Window().Text("reason") == "opening" ? 2 : 60;
        Assert.Equal(fresh.GetProperty("serverTimeUtc").GetDateTimeOffset().AddSeconds(freshLimit),
            fresh.Window().GetProperty("deadlineUtc").GetDateTimeOffset());
        var dealerTurn = await DealerTurn(host, fresh);

        await server.StopAsync();
        await server.StartAsync();
        Assert.Equal(roomId, (await user.Guest()).Text("resumeRoomId"));
        using var resumed = await user.Connect();
        var replay = await resumed.Command("restartMatch", restart, requestId, lease);
        Assert.Equal(committed.GetRawText(), replay.GetRawText());
        await AssertNoSnapshotAfterAck(resumed);
        (await resumed.Sync(roomId)).Ok();
        var recovered = await resumed.Snapshot();
        Assert.Equal(dealerTurn.RoomRevision(), recovered.RoomRevision());
        Assert.Equal(dealerTurn.GameRevision(), recovered.GameRevision());
        Assert.Equal(dealerTurn.Table().GetRawText(), recovered.Table().GetRawText());
        Assert.Equal(dealerTurn.Private().GetRawText(), recovered.Private().GetRawText());
        Assert.Equal("stale_window", (await resumed.Command("choose", oldChoice)).Error());
        var discard = recovered.Private().GetProperty("legalOptions").EnumerateArray()
            .First(option => option.Text("kind") == "discard");
        var played = await resumed.Command("choose", Choice(recovered, discard));
        played.Ok();
        Assert.Equal(recovered.GameRevision() + 1, played.GameRevision());
    }

    [Theory]
    [InlineData(1, "waiting")]
    [InlineData(1, "playing")]
    [InlineData(4, "betweenHands")]
    [InlineData(1, "complete")]
    public async Task SoleHostCanAbandonBotRoomWithoutReservationAcrossRecovery(int hands, string status)
    {
        var settle = status is "betweenHands" or "complete";
        await using var server = new Kestrel(("TurnSeconds", 30), ("ClaimSeconds", settle ? 2 : 8),
            ("BotDelayMilliseconds", settle ? 15 : 5000));
        await server.StartAsync();
        var user = new User(server);
        await user.Guest("Host");
        using (var profile = await user.Request(HttpMethod.Patch, "/api/profile",
                   new { preferences = new { sound = false, music = false, volume = 0.25, reducedMotion = true } }))
            profile.EnsureSuccessStatusCode();
        using var host = await user.Connect();
        JsonElement current;
        if (status == "waiting")
        {
            (await host.Command("createRoom", new { visibility = "invite", matchHands = hands, fillBots = true })).Ok();
            current = await host.Snapshot();
            Assert.Equal("invalid_state", (await host.Command("restartMatch",
                new { roomId = current.Text("roomId"), expectedRoomRevision = current.RoomRevision() })).Error());
            Assert.Equal(JsonValueKind.Null, (await host.Snapshot()).Table().ValueKind);
        }
        else current = await Start(host, hands: hands);
        var roomId = current.Text("roomId");
        var inviteCode = current.Private().Text("inviteCode");
        if (settle)
        {
            var settled = await PlayHand(host, current);
            Assert.Equal(status, settled.Status());
            (await host.Command("restartMatch", new { roomId, expectedRoomRevision = settled.RoomRevision() })).Ok();
            var fresh = await host.Snapshot();
            AssertFreshMatch(settled, fresh);
            current = await PlayHand(host, fresh);
        }
        Assert.Equal(status, current.Status());

        var quit = new { roomId, expectedRoomRevision = current.RoomRevision(), abandon = true };
        var requestId = Guid.NewGuid();
        var lease = host.Lease;
        var left = await host.Command("leaveRoom", quit, requestId);
        left.Ok();
        Assert.Equal(roomId, left.Text("roomId"));
        Assert.Equal(current.RoomRevision() + 1, left.RoomRevision());
        Assert.Equal(current.GameRevision(), left.GameRevision());
        await AssertNoSnapshotAfterAck(host);
        Assert.Null((await user.Guest()).Text("resumeRoomId"));

        await server.StopAsync();
        await server.StartAsync();
        var guest = await user.Guest("Ignored rename");
        Assert.Null(guest.Text("resumeRoomId"));
        Assert.Equal("Host", guest.Text("displayName"));
        Assert.Equal(0.25, guest.At("preferences", "volume").GetDouble());
        Assert.True(guest.At("preferences", "reducedMotion").GetBoolean());
        using var resumed = await user.Connect();
        var replay = await resumed.Command("leaveRoom", quit, requestId, lease);
        Assert.Equal(left.GetRawText(), replay.GetRawText());
        await AssertNoSnapshotAfterAck(resumed);
        Assert.Equal("request_conflict", (await resumed.Command("leaveRoom", quit with { abandon = false }, requestId, lease)).Error());
        Assert.Equal("room_not_found", (await resumed.Sync(roomId)).Error());
        var retiredInvite = await resumed.Command("joinRoom", new { inviteCode });
        Assert.Equal("room_not_found", retiredInvite.Error());
        Assert.Null(retiredInvite.Text("roomId"));
        (await resumed.Command("createRoom", new { visibility = "public", matchHands = 1 })).Ok();
        var replacement = await resumed.Snapshot();
        Assert.NotEqual(roomId, replacement.Text("roomId"));
        Assert.Equal(replacement.Text("roomId"), (await user.Guest()).Text("resumeRoomId"));
    }

    private static void AssertFreshMatch(JsonElement prior, JsonElement fresh)
    {
        Assert.Equal("playing", fresh.Status());
        Assert.Equal(prior.Text("roomId"), fresh.Text("roomId"));
        Assert.Equal(prior.RoomRevision() + 1, fresh.RoomRevision());
        Assert.Equal(prior.GameRevision() + 1, fresh.GameRevision());
        foreach (var preserved in new[] { "hostSeat", "visibility", "matchHands", "rulesVersion", "seats", "chat" })
            Assert.Equal(prior.At("room", preserved).GetRawText(), fresh.At("room", preserved).GetRawText());
        Assert.Equal(prior.Private().Text("inviteCode"), fresh.Private().Text("inviteCode"));
        Assert.Equal(prior.Private().GetProperty("seat").GetInt32(), fresh.Private().GetProperty("seat").GetInt32());
        Assert.False(fresh.At("room", "paused").GetBoolean());
        Assert.Equal(JsonValueKind.Null, fresh.At("room", "autoResumeAtUtc").ValueKind);
        Assert.Equal(1, fresh.Table().GetProperty("handNumber").GetInt32());
        Assert.Equal(0, fresh.Table().GetProperty("dealerSeat").GetInt32());
        Assert.Equal(55, fresh.Table().GetProperty("wallCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, fresh.Table().GetProperty("settlement").ValueKind);
        Assert.Equal(fresh.GameRevision(), fresh.Window().GetProperty("anchorGameRevision").GetInt64());
        Assert.All(fresh.Table().GetProperty("seats").EnumerateArray(), seat =>
        {
            Assert.Empty(seat.GetProperty("discards").EnumerateArray());
            Assert.Empty(seat.GetProperty("melds").EnumerateArray());
            Assert.Equal(0, seat.GetProperty("total").GetInt32());
        });
        var hand = fresh.Private().GetProperty("hand");
        Assert.Equal(14, hand.GetArrayLength());
        Assert.Empty(hand.EnumerateArray().Select(tile => tile.Text("tileId"))
            .Intersect(prior.Private().GetProperty("hand").EnumerateArray().Select(tile => tile.Text("tileId"))));
        AssertRedacted(fresh);
    }

    private static async Task AssertNoSnapshotAfterAck(Client client)
    {
        await client.Send(new { v = 1, type = "renewLease" });
        Assert.Equal("lease", (await client.Take()).Text("type"));
    }

    private static async Task<JsonElement> Start(Client host, Client? other = null, int hands = 1)
    {
        (await host.Command("createRoom", new { visibility = "invite", matchHands = hands, fillBots = other is null })).Ok();
        var snapshot = await host.Snapshot();
        var roomId = snapshot.Text("roomId");
        if (other is not null)
        {
            (await other.Command("joinRoom", new { inviteCode = snapshot.Private().Text("inviteCode") })).Ok();
            snapshot = await host.Snapshot(s => s.Connected(1));
            for (var seat = 2; seat <= 3; seat++)
            {
                (await host.Command("setBot", new { roomId, expectedRoomRevision = snapshot.RoomRevision(), seat, enabled = true })).Ok();
                snapshot = await host.Snapshot();
            }
            (await other.Command("setReady", new { roomId, expectedRoomRevision = snapshot.RoomRevision(), ready = true })).Ok();
            snapshot = await host.Snapshot(s => s.Chair(1).GetProperty("ready").GetBoolean());
        }
        (await host.Command("setReady", new { roomId, expectedRoomRevision = snapshot.RoomRevision(), ready = true })).Ok();
        snapshot = await host.Snapshot(s => s.Chair(0).GetProperty("ready").GetBoolean());
        (await host.Command("start", new { roomId, expectedRoomRevision = snapshot.RoomRevision() })).Ok();
        return await host.Snapshot(s => s.Status() == "playing");
    }

    private static Task<JsonElement> DealerTurn(Client host, JsonElement snapshot) =>
        IsDealerTurn(snapshot) ? Task.FromResult(snapshot) : host.Snapshot(IsDealerTurn);

    private static bool IsDealerTurn(JsonElement snapshot) =>
        snapshot.Status() == "playing" && snapshot.Window().Text("reason") == "turn"
        && snapshot.Table().GetProperty("turnSeat").GetInt32() == 0;

    private static async Task<JsonElement> PlayHand(Client host, JsonElement snapshot)
    {
        for (var transitions = 0; transitions < 500 && snapshot.Status() == "playing"; transitions++)
        {
            var table = snapshot.Table();
            var roll = table.GetProperty("kongRoll");
            if (roll.ValueKind != JsonValueKind.Null)
            {
                var dice = roll.GetProperty("dice").EnumerateArray().Select(die => die.GetInt32()).ToArray();
                Assert.Equal(2, dice.Length);
                Assert.All(dice, die => Assert.InRange(die, 1, 6));
                Assert.Equal(dice.Min(), roll.GetProperty("lowerDie").GetInt32());
                Assert.Equal(table.GetProperty("turnSeat").GetInt32(), roll.GetProperty("seat").GetInt32());
            }
            var options = snapshot.Private().GetProperty("legalOptions").EnumerateArray().ToArray();
            if (options.Length != 0)
            {
                var option = options.OrderBy(option => option.Text("kind") switch
                {
                    "hu" => 0, "reveal" => 1, "draw" => 2, "discard" => 3, "pass" => 4, _ => 5
                }).First();
                var ack = await host.Command("choose", Choice(snapshot, option));
                if (ack.Text("status") != "ok") Assert.Equal("stale_window", ack.Error());
            }
            var revision = snapshot.GameRevision();
            snapshot = await host.Snapshot(next => next.GameRevision() > revision);
        }
        return snapshot;
    }

    private sealed record ChoicePayload(string roomId, string windowId, long anchorGameRevision, string optionId);

    private static ChoicePayload Choice(JsonElement snapshot, JsonElement option) =>
        new(snapshot.Text("roomId"), snapshot.Window().Text("windowId"),
            snapshot.Window().GetProperty("anchorGameRevision").GetInt64(), option.Text("optionId"));

    private static async Task WaitUntil(DateTimeOffset utc)
    {
        var left = utc - DateTimeOffset.UtcNow;
        if (left > TimeSpan.Zero) await Task.Delay(left);
    }

    private static async Task<object?> ReadScalar(Kestrel server, string sql)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(server.DataDirectory, "mahjong.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false
        };
        await using var database = new SqliteConnection(connectionString.ToString());
        await database.OpenAsync();
        await using var command = database.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static void AssertRedacted(JsonElement snapshot)
    {
        var table = snapshot.Table();
        foreach (var player in table.GetProperty("seats").EnumerateArray())
        {
            Assert.False(player.TryGetProperty("hand", out _));
            Assert.False(player.TryGetProperty("drawnTileId", out _));
            Assert.False(player.TryGetProperty("legalOptions", out _));
            foreach (var meld in player.GetProperty("melds").EnumerateArray().Where(meld => meld.Text("method") == "concealed"))
            {
                var tiles = meld.GetProperty("tiles");
                Assert.Equal(4, tiles.GetArrayLength());
                Assert.All(tiles.EnumerateArray(), tile => Assert.Equal(JsonValueKind.Null, tile.ValueKind));
            }
        }
        var raw = snapshot.GetRawText();
        foreach (var forbidden in new[] { "\"seed\"", "\"wall\"", "\"guestId\"", "\"shuffleVersion\"", "\"decisions\"" })
            Assert.DoesNotContain(forbidden, raw);
    }
}
