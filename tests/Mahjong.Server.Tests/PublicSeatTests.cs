using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mahjong.Rules;
using Microsoft.Data.Sqlite;

namespace Mahjong.Server.Tests;

public sealed class PublicSeatTests
{
    [Fact]
    public async Task PortalWaitingDiscoveryKeepsTheOldOpenSeatIntentAndAllowsFullBotTakeovers()
    {
        const string mount = "/portal";
        await using var server = Mounted(mount);
        await server.StartAsync();
        using (var anonymous = await new User(server, mount).Request(HttpMethod.Get, "/api/rooms"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.True(anonymous.Headers.CacheControl?.NoStore);
            Assert.Equal("not_authenticated", (await anonymous.Content.ReadFromJsonAsync<JsonElement>()).Error());
        }
        var hostUser = await Guest(server, mount, "Public host");
        var memberUser = await Guest(server, mount, "Existing member");
        var newcomerUser = await Guest(server, mount, "New member");
        var privateUser = await Guest(server, mount, "Private host");
        var expiringUser = await Guest(server, mount, "Expiring host");
        using var host = await hostUser.Connect();
        using var member = await memberUser.Connect();
        using var newcomer = await newcomerUser.Connect();
        using var privateHost = await privateUser.Connect();
        using var expiring = await expiringUser.Connect();
        var waiting = await Create(host, "public", 4, bots: false);
        var roomId = waiting.Text("roomId");
        var privateRoom = await Create(privateHost, "invite", 4, bots: true);
        var expiredId = (await Create(expiring, "public", 1, bots: false)).Text("roomId");
        Assert.Equal(new[] { roomId, expiredId }.Order(StringComparer.Ordinal),
            (await List(newcomerUser)).Select(row => row.Text("roomId")));
        await Sql(server, "UPDATE Rooms SET ExpiresAtTicks=1 WHERE Id=$id", ("$id", expiredId));
        const string savedState = "SELECT Checkpoint || ':' || ExpiresAtTicks FROM Rooms WHERE Id=$id";
        var untouched = await Sql(server, savedState, ("$id", roomId));
        AssertRow(Assert.Single(await List(newcomerUser)), roomId, "waiting", "Public host", occupied: 1, bots: 0);
        Assert.Equal(untouched, await Sql(server, savedState, ("$id", roomId)));
        Assert.Equal(1L, await Sql(server, "SELECT ExpiresAtTicks FROM Rooms WHERE Id=$id", ("$id", expiredId)));

        var inviteCode = privateRoom.Private().Text("inviteCode");
        foreach (var payload in new object[]
        {
            new { inviteCode, takeBot = true }, new { inviteCode, takeBot = false },
            new { roomId, inviteCode, takeBot = true }, new { roomId, takeBot = 1 },
            new { roomId, takeBot = (bool?)null }
        })
            NoMetadata(await newcomer.Command("joinRoom", payload), "invalid_request");
        NoMetadata(await newcomer.Command("joinRoom", new { roomId = privateRoom.Text("roomId"), takeBot = true }),
            "room_not_found");
        NoMetadata(await newcomer.Command("joinRoom", new { roomId = expiredId, takeBot = true }), "room_not_found");

        (await host.Command("setBot", new { roomId, expectedRoomRevision = waiting.RoomRevision(), seat = 1, enabled = true })).Ok();
        waiting = await host.Snapshot();
        NoMetadata(await newcomer.Command("joinRoom", new { roomId, takeBot = true }), "invalid_state");
        var oldJoinId = Guid.NewGuid();
        var oldJoined = await member.Command("joinRoom", new { roomId }, oldJoinId);
        oldJoined.Ok();
        Assert.Equal(2, Seat(await member.Snapshot()));
        await AssertOldFingerprint(server, oldJoinId, "joinRoom", roomId);
        Assert.Equal(oldJoined.GetRawText(), (await member.Command("joinRoom", new { roomId, takeBot = false }, oldJoinId)).GetRawText());
        Assert.Equal(2, Seat(await member.Snapshot()));
        NoMetadata(await member.Command("joinRoom", new { roomId, takeBot = true }, oldJoinId), "request_conflict");

        waiting = await host.Snapshot();
        Assert.Equal("invalid_state", (await host.Command("leaveRoom", new Handoff(roomId, waiting.RoomRevision()))).Error());
        Assert.Equal(waiting.RoomRevision(), (await host.Snapshot()).RoomRevision());
        (await host.Command("setBot", new { roomId, expectedRoomRevision = waiting.RoomRevision(), seat = 3, enabled = true })).Ok();
        waiting = await host.Snapshot();
        waiting = await Ready(host, host, waiting);
        waiting = await Ready(host, member, waiting);
        using (var response = await hostUser.Request(HttpMethod.Patch, "/api/profile", new { displayName = "Renamed host" }))
            response.EnsureSuccessStatusCode();
        waiting = await host.Snapshot();
        (await host.Command("chat", new { roomId, text = "Not in discovery" })).Ok();
        waiting = await host.Snapshot();
        AssertRow(Assert.Single(await List(newcomerUser)), roomId, "waiting", "Renamed host", occupied: 4, bots: 2);

        NoMetadata(await newcomer.Command("joinRoom", new { roomId }), "room_full");
        NoMetadata(await member.Command("joinRoom", new { roomId, takeBot = true }), "already_seated");
        var joined = await TakeBot(newcomer, roomId);
        Assert.Equal(1, Seat(joined));
        Assert.Equal(waiting.RoomRevision() + 1, joined.RoomRevision());
        Assert.Equal(waiting.GameRevision(), joined.GameRevision());
        Assert.False(joined.Chair(0).GetProperty("ready").GetBoolean());
        Assert.False(joined.Chair(2).GetProperty("ready").GetBoolean());
        Assert.Equal("bot", joined.Chair(3).Text("kind"));
        Assert.Equal(roomId, (await newcomerUser.Guest()).Text("resumeRoomId"));
        AssertRow(Assert.Single(await List(newcomerUser)), roomId, "waiting", "Renamed host", occupied: 4, bots: 1);
        NoMetadata(await privateHost.Command("joinRoom", new { roomId, takeBot = true }), "already_seated");
        Assert.Equal(privateRoom.Text("roomId"), (await privateUser.Guest()).Text("resumeRoomId"));
    }

    [Fact]
    public async Task ConcurrentLiveTakeoversCommitOneHumanPerBotAndNeverOfferTheAllHumanRoom()
    {
        const string mount = "/game-portal";
        await using var server = Mounted(mount);
        await server.StartAsync();
        var owner = await Guest(server, mount, "Public dealer");
        using var host = await owner.Connect();
        var playing = await Start(host, "public", 1);
        var roomId = playing.Text("roomId");
        var paused = await Pause(host, playing);
        var users = new User[4];
        var peers = new List<Client>();
        try
        {
            for (var index = 0; index < users.Length; index++)
            {
                users[index] = await Guest(server, mount, $"Applicant {index}");
                peers.Add(await users[index].Connect());
            }
            AssertRow(Assert.Single(await List(users[0])), roomId, "playing", "Public dealer", occupied: 4, bots: 3);
            NoMetadata(await peers[0].Command("joinRoom", new { roomId, takeBot = false }), "invalid_state");
            NoMetadata(await peers[0].Sync(roomId), "room_not_found");
            var replies = await Task.WhenAll(peers.Select(candidate => candidate.Command("joinRoom",
                new { roomId, takeBot = true })));
            var winners = Enumerable.Range(0, peers.Count).Where(index => replies[index].Text("status") == "ok")
                .OrderBy(index => replies[index].RoomRevision()).ToArray();
            Assert.Equal(3, winners.Length);
            var loser = Assert.Single(Enumerable.Range(0, peers.Count).Except(winners));
            NoMetadata(replies[loser], "room_full");
            var projectedTiles = new HashSet<string>();
            for (var seat = 1; seat <= winners.Length; seat++)
            {
                var index = winners[seat - 1];
                var reply = replies[index];
                Assert.Equal(paused.RoomRevision() + seat, reply.RoomRevision());
                Assert.Equal(paused.GameRevision(), reply.GameRevision());
                var admitted = await peers[index].Snapshot(s => s.RoomRevision() == reply.RoomRevision());
                Assert.Equal(seat, Seat(admitted));
                Assert.Equal(paused.Table().GetRawText(), admitted.Table().GetRawText());
                Assert.All(TileIds(admitted), id => Assert.True(projectedTiles.Add(id)));
                Assert.Equal(roomId, (await users[index].Guest()).Text("resumeRoomId"));
            }
            var filled = await host.Snapshot(s => s.RoomRevision() == paused.RoomRevision() + 3);
            Assert.All(filled.At("room", "seats").EnumerateArray(), chair => Assert.Equal("human", chair.Text("kind")));
            var member = peers[winners[0]];
            (await member.Command("leaveRoom", new { roomId, expectedRoomRevision = filled.RoomRevision() })).Ok();
            var detached = await host.Snapshot(s => !s.Connected(1));
            Assert.Equal("human", detached.Chair(1).Text("kind"));
            Assert.Equal(roomId, (await users[winners[0]].Guest()).Text("resumeRoomId"));
            Assert.Empty(await List(users[loser]));
            await NoSnapshot(peers[loser]);
            NoMetadata(await peers[loser].Command("joinRoom", new { roomId, takeBot = true }), "room_full");
            Assert.Null((await users[loser].Guest()).Text("resumeRoomId"));
            await NoSnapshot(peers[loser]);
            var hostLeft = await host.Command("leaveRoom", new Handoff(roomId, detached.RoomRevision()));
            hostLeft.Ok();
            var transferred = await peers[winners[1]].Snapshot(s => s.RoomRevision() == hostLeft.RoomRevision());
            Assert.Equal(1, transferred.At("room", "hostSeat").GetInt32());
            Assert.False(transferred.Connected(1));
            AssertRow(Assert.Single(await List(users[loser])), roomId, "playing", $"Applicant {winners[0]}", occupied: 4, bots: 1);
            await NoSnapshot(peers[loser]);
        }
        finally
        {
            foreach (var peer in peers) peer.Dispose();
        }
    }

    [Fact]
    public async Task PausedKongTakeoverAndBothHandoffsPreserveTheGameAndRecoverTheirReceipts()
    {
        const string mount = "/portal";
        const int delay = 1000;
        await using var server = Mounted(mount, delay: delay);
        await server.StartAsync();
        var hostUser = await Guest(server, mount, "First host");
        var memberUser = await Guest(server, mount, "Remaining member");
        var newcomerUser = await Guest(server, mount, "New player");
        using var firstHost = await hostUser.Connect();
        using var firstMember = await memberUser.Connect();
        var roomId = (await Start(firstHost, "public", 4, firstMember)).Text("roomId");

        await server.StopAsync();
        var turn = DealForTurn(2, 2, seed: 369045);
        var rolled = Apply(turn, 2, ChangshaEngine.ViewSeat(turn, 2).LegalOptions.Single(option => option.Kind == "gang"));
        await Seed(server, roomId, rolled, room =>
        {
            room.NextBotGameTime = rolled.AdvancedAtGameTime.AddSeconds(20);
            room.Totals = [-6, 2, 7, -3];
            new[] { 9, -3, 7, -13 }.CopyTo(room.TableTotals, 0);
        });
        await server.StartAsync();
        using var host = await hostUser.Connect();
        using var member = await memberUser.Connect();
        using var newcomer = await newcomerUser.Connect();
        var playing = await Sync(host, roomId);
        await Sync(member, roomId);
        var paused = await Pause(host, playing);
        var memberBefore = await member.Snapshot(s => s.RoomRevision() == paused.RoomRevision());
        var rolledDetails = paused.At("game", "kongRoll");
        Assert.Equal(2, rolledDetails.GetProperty("seat").GetInt32());
        Assert.Equal(new[] { 4, 1 }, rolledDetails.GetProperty("dice").EnumerateArray().Select(die => die.GetInt32()));
        Assert.Equal(1, rolledDetails.GetProperty("lowerDie").GetInt32());
        Assert.Equal(rolled.AdvancedAtGameTime.AddSeconds(20), (await ReadCheckpoint(server, roomId)).NextBotGameTime);

        var normalId = Guid.NewGuid();
        var ordinary = await member.Command("leaveRoom", Revision(paused), normalId);
        ordinary.Ok();
        await NoSnapshot(member);
        var reserved = await host.Snapshot(s => !s.Connected(1));
        Assert.Equal(paused.RoomRevision(), reserved.RoomRevision());
        Assert.Equal("human", reserved.Chair(1).Text("kind"));
        Assert.Equal(new[] { 9, -3, 7, -13 }, TableTotals(reserved));
        Assert.Equal(roomId, (await memberUser.Guest()).Text("resumeRoomId"));
        await AssertOldFingerprint(server, normalId, "leaveRoom", roomId, paused.RoomRevision());
        Assert.Equal(memberBefore.Private().GetRawText(), (await Sync(member, roomId)).Private().GetRawText());
        Assert.Equal(ordinary.GetRawText(), (await member.Command("leaveRoom",
            new { roomId, expectedRoomRevision = paused.RoomRevision(), replaceWithBot = false }, normalId)).GetRawText());
        await NoSnapshot(member);

        var admission = new { roomId, takeBot = true };
        var admissionId = Guid.NewGuid();
        var newcomerLease = newcomer.Lease;
        var admitted = await newcomer.Command("joinRoom", admission, admissionId);
        admitted.Ok();
        var joined = await newcomer.Snapshot();
        Assert.Equal(2, Seat(joined));
        AssertRosterOnly(paused, joined, [9, -3, 0, -13]);
        Assert.Equal("New player", joined.Chair(2).Text("displayName"));
        Assert.True(joined.Chair(0).GetProperty("ready").GetBoolean());
        Assert.True(joined.Chair(1).GetProperty("ready").GetBoolean());
        var expectedRack = ChangshaEngine.ViewSeat(rolled, 2).Hand.Select(tile => (tile.TileId, tile.Face));
        Assert.Equal(expectedRack, joined.At("self", "hand").EnumerateArray().Select(tile => (tile.Text("tileId"), tile.Text("face"))));
        Assert.Empty(joined.At("self", "legalOptions").EnumerateArray());
        var hostWithNewcomer = await host.Snapshot(s => s.RoomRevision() == joined.RoomRevision());
        Assert.Equal(paused.Private().GetRawText(), hostWithNewcomer.Private().GetRawText());
        Assert.All(TileIds(joined), id => Assert.DoesNotContain(id, hostWithNewcomer.GetRawText()));
        Assert.All(TileIds(hostWithNewcomer).Concat(TileIds(memberBefore)), id => Assert.DoesNotContain(id, joined.GetRawText()));
        var afterTakeover = await ReadCheckpoint(server, roomId);
        Assert.Null(afterTakeover.NextBotGameTime);
        Assert.Equal(rolled.Window!.ClosesAt, afterTakeover.Game!.Window!.ClosesAt);

        var release = new Handoff(roomId, joined.RoomRevision());
        var stale = await newcomer.Command("leaveRoom", release with { expectedRoomRevision = release.expectedRoomRevision - 1 });
        Assert.Equal("stale_room", stale.Error());
        Assert.Equal(joined.RoomRevision(), stale.RoomRevision());
        Assert.Equal(joined.Private().GetRawText(), (await newcomer.Snapshot()).Private().GetRawText());
        var releaseId = Guid.NewGuid();
        var released = await newcomer.Command("leaveRoom", release, releaseId);
        released.Ok();
        var nonhostLeft = await host.Snapshot();
        AssertRosterOnly(joined, nonhostLeft, [9, -3, 0, -13]);
        Assert.Equal("Tallow", nonhostLeft.Chair(2).Text("displayName"));
        Assert.False(nonhostLeft.Connected(2));
        var checkpoint = await ReadCheckpoint(server, roomId);
        var scheduled = checkpoint.GameTime(checkpoint.PauseStartedUtc!.Value).AddMilliseconds(delay);
        Assert.Equal(scheduled, checkpoint.NextBotGameTime);
        await NoSnapshot(newcomer);

        var hostRelease = await host.Command("leaveRoom", new Handoff(roomId, nonhostLeft.RoomRevision()));
        hostRelease.Ok();
        var transferred = await member.Snapshot(s => s.RoomRevision() == hostRelease.RoomRevision());
        AssertRosterOnly(nonhostLeft, transferred, [0, -3, 0, -13]);
        Assert.Equal(1, transferred.At("room", "hostSeat").GetInt32());
        Assert.Equal("Maple", transferred.Chair(0).Text("displayName"));
        Assert.Equal(memberBefore.Private().GetRawText(), transferred.Private().GetRawText());
        await NoSnapshot(host);
        await NoSnapshot(newcomer);
        // Seat 0 is now the next available bot, not the newcomer's former seat 2.
        NoMetadata(await newcomer.Command("joinRoom", admission), "seat_released");
        NoMetadata(await newcomer.Sync(roomId), "room_not_found");
        Assert.Null((await hostUser.Guest()).Text("resumeRoomId"));
        Assert.Null((await newcomerUser.Guest()).Text("resumeRoomId"));
        Assert.Empty(await Saves(hostUser));
        Assert.Empty(await Saves(newcomerUser));
        Assert.Equal(roomId, Assert.Single(await Saves(memberUser)).Text("tableId"));
        AssertRow(Assert.Single(await List(newcomerUser)), roomId, "playing", "Remaining member", occupied: 4, bots: 3);
        Assert.Equal(1L, await Sql(server, "SELECT count(*) FROM Memberships WHERE RoomId=$id", ("$id", roomId)));

        await server.StopAsync();
        server.WithSetting("Mahjong__Testing__BotDelayMilliseconds", "5000");
        await server.StartAsync();
        using var remaining = await memberUser.Connect();
        using var returned = await newcomerUser.Connect();
        Assert.Equal(released.GetRawText(), (await returned.Command("leaveRoom", release, releaseId, newcomerLease)).GetRawText());
        await NoSnapshot(returned);
        NoMetadata(await returned.Command("leaveRoom", Revision(joined), releaseId, newcomerLease), "request_conflict");
        Assert.Equal(admitted.GetRawText(), (await returned.Command("joinRoom", admission, admissionId, newcomerLease)).GetRawText());
        await NoSnapshot(returned);
        NoMetadata(await returned.Command("joinRoom", admission), "seat_released");
        var recovered = await Sync(remaining, roomId);
        Assert.Equal(transferred.RoomRevision(), recovered.RoomRevision());
        Assert.Equal(transferred.GameRevision(), recovered.GameRevision());
        Assert.Equal(transferred.Table().GetRawText(), recovered.Table().GetRawText());
        Assert.Equal(transferred.Private().GetRawText(), recovered.Private().GetRawText());
        Assert.Equal(scheduled, (await ReadCheckpoint(server, roomId)).NextBotGameTime);
        Assert.DoesNotContain("releasedGuests", recovered.GetRawText());

        var resumed = await Resume(remaining, recovered);
        var deadline = resumed.Window().GetProperty("deadlineUtc").GetDateTimeOffset();
        var botDueUtc = scheduled + (deadline - rolled.Window.ClosesAt);
        var processed = await remaining.Snapshot(s => s.GameRevision() > resumed.GameRevision());
        Assert.Equal(resumed.GameRevision() + 1, processed.GameRevision());
        Assert.True(processed.GetProperty("serverTimeUtc").GetDateTimeOffset() >= botDueUtc);
        var forcedDiscard = ChangshaEngine.ViewSeat(rolled, 2).LegalOptions.Single(option => option.Kind == "discard").Faces[0];
        Assert.Equal(forcedDiscard, Assert.Single(processed.At("game", "seats")[2].GetProperty("discards").EnumerateArray()).GetString());
        await NoSnapshot(returned);
    }

    [Theory]
    [InlineData(1, "complete")]
    [InlineData(4, "betweenHands")]
    public async Task AClaimWithOtherLegalBotsRetainsItsPendingTimeAndNeverReopensADecidedOption(int hands, string terminal)
    {
        const string mount = "/game-portal";
        await using var server = Mounted(mount, delay: 1000);
        await server.StartAsync();
        var owner = await Guest(server, mount, "Dealer");
        var guest = await Guest(server, mount, "Taking a claim");
        using var first = await owner.Connect();
        var roomId = (await Start(first, "public", hands)).Text("roomId");

        await server.StopAsync();
        var turn = DealForTurn(1, 0, seed: 218119);
        var claim = Apply(turn, 0, ChangshaEngine.ViewSeat(turn, 0).LegalOptions.First(option =>
            option.Kind == "discard" && option.Faces[0] == "b7"));
        var persistedDue = claim.AdvancedAtGameTime.AddSeconds(20);
        await Seed(server, roomId, claim, room => room.NextBotGameTime = persistedDue);
        await server.StartAsync();
        using var host = await owner.Connect();
        using var member = await guest.Connect();
        var original = await Sync(host, roomId);
        var joined = await TakeBot(member, roomId);
        AssertRosterOnly(original, joined, [0, 0, 0, 0]);
        Assert.Equal(persistedDue, (await ReadCheckpoint(server, roomId)).NextBotGameTime);
        Assert.Contains(joined.At("self", "legalOptions").EnumerateArray(), option => option.Text("kind") == "chi");
        var pass = Assert.Single(joined.At("self", "legalOptions").EnumerateArray(), option => option.Text("kind") == "pass");
        (await member.Command("choose", Choice(joined, pass))).Ok();
        var decided = await member.Snapshot();
        Assert.Equal(joined.GameRevision() + 1, decided.GameRevision());
        Assert.Equal(joined.Window().GetRawText(), decided.Window().GetRawText());
        Assert.Empty(decided.At("self", "legalOptions").EnumerateArray());
        var pending = (await ReadCheckpoint(server, roomId)).NextBotGameTime;
        Assert.NotNull(pending);
        var handoff = await member.Command("leaveRoom", new Handoff(roomId, decided.RoomRevision()));
        handoff.Ok();
        var released = await host.Snapshot(s => s.RoomRevision() == handoff.RoomRevision());
        AssertRosterOnly(decided, released, [0, 0, 0, 0]);
        var saved = await ReadCheckpoint(server, roomId);
        Assert.Equal(pending, saved.NextBotGameTime);
        Assert.Empty(ChangshaEngine.ViewSeat(saved.Game!, 1).LegalOptions);
        await NoSnapshot(member);
        var settled = await host.Snapshot(s => s.Status() == terminal);
        Assert.Equal(2, Assert.Single(settled.At("game", "settlement").GetProperty("winners").EnumerateArray())
            .GetProperty("seat").GetInt32());
        Assert.Empty(await List(guest));
        NoMetadata(await member.Command("joinRoom", new { roomId, takeBot = true }), "invalid_state");
        await NoSnapshot(member);
    }

    [Theory]
    [InlineData("/portal", true)]
    [InlineData("/mahjong", false)]
    public async Task PrivateHandoffKeepsTheHandForDurableMembersAndStandaloneRejectsTheOptIns(string mount, bool portal)
    {
        await using var server = Mounted(mount, portal);
        await server.StartAsync();
        var owner = await Guest(server, mount, "Original host");
        var peer = await Guest(server, mount, "Reserved member");
        var outsider = await Guest(server, mount, "Outsider");
        using var host = await owner.Connect();
        using var member = await peer.Connect();
        using var observer = await outsider.Connect();
        var playing = await Start(host, portal ? "invite" : "public", 1, member);
        var roomId = playing.Text("roomId");
        var inviteCode = playing.Private().Text("inviteCode");
        var paused = await Pause(host, playing);
        var handoff = new Handoff(roomId, paused.RoomRevision());
        await member.Snapshot(s => s.RoomRevision() == paused.RoomRevision());
        foreach (var payload in new object[]
        {
            new { roomId, handoff.expectedRoomRevision, replaceWithBot = "true" },
            new { roomId, handoff.expectedRoomRevision, replaceWithBot = (bool?)null },
            new { roomId, handoff.expectedRoomRevision, replaceWithBot = true, abandon = true }
        })
            NoMetadata(await member.Command("leaveRoom", payload), "invalid_request");

        if (!portal)
        {
            NoMetadata(await host.Command("leaveRoom", handoff), "invalid_request");
            NoMetadata(await observer.Command("joinRoom", new { roomId, takeBot = true }), "invalid_request");
            Assert.Empty(await List(outsider));
            Assert.Equal(roomId, (await owner.Guest()).Text("resumeRoomId"));
            await NoSnapshot(host);
            return;
        }

        using var unattached = await owner.Connect();
        NoMetadata(await unattached.Command("leaveRoom", handoff), "invalid_state");
        await NoSnapshot(unattached);
        var ordinary = await member.Command("leaveRoom", Revision(paused));
        ordinary.Ok();
        Assert.Equal("human", (await host.Snapshot(s => !s.Connected(1))).Chair(1).Text("kind"));
        var left = await host.Command("leaveRoom", handoff);
        left.Ok();
        await NoSnapshot(host);
        await NoSnapshot(member);
        Assert.Empty(await List(outsider));
        NoMetadata(await observer.Command("joinRoom", new { roomId, takeBot = true }), "room_not_found");
        NoMetadata(await observer.Command("joinRoom", new { inviteCode }), "invalid_state");
        NoMetadata(await observer.Sync(roomId), "room_not_found");
        await NoSnapshot(observer);
        Assert.Null((await owner.Guest()).Text("resumeRoomId"));
        Assert.Equal(roomId, (await peer.Guest()).Text("resumeRoomId"));
        var continued = await Sync(member, roomId);
        AssertRosterOnly(paused, continued, [0, 0, 0, 0]);
        Assert.Equal(1, continued.At("room", "hostSeat").GetInt32());
        Assert.Equal("bot", continued.Chair(0).Text("kind"));
        Assert.Equal(inviteCode, continued.Private().Text("inviteCode"));
        Assert.Equal("invalid_state", (await member.Command("leaveRoom", new Handoff(roomId, left.RoomRevision()))).Error());
    }

    [Theory]
    [InlineData(1, "complete", "rematch", "waiting")]
    [InlineData(4, "betweenHands", "nextHand", "playing")]
    public async Task CappedHandBlocksNewTakeoversButCanFinishAndAdmitGuestsAgain(
        int hands, string terminal, string advance, string reopened)
    {
        const string mount = "/portal";
        await using var server = Mounted(mount, delay: 50);
        await server.StartAsync();
        var owner = await Guest(server, mount, "Dealer");
        var first = await Guest(server, mount, "First member");
        var second = await Guest(server, mount, "Second member");
        var third = await Guest(server, mount, "Third member");
        var outsider = await Guest(server, mount, "New applicant");
        using var originalHost = await owner.Connect();
        using var originalFirst = await first.Connect();
        using var originalSecond = await second.Connect();
        using var originalThird = await third.Connect();
        var roomId = (await Start(originalHost, "public", hands, originalFirst, originalSecond, originalThird)).Text("roomId");

        await server.StopAsync();
        var turn = DealForTurn(1, 0, seed: 218119);
        await Seed(server, roomId, turn, room => room.ReleasedGuests = Enumerable.Range(0, 31).Select(_ => Guid.NewGuid()).ToArray());
        await server.StartAsync();
        using var host = await owner.Connect();
        using var memberOne = await first.Connect();
        using var memberTwo = await second.Connect();
        using var memberThree = await third.Connect();
        using var joiner = await outsider.Connect();
        var playing = await Sync(host, roomId);
        await Sync(memberOne, roomId);
        await Sync(memberTwo, roomId);
        await Sync(memberThree, roomId);
        var snapshot = await Pause(host, playing);
        var admission = new { roomId, takeBot = true };
        foreach (var member in new[] { memberThree, memberTwo, memberOne })
        {
            var left = await member.Command("leaveRoom", new Handoff(roomId, snapshot.RoomRevision()));
            left.Ok();
            snapshot = await host.Snapshot(s => s.RoomRevision() == left.RoomRevision());
            Assert.Empty(await List(outsider));
            NoMetadata(await joiner.Command("joinRoom", admission), "room_full");
            NoMetadata(await member.Command("joinRoom", admission), "seat_released");
        }
        Assert.Equal(34, (await ReadCheckpoint(server, roomId)).ReleasedGuests!.Length);
        Assert.Equal("invalid_state", (await host.Command("leaveRoom", new Handoff(roomId, snapshot.RoomRevision()))).Error());
        snapshot = await Resume(host, snapshot);
        var discard = Assert.Single(snapshot.At("self", "legalOptions").EnumerateArray(), option =>
            option.Text("kind") == "discard" && option.GetProperty("faces")[0].GetString() == "b7");
        (await host.Command("choose", Choice(snapshot, discard))).Ok();
        snapshot = await host.Snapshot(s => s.Status() == terminal);
        Assert.Equal(2, Assert.Single(snapshot.At("game", "settlement").GetProperty("winners").EnumerateArray())
            .GetProperty("seat").GetInt32());
        Assert.Empty(await List(outsider));
        NoMetadata(await joiner.Command("joinRoom", admission), "invalid_state");
        Assert.Equal("invalid_state", (await host.Command("leaveRoom", new Handoff(roomId, snapshot.RoomRevision()))).Error());

        (await host.Command(advance, Revision(snapshot))).Ok();
        var next = await host.Snapshot(s => s.Status() == reopened);
        AssertRow(Assert.Single(await List(outsider)), roomId, reopened, "Dealer", occupied: 4, bots: 3);
        Assert.Null((await ReadCheckpoint(server, roomId)).ReleasedGuests);
        var rejoined = await TakeBot(memberOne, roomId);
        Assert.Equal(1, Seat(rejoined));
        Assert.Equal(next.Status(), rejoined.Status());
        Assert.Equal(roomId, (await first.Guest()).Text("resumeRoomId"));
    }

    private static Kestrel Mounted(string mount, bool portal = true, int delay = 5000)
    {
        var server = new Kestrel(("TurnSeconds", 120), ("ClaimSeconds", 30), ("BotDelayMilliseconds", delay))
            .WithSetting("BASE_URL", mount).WithSetting("Portal__Enabled", portal ? "true" : "false");
        return server.WithSetting("GamePortal__DataDirectory", server.DataDirectory);
    }

    private static async Task<User> Guest(Kestrel server, string mount, string name)
    {
        var user = new User(server, mount);
        await user.Guest(name);
        return user;
    }

    private static async Task<JsonElement> Create(Client host, string visibility, int matchHands, bool bots)
    {
        (await host.Command("createRoom", new { visibility, matchHands, fillBots = bots })).Ok();
        return await host.Snapshot();
    }

    private static async Task<JsonElement> Start(Client host, string visibility, int hands, params Client[] members)
    {
        var current = await Create(host, visibility, hands, bots: members.Length == 0);
        var roomId = current.Text("roomId");
        var inviteCode = current.Private().Text("inviteCode");
        foreach (var member in members)
        {
            object locator = visibility == "invite" ? new { inviteCode } : new { roomId };
            (await member.Command("joinRoom", locator)).Ok();
            current = await host.Snapshot();
        }
        for (var seat = members.Length + 1; members.Length > 0 && seat < 4; seat++)
        {
            (await host.Command("setBot", new { roomId, expectedRoomRevision = current.RoomRevision(), seat, enabled = true })).Ok();
            current = await host.Snapshot();
        }
        foreach (var human in members.Prepend(host))
            current = await Ready(host, human, current);
        (await host.Command("start", Revision(current))).Ok();
        return await host.Snapshot(s => s.Status() == "playing");
    }

    private static async Task<JsonElement> Ready(Client host, Client member, JsonElement snapshot)
    {
        (await member.Command("setReady", new { roomId = snapshot.Text("roomId"), expectedRoomRevision = snapshot.RoomRevision(),
            ready = true })).Ok();
        return await host.Snapshot();
    }

    private static async Task<JsonElement> Pause(Client host, JsonElement playing)
    {
        (await host.Command("setPaused", new { roomId = playing.Text("roomId"), expectedRoomRevision = playing.RoomRevision(),
            paused = true })).Ok();
        return await host.Snapshot(s => s.At("room", "paused").GetBoolean());
    }

    private static async Task<JsonElement> Resume(Client host, JsonElement paused)
    {
        (await host.Command("setPaused", new { roomId = paused.Text("roomId"), expectedRoomRevision = paused.RoomRevision(),
            paused = false })).Ok();
        return await host.Snapshot(s => !s.At("room", "paused").GetBoolean() && s.RoomRevision() == paused.RoomRevision() + 1);
    }

    private static async Task<JsonElement> Sync(Client actor, string roomId)
    {
        (await actor.Sync(roomId)).Ok();
        return await actor.Snapshot();
    }

    private static async Task<JsonElement> TakeBot(Client actor, string roomId, Guid? id = null)
    {
        var requestId = id ?? Guid.NewGuid();
        await actor.Send(new { v = 1, type = "command", requestId, lease = actor.Lease, op = "joinRoom",
            payload = new { roomId, takeBot = true } });
        var ack = await actor.Take();
        Assert.Equal("ack", ack.Text("type"));
        Assert.Equal(requestId, ack.GetProperty("requestId").GetGuid());
        ack.Ok();
        var snapshot = await actor.Take();
        Assert.Equal("snapshot", snapshot.Text("type"));
        Assert.Equal(roomId, snapshot.Text("roomId"));
        Assert.Equal(ack.RoomRevision(), snapshot.RoomRevision());
        Assert.Equal(ack.GameRevision(), snapshot.GameRevision());
        return snapshot;
    }

    private static object Revision(JsonElement snapshot) =>
        new { roomId = snapshot.Text("roomId"), expectedRoomRevision = snapshot.RoomRevision() };

    private static int Seat(JsonElement snapshot) => snapshot.Private().GetProperty("seat").GetInt32();

    private static IEnumerable<string> TileIds(JsonElement snapshot) =>
        snapshot.At("self", "hand").EnumerateArray().Select(tile => tile.Text("tileId"));

    private static IEnumerable<int> TableTotals(JsonElement snapshot) =>
        snapshot.At("room", "tableTotals").EnumerateArray().Select(score => score.GetInt32());

    private static void AssertRosterOnly(JsonElement before, JsonElement after, int[] scores)
    {
        Assert.Equal(before.RoomRevision() + 1, after.RoomRevision());
        Assert.Equal(before.GameRevision(), after.GameRevision());
        Assert.Equal(before.Table().GetRawText(), after.Table().GetRawText());
        Assert.Equal(before.At("room", "paused").GetRawText(), after.At("room", "paused").GetRawText());
        Assert.Equal(before.At("room", "autoResumeAtUtc").GetRawText(), after.At("room", "autoResumeAtUtc").GetRawText());
        Assert.Equal(scores, TableTotals(after));
    }

    private static void NoMetadata(JsonElement rejected, string code)
    {
        Assert.Equal("error", rejected.Text("status"));
        Assert.Equal(code, rejected.Error());
        foreach (var field in new[] { "roomId", "roomRevision", "gameRevision" })
            Assert.Equal(JsonValueKind.Null, rejected.GetProperty(field).ValueKind);
    }

    private static async Task NoSnapshot(Client actor)
    {
        await actor.Send(new { v = 1, type = "renewLease" });
        Assert.Equal("lease", (await actor.Take()).Text("type"));
    }

    private static async Task<JsonElement[]> List(User observer)
    {
        using var response = await observer.Request(HttpMethod.Get, "/api/rooms");
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rooms", Assert.Single(json.EnumerateObject()).Name);
        return json.GetProperty("rooms").EnumerateArray().ToArray();
    }

    private static async Task<JsonElement[]> Saves(User observer)
    {
        using var response = await observer.Request(HttpMethod.Get, "/api/saves");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("saves").EnumerateArray().ToArray();
    }

    private static void AssertRow(JsonElement row, string roomId, string status, string host, int occupied, int bots)
    {
        Assert.Equal(new[] { "botSeats", "hostName", "matchHands", "occupiedSeats", "roomId", "status" },
            row.EnumerateObject().Select(field => field.Name).Order());
        Assert.Equal(roomId, row.Text("roomId"));
        Assert.Equal(status, row.Text("status"));
        Assert.Equal(host, row.Text("hostName"));
        Assert.Equal(occupied, row.GetProperty("occupiedSeats").GetInt32());
        Assert.Equal(bots, row.GetProperty("botSeats").GetInt32());
    }

    private static async Task AssertOldFingerprint(Kestrel server, Guid request, string op, string roomId, long revision = 0)
    {
        // The pre-feature Intent serialized these fields, in this order, without either opt-in flag.
        var historical = JsonSerializer.SerializeToUtf8Bytes(new { op, roomId, inviteCode = (string?)null, visibility = (string?)null,
            matchHands = 0, flag = false, seat = 0, revision, windowId = (string?)null, optionId = (string?)null, text = (string?)null });
        Assert.Equal(Convert.ToHexString(SHA256.HashData(historical)), await Sql(server,
            "SELECT Fingerprint FROM Receipts WHERE RequestId=$id", ("$id", request)));
    }

    private sealed record Handoff(string roomId, long expectedRoomRevision, bool replaceWithBot = true);

    private static object Choice(JsonElement snapshot, JsonElement option) => new
    {
        roomId = snapshot.Text("roomId"), windowId = snapshot.Window().Text("windowId"),
        anchorGameRevision = snapshot.Window().GetProperty("anchorGameRevision").GetInt64(), optionId = option.Text("optionId")
    };

    private static GameState DealForTurn(int hand, int dealer, ulong seed)
    {
        var game = ChangshaEngine.StartHand(new(hand, dealer, new(seed, 29), ChangshaEngine.Profile,
            TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30)), DateTimeOffset.UtcNow);
        if (game.Window is { Kind: DecisionKind.Opening } opening)
            foreach (var decision in opening.Decisions)
                game = Apply(game, decision.Seat,
                    ChangshaEngine.ViewSeat(game, decision.Seat).LegalOptions.Single(option => option.Kind == "pass"));
        return game;
    }

