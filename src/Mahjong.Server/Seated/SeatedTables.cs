using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Mahjong.Server.Social;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Server.Seated;

internal sealed record SeatedSeat(int Seat, string Name, bool Bot, bool Occupied);
internal sealed record SeatedTable(string Id, long Revision, string Status, int Seat, SeatedSeat[] Seats,
    string? Invite, object? Position,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EndReason);

internal sealed record SeatedTurn(string Checkpoint, object Position, bool Finished = false)
{
    public string Status => Finished ? "finished" : "playing";
}

internal sealed record SeatedRules(string Game, int MinPlayers, int MaxPlayers,
    Func<int, int, bool, CancellationToken, SeatedTurn> Start, Func<string, int, bool, object> View);

internal delegate SeatedTurn SeatedCommand(string checkpoint, int seat, bool bots, CancellationToken stop);

internal sealed class SeatedTables(IDbContextFactory<MahjongDb> factory, TimeProvider time)
{
    private const int MaxTables = 512;
    private const int MaxOwnedTables = 32;
    private const int MaxCheckpointLength = 256_000;
    private static readonly TimeSpan WaitingLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan PlayingLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan EndedLifetime = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim _botWorkers = new(4);

    public async Task<SeatedTable> CreateAsync(SeatedRules rules, GuestRow guest, int players, bool bots, CancellationToken stop)
    {
        if (players < rules.MinPlayers || players > rules.MaxPlayers)
            throw new PortalFault(400, "invalid_request");
        var initial = bots ? Compute(true, () => rules.Start(players, 0, true, stop)) : null;
        var utc = time.GetUtcNow();
        await using var db = await factory.CreateDbContextAsync(stop);
        await using var transaction = await db.Database.BeginTransactionAsync(stop);
        var open = OpenTables(db, utc.UtcTicks);
        if (await open.CountAsync(stop) >= MaxTables || await open.CountAsync(
                t => t.Members.Any(m => m.GuestId == guest.Id && m.Seat == 0), stop) >= MaxOwnedTables)
            throw new PortalFault(429, "table_limit");

        var table = new SeatedTableRow
        {
            Id = Guid.NewGuid().ToString("N"), Game = rules.Game, Status = initial?.Status ?? "waiting",
            InviteCode = bots ? null : Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            Players = players, Bots = bots, Checkpoint = initial?.Checkpoint,
            ExpiresAtTicks = ExpiresAt(utc, initial?.Status ?? "waiting")
        };
        var member = NewMember(table.Id, guest.Id, 0, utc);
        table.Members.Add(member);
        db.SeatedTables.Add(table);
        await db.SaveChangesAsync(stop);
        await transaction.CommitAsync(stop);
        member.Guest = guest;
        return Project(table, 0, 0, table.Status, initial?.Position);
    }

    public async Task<SeatedTable> JoinAsync(SeatedRules rules, GuestRow guest, string invite, CancellationToken stop) =>
        (await JoinCoreAsync(rules, guest, invite, null, stop))!;

    public async Task JoinSocialAsync(SeatedRules rules, GuestRow guest, SocialAcceptance admission, CancellationToken stop) =>
        await JoinCoreAsync(rules, guest, null, admission, stop);

    private async Task<SeatedTable?> JoinCoreAsync(SeatedRules rules, GuestRow guest, string? invite, SocialAcceptance? admission,
        CancellationToken stop)
    {
        for (var attempt = 0; attempt < rules.MaxPlayers; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(stop);
            var now = time.GetUtcNow().UtcTicks;
            var table = await (admission is { } social ? SocialTargetQuery(db, rules, social.Target.TableId, now)
                : WithSeats(db).Where(t => t.Game == rules.Game && t.InviteCode == invite && t.ExpiresAtTicks > now))
                .SingleOrDefaultAsync(stop) ?? throw (admission is null ? Missing() : SocialLedger.Missing());
            var existing = table.Members.SingleOrDefault(m => m.GuestId == guest.Id);
            var open = admission is null ? table.Status == "waiting" && table.Members.Count < table.Players
                : CanInvite(table, admission.SenderId);
            if (admission is null)
            {
                if (existing is not null) return Read(rules, table, existing.Seat);
                if (!open) throw Missing();
            }

            var seat = table.Members.Count;
            var initial = open && existing is null && admission?.ReceiptHint != true && seat + 1 == table.Players
                ? Compute(false, () => rules.Start(table.Players, seat, false, stop)) : null;
            var status = initial?.Status ?? "waiting";
            await using var transaction = await db.Database.BeginTransactionAsync(stop);
            var utc = time.GetUtcNow();
            SocialInviteRow? receipt = null;
            if (admission is not null)
            {
                receipt = await admission.AuthorizeAsync(db, utc, stop);
                if (!await db.SeatedTables.AnyAsync(t => t.Id == table.Id && t.Revision == table.Revision
                        && t.ExpiresAtTicks > utc.UtcTicks, stop)) continue;
                var entitled = existing is not null && table.Status != "closed";
                if (receipt.Accepted)
                {
                    if (entitled) return null;
                    throw SocialLedger.Missing();
                }
                if (entitled) throw SocialLedger.AlreadySeated();
                if (!open) throw SocialLedger.Missing();
            }
            if (!await CommitAsync(db, table, status, initial?.Checkpoint, utc, stop)) continue;
            using var handoff = receipt is null ? null : admission!.Stage(receipt);
            var member = NewMember(table.Id, guest.Id, seat, utc);
            db.SeatedMemberships.Add(member);
            await db.SaveChangesAsync(stop);
            await transaction.CommitAsync(stop);
            if (handoff is not null)
            {
                handoff.Commit();
                return null;
            }
            member.Guest = guest;
            table.Members.Add(member);
            return Project(table, seat, table.Revision + 1, status, initial?.Position);
        }
        throw new BusyException();
    }

