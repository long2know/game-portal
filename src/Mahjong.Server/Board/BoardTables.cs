using System.Security.Cryptography;
using JadeCourt.Games;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Server.Board;

internal sealed record BoardSeat(int Seat, string Name, bool Bot, bool Occupied);
internal sealed record BoardTable(string Id, string Game, string Variant, long Revision, string Status,
    int Seat, string? Invite, BoardSeat[] Seats, GamePosition? Position);

internal sealed class BoardFault(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

internal sealed class BoardTables(IDbContextFactory<MahjongDb> factory, BoardRegistry registry,
    TimeProvider time, ILogger<BoardTables> log)
{
    private const int MaxPlayers = 6;
    private const int MaxTables = 512;
    private const int MaxOwnedTables = 32;
    private const int MaxCheckpointLength = 600_000;
    private static readonly TimeSpan WaitingLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan GameLifetime = TimeSpan.FromDays(7);
    private readonly SemaphoreSlim _botWorkers = new(4);

    public async Task<GuestRow?> GuestAsync(Guid id, CancellationToken stop)
    {
        await using var db = await factory.CreateDbContextAsync(stop);
        return await db.Guests.AsNoTracking().SingleOrDefaultAsync(g => g.Id == id, stop);
    }

    public async Task<BoardTable> CreateAsync(GuestRow guest, string gameId, string variant, int players, bool bots,
        CancellationToken stop)
    {
        var game = FindGame(gameId);
        if (players is < 2 or > MaxPlayers || !game.Definition.PlayerCounts.Contains(players)
            || !game.Definition.Variants.Any(v => v.Id == variant))
            throw new BoardFault(400, "invalid_request");

        string start;
        try { start = game.Start(variant, players); }
        catch (ArgumentException) { throw new BoardFault(400, "invalid_request"); }

        var initial = bots ? FinishTurn(game, start, players, bots, stop) : null;

        await using var db = await factory.CreateDbContextAsync(stop);
        await using var transaction = await db.Database.BeginTransactionAsync(stop);
        var utc = time.GetUtcNow();
        if (await db.BoardTables.CountAsync(t => t.ExpiresAtTicks > utc.UtcTicks, stop) >= MaxTables
            || await db.BoardMemberships.CountAsync(m => m.GuestId == guest.Id && m.Seat == 0
                && m.Table.ExpiresAtTicks > utc.UtcTicks, stop) >= MaxOwnedTables)
            throw new BoardFault(429, "table_limit");

        var table = new BoardTableRow
        {
            Id = Guid.NewGuid().ToString("N"), Game = gameId, Variant = variant, Players = players, Bots = bots,
            InviteCode = bots ? null : Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            Checkpoint = initial?.Checkpoint,
            ExpiresAtTicks = ExpiresAt(utc, bots)
        };
        var member = NewMember(table, guest.Id, 0, utc);
        table.Members.Add(member);
        db.BoardTables.Add(table);
        await db.SaveChangesAsync(stop);
        await transaction.CommitAsync(stop);
        member.Guest = guest;
        return Project(table, 0, initial?.Position, table.Revision);
    }

    public async Task<BoardTable> JoinAsync(GuestRow guest, string invite, CancellationToken stop)
    {
        // Each unsuccessful compare-and-swap can account for at most one added human seat.
        for (var attempt = 0; attempt < MaxPlayers; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(stop);
            var now = time.GetUtcNow().UtcTicks;
            var table = await WithSeats(db).SingleOrDefaultAsync(t => t.InviteCode == invite && t.ExpiresAtTicks > now, stop)
                ?? throw Missing();
            if (table.Members.SingleOrDefault(m => m.GuestId == guest.Id) is { } existing)
                return Read(table, existing.Seat);
            if (table.Checkpoint is not null || table.Members.Count >= table.Players)
                throw Missing();

            var seat = table.Members.Count;
            string? checkpoint = null;
            GamePosition? position = null;
            if (seat + 1 == table.Players)
            {
                var game = FindGame(table.Game);
                checkpoint = game.Start(table.Variant, table.Players);
                position = View(game, checkpoint, table.Players);
            }

            await using var transaction = await db.Database.BeginTransactionAsync(stop);
            var utc = time.GetUtcNow();
            if (!await CommitAsync(db, table, checkpoint, utc, stop)) continue;
            var member = NewMember(table, guest.Id, seat, utc);
            db.BoardMemberships.Add(member);
            await db.SaveChangesAsync(stop);
            await transaction.CommitAsync(stop);
            member.Guest = guest;
            table.Members.Add(member);
            return Project(table, seat, position, table.Revision + 1);
        }
        throw new BusyException();
    }

    public async Task<BoardTable> GetAsync(Guid guest, string id, CancellationToken stop)
    {
        await using var db = await factory.CreateDbContextAsync(stop);
        var table = await ForMemberAsync(db, guest, id, stop);
        return Read(table, table.Members.Single(m => m.GuestId == guest).Seat);
    }

    public async Task<string?> ResumeAsync(Guid guest, string game, CancellationToken stop)
    {
        FindGame(game);
        await using var db = await factory.CreateDbContextAsync(stop);
        var now = time.GetUtcNow().UtcTicks;
        return await db.BoardMemberships.AsNoTracking()
            .Where(m => m.GuestId == guest && m.Table.Game == game && m.Table.ExpiresAtTicks > now)
            .OrderByDescending(m => m.JoinedAtTicks).ThenByDescending(m => m.TableId)
            .Select(m => m.TableId).FirstOrDefaultAsync(stop);
    }

    public async Task<BoardTable> MoveAsync(Guid guest, string id, long revision, string moveId, CancellationToken stop)
    {
        await using var db = await factory.CreateDbContextAsync(stop);
        var table = await ForMemberAsync(db, guest, id, stop);
        if (revision != table.Revision) throw Stale();
        if (table.Checkpoint is not { } checkpoint) throw new BoardFault(400, "room_not_started");
        var seat = table.Members.Single(m => m.GuestId == guest).Seat;
        var game = FindGame(table.Game);
        var position = View(game, checkpoint, table.Players);
        if (position.Status == "finished") throw new BoardFault(400, "game_finished");
        if (position.Turn != seat) throw new BoardFault(400, "not_your_turn");
        if (!position.Moves.Any(move => move.Id == moveId)) throw new BoardFault(400, "illegal_move");
        try { checkpoint = game.Apply(checkpoint, moveId); }
        catch (ArgumentException) { throw new BoardFault(400, "illegal_move"); }

        var next = FinishTurn(game, checkpoint, table.Players, table.Bots, stop);
        if (!await CommitAsync(db, table, next.Checkpoint, time.GetUtcNow(), stop)) throw Stale();
        return Project(table, seat, next.Position, table.Revision + 1);
    }

    private IBoardGame FindGame(string id) => registry.Find(id) ?? throw new BoardFault(400, "invalid_request");

    private static BoardFault Missing() => new(404, "room_not_found");
    private static BoardFault Stale() => new(409, "stale_room");

    private static IQueryable<BoardTableRow> WithSeats(MahjongDb db) =>
        db.BoardTables.AsNoTracking().AsSingleQuery().Include(t => t.Members).ThenInclude(m => m.Guest);

    private async Task<BoardTableRow> ForMemberAsync(MahjongDb db, Guid guest, string id, CancellationToken stop)
    {
        var now = time.GetUtcNow().UtcTicks;
        return await WithSeats(db).SingleOrDefaultAsync(t => t.Id == id && t.ExpiresAtTicks > now
            && t.Members.Any(m => m.GuestId == guest), stop) ?? throw Missing();
    }

    private static BoardMembershipRow NewMember(BoardTableRow table, Guid guest, int seat, DateTimeOffset utc) =>
        new() { TableId = table.Id, GuestId = guest, Seat = seat, JoinedAtTicks = utc.UtcTicks };

    private static long ExpiresAt(DateTimeOffset utc, bool started) =>
        (utc + (started ? GameLifetime : WaitingLifetime)).UtcTicks;

    private static async Task<bool> CommitAsync(MahjongDb db, BoardTableRow table, string? checkpoint, DateTimeOffset utc,
        CancellationToken stop)
    {
        var expires = ExpiresAt(utc, checkpoint is not null);
        return await db.BoardTables
            .Where(t => t.Id == table.Id && t.Revision == table.Revision && t.ExpiresAtTicks > utc.UtcTicks)
            .ExecuteUpdateAsync(update => update
                .SetProperty(t => t.Revision, t => t.Revision + 1)
                .SetProperty(t => t.Checkpoint, checkpoint)
                .SetProperty(t => t.ExpiresAtTicks, expires), stop) == 1;
    }

    private BoardTable Read(BoardTableRow table, int seat) => Project(table, seat,
        table.Checkpoint is { } checkpoint ? View(FindGame(table.Game), checkpoint, table.Players) : null, table.Revision);

    private static BoardTable Project(BoardTableRow table, int seat, GamePosition? position, long revision)
    {
        var seats = new BoardSeat[table.Players];
        for (var index = 0; index < seats.Length; index++)
            seats[index] = table.Bots && index > 0
                ? new(index, $"Bot {index}", true, true)
                : new(index, "", false, false);
        foreach (var member in table.Members)
            seats[member.Seat] = new(member.Seat, member.Guest.DisplayName, false, true);
        return new(table.Id, table.Game, table.Variant, revision, position?.Status ?? "waiting", seat,
            position is null ? table.InviteCode : null, seats, position);
    }

    private sealed record TurnResult(string Checkpoint, GamePosition Position);

    private TurnResult FinishTurn(IBoardGame game, string checkpoint, int players, bool bots, CancellationToken stop)
    {
        var position = View(game, checkpoint, players);
        if (!bots || position.Turn is not > 0) return new(checkpoint, position);
        if (!_botWorkers.Wait(0)) throw new BusyException();
        try
        {
            for (var ply = 1; ply < players && position.Turn is > 0; ply++)
            {
                stop.ThrowIfCancellationRequested();
                var move = ChooseBotMove(game, checkpoint, position);
                try { checkpoint = game.Apply(checkpoint, move); }
                catch (ArgumentException) { throw EngineFault(game, "the bot's legal move was rejected"); }
                position = View(game, checkpoint, players);
            }
            if (position.Turn is > 0) throw EngineFault(game, "the turn did not return to the human seat");
            return new(checkpoint, position);
        }
        finally
        {
            _botWorkers.Release();
        }
    }

    private string ChooseBotMove(IBoardGame game, string checkpoint, GamePosition position)
    {
        string? selected;
        try { selected = game.SelectBotMove(checkpoint); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { selected = null; }
        if (position.Moves.Any(move => move.Id == selected)) return selected!;
        log.LogWarning("Board game {Game} bot did not select a legal move; using the first legal move.", game.Definition.Id);
        return position.Moves[0].Id;
    }

    private GamePosition View(IBoardGame game, string checkpoint, int players)
    {
        if (checkpoint.Length > MaxCheckpointLength) throw EngineFault(game, "checkpoint exceeded its limit");
        var position = game.View(checkpoint);
        if (position is { Status: "finished", Turn: null, Moves.Length: 0 }
            || (position is { Status: "playing", Turn: { } turn, Moves.Length: > 0 } && turn >= 0 && turn < players))
            return position;
        throw EngineFault(game, "invalid position");
    }

    private BoardFault EngineFault(IBoardGame game, string reason)
    {
        log.LogError("Board game {Game} could not complete the operation: {Reason}.", game.Definition.Id, reason);
        return new(500, "engine_unavailable");
    }
}
