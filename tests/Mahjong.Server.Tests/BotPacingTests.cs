using System.Text.Json;
using Mahjong.Rules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Server.Tests;

public sealed class BotPacingTests
{
    private static readonly DateTimeOffset HandStarted = DateTimeOffset.UnixEpoch;
    private static readonly DateTimeOffset DiscardedAt = HandStarted.AddSeconds(1);

    [Theory]
    [InlineData(null, null, 700)]
    [InlineData("false", null, 700)]
    [InlineData("true", null, 1000)]
    [InlineData(null, "15", 15)]
    [InlineData("true", "15", 15)]
    public void AutomaticTurnAdvancesAtTheConfiguredDelay(string? portal, string? testDelay, int delayMs)
    {
        var options = Options(portal, testDelay);
        var scheduler = Scheduler(options);
        var room = DealerTurn(options, 1);
        Assert.Equal(HandStarted.AddSeconds(30), Deadline(Snapshot(room, HandStarted)));

        Discard(scheduler, room, "b3");
        var due = DiscardedAt.AddMilliseconds(delayMs);
        var before = Snapshot(room, DiscardedAt);
        Assert.Equal(1, before.Table().GetProperty("turnSeat").GetInt32());
        Assert.Equal(DiscardedAt.AddSeconds(30), Deadline(before));
        Assert.Empty(before.Private().GetProperty("legalOptions").EnumerateArray());

        Assert.False(scheduler.AdvanceDue(room, due.AddTicks(-1)));
        Assert.Equal(before.Table().GetRawText(), Snapshot(room, due.AddTicks(-1)).Table().GetRawText());
        Assert.Equal(before.GameRevision(), room.GameRevision);

        Assert.True(scheduler.AdvanceDue(room, due));
        var after = Snapshot(room, due);
        Assert.Equal(before.RoomRevision(), after.RoomRevision());
        Assert.Equal(before.GameRevision() + 1, after.GameRevision());
        Assert.Equal("c2", Assert.Single(after.Table().GetProperty("seats")[1].GetProperty("discards").EnumerateArray()).GetString());
        Assert.Equal(2, after.Table().GetProperty("turnSeat").GetInt32());
        Assert.NotEqual(before.Window().Text("windowId"), after.Window().Text("windowId"));
        Assert.Equal(after.GameRevision(), after.Window().GetProperty("anchorGameRevision").GetInt64());
        Assert.Equal(due.AddSeconds(30), Deadline(after));
        Assert.Equal(due.AddMilliseconds(delayMs), room.NextBotGameTime);
    }

    [Fact]
    public void ThreePortalBotsRespondWithinTheOriginalClaimWindow()
    {
        var options = Options("true");
        var scheduler = Scheduler(options);
        var room = DealerTurn(options, 218119);
        Discard(scheduler, room, "b7");
        var before = Snapshot(room, DiscardedAt);
        Assert.Equal("discard", before.Window().Text("reason"));
        Assert.Equal(DiscardedAt.AddSeconds(8), Deadline(before));

        string[] competingClaims = ["chi", "hu", "peng"];
        var after = before;
        for (var seat = 1; seat <= competingClaims.Length; seat++)
        {
            var due = DiscardedAt.AddSeconds(seat);
            Assert.False(scheduler.AdvanceDue(room, due.AddTicks(-1)));
            Assert.Contains(ChangshaEngine.ViewSeat(room.Game!, seat).LegalOptions,
                option => option.Kind == competingClaims[seat - 1]);
            Assert.True(scheduler.AdvanceDue(room, due));
            Assert.Empty(ChangshaEngine.ViewSeat(room.Game!, seat).LegalOptions);
            after = Snapshot(room, due);
            Assert.Equal(before.GameRevision() + seat, after.GameRevision());
            if (seat < competingClaims.Length)
            {
                Assert.Equal(before.RoomRevision(), after.RoomRevision());
                Assert.Equal(before.Window().GetRawText(), after.Window().GetRawText());
            }
        }
        Assert.Equal("complete", after.Status());
        Assert.Equal(before.RoomRevision() + 1, after.RoomRevision());
        Assert.Equal(JsonValueKind.Null, after.Window().ValueKind);
        var winner = Assert.Single(after.Table().GetProperty("settlement").GetProperty("winners").EnumerateArray());
        Assert.Equal(2, winner.GetProperty("seat").GetInt32());
    }

