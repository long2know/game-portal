using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Mahjong.Rules;
using Mahjong.Server.Social;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Server;

internal sealed class BusyException : Exception;

internal sealed class WorkLane(int capacity)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _queued;

    public async Task<T> Run<T>(Func<Task<T>> work, bool bounded)
    {
        if (Interlocked.Increment(ref _queued) > capacity && bounded)
        {
            Interlocked.Decrement(ref _queued);
            throw new BusyException();
        }
        try
        {
            await _gate.WaitAsync();
            try { return await work(); }
            finally { _gate.Release(); }
        }
        finally
        {
            Interlocked.Decrement(ref _queued);
        }
    }
}

internal sealed class Rooms(IDbContextFactory<MahjongDb> factory, ServerOptions options, ProtectedIdentity identity,
    RoomGame game, TimeProvider time, ILogger<Rooms> log, SocialPresence? socialPresence = null)
{
    private const int RoomLimit = 64;
    private const int ActiveReceiptLimit = 2048;
    private const int TotalActiveReceiptLimit = 65_536;
    private const int GuestLimit = 50_000;
    private static readonly TimeSpan ChatPeriod = TimeSpan.FromSeconds(10);
    private readonly WorkLane _writer = new(128);
    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Peer> _attached = [];
    private long _leasePruneTicks;
    private long _lastPrune;
    private volatile bool _recovered;

    private sealed class Slot(long due)
    {
        public readonly WorkLane Work = new(24);
        public long DueTicks = due;
    }

    private sealed record Loaded(RoomRow Row, RoomCheckpoint Checkpoint, bool TimerChanged);

    private Task<T> Run<T>(string? roomId, Func<Task<T>> work, bool bounded = true) =>
        roomId is not null && _slots.TryGetValue(roomId, out var slot)
            ? slot.Work.Run(() => _writer.Run(work, bounded), bounded) : _writer.Run(work, bounded);

    private DateTimeOffset EffectiveLeaseClock(DateTimeOffset utc) =>
        new(Math.Max(utc.UtcTicks, Volatile.Read(ref _leasePruneTicks)), TimeSpan.Zero);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
        var utc = time.GetUtcNow();
        var rows = await db.Rooms.ToListAsync(cancellationToken);
        if (rows.Count > RoomLimit) throw new InvalidOperationException("The database exceeds the supported room limit.");
        var known = new Dictionary<Guid, (string RoomId, int Seat)>();
        foreach (var row in rows.Where(row => row.ExpiresAtTicks > utc.UtcTicks))
        {
            var checkpoint = RoomCheckpoint.Read(row);
            if (Advance(checkpoint, utc)) checkpoint.Store(row);
            for (var seat = 0; seat < checkpoint.Seats.Length; seat++)
                if (checkpoint.Seats[seat].GuestId is { } guest)
                    known.Add(guest, (row.Id, seat));
            Register(row, checkpoint);
        }
        db.Rooms.RemoveRange(rows.Where(row => row.ExpiresAtTicks <= utc.UtcTicks));
        await db.SaveChangesAsync(cancellationToken);
        var memberships = await db.Memberships.AsNoTracking().ToListAsync(cancellationToken);
        if (known.Count != memberships.Count || memberships.Any(member =>
                !known.TryGetValue(member.GuestId, out var chair) || chair != (member.RoomId, member.Seat)))
            throw new InvalidOperationException("Room membership does not match the durable checkpoint.");
        _leasePruneTicks = await db.Clock.Select(c => c.LeasePruneTicks).SingleAsync(cancellationToken);
        await PruneAsync(db, utc);
        _recovered = true;
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        if (!_recovered) return false;
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            return await db.Clock.AsNoTracking().AnyAsync(row => row.Id == 1, cancellationToken);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    public async Task<bool> IsGuestAsync(Guid guestId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Guests.AnyAsync(g => g.Id == guestId, cancellationToken);
    }

    public Task<(Guid Id, GuestView View)> EnterAsync(Guid? guestId, string? name)
    {
        return Run(null, async () =>
        {
            await using var db = await factory.CreateDbContextAsync();
            var utc = time.GetUtcNow();
            var guest = guestId.HasValue ? await db.Guests.FindAsync(guestId.Value) : null;
            if (guest is null)
            {
                if (await db.Guests.CountAsync() >= GuestLimit) throw new BusyException();
                guest = new GuestRow { Id = Guid.NewGuid(), DisplayName = name ?? "Guest" };
                db.Guests.Add(guest);
            }
            // Cover the short delay between this commit and issuance of the age-limited cookie.
            guest.ExpiresAtTicks = Math.Max(guest.ExpiresAtTicks,
                (utc + ProtectedIdentity.GuestLifetime + TimeSpan.FromMinutes(1)).UtcTicks);
            await db.SaveChangesAsync();
            return (guest.Id, await GuestProfileAsync(db, guest, utc));
        });
    }

    public async Task<GuestView?> EditProfileAsync(Guid guestId, ProfileChange change)
    {
        var roomId = change.DisplayName is null ? null : await MembershipRoomAsync(guestId);
        return await Run(roomId, () => UpdateProfileAsync(guestId, change));
    }

    private async Task<GuestView?> UpdateProfileAsync(Guid guestId, ProfileChange change)
    {
        await using var db = await factory.CreateDbContextAsync();
        if (await db.Guests.FindAsync(guestId) is not { } guest) return null;
        var utc = time.GetUtcNow();
        Loaded? live = null;
        if (change.DisplayName is { } name && name != guest.DisplayName)
        {
            guest.DisplayName = name;
            if (await LiveMembershipAsync(db, guestId, utc) is { } member)
            {
                live = await LoadAsync(db, member.RoomId, utc);
                if (live is not null)
                {
                    var room = live.Checkpoint;
                    room.Seats[member.Seat] = room.Seats[member.Seat] with { DisplayName = name };
                    room.TouchRevision();
                    room.Store(live.Row);
                }
            }
        }
        if (change.Preferences is { } preferences)
        {
            guest.Sound = preferences.Sound;
            guest.Music = preferences.Music;
            guest.Volume = preferences.Volume;
            guest.ReducedMotion = preferences.ReducedMotion;
        }
        await db.SaveChangesAsync();
        if (live is not null)
        {
            Register(live.Row, live.Checkpoint);
            Broadcast(live.Checkpoint, utc);
        }
        return await GuestProfileAsync(db, guest, utc);
    }

    public async Task<object> DiscoverAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcTicks;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var candidates = db.Rooms.AsNoTracking().Where(r => r.IsPublic && r.ExpiresAtTicks > now);
        if (!options.PortalEnabled)
            candidates = candidates.Where(r => r.Waiting && r.OccupiedSeats < 4);
        var rows = await candidates
            .OrderBy(r => r.Id).Take(RoomLimit)
            .Select(r => new { r.Id, r.OccupiedSeats, r.MatchHands, r.Checkpoint }).ToArrayAsync(cancellationToken);
        var rooms = new List<PublicRoom>(rows.Length);
        foreach (var row in rows)
        {
            var room = RoomCheckpoint.Read(row.Id, row.Checkpoint);
            var profile = room.RulesVersion == ChangshaEngine.Profile ? null : room.RulesVersion;
            if (!options.PortalEnabled)
                rooms.Add(new(row.Id, row.OccupiedSeats, row.MatchHands, profile));
            else if (room.Visibility == "public" && (room.Status == "waiting" && OpenSeat(room) >= 0
                || IsTakeoverPhase(room) && AvailableBotSeat(room) >= 0))
                rooms.Add(new(row.Id, row.OccupiedSeats, row.MatchHands, profile, room.Status,
                    room.Seats.Count(chair => chair.IsBot), room.Seats[room.HostSeat].DisplayName ?? "Guest"));
        }
        return new { rooms };
    }

    public static async Task<(string RoomId, bool CanStartFresh)?> ResumeTargetAsync(MahjongDb db, Guid guest, long now,
        CancellationToken stop)
    {
        var saved = await (from member in db.Memberships.AsNoTracking()
            join room in db.Rooms.AsNoTracking() on member.RoomId equals room.Id
            where member.GuestId == guest && room.ExpiresAtTicks > now
            select new { room.Id, member.Seat, Checkpoint = !room.Waiting && room.OccupiedSeats == 4 ? room.Checkpoint : null })
            .SingleOrDefaultAsync(stop);
        if (saved is null) return null;
        if (saved.Checkpoint is null) return (saved.Id, false);
        var checkpoint = RoomCheckpoint.Read(saved.Id, saved.Checkpoint);
        return (saved.Id, checkpoint.HostSeat == saved.Seat && checkpoint.Seats[saved.Seat].GuestId == guest
            && IsRestartableBotRoom(checkpoint));
    }

    public void IssueLease(Peer peer)
    {
        var utc = time.GetUtcNow();
        peer.Send(identity.NewLease(peer.GuestId, utc, EffectiveLeaseClock(utc)));
    }

    public async Task SyncAsync(Peer peer, Guid requestId, string roomId)
    {
        try
        {
            if (await MembershipRoomAsync(peer.GuestId) != roomId)
            {
                peer.Send(Acknowledgement.For(requestId, "room_not_found"));
                return;
            }
            await Run(roomId, async () =>
            {
                if (peer.IsClosing) return false;
                await using var db = await factory.CreateDbContextAsync();
                var utc = time.GetUtcNow();
                var member = await db.Memberships.FindAsync(peer.GuestId);
                var live = member?.RoomId == roomId ? await LoadAsync(db, roomId, utc) : null;
                if (live is null)
                {
                    peer.Send(Acknowledgement.For(requestId, "room_not_found"));
                    return false;
                }
                Touch(live.Row, utc);
                if (live.TimerChanged) live.Checkpoint.Store(live.Row);
                await db.SaveChangesAsync();
                Register(live.Row, live.Checkpoint);
                Attach(peer, roomId);
                peer.Send(Acknowledgement.For(requestId, null, live.Checkpoint));
                SendSnapshot(peer, member!.Seat, live.Checkpoint, utc);
                Broadcast(live.Checkpoint, utc, peer);
                return true;
            });
        }
        catch (Exception ex) when (Unavailable(ex))
        {
            peer.Send(Acknowledgement.For(requestId, "busy"));
        }
    }

    public async Task CommandAsync(Peer peer, Guid requestId, string? lease, string op, JsonElement payload)
    {
        var intent = Wire.ParseIntent(op, payload);
        if (!options.PortalEnabled && intent is ({ TakeBot: true } or { ReplaceWithBot: true })) intent = null;
        var fingerprint = Wire.Fingerprint(op, payload, intent);
        try
        {
            var hint = intent?.RoomId == peer.RoomId ? peer.RoomId : null;
            if (intent?.Op == "joinRoom")
            {
                await using var read = await factory.CreateDbContextAsync();
                var target = intent.InviteCode is { } invite
                    ? read.Rooms.Where(room => room.InviteCode == invite)
                    : read.Rooms.Where(room => room.IsPublic && room.Id == intent.RoomId);
                hint = await target.Select(room => room.Id).SingleOrDefaultAsync();
            }
            await Run(hint, () => ExecuteCommandAsync(peer, requestId, lease, intent, fingerprint));
        }
        catch (Exception ex) when (Unavailable(ex))
        {
            peer.Send(Acknowledgement.For(requestId, "busy"));
        }
    }

    private async Task<bool> ExecuteCommandAsync(Peer peer, Guid requestId, string? lease, Intent? intent, string fingerprint)
    {
        if (peer.IsClosing) return false;
        var utc = time.GetUtcNow();
        var leaseNow = EffectiveLeaseClock(utc);
        var leaseError = identity.ReadLease(lease, peer.GuestId, leaseNow, out var leaseId, out var expiresAt);
        if (leaseError is not null)
        {
            peer.Send(Acknowledgement.For(requestId, leaseError));
            return false;
        }
        await using var db = await factory.CreateDbContextAsync();
        if (await db.Receipts.FindAsync(peer.GuestId, leaseId, requestId) is { } receipt)
        {
            await ReplayAsync(db, peer, requestId, receipt, fingerprint, intent, utc);
            return true;
        }
        if (await db.Receipts.CountAsync(r => r.GuestId == peer.GuestId && r.ExpiresAtTicks > leaseNow.UtcTicks) >= ActiveReceiptLimit
            || await db.Receipts.CountAsync(r => r.ExpiresAtTicks > leaseNow.UtcTicks) >= TotalActiveReceiptLimit)
        {
            peer.Send(Acknowledgement.For(requestId, "busy"));
            return false;
        }

        utc = time.GetUtcNow();
        var code = intent is null ? "invalid_request" : null;
        Loaded? live = null;
        int? seat = null;
        var attach = false;
        var leave = false;
        var delete = false;
        if (intent?.Op is "createRoom" or "joinRoom")
        {
            (code, live, seat) = await AdmitAsync(db, peer.GuestId, intent, utc);
            attach = code is null;
        }
        else if (intent is not null)
        {
            var member = await db.Memberships.FindAsync(peer.GuestId);
            if (member is null || member.RoomId != intent.RoomId || (live = await LoadAsync(db, member.RoomId, utc)) is null)
                code = "room_not_found";
            else
            {
                var room = live.Checkpoint;
                if (!IsAttached(peer, room.Id)) code = "invalid_state";
                else
                {
                    seat = member.Seat;
                    code = Mutate(room, member.Seat, intent, utc);
                }
                leave = code is null && intent.Op == "leaveRoom";
                if (leave && (intent.Flag || intent.ReplaceWithBot || room.Status is "waiting" or "complete"))
                {
                    db.Memberships.Remove(member);
                    room.ReplaceChair(member.Seat, intent.ReplaceWithBot ? Chair.Bot(member.Seat) : Chair.Open);
                    if (intent.ReplaceWithBot)
                    {
                        room.RecordRelease(member.GuestId);
                        game.RefreshBotSchedule(room, utc);
                    }
                    else room.ResetReadiness();
                    room.TouchRevision();
                    var nextHost = Array.FindIndex(room.Seats, chair => chair.GuestId.HasValue);
                    delete = nextHost < 0;
                    if (!delete && room.HostSeat == member.Seat) room.HostSeat = nextHost;
                    if (delete) db.Rooms.Remove(live.Row);
                }
            }
        }

        var tracking = live?.Checkpoint;
        var ack = Acknowledgement.For(requestId, code, seat.HasValue ? tracking : null);
        if (live is not null && !delete)
        {
            if (code is null) Touch(live.Row, utc);
            if (code is null || live.TimerChanged) tracking!.Store(live.Row);
        }
        db.Receipts.Add(new ReceiptRow
        {
            GuestId = peer.GuestId, LeaseId = leaseId, RequestId = requestId, ExpiresAtTicks = expiresAt,
            Fingerprint = fingerprint, Acknowledgement = JsonSerializer.Serialize(ack, Wire.Json)
        });
        await db.SaveChangesAsync();

        if (attach) Attach(peer, tracking!.Id);
        if (leave) Detach(peer);
        peer.Send(ack);
        if (live is not null)
        {
            if (delete) Forget(live.Row.Id);
            else
            {
                Register(live.Row, tracking!);
                if (!leave && seat.HasValue) SendSnapshot(peer, seat.Value, tracking!, utc);
                if (code is null || live.TimerChanged) Broadcast(tracking!, utc, peer);
            }
        }
        return code is null;
    }

    private async Task ReplayAsync(MahjongDb db, Peer peer, Guid requestId, ReceiptRow receipt, string fingerprint,
        Intent? intent, DateTimeOffset utc)
    {
        if (!string.Equals(receipt.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            peer.Send(Acknowledgement.For(requestId, "request_conflict"));
            return;
        }
        var ack = JsonSerializer.Deserialize<Acknowledgement>(receipt.Acknowledgement, Wire.Json)
            ?? throw new InvalidOperationException("Missing durable command acknowledgement.");
        Loaded? live = null;
        int? seat = null;
        if (intent?.Op != "leaveRoom" && ack.RoomId is { } roomId && IsAttached(peer, roomId))
        {
            var member = await db.Memberships.FindAsync(peer.GuestId);
            if (member?.RoomId == roomId)
            {
                seat = member.Seat;
                live = await LoadAsync(db, roomId, utc, advance: false);
            }
        }
        peer.Send(ack);
        if (live is not null && seat.HasValue)
            SendSnapshot(peer, seat.Value, live.Checkpoint, utc);
    }

    public static async Task<long?> SocialWaitingVersionAsync(MahjongDb db, string roomId, Guid sender, DateTimeOffset utc,
        CancellationToken stop)
    {
        var row = await db.Rooms.AsNoTracking().SingleOrDefaultAsync(r => r.Id == roomId
            && r.ExpiresAtTicks > utc.UtcTicks && r.Waiting && r.OccupiedSeats < 4, stop);
        if (row is null) return null;
        var room = RoomCheckpoint.Read(row);
        return SocialOpenSeat(room, sender) >= 0 ? room.GameRevision : null;
    }

    public Task JoinSocialAsync(SocialAcceptance admission, CancellationToken stop) =>
        Run(admission.Target.TableId, async () =>
        {
            // The recipient's durable hint was resolved before entering the room slot and writer.
            await using var db = await factory.CreateDbContextAsync(stop);
            await using var transaction = await db.Database.BeginTransactionAsync(stop);
            var utc = time.GetUtcNow();
            var receipt = await admission.AuthorizeAsync(db, utc, stop);
            var row = await db.Rooms.FindAsync([admission.Target.TableId], stop);
            if (row is null || row.ExpiresAtTicks <= utc.UtcTicks) throw SocialLedger.Missing();
            var member = await db.Memberships.FindAsync([admission.RecipientId], stop);
            if (receipt.Accepted)
            {
                if (member?.RoomId == row.Id) return true;
                throw SocialLedger.Missing();
            }

            RoomRow? expired = null;
            if (member is not null)
            {
                if (member.RoomId == row.Id) throw SocialLedger.AlreadySeated();
                expired = await db.Rooms.FindAsync([member.RoomId], stop);
                if (expired is null || expired.ExpiresAtTicks > utc.UtcTicks) throw SocialLedger.AlreadySeated();
            }

            var room = RoomCheckpoint.Read(row);
            var seat = SocialOpenSeat(room, admission.SenderId);
            if (seat < 0 || room.GameRevision != receipt.WaitingVersion) throw SocialLedger.Missing();
            var guest = await db.Guests.FindAsync([admission.RecipientId], stop) ?? throw SocialLedger.Missing();
            if (expired is not null)
            {
                // Free the unique membership key inside this transaction, with no expiry publication yet.
                db.Rooms.Remove(expired);
                await db.SaveChangesAsync(stop);
            }
            using var handoff = await admission.StageAsync(db, receipt, stop);
            SeatHuman(db, room, guest, seat);
            Touch(row, utc);
            room.Store(row);
            await db.SaveChangesAsync(stop);
            await transaction.CommitAsync(stop);
            handoff.Commit();

            if (expired is not null) Forget(expired.Id);
            Register(row, room);
            Broadcast(room, utc);
            return true;
        });

    private static int SocialOpenSeat(RoomCheckpoint room, Guid sender) =>
        room.Status == "waiting" && room.Seats[room.HostSeat].GuestId == sender ? OpenSeat(room) : -1;

    private static int OpenSeat(RoomCheckpoint room) => Array.FindIndex(room.Seats, chair => chair.IsOpen);

    private static bool IsTakeoverPhase(RoomCheckpoint room) =>
        room.Status == "playing" || room.Status == "waiting" && OpenSeat(room) < 0;

    private static int AvailableBotSeat(RoomCheckpoint room) => room.BotAdmissionCapped()
        ? -1 : Array.FindIndex(room.Seats, chair => chair.IsBot && !chair.GuestId.HasValue);

    private static void SeatHuman(MahjongDb db, RoomCheckpoint room, GuestRow guest, int seat)
    {
        room.ReplaceChair(seat, Chair.Human(guest.Id, guest.DisplayName));
        if (room.Status == "waiting") room.ResetReadiness();
        room.TouchRevision();
        db.Memberships.Add(new MembershipRow { GuestId = guest.Id, RoomId = room.Id, Seat = seat });
    }

    private async Task<(string? Code, Loaded? Live, int? Seat)> AdmitAsync(MahjongDb db, Guid guestId, Intent intent,
        DateTimeOffset utc)
    {
        Loaded? live = null;
        if (intent.Op == "joinRoom")
        {
            var target = intent.InviteCode is { } invite
                ? await db.Rooms.SingleOrDefaultAsync(r => r.InviteCode == invite)
                : await db.Rooms.SingleOrDefaultAsync(r => r.Id == intent.RoomId && r.IsPublic);
            if (target is null || (live = await LoadAsync(db, target.Id, utc)) is null
                || intent.TakeBot && live.Checkpoint.Visibility != "public")
                return ("room_not_found", null, null);
        }
        if (await db.Memberships.FindAsync(guestId) is { } member)
        {
            if (await LoadAsync(db, member.RoomId, utc, advance: false) is not null)
                return ("already_seated", live, null);
        }
        var guest = await db.Guests.FindAsync(guestId);
        if (guest is null) return ("forbidden", live, null);
        if (live is not null)
        {
            var room = live.Checkpoint;
            if (intent.TakeBot)
            {
                if (!IsTakeoverPhase(room)) return ("invalid_state", live, null);
                if (room.ReleasedGuests?.Contains(guestId) == true) return ("seat_released", live, null);
                var bot = AvailableBotSeat(room);
                if (bot < 0) return ("room_full", live, null);
                SeatHuman(db, room, guest, bot);
                if (room.Status == "playing") game.RefreshBotSchedule(room, utc);
                return (null, live, bot);
            }
            if (room.Status != "waiting") return ("invalid_state", live, null);
            var open = OpenSeat(room);
            if (open < 0) return ("room_full", live, null);
            SeatHuman(db, room, guest, open);
            return (null, live, open);
        }
        foreach (var expired in await db.Rooms.Where(room => room.ExpiresAtTicks <= utc.UtcTicks)
                     .Select(room => room.Id).ToArrayAsync())
            await LoadAsync(db, expired, utc, advance: false);
        if (await db.Rooms.CountAsync() >= RoomLimit) return ("room_full", null, null);
        var created = new RoomCheckpoint
        {
            Id = RoomGame.RandomCode(16), Visibility = intent.Visibility!, MatchHands = intent.MatchHands,
            RulesVersion = intent.RulesVersion ?? ChangshaEngine.Profile,
            NextDealerSeat = options.PortalEnabled ? RandomNumberGenerator.GetInt32(4) : null,
            InviteCode = intent.Visibility == "invite" ? RoomGame.RandomCode(12) : null,
            Seats = [Chair.Human(guestId, guest.DisplayName),
                intent.Flag ? Chair.Bot(1) : Chair.Open, intent.Flag ? Chair.Bot(2) : Chair.Open,
                intent.Flag ? Chair.Bot(3) : Chair.Open]
        };
        var row = new RoomRow
        {
            Id = created.Id, InviteCode = created.InviteCode, IsPublic = created.Visibility == "public",
            MatchHands = created.MatchHands, Checkpoint = ""
        };
        db.Rooms.Add(row);
        db.Memberships.Add(new MembershipRow { GuestId = guestId, RoomId = row.Id, Seat = 0 });
        return (null, new Loaded(row, created, false), 0);
    }

    private string? Mutate(RoomCheckpoint room, int seat, Intent intent, DateTimeOffset utc)
    {
        var hostOnly = intent.Op is "setBot" or "start" or "setPaused" or "rematch" or "restartMatch"
            || intent.Op == "leaveRoom" && intent.Flag;
        if (hostOnly && room.HostSeat != seat) return "forbidden";
        if (intent.Op == "choose") return game.Choose(room, intent, seat, utc);
        if (intent.Op == "chat") return Chat(room, seat, intent.Text!, utc);
        if (room.RoomRevision != intent.Revision) return "stale_room";

        switch (intent.Op)
        {
            case "setReady" when room.Status == "waiting":
                if (room.Seats[seat].Ready != intent.Flag)
                {
                    room.Seats[seat] = room.Seats[seat] with { Ready = intent.Flag };
                    room.TouchRevision();
                }
                return null;
            case "setBot" when room.Status == "waiting" && room.Seats[intent.Seat].GuestId is null:
                if (room.Seats[intent.Seat].IsBot != intent.Flag)
                {
                    room.ReplaceChair(intent.Seat, intent.Flag ? Chair.Bot(intent.Seat) : Chair.Open);
                    room.ResetReadiness();
                    room.TouchRevision();
                }
                return null;
            case "start" when room.Status == "waiting":
                if (room.Seats.Any(chair => !chair.Ready || chair.IsOpen)) return "not_ready";
                game.StartHand(room, utc);
                return null;
            case "setPaused" when room.Status == "playing":
                game.SetPaused(room, intent.Flag, utc);
                return null;
            case "nextHand" when room.Status == "betweenHands":
                game.StartHand(room, utc);
                return null;
            case "rematch" when room.Status == "complete":
                RoomGame.Rematch(room);
                return null;
            case "restartMatch" when IsRestartableBotRoom(room):
                game.RestartMatch(room, utc);
                return null;
            case "leaveRoom":
                if (intent.ReplaceWithBot)
                    return room.Status == "playing" && room.Seats.Any(chair =>
                        chair.GuestId is { } other && other != room.Seats[seat].GuestId) ? null : "invalid_state";
                return !intent.Flag || room.Seats.Count(chair => chair.GuestId.HasValue) == 1 ? null : "invalid_state";
            default:
                return "invalid_state";
        }
    }

    private static bool IsRestartableBotRoom(RoomCheckpoint room) =>
        room.Status is ("playing" or "betweenHands" or "complete")
        && room.Seats.Count(chair => chair.IsBot && !chair.GuestId.HasValue) == 3;

    private static string? Chat(RoomCheckpoint room, int seat, string text, DateTimeOffset utc)
    {
        var guest = room.Seats[seat];
        if (room.Chat.Count != 0 && room.Chat[^1].SentAtUtc > utc) utc = room.Chat[^1].SentAtUtc;
        var cutoff = utc - ChatPeriod;
        if (room.Chat.Count(post => post.GuestId == guest.GuestId && post.SentAtUtc > cutoff) >= 5
            || room.Chat.Count == 30 && room.Chat[0].SentAtUtc > cutoff)
            return "rate_limited";
        if (room.Chat.Count == 30) room.Chat.RemoveAt(0);
        room.Chat.Add(new ChatPost(guest.GuestId!.Value, RoomGame.RandomCode(12), seat, guest.DisplayName!, text, utc));
        room.TouchRevision();
        return null;
    }

    private async Task<Loaded?> LoadAsync(MahjongDb db, string roomId, DateTimeOffset utc, bool advance = true)
    {
        var row = await db.Rooms.FindAsync(roomId);
        if (row is null) return null;
        if (row.ExpiresAtTicks <= utc.UtcTicks)
        {
            db.Rooms.Remove(row);
            await db.SaveChangesAsync();
            Forget(roomId);
            return null;
        }
        var room = RoomCheckpoint.Read(row);
        return new Loaded(row, room, advance && Advance(room, utc));
    }

    private static async Task<MembershipRow?> LiveMembershipAsync(MahjongDb db, Guid guestId, DateTimeOffset utc)
    {
        var member = await db.Memberships.FindAsync(guestId);
        if (member is null) return null;
        var room = await db.Rooms.FindAsync(member.RoomId);
        return room is not null && room.ExpiresAtTicks > utc.UtcTicks ? member : null;
    }

    private async Task<string?> MembershipRoomAsync(Guid guestId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Memberships.Where(member => member.GuestId == guestId)
            .Select(member => member.RoomId).SingleOrDefaultAsync();
    }

    private async Task<GuestView> GuestProfileAsync(MahjongDb db, GuestRow guest, DateTimeOffset utc) =>
        Profile(guest, (await LiveMembershipAsync(db, guest.Id, utc))?.RoomId);

    private static GuestView Profile(GuestRow guest, string? roomId) =>
        new(guest.DisplayName, new Preferences(guest.Sound, guest.Music, guest.Volume, guest.ReducedMotion), roomId);

    private bool Advance(RoomCheckpoint room, DateTimeOffset utc)
    {
        var changes = 0;
        while (game.AdvanceDue(room, utc))
            if (++changes > 1024) throw new InvalidOperationException("Exceeded the maximum due transitions for a hand.");
        return changes != 0;
    }

    private static void Touch(RoomRow row, DateTimeOffset utc) =>
        row.ExpiresAtTicks = Math.Max(row.ExpiresAtTicks, (utc + ServerOptions.RoomIdleLimit).UtcTicks);

    private void Register(RoomRow row, RoomCheckpoint room)
    {
        var due = game.DueTicks(row, room);
        var slot = _slots.GetOrAdd(row.Id, _ => new Slot(due));
        Volatile.Write(ref slot.DueTicks, due);
    }

    private void Forget(string roomId)
    {
        _slots.TryRemove(roomId, out _);
        foreach (var peer in _attached.Values.Where(p => p.RoomId == roomId).ToArray())
        {
            Detach(peer);
            peer.Send(Wire.Error("room_not_found"));
        }
    }

    private void Attach(Peer peer, string roomId)
    {
        if (peer.IsClosing) return;
        if (_attached.TryGetValue(peer.GuestId, out var previous) && previous != peer)
        {
            previous.RoomId = null;
            previous.Close(4001, "superseded");
        }
        peer.RoomId = roomId;
        _attached[peer.GuestId] = peer;
    }

    private bool IsAttached(Peer peer, string roomId) => !peer.IsClosing && peer.RoomId == roomId
        && _attached.TryGetValue(peer.GuestId, out var active) && active == peer;

    private bool Detach(Peer peer)
    {
        peer.RoomId = null;
        if (!_attached.TryGetValue(peer.GuestId, out var active) || active != peer) return false;
        return _attached.Remove(peer.GuestId);
    }

    public async Task DisconnectAsync(Peer peer)
    {
        try
        {
            await Run(peer.RoomId, async () =>
            {
                var id = peer.RoomId;
                if (!Detach(peer) || id is null) return false;
                await using var db = await factory.CreateDbContextAsync();
                var utc = time.GetUtcNow();
                if (await LoadAsync(db, id, utc, advance: false) is { } live)
                    Broadcast(live.Checkpoint, utc);
                return true;
            }, bounded: false);
        }
        catch (Exception ex) when (Unavailable(ex))
        {
            log.LogWarning("Unable to publish a guest disconnection while storage is unavailable.");
        }
    }

    private void SendSnapshot(Peer peer, int seat, RoomCheckpoint room, DateTimeOffset utc) =>
        peer.Send(Projection.Snapshot(room, seat, utc, portalEnabled: options.PortalEnabled,
            id => _attached.TryGetValue(id, out var current) && IsAttached(current, room.Id)));

    private void Broadcast(RoomCheckpoint room, DateTimeOffset utc, Peer? except = null)
    {
        for (var seat = 0; seat < room.Seats.Length; seat++)
            if (room.Seats[seat].GuestId is { } guest && _attached.TryGetValue(guest, out var peer)
                && peer != except && IsAttached(peer, room.Id))
                SendSnapshot(peer, seat, room, utc);
    }

    public async Task TickAsync()
    {
        if (!_recovered) return;
        foreach (var (id, slot) in _slots)
        {
            if (Volatile.Read(ref slot.DueTicks) > time.GetUtcNow().UtcTicks) continue;
            await Run(id, async () =>
            {
                await using var db = await factory.CreateDbContextAsync();
                var utc = time.GetUtcNow();
                if (await LoadAsync(db, id, utc) is { TimerChanged: true } live)
                {
                    live.Checkpoint.Store(live.Row);
                    await db.SaveChangesAsync();
                    Register(live.Row, live.Checkpoint);
                    Broadcast(live.Checkpoint, utc);
                }
                return true;
            }, bounded: false);
        }
        if (time.GetElapsedTime(_lastPrune) < options.PruneInterval) return;
        await Run(null, async () =>
        {
            await using var db = await factory.CreateDbContextAsync();
            await PruneAsync(db, time.GetUtcNow());
            return true;
        }, bounded: false);
    }

    private async Task PruneAsync(MahjongDb db, DateTimeOffset utc)
    {
        var cutoff = EffectiveLeaseClock(utc).UtcTicks;
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Receipts.Where(r => r.ExpiresAtTicks <= cutoff).ExecuteDeleteAsync();
        await db.BoardTables.Where(table => table.ExpiresAtTicks <= utc.UtcTicks).ExecuteDeleteAsync();
        await db.SeatedTables.Where(table => table.ExpiresAtTicks <= utc.UtcTicks).ExecuteDeleteAsync();
        if (socialPresence is not null)
            await db.SocialInvites.Where(invite => invite.ExpiresAtTicks <= utc.UtcTicks).ExecuteDeleteAsync();
        await db.Clock.Where(c => c.Id == 1).ExecuteUpdateAsync(setters => setters.SetProperty(c => c.LeasePruneTicks, cutoff));
        await db.Guests.Where(guest => guest.ExpiresAtTicks <= utc.UtcTicks
            && !db.Memberships.Any(member => member.GuestId == guest.Id)
            && !db.BoardMemberships.Any(member => member.GuestId == guest.Id)
            && !db.SeatedMemberships.Any(member => member.GuestId == guest.Id)).ExecuteDeleteAsync();
        await transaction.CommitAsync();
        socialPresence?.Prune(utc);
        Volatile.Write(ref _leasePruneTicks, cutoff);
        _lastPrune = time.GetTimestamp();
    }

    internal static bool Unavailable(Exception exception) =>
        exception is BusyException or SqliteException or DbUpdateException { InnerException: SqliteException };
}

internal sealed class RoomClock(Rooms rooms, ServerOptions options, ILogger<RoomClock> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.TickInterval);
        var warned = false;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await rooms.TickAsync();
                warned = false;
            }
            catch (Exception ex) when (Rooms.Unavailable(ex))
            {
                if (!warned) log.LogWarning("Room timers are waiting for SQLite to become available.");
                warned = true;
            }
        }
    }
}
