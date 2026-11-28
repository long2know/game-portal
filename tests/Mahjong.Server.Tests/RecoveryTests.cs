using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mahjong.Rules;
using Microsoft.Data.Sqlite;

namespace Mahjong.Server.Tests;

public sealed class RecoveryTests
{
    [Fact]
    public async Task PortalBotNamesProjectLegacyCheckpointWithoutChangingRecoveredTable()
    {
        const string mount = "/game-portal";
        await using var server = new Kestrel(("TurnSeconds", 120), ("ClaimSeconds", 30), ("BotDelayMilliseconds", 5000))
            .WithSetting("BASE_URL", mount).WithSetting("Portal__Enabled", "true");
        server.WithSetting("GamePortal__DataDirectory", server.DataDirectory);
        await server.StartAsync();
        var user = new User(server, mount);
        await user.Guest("Jade Guest");
        using var host = await user.Connect();
        var playing = await Start(host);
        var roomId = playing.Text("roomId");
        (await host.Command("setPaused", new { roomId, expectedRoomRevision = playing.RoomRevision(), paused = true })).Ok();
        var paused = await host.Snapshot(s => s.At("room", "paused").GetBoolean());
        const string selectCheckpoint = "SELECT Checkpoint FROM Rooms";
        var checkpoint = Assert.IsType<string>(await ReadScalar(server, selectCheckpoint));
        using (var saved = JsonDocument.Parse(checkpoint))
        {
            Assert.Equal(["Jade Guest", "Bot 2", "Bot 3", "Bot 4"],
                saved.RootElement.GetProperty("seats").EnumerateArray().Select(chair => chair.Text("displayName")));
        }

        await server.StopAsync();
        await server.StartAsync();
        using var resumed = await user.Connect();
        (await resumed.Sync(roomId)).Ok();
        var recovered = await resumed.Snapshot();
        Assert.Equal(roomId, recovered.Text("roomId"));
        Assert.Equal(paused.RoomRevision(), recovered.RoomRevision());
        Assert.Equal(paused.GameRevision(), recovered.GameRevision());
        Assert.Equal(["Jade Guest", "Pip", "Tallow", "Nori"],
            recovered.At("room", "seats").EnumerateArray().Select(chair => chair.Text("displayName")));
        Assert.True(JsonElement.DeepEquals(paused.GetProperty("room"), recovered.GetProperty("room")), "The paused room must be preserved.");
        Assert.True(JsonElement.DeepEquals(paused.Table(), recovered.Table()), "The recovered game must keep its state and window.");
        Assert.True(JsonElement.DeepEquals(paused.Private(), recovered.Private()), "The returned player must keep their seat and hand.");
        Assert.True(string.Equals(checkpoint, await ReadScalar(server, selectCheckpoint) as string, StringComparison.Ordinal),
            "Recovery and projection must not rewrite the checkpoint.");
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoTileKongRecoversPrivateCandidatesAndCommitsPublicBatchExactlyOnce(bool expire)
    {
        const string mount = "/game-portal";
        const string profile = ChangshaEngine.TwoTileKongProfile;
        await using var server = new Kestrel(("TurnSeconds", 120), ("ClaimSeconds", 30))
            .WithSetting("BASE_URL", mount).WithSetting("Portal__Enabled", "true");
        server.WithSetting("GamePortal__DataDirectory", server.DataDirectory);
        await server.StartAsync();
        var hostUser = new User(server, mount);
        var firstUser = new User(server, mount);
        var secondUser = new User(server, mount);
        await hostUser.Guest("Kong declarer");
        await firstUser.Guest("Bamboo claimant");
        await secondUser.Guest("Dots claimant");
        using var host = await hostUser.Connect();
        using var first = await firstUser.Connect();
        using var second = await secondUser.Connect();
        var roomId = (await Start(host, first, hands: 4, profile: profile, another: second)).Text("roomId");

        // Stage only the pre-Kong deal. Kestrel must commit and recover both of the new windows itself.
        await server.StopAsync();
        await RewriteCheckpoint(server, roomId, room =>
        {
            room["game"] = JsonSerializer.SerializeToNode(TwoTileKongTurn(), Wire.Json);
            room["windowAnchor"] = room["gameRevision"]!.GetValue<long>();
            room["nextBotGameTime"] = null;
        });
        await server.StartAsync();
        using var declarer = await hostUser.Connect();
        using var observer = await firstUser.Connect();
        (await observer.Sync(roomId)).Ok();
        (await declarer.Sync(roomId)).Ok();
        var turn = await declarer.Snapshot();
        Assert.False(turn.Private().TryGetProperty("kongReplacements", out _));
        Assert.Equal(JsonValueKind.Null, turn.At("game", "kongRoll").ValueKind);
        var declare = Choice(turn, Assert.Single(Options(turn, "gang")));
        var declareId = Guid.NewGuid();
        var declaration = await declarer.Command("choose", declare, declareId);
        declaration.Ok();
        var pending = await declarer.Snapshot();
        Assert.Equal(turn.GameRevision() + 1, pending.GameRevision());
        Assert.Equal(turn.RoomRevision(), pending.RoomRevision());
        Assert.Equal(turn.Table().GetProperty("wallCount").GetInt32() - 2, pending.Table().GetProperty("wallCount").GetInt32());
        Assert.Equal("turn", pending.Table().Text("phase"));
        Assert.Equal("turn", pending.Window().Text("reason"));
        Assert.Equal(JsonValueKind.Null, pending.Window().GetProperty("trigger").ValueKind);
        Assert.Equal(pending.GameRevision(), pending.Window().GetProperty("anchorGameRevision").GetInt64());
        Assert.Equal(pending.GetProperty("serverTimeUtc").GetDateTimeOffset().AddSeconds(120),
            pending.Window().GetProperty("deadlineUtc").GetDateTimeOffset());
        var roll = pending.At("game", "kongRoll");
        Assert.Equal(["dice", "lowerDie", "seat", "stackCount", "stackMethod"], roll.EnumerateObject().Select(field => field.Name).Order());
        Assert.Equal(0, roll.GetProperty("seat").GetInt32());
        Assert.Equal([4, 1], roll.GetProperty("dice").EnumerateArray().Select(die => die.GetInt32()));
        Assert.Equal(1, roll.GetProperty("lowerDie").GetInt32());
        Assert.Equal(5, roll.GetProperty("stackCount").GetInt32());
        Assert.Equal("sum", roll.Text("stackMethod"));

        var replacements = pending.At("self", "kongReplacements").EnumerateArray().ToArray();
        var faces = replacements.Select(tile => tile.Text("face")).ToArray();
        var ids = replacements.Select(tile => tile.Text("tileId")).ToArray();
        Assert.Equal(["b5", "d5"], faces);
        Assert.All(replacements, tile => Assert.Equal(["face", "tileId"], tile.EnumerateObject().Select(field => field.Name).Order()));
        Assert.All(ids, id => Assert.False(string.IsNullOrEmpty(id)));
        Assert.Equal(2, ids.Distinct().Count());
        Assert.Empty(pending.At("self", "hand").EnumerateArray().Select(tile => tile.Text("tileId")).Intersect(ids));
        var discard = Assert.Single(Options(pending, "discardKong"));
        Assert.Equal(ids, discard.GetProperty("tileIds").EnumerateArray().Select(tile => tile.GetString()));
        Assert.Equal(faces, discard.GetProperty("faces").EnumerateArray().Select(tile => tile.GetString()));
        Assert.Empty(Options(pending, "discard"));
        var batch = Choice(pending, discard);
        AssertPrivate(await observer.Snapshot(s => s.GameRevision() == pending.GameRevision()));

        await server.StopAsync();
        await server.StartAsync();
        using var recoveredHost = await hostUser.Connect();
        using var recoveredFirst = await firstUser.Connect();
        (await recoveredHost.Sync(roomId)).Ok();
        var recovered = await recoveredHost.Snapshot();
        Assert.Equal(profile, recovered.At("room", "rulesVersion").GetString());
        Assert.Equal(pending.GameRevision(), recovered.GameRevision());
        Assert.Equal(pending.RoomRevision(), recovered.RoomRevision());
        Assert.Equal(pending.Table().GetRawText(), recovered.Table().GetRawText());
        Assert.Equal(pending.Private().GetRawText(), recovered.Private().GetRawText());
        Assert.Equal(declaration.GetRawText(), (await recoveredHost.Command("choose", declare, declareId, declarer.Lease)).GetRawText());
        Assert.Equal(pending.Private().GetRawText(), (await recoveredHost.Snapshot()).Private().GetRawText());
        (await recoveredFirst.Sync(roomId)).Ok();
        AssertPrivate(await recoveredFirst.Snapshot());
        var stolen = await recoveredFirst.Command("choose", batch);
        Assert.Equal("illegal_option", stolen.Error());
        Assert.Equal(pending.GameRevision(), stolen.GameRevision());
        AssertPrivate(await recoveredFirst.Snapshot());

        if (expire)
        {
            var deadline = pending.Window().GetProperty("deadlineUtc").GetDateTimeOffset();
            await server.StopAsync();
            server.SetClockOffset((int)Math.Ceiling((deadline - DateTimeOffset.UtcNow).TotalSeconds));
            await server.StartAsync();
            using var afterExpiry = await hostUser.Connect();
            (await afterExpiry.Sync(roomId)).Ok();
            var expired = await afterExpiry.Snapshot();
            AssertBatch(expired, deadline);
            Assert.Equal("stale_window", (await afterExpiry.Command("choose", batch)).Error());
            Assert.Equal(expired.Table().GetRawText(), (await afterExpiry.Snapshot()).Table().GetRawText());
            return;
        }

        var batchId = Guid.NewGuid();
        var batchLease = recoveredHost.Lease;
        await recoveredHost.Send(new { v = 1, type = "command", requestId = batchId, lease = batchLease, op = "choose", payload = batch });
        var claim = await recoveredFirst.Snapshot(s => s.GameRevision() > pending.GameRevision());
        AssertBatch(claim, claim.GetProperty("serverTimeUtc").GetDateTimeOffset());

        // The observer has the commit. Lose the declarer's ack and prove the same receipt and claim are recovered.
        await server.StopAsync();
        await server.StartAsync();
        using var returnedHost = await hostUser.Connect();
        using var returnedFirst = await firstUser.Connect();
        using var returnedSecond = await secondUser.Connect();
        (await returnedHost.Sync(roomId)).Ok();
        var recoveredClaim = await returnedHost.Snapshot();
        AssertNoCandidates(recoveredClaim);
        Assert.Equal(claim.Table().GetRawText(), recoveredClaim.Table().GetRawText());
        var replay = await returnedHost.Command("choose", batch, batchId, batchLease);
        replay.Ok();
        Assert.Equal(claim.GameRevision(), replay.GameRevision());
        Assert.Equal(claim.RoomRevision(), replay.RoomRevision());
        Assert.Equal(claim.Table().GetRawText(), (await returnedHost.Snapshot()).Table().GetRawText());

        (await returnedFirst.Sync(roomId)).Ok();
        var firstClaim = await returnedFirst.Snapshot();
        (await returnedSecond.Sync(roomId)).Ok();
        var secondClaim = await returnedSecond.Snapshot();
        var firstHu = ClaimChoice(firstClaim, "b5");
        var secondHu = ClaimChoice(secondClaim, "d5");
        var forged = await returnedSecond.Command("choose", new
        {
            secondHu.roomId, secondHu.windowId, secondHu.anchorGameRevision, secondHu.optionId, faces = new[] { "b5" }
        });
        Assert.Equal("invalid_request", forged.Error());
        Assert.Equal("illegal_option", (await returnedSecond.Command("choose", firstHu)).Error());
        Assert.Equal(claim.Table().GetRawText(), (await returnedSecond.Snapshot()).Table().GetRawText());
        var accepted = await returnedFirst.Command("choose", firstHu);
        accepted.Ok();
        var waiting = await returnedSecond.Snapshot(s => s.GameRevision() > claim.GameRevision());
        Assert.Equal(claim.GameRevision() + 1, waiting.GameRevision());
        Assert.Equal(claim.Window().GetRawText(), waiting.Window().GetRawText());
        Assert.Equal(secondHu, ClaimChoice(waiting, "d5"));
        Assert.Empty((await returnedFirst.Snapshot()).At("self", "legalOptions").EnumerateArray());
        var duplicate = await returnedFirst.Command("choose", firstHu);
        Assert.Equal("illegal_option", duplicate.Error());
        Assert.Equal(waiting.GameRevision(), duplicate.GameRevision());

        (await returnedSecond.Command("choose", secondHu)).Ok();
        var settled = await returnedHost.Snapshot(s => s.Status() == "betweenHands");
        Assert.Equal(waiting.GameRevision() + 1, settled.GameRevision());
        AssertNoCandidates(settled);
        var winners = settled.At("game", "settlement").GetProperty("winners").EnumerateArray();
        Assert.Equal([(1, "b5"), (2, "d5")], winners.Select(winner =>
            (winner.GetProperty("seat").GetInt32(), Assert.Single(winner.GetProperty("winningTiles").EnumerateArray()).GetString())));
        Assert.Equal(replay.GetRawText(), (await returnedHost.Command("choose", batch, batchId, batchLease)).GetRawText());
        Assert.Equal(settled.Table().GetRawText(), (await returnedHost.Snapshot()).Table().GetRawText());
        Assert.Equal("stale_window", (await returnedHost.Command("choose", batch)).Error());
        Assert.Equal(settled.GameRevision(), (await returnedHost.Snapshot()).GameRevision());

        void AssertPrivate(JsonElement view)
        {
            Assert.Equal(pending.Table().GetRawText(), view.Table().GetRawText());
            Assert.Empty(view.At("self", "legalOptions").EnumerateArray());
            Assert.All(faces, face => Assert.DoesNotContain($"\"{face}\"", view.GetRawText()));
            AssertNoCandidates(view);
        }

        void AssertNoCandidates(JsonElement view)
        {
            Assert.DoesNotContain("\"kongReplacements\"", view.GetRawText());
            Assert.All(ids, id => Assert.DoesNotContain(id, view.GetRawText()));
            AssertRedacted(view);
        }

        void AssertBatch(JsonElement view, DateTimeOffset openedAt)
        {
            Assert.Equal(pending.GameRevision() + 1, view.GameRevision());
            Assert.Equal(pending.RoomRevision(), view.RoomRevision());
            Assert.Equal(pending.Table().GetProperty("wallCount").GetInt32(), view.Table().GetProperty("wallCount").GetInt32());
            Assert.Equal("claim", view.Table().Text("phase"));
            Assert.Equal("kong", view.Window().Text("reason"));
            Assert.Equal(JsonValueKind.Null, view.At("game", "kongRoll").ValueKind);
            Assert.NotEqual(batch.windowId, view.Window().Text("windowId"));
            Assert.Equal(view.GameRevision(), view.Window().GetProperty("anchorGameRevision").GetInt64());
            Assert.Equal(openedAt.AddSeconds(30), view.Window().GetProperty("deadlineUtc").GetDateTimeOffset());
            var trigger = view.Window().GetProperty("trigger");
            Assert.Equal(["faces", "seat"], trigger.EnumerateObject().Select(field => field.Name).Order());
            Assert.Equal(0, trigger.GetProperty("seat").GetInt32());
            Assert.Equal(faces, trigger.GetProperty("faces").EnumerateArray().Select(tile => tile.GetString()));
            Assert.Equal(faces, view.At("game", "seats")[0].GetProperty("discards").EnumerateArray().Select(tile => tile.GetString()));
            AssertNoCandidates(view);
        }

        ChoicePayload ClaimChoice(JsonElement view, string face)
        {
            AssertNoCandidates(view);
            Assert.Equal(["hu", "pass"], view.At("self", "legalOptions").EnumerateArray()
                .Select(option => option.Text("kind")).Order());
            var hu = Assert.Single(Options(view, "hu"));
            Assert.Equal(face, Assert.Single(hu.GetProperty("faces").EnumerateArray()).GetString());
            Assert.Empty(hu.GetProperty("tileIds").EnumerateArray());
            return Choice(view, hu);
        }
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
        var created = await first.Command("createRoom", create, id);
        created.Ok();
        Assert.Equal("ED5EC1078A1E08DB968A7B68BF0B80547239FA803AA87FAA16D6EBDBED3F181E",
            await ReadScalar(server, "SELECT Fingerprint FROM Receipts"));
        var initial = await first.Snapshot();
        var result = await first.Command("leaveRoom", new { roomId = initial.Text("roomId"), expectedRoomRevision = initial.RoomRevision() });
        result.Ok();
        var retry = await first.Command("createRoom",
            new { matchHands = 1, fillBots = false, visibility = "public", rulesVersion = ChangshaEngine.Profile }, id);
        Assert.Equal(created.GetRawText(), retry.GetRawText());
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
            Assert.Equal(dealer, Dealer(snapshot));
            Assert.Equal(totals, TableTotals(snapshot));
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
            Assert.Equal(dealer, NextDealer(settled));
            Assert.Equal(totals, TableTotals(settled));
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
        Assert.Equal(dealer, NextDealer(waiting));
        Assert.Equal(totals, TableTotals(waiting));
        Assert.False(waiting.Chair(0).GetProperty("ready").GetBoolean());
        Assert.Equal(snapshot.Private().Text("inviteCode"), waiting.Private().Text("inviteCode"));
        for (var seat = 1; seat < 4; seat++) Assert.Equal("bot", waiting.Chair(seat).Text("kind"));
        await server.StopAsync();
        await server.StartAsync();
        using var returned = await user.Connect();
        (await returned.Sync(roomId)).Ok();
        var recovered = await returned.Snapshot();
        Assert.Equal(waiting.GetProperty("room").GetRawText(), recovered.GetProperty("room").GetRawText());
        (await returned.Command("setReady", new { roomId, expectedRoomRevision = recovered.RoomRevision(), ready = true })).Ok();
        waiting = await returned.Snapshot();
        (await returned.Command("start", new { roomId, expectedRoomRevision = waiting.RoomRevision() })).Ok();
        var rematch = await returned.Snapshot(s => s.Status() == "playing");
        Assert.Equal(1, rematch.Table().GetProperty("handNumber").GetInt32());
        Assert.Equal(dealer, Dealer(rematch));
        Assert.Equal(JsonValueKind.Null, rematch.Table().GetProperty("settlement").ValueKind);
        Assert.NotEqual(firstWindow, rematch.Window().Text("windowId"));
        Assert.Equal(new[] { 0, 0, 0, 0 }, MatchTotals(rematch));
        Assert.Equal(totals, TableTotals(rematch));
        Assert.Equal("stale_window", (await returned.Command("choose",
            new ChoicePayload(roomId, firstWindow, oldAnchor, "cannot-reuse"))).Error());
        var rematchSettlement = await PlayHand(returned, rematch);
        var deltas = rematchSettlement.At("game", "settlement").GetProperty("seatResults").EnumerateArray()
            .Select(seat => seat.GetProperty("delta").GetInt32()).ToArray();
        Assert.Equal(deltas, MatchTotals(rematchSettlement));
        Assert.Equal(totals.Zip(deltas, (prior, delta) => prior + delta), TableTotals(rematchSettlement));
    }

    [Fact]
    public async Task ProductionPortalChoosesAllFourInitialDealersAndPersistsTheChoiceBeforeDealing()
    {
        await using var server = new Kestrel().InProduction().WithSetting("BASE_URL", "/portal")
            .WithSetting("Portal__Enabled", "true");
        server.WithSetting("GamePortal__DataDirectory", server.DataDirectory);
        await server.StartAsync();
        var user = new User(server, "/portal");
        await user.Guest();
        using var host = await user.Connect();
        var seen = new HashSet<int>();
        var waiting = default(JsonElement);
        for (var attempt = 0; attempt < 80; attempt++)
        {
            (await host.Command("createRoom", new { visibility = "invite", matchHands = 1, fillBots = true })).Ok();
            waiting = await host.Snapshot();
            var seat = NextDealer(waiting);
            Assert.InRange(seat, 0, 3);
            seen.Add(seat);
            if (seen.Count == 4 && seat != 0) break;
            (await host.Command("leaveRoom", new { roomId = waiting.Text("roomId"),
                expectedRoomRevision = waiting.RoomRevision(), abandon = true })).Ok();
        }
        Assert.Equal(new[] { 0, 1, 2, 3 }, seen.Order());
        var expectedDealer = NextDealer(waiting);
        Assert.InRange(expectedDealer, 1, 3);
        var roomId = waiting.Text("roomId");
        await server.StopAsync();
        await server.StartAsync();
        using var resumed = await user.Connect();
        (await resumed.Sync(roomId)).Ok();
        var recovered = await resumed.Snapshot();
        Assert.Equal(expectedDealer, NextDealer(recovered));
        (await resumed.Command("setReady", new { roomId, expectedRoomRevision = recovered.RoomRevision(), ready = true })).Ok();
        var ready = await resumed.Snapshot();
        (await resumed.Command("start", new { roomId, expectedRoomRevision = ready.RoomRevision() })).Ok();
        var started = await resumed.Snapshot();
        Assert.Equal(expectedDealer, Dealer(started));
        Assert.Equal(JsonValueKind.Null, started.At("room", "nextDealerSeat").ValueKind);
        Assert.Equal(13, started.Private().GetProperty("hand").GetArrayLength());
        Assert.Equal(14, started.At("game", "seats")[expectedDealer].GetProperty("concealedCount").GetInt32());
        AssertRedacted(started);
    }

    [Theory]
    [InlineData(ChangshaEngine.Profile, 1, "c1")]
    [InlineData(ChangshaEngine.TwoBirdProfile, 4, "c1 c6")]
    public async Task WinningClaimSettlesOnceAcrossRecoveryAndOnlyOccupiedSeatsKeepTheirLedger(
        string profile, int hands, string birds)
    {
        await using var server = new Kestrel(("TurnSeconds", 120), ("ClaimSeconds", 8), ("BotDelayMilliseconds", 30));
        await server.StartAsync();
        var hostUser = new User(server);
        var memberUser = new User(server);
        await hostUser.Guest("Host");
        await memberUser.Guest("Member");
        using var host = await hostUser.Connect();
        using var member = await memberUser.Connect();
        var started = await Start(host, member, hands, profile);
        var roomId = started.Text("roomId");

        // Restore the reproducible b7 → Hu fixture used by the bot-pacing suite, but drive it via the real sockets.
        await server.StopAsync();
        await RewriteCheckpoint(server, roomId, room =>
        {
            var hand = ChangshaEngine.StartHand(new(1, 0, new(218119, 29), profile,
                TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(8)), DateTimeOffset.UtcNow);
            room["game"] = JsonSerializer.SerializeToNode(hand, Wire.Json);
            room["windowAnchor"] = room["gameRevision"]!.GetValue<long>();
            room["nextBotGameTime"] = null;
            if (profile == ChangshaEngine.Profile)
                Assert.True(room.Remove("tableTotals"));
        });
        await server.StartAsync();
        using var firstHost = await hostUser.Connect();
        using var firstMember = await memberUser.Connect();
        (await firstHost.Sync(roomId)).Ok();
        var turn = await firstHost.Snapshot();
        (await firstMember.Sync(roomId)).Ok();
        var discard = Assert.Single(turn.Private().GetProperty("legalOptions").EnumerateArray(),
            option => option.Text("kind") == "discard" && option.GetProperty("faces")[0].GetString() == "b7");
        var opened = await firstHost.Command("choose", Choice(turn, discard));
        opened.Ok();
        var claim = await firstMember.Snapshot(s => s.GameRevision() == opened.GameRevision() + 2);
        Assert.Equal("discard", claim.Window().Text("reason"));
        Assert.Equal(JsonValueKind.Null, claim.At("game", "settlement").ValueKind);
        var pass = Assert.Single(claim.Private().GetProperty("legalOptions").EnumerateArray(), option => option.Text("kind") == "pass");
        var choose = Choice(claim, pass);
        var choiceId = Guid.NewGuid();
        var lease = firstMember.Lease;
        await firstMember.Send(new { v = 1, type = "command", requestId = choiceId, lease, op = "choose", payload = choose });
        var settled = await firstHost.Snapshot(s => s.Status() == (hands == 1 ? "complete" : "betweenHands"));
        var settlement = settled.At("game", "settlement");
        Assert.Equal(2, NextDealer(settled));
        Assert.Equal(2, Assert.Single(settlement.GetProperty("winners").EnumerateArray()).GetProperty("seat").GetInt32());
        Assert.Equal(birds.Split(' '), settlement.GetProperty("birds").EnumerateArray().Select(bird => bird.GetString()));
        var scores = new[] { -14, 0, 14, 0 };
        Assert.Equal(scores, MatchTotals(settled));
        Assert.Equal(scores, TableTotals(settled));
        AssertRedacted(settled);

        if (hands == 1)
        {
            (await firstHost.Command("leaveRoom", new { roomId, expectedRoomRevision = settled.RoomRevision() })).Ok();
            Assert.Null((await hostUser.Guest()).Text("resumeRoomId"));
        }
        // Do not read the terminal choice's ack. The observed snapshot proves it was already committed.
        await server.StopAsync();
        if (hands == 1)
        {
            await RewriteCheckpoint(server, roomId, room =>
            {
                Assert.Equal(0, room["tableTotals"]![0]!.GetValue<int>());
                Assert.True(room.Remove("tableTotals"));
            });
        }
        await server.StartAsync();
        using var resumedMember = await memberUser.Connect();
        (await resumedMember.Sync(roomId)).Ok();
        var recovered = await resumedMember.Snapshot();
        var expectedTable = hands == 1 ? new[] { 0, 0, 14, 0 } : scores;
        Assert.Equal(profile, recovered.At("room", "rulesVersion").GetString());
        Assert.Equal(settlement.GetRawText(), recovered.At("game", "settlement").GetRawText());
        Assert.Equal(scores, MatchTotals(recovered));
        Assert.Equal(expectedTable, TableTotals(recovered));
        Assert.Equal(2, NextDealer(recovered));
        AssertRedacted(recovered);
        var replay = await resumedMember.Command("choose", choose, choiceId, lease);
        replay.Ok();
        Assert.Equal(settled.GameRevision(), replay.GameRevision());
        Assert.Equal(settled.RoomRevision(), replay.RoomRevision());
        Assert.Equal(expectedTable, TableTotals(await resumedMember.Snapshot()));

        if (hands == 1)
        {
            Assert.Equal("open", recovered.Chair(0).Text("kind"));
            Assert.Equal(1, recovered.At("room", "hostSeat").GetInt32());
            (await resumedMember.Command("rematch", new { roomId, expectedRoomRevision = recovered.RoomRevision() })).Ok();
            var waiting = await resumedMember.Snapshot();
            Assert.Equal(2, NextDealer(waiting));
            Assert.Equal(expectedTable, TableTotals(waiting));
            var newUser = new User(server);
            await newUser.Guest("New seat");
            using var newcomer = await newUser.Connect();
            (await newcomer.Command("joinRoom", new { inviteCode = waiting.Private().Text("inviteCode") })).Ok();
            var joined = await newcomer.Snapshot();
            Assert.Equal(0, joined.Private().GetProperty("seat").GetInt32());
            Assert.Equal(expectedTable, TableTotals(joined));
            Assert.Null(joined.Private().Text("inviteCode"));
            (await resumedMember.Command("setBot", new { roomId, expectedRoomRevision = joined.RoomRevision(),
                seat = 2, enabled = false })).Ok();
            var removed = await resumedMember.Snapshot(s => s.Chair(2).Text("kind") == "open");
            Assert.Equal(new[] { 0, 0, 0, 0 }, TableTotals(removed));
            (await resumedMember.Command("setBot", new { roomId, expectedRoomRevision = removed.RoomRevision(),
                seat = 2, enabled = true })).Ok();
            var replaced = await resumedMember.Snapshot();
            Assert.Equal("bot", replaced.Chair(2).Text("kind"));
            Assert.Equal(new[] { 0, 0, 0, 0 }, TableTotals(replaced));
            Assert.Equal(2, NextDealer(replaced));
            return;
        }

        using var resumedHost = await hostUser.Connect();
        (await resumedHost.Sync(roomId)).Ok();
        var attached = await resumedHost.Snapshot();
        var leave = new { roomId, expectedRoomRevision = attached.RoomRevision() };
        var leaveId = Guid.NewGuid();
        var left = await resumedHost.Command("leaveRoom", leave, leaveId);
        left.Ok();
        var reserved = await resumedMember.Snapshot(s => !s.Connected(0));
        Assert.Equal("human", reserved.Chair(0).Text("kind"));
        Assert.Equal(scores, TableTotals(reserved));
        (await resumedHost.Sync(roomId)).Ok();
        var returned = await resumedHost.Snapshot();
        Assert.True(returned.Connected(0));
        Assert.Equal(scores, TableTotals(returned));
        Assert.Equal(left.GetRawText(), (await resumedHost.Command("leaveRoom", leave, leaveId)).GetRawText());
        await AssertNoSnapshotAfterAck(resumedHost);
        (await resumedMember.Command("nextHand", new { roomId, expectedRoomRevision = reserved.RoomRevision() })).Ok();
        var next = await resumedHost.Snapshot(s => s.Status() == "playing");
        Assert.Equal(2, Dealer(next));
        Assert.Equal(2, next.At("game", "handNumber").GetInt32());
        Assert.Equal(scores, MatchTotals(next));
        Assert.Equal(scores, TableTotals(next));
        (await resumedHost.Command("setPaused", new { roomId, expectedRoomRevision = next.RoomRevision(), paused = true })).Ok();
        Assert.Equal(scores, TableTotals(await resumedHost.Snapshot(s => s.At("room", "paused").GetBoolean())));
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
        var turn = await Start(host, hands: 4, profile: ChangshaEngine.TwoBirdProfile);
        var roomId = turn.Text("roomId");
        var oldChoice = new ChoicePayload(roomId, turn.Window().Text("windowId"),
            turn.Window().GetProperty("anchorGameRevision").GetInt64(), "retired");
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
        (await host.Command("setPaused", new { roomId, expectedRoomRevision = fresh.RoomRevision(), paused = true })).Ok();
        var pausedFresh = await host.Snapshot(s => s.At("room", "paused").GetBoolean());

        await server.StopAsync();
        await server.StartAsync();
        Assert.Equal(roomId, (await user.Guest()).Text("resumeRoomId"));
        using var resumed = await user.Connect();
        var replay = await resumed.Command("restartMatch", restart, requestId, lease);
        Assert.Equal(committed.GetRawText(), replay.GetRawText());
        await AssertNoSnapshotAfterAck(resumed);
        (await resumed.Sync(roomId)).Ok();
        var recovered = await resumed.Snapshot();
        Assert.Equal(pausedFresh.RoomRevision(), recovered.RoomRevision());
        Assert.Equal(pausedFresh.GameRevision(), recovered.GameRevision());
        Assert.Equal(pausedFresh.Table().GetRawText(), recovered.Table().GetRawText());
        Assert.Equal(pausedFresh.Private().GetRawText(), recovered.Private().GetRawText());
        Assert.Equal("stale_window", (await resumed.Command("choose", oldChoice)).Error());
        var unpaused = await resumed.Command("setPaused", new { roomId, expectedRoomRevision = recovered.RoomRevision(), paused = false });
        unpaused.Ok();
        Assert.Equal(recovered.GameRevision(), unpaused.GameRevision());
        Assert.Equal(recovered.Window().Text("windowId"),
            (await resumed.Snapshot(s => !s.At("room", "paused").GetBoolean())).Window().Text("windowId"));
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
        Assert.Equal(new[] { 0, 0, 0, 0 }, TableTotals(replacement));
        Assert.Equal(replacement.Text("roomId"), (await user.Guest()).Text("resumeRoomId"));
    }

    private static void AssertFreshMatch(JsonElement prior, JsonElement fresh)
    {
        Assert.Equal("playing", fresh.Status());
        Assert.Equal(prior.Text("roomId"), fresh.Text("roomId"));
        Assert.Equal(prior.RoomRevision() + 1, fresh.RoomRevision());
        Assert.Equal(prior.GameRevision() + 1, fresh.GameRevision());
        foreach (var preserved in new[] { "hostSeat", "visibility", "matchHands", "rulesVersion", "seats", "chat", "tableTotals" })
            Assert.Equal(prior.At("room", preserved).GetRawText(), fresh.At("room", preserved).GetRawText());
        Assert.Equal(prior.Private().Text("inviteCode"), fresh.Private().Text("inviteCode"));
        Assert.Equal(prior.Private().GetProperty("seat").GetInt32(), fresh.Private().GetProperty("seat").GetInt32());
        Assert.False(fresh.At("room", "paused").GetBoolean());
        Assert.Equal(JsonValueKind.Null, fresh.At("room", "autoResumeAtUtc").ValueKind);
        Assert.Equal(1, fresh.Table().GetProperty("handNumber").GetInt32());
        var dealer = prior.At("game", "settlement").ValueKind == JsonValueKind.Null ? Dealer(prior) : NextDealer(prior);
        Assert.Equal(dealer, Dealer(fresh));
        Assert.Equal(JsonValueKind.Null, fresh.At("room", "nextDealerSeat").ValueKind);
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
        Assert.Equal(fresh.Private().GetProperty("seat").GetInt32() == dealer ? 14 : 13, hand.GetArrayLength());
        Assert.Empty(hand.EnumerateArray().Select(tile => tile.Text("tileId"))
            .Intersect(prior.Private().GetProperty("hand").EnumerateArray().Select(tile => tile.Text("tileId"))));
        AssertRedacted(fresh);
    }

    private static async Task AssertNoSnapshotAfterAck(Client client)
    {
        await client.Send(new { v = 1, type = "renewLease" });
        Assert.Equal("lease", (await client.Take()).Text("type"));
    }

    private static async Task<JsonElement> Start(Client host, Client? other = null, int hands = 1, string? profile = null,
        Client? another = null)
    {
        var members = new[] { other, another }.OfType<Client>().ToArray();
        object create = profile is null ? new { visibility = "invite", matchHands = hands, fillBots = members.Length == 0 } :
            new { visibility = "invite", matchHands = hands, fillBots = members.Length == 0, rulesVersion = profile };
        (await host.Command("createRoom", create)).Ok();
        var snapshot = await host.Snapshot();
        var roomId = snapshot.Text("roomId");
        if (members.Length > 0)
        {
            for (var index = 0; index < members.Length; index++)
            {
                (await members[index].Command("joinRoom", new { inviteCode = snapshot.Private().Text("inviteCode") })).Ok();
                snapshot = await host.Snapshot(s => s.Connected(index + 1));
            }
            for (var seat = members.Length + 1; seat <= 3; seat++)
            {
                (await host.Command("setBot", new { roomId, expectedRoomRevision = snapshot.RoomRevision(), seat, enabled = true })).Ok();
                snapshot = await host.Snapshot();
            }
            for (var index = 0; index < members.Length; index++)
            {
                (await members[index].Command("setReady", new { roomId, expectedRoomRevision = snapshot.RoomRevision(), ready = true })).Ok();
                snapshot = await host.Snapshot(s => s.Chair(index + 1).GetProperty("ready").GetBoolean());
            }
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

    private static GameState TwoTileKongTurn()
    {
        var state = ChangshaEngine.StartHand(new(2, 0, new(369045, 29), ChangshaEngine.TwoTileKongProfile,
            TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30)), DateTimeOffset.UtcNow);
        if (state.Window is { Kind: DecisionKind.Opening } opening)
            foreach (var decision in opening.Decisions)
            {
                var pass = ChangshaEngine.ViewSeat(state, decision.Seat).LegalOptions.Single(option => option.Kind == "pass");
                var moved = ChangshaEngine.Apply(state, new Choose(decision.Seat, opening.Id, pass.OptionId), state.AdvancedAtGameTime);
                Assert.Equal(MoveStatus.Accepted, moved.Status);
                state = moved.State;
            }

        // The dealer rolls 4+1. Put distinct rival Hu waits in the fifth live stack, rear first.
        var pool = state.Wall.Select(tile => tile.Tile)
            .Concat(state.Seats.Skip(1).SelectMany(seat => seat.Hand)).OrderBy(tile => tile.Number).ToList();
        WallTile[] stack = [new(Take("d5"), 98), new(Take("b5"), 99)];
        var seats = state.Seats.ToBuilder();
        seats[1] = seats[1] with { Hand = Hand("b3 b4 c4 c5 c6 d1 d2 d3 d7 d8 d9 c8 c8") };
        seats[2] = seats[2] with { Hand = Hand("d6 d7 b1 b2 b3 c2 c3 c4 c7 c8 c9 b8 b8") };
        seats[3] = seats[3] with { Hand = Hand("b1 b1 b4 b6 b9 c2 c2 c5 c7 c9 d1 d6 d9") };
        var rest = state.Wall.Where(tile => tile.Slot is not (98 or 99)).Zip(pool, (slot, tile) => slot with { Tile = tile });
        return state with
        {
            Seats = seats.ToImmutable(),
            Wall = [.. stack.Concat(rest).OrderBy(tile => tile.Slot)]
        };

        ImmutableArray<PhysicalTile> Hand(string faces) => [.. faces.Split(' ').Select(Take).OrderBy(tile => tile.Number)];

        PhysicalTile Take(string face)
        {
            var faceIndex = "bcd".IndexOf(face[0]) * 9 + face[1] - '1';
            var index = pool.FindIndex(tile => tile.Number / 4 == faceIndex);
            Assert.True(index >= 0, $"No tile remains for fixture face {face}.");
            var tile = pool[index];
            pool.RemoveAt(index);
            return tile;
        }
    }

    private static IEnumerable<JsonElement> Options(JsonElement snapshot, string kind) =>
        snapshot.At("self", "legalOptions").EnumerateArray().Where(option => option.Text("kind") == kind);

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

    private static int Dealer(JsonElement snapshot) => snapshot.At("game", "dealerSeat").GetInt32();
    private static int NextDealer(JsonElement snapshot) => snapshot.At("room", "nextDealerSeat").GetInt32();
    private static int[] TableTotals(JsonElement snapshot) =>
        snapshot.At("room", "tableTotals").EnumerateArray().Select(score => score.GetInt32()).ToArray();
    private static int[] MatchTotals(JsonElement snapshot) =>
        snapshot.At("game", "seats").EnumerateArray().Select(seat => seat.GetProperty("total").GetInt32()).ToArray();

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

    private static async Task RewriteCheckpoint(Kestrel server, string roomId, Action<JsonObject> edit)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(server.DataDirectory, "mahjong.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false
        };
        await using var database = new SqliteConnection(connectionString.ToString());
        await database.OpenAsync();
        await using var command = database.CreateCommand();
        command.Parameters.AddWithValue("$id", roomId);
        command.CommandText = "SELECT Checkpoint FROM Rooms WHERE Id=$id";
        var checkpoint = JsonNode.Parse(Assert.IsType<string>(await command.ExecuteScalarAsync()))!.AsObject();
        edit(checkpoint);
        command.CommandText = "UPDATE Rooms SET Checkpoint=$checkpoint WHERE Id=$id";
        command.Parameters.AddWithValue("$checkpoint", checkpoint.ToJsonString());
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
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