    private static GameState Apply(GameState game, int seat, LegalOption option)
    {
        var result = ChangshaEngine.Apply(game, new Choose(seat, game.Window!.Id, option.OptionId), game.AdvancedAtGameTime);
        Assert.Equal(MoveStatus.Accepted, result.Status);
        return result.State;
    }

    private static async Task Seed(Kestrel server, string roomId, GameState game, Action<RoomCheckpoint> edit)
    {
        var room = await ReadCheckpoint(server, roomId);
        room.Game = game;
        room.GameRevision += game.WindowNumber;
        room.WindowAnchor = room.GameRevision;
        edit(room);
        var row = new RoomRow { Id = roomId, Checkpoint = "" };
        room.Store(row);
        await Sql(server, "UPDATE Rooms SET Checkpoint=$checkpoint WHERE Id=$id", ("$id", roomId), ("$checkpoint", row.Checkpoint));
    }

    private static async Task<RoomCheckpoint> ReadCheckpoint(Kestrel server, string roomId) =>
        RoomCheckpoint.Read(roomId, Assert.IsType<string>(await Sql(server, "SELECT Checkpoint FROM Rooms WHERE Id=$id", ("$id", roomId))));

    private static async Task<object?> Sql(Kestrel server, string sql, params (string Name, object Value)[] parameters)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(server.DataDirectory, "mahjong.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false
        };
        await using var database = new SqliteConnection(connectionString.ToString());
        await database.OpenAsync();
        await using var command = database.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync();
    }
}