    public static async Task<bool> CanInviteAsync(MahjongDb db, SeatedRules rules, string id, Guid sender,
        DateTimeOffset utc, CancellationToken stop) =>
        await SocialTargetQuery(db, rules, id, utc.UtcTicks).SingleOrDefaultAsync(stop) is { } table
        && CanInvite(table, sender);

    private static bool CanInvite(SeatedTableRow table, Guid sender) => !table.Bots && table.Status == "waiting"
        && table.Members.Count < table.Players && table.Members.Any(m => m.GuestId == sender);

    private static IQueryable<SeatedTableRow> SocialTargetQuery(MahjongDb db, SeatedRules rules, string id, long now) =>
        WithSeats(db).Where(t => t.Game == rules.Game && t.Id == id && t.ExpiresAtTicks > now);

    public async Task<SeatedTable> GetAsync(SeatedRules rules, Guid guest, string id, CancellationToken stop)
    {
        await using var db = await factory.CreateDbContextAsync(stop);
        var (table, seat) = await ForMemberAsync(db, rules.Game, guest, id, stop);
        return Read(rules, table, seat);
    }

    public async Task<string?> ResumeAsync(SeatedRules rules, Guid guest, CancellationToken stop)
    {
        await using var db = await factory.CreateDbContextAsync(stop);
        var now = time.GetUtcNow().UtcTicks;
        return await db.SeatedMemberships.AsNoTracking().Where(m => m.GuestId == guest && m.Table.Game == rules.Game
                && m.Table.ExpiresAtTicks > now && (m.Table.Status == "waiting" || m.Table.Status == "playing"))
            .OrderByDescending(m => m.JoinedAtTicks).ThenByDescending(m => m.TableId)
            .Select(m => m.TableId).FirstOrDefaultAsync(stop);
    }

    public async Task<SeatedTable> ChangeAsync(SeatedRules rules, Guid guest, string id, long revision, SeatedCommand command,
        CancellationToken stop)
    {
        await using var db = await factory.CreateDbContextAsync(stop);
        var (table, seat) = await ForMemberAsync(db, rules.Game, guest, id, stop);
        RequireOpen(table, revision);
        if (table.Status == "waiting") throw new PortalFault(400, "room_not_started");
        var checkpoint = CheckedCheckpoint(table.Checkpoint);
        var next = Compute(table.Bots, () => command(checkpoint, seat, table.Bots, stop));
        if (!await CommitAsync(db, table, next.Status, next.Checkpoint, time.GetUtcNow(), stop)) throw Stale();
        return Project(table, seat, revision + 1, next.Status, next.Position);
    }