    [Fact]
    public async Task RecoveryKeepsThePendingBotDeadlineAndUsesPortalPaceForTheNextTurn()
    {
        var standalone = Options(null);
        var room = DealerTurn(standalone, 1);
        Discard(Scheduler(standalone), room, "b3");

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<MahjongDb>().UseSqlite(connection).Options;
        await using var db = new MahjongDb(dbOptions);
        await db.Database.MigrateAsync();
        var row = new RoomRow { Id = room.Id, Checkpoint = "", ExpiresAtTicks = DiscardedAt.AddDays(1).UtcTicks };
        room.Store(row);
        var guestId = room.Seats[0].GuestId!.Value;
        db.Guests.Add(new GuestRow { Id = guestId, ExpiresAtTicks = row.ExpiresAtTicks });
        db.Rooms.Add(row);
        db.Memberships.Add(new MembershipRow { GuestId = guestId, RoomId = room.Id, Seat = 0 });
        await db.SaveChangesAsync();

        var due = DiscardedAt.AddMilliseconds(700);
        var clock = new TestClock { Now = due.AddTicks(-1) };
        var portal = Options("true");
        var rooms = new Rooms(new PooledDbContextFactory<MahjongDb>(dbOptions), portal,
            new ProtectedIdentity(new EphemeralDataProtectionProvider(), portal),
            Scheduler(portal), clock, NullLogger<Rooms>.Instance);
        await rooms.InitializeAsync(CancellationToken.None);
        await rooms.TickAsync();
        var recovered = await ReadCheckpoint();
        Assert.Equal(due, recovered.NextBotGameTime);
        Assert.Equal(room.GameRevision, recovered.GameRevision);
        Assert.Equal(Snapshot(room, clock.Now).Window().GetRawText(), Snapshot(recovered, clock.Now).Window().GetRawText());

        clock.Now = due;
        await rooms.TickAsync();
        var advanced = await ReadCheckpoint();
        Assert.Equal(room.GameRevision + 1, advanced.GameRevision);
        Assert.Equal(room.RoomRevision, advanced.RoomRevision);
        Assert.Equal(due.AddSeconds(1), advanced.NextBotGameTime);

        clock.Now = due.AddSeconds(1).AddTicks(-1);
        await rooms.TickAsync();
        Assert.Equal(advanced.GameRevision, (await ReadCheckpoint()).GameRevision);
        clock.Now = due.AddSeconds(1);
        await rooms.TickAsync();
        Assert.Equal(advanced.GameRevision + 1, (await ReadCheckpoint()).GameRevision);

        async Task<RoomCheckpoint> ReadCheckpoint() => RoomCheckpoint.Read(await db.Rooms.AsNoTracking().SingleAsync());
    }

    private static ServerOptions Options(string? portal, string? testDelay = null)
    {
        var configuration = new ConfigurationManager();
        if (portal is not null) configuration["Portal:Enabled"] = portal;
        if (testDelay is not null) configuration["Mahjong:Testing:BotDelayMilliseconds"] = testDelay;
        var environment = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            EnvironmentName = testDelay is null ? "Production" : "Testing"
        }).Environment;
        return ServerOptions.Read(configuration, environment);
    }

    private static RoomGame Scheduler(ServerOptions options) => new(options, NullLogger<RoomGame>.Instance);

    private static RoomCheckpoint DealerTurn(ServerOptions options, ulong seed) => new()
    {
        Id = "pacing", Visibility = "public", MatchHands = 1, Status = "playing", GameRevision = 1, WindowAnchor = 1,
        Seats = [Chair.Human(Guid.NewGuid(), "Dealer"), Chair.Bot(1), Chair.Bot(2), Chair.Bot(3)],
        Game = ChangshaEngine.StartHand(new(1, 0, new(seed, 29), ChangshaEngine.Profile,
            options.TurnLimit, options.ClaimLimit), HandStarted)
    };

    private static void Discard(RoomGame scheduler, RoomCheckpoint room, string face)
    {
        var dealer = ChangshaEngine.ViewSeat(room.Game!, 0);
        var discard = dealer.LegalOptions.First(option => option.Kind == "discard" && option.Faces[0] == face);
        var window = ChangshaEngine.ViewPublic(room.Game!).Window!;
        Assert.Null(scheduler.Choose(room, new Intent("choose", room.Id, Revision: room.WindowAnchor!.Value,
            WindowId: room.PublicWindowId(window), OptionId: discard.OptionId), 0, DiscardedAt));
    }

    private static JsonElement Snapshot(RoomCheckpoint room, DateTimeOffset now) =>
        JsonSerializer.SerializeToElement(Projection.Snapshot(room, 0, now, _ => false), Wire.Json);

    private static DateTimeOffset Deadline(JsonElement snapshot) => snapshot.Window().GetProperty("deadlineUtc").GetDateTimeOffset();

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