    public async Task<SeatedTable> LeaveAsync(SeatedRules rules, Guid guest, string id, long revision, CancellationToken stop)
    {
        await using var db = await factory.CreateDbContextAsync(stop);
        var (table, seat) = await ForMemberAsync(db, rules.Game, guest, id, stop);
        RequireOpen(table, revision);
        var position = table.Checkpoint is { } checkpoint ? rules.View(CheckedCheckpoint(checkpoint), seat, false) : null;
        var utc = time.GetUtcNow();
        var expires = ExpiresAt(utc, "closed");
        if (await AtRevision(db, table, utc).ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.Revision, t => t.Revision + 1)
                .SetProperty(t => t.Status, "closed")
                .SetProperty(t => t.EndReason, "left")
                .SetProperty(t => t.InviteCode, (string?)null)
                .SetProperty(t => t.ExpiresAtTicks, expires), stop) != 1)
            throw Stale();
        return Project(table, seat, revision + 1, "closed", position, "left");
    }

    private static void RequireOpen(SeatedTableRow table, long revision)
    {
        if (revision != table.Revision) throw Stale();
        if (table.Status == "closed") throw new PortalFault(400, "room_closed");
        if (table.Status == "finished") throw new PortalFault(400, "game_finished");
    }

    private SeatedTurn Compute(bool bots, Func<SeatedTurn> transition)
    {
        if (bots && !_botWorkers.Wait(0)) throw new BusyException();
        try
        {
            var turn = transition();
            CheckedCheckpoint(turn.Checkpoint);
            return turn;
        }
        finally
        {
            if (bots) _botWorkers.Release();
        }
    }

    private static string CheckedCheckpoint(string? checkpoint) =>
        checkpoint is { Length: > 0 and <= MaxCheckpointLength } ? checkpoint : throw new InvalidDataException();

    private static IQueryable<SeatedTableRow> OpenTables(MahjongDb db, long now) =>
        db.SeatedTables.Where(t => t.ExpiresAtTicks > now && (t.Status == "waiting" || t.Status == "playing"));

    private static IQueryable<SeatedTableRow> WithSeats(MahjongDb db) =>
        db.SeatedTables.AsNoTracking().AsSingleQuery().Include(t => t.Members).ThenInclude(m => m.Guest);

    private async Task<(SeatedTableRow Table, int Seat)> ForMemberAsync(MahjongDb db, string game, Guid guest, string id,
        CancellationToken stop)
    {
        var now = time.GetUtcNow().UtcTicks;
        var table = await WithSeats(db).SingleOrDefaultAsync(t => t.Game == game && t.Id == id && t.ExpiresAtTicks > now
            && t.Members.Any(m => m.GuestId == guest), stop) ?? throw Missing();
        return (table, table.Members.Single(m => m.GuestId == guest).Seat);
    }

    private static SeatedMembershipRow NewMember(string id, Guid guest, int seat, DateTimeOffset utc) =>
        new() { TableId = id, GuestId = guest, Seat = seat, JoinedAtTicks = utc.UtcTicks };

    private static long ExpiresAt(DateTimeOffset utc, string status) =>
        (utc + (status switch
        {
            "waiting" => WaitingLifetime, "playing" => PlayingLifetime, _ => EndedLifetime
        })).UtcTicks;

    private static IQueryable<SeatedTableRow> AtRevision(MahjongDb db, SeatedTableRow table, DateTimeOffset utc) =>
        OpenTables(db, utc.UtcTicks).Where(t => t.Id == table.Id && t.Revision == table.Revision);

    private static async Task<bool> CommitAsync(MahjongDb db, SeatedTableRow table, string status, string? checkpoint,
        DateTimeOffset utc, CancellationToken stop)
    {
        var expires = ExpiresAt(utc, status);
        return await AtRevision(db, table, utc).ExecuteUpdateAsync(setters => setters
            .SetProperty(t => t.Revision, t => t.Revision + 1)
            .SetProperty(t => t.Status, status)
            .SetProperty(t => t.Checkpoint, checkpoint)
            .SetProperty(t => t.ExpiresAtTicks, expires), stop) == 1;
    }

    private static SeatedTable Read(SeatedRules rules, SeatedTableRow table, int seat) =>
        Project(table, seat, table.Revision, table.Status, table.Checkpoint is { } checkpoint
            ? rules.View(CheckedCheckpoint(checkpoint), seat, table.Status == "playing") : null, table.EndReason);

    private static SeatedTable Project(SeatedTableRow table, int seat, long revision, string status, object? position,
        string? endReason = null)
    {
        var seats = new SeatedSeat[table.Players];
        for (var index = 0; index < seats.Length; index++)
            seats[index] = table.Bots && index > 0 ? new(index, $"Bot {index}", true, true) : new(index, "", false, false);
        foreach (var member in table.Members)
            seats[member.Seat] = new(member.Seat, member.Guest.DisplayName, false, true);
        return new(table.Id, revision, status, seat, seats, status == "waiting" ? table.InviteCode : null, position, endReason);
    }

    private static PortalFault Missing() => new(404, "room_not_found");
    private static PortalFault Stale() => new(409, "stale_room");
}
