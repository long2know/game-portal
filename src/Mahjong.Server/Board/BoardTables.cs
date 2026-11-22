using System.Security.Cryptography;
using System.Text.Json.Serialization;
using JadeCourt.Chess;
using JadeCourt.Games;
using Mahjong.Server.Social;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Server.Board;

internal sealed record BoardSeat(int Seat, string Name, bool Bot, bool Occupied);
internal sealed record BoardMotionStep(GameMove Move, GameCell[] Changes);
internal sealed record BoardTurnMotion(long FromRevision, BoardMotionStep[] Steps);
internal sealed record BoardResult(string Cause, int LoserSeat, int WinnerSeat);
internal sealed record BoardTable(string Id, string Game, string Variant, long Revision, string Status,
    int Seat, string? Invite, BoardSeat[] Seats, GamePosition? Position,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BoardTurnMotion? TurnMotion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BotDifficulty,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EndReason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EndedBySeat,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BoardResult? Result);

internal sealed class BoardTables(IDbContextFactory<MahjongDb> factory, BoardRegistry registry,
    TimeProvider time, ILogger<BoardTables> log)
{
    private const int MaxPlayers = 6;
    private const int MaxTables = 512;
    private const int MaxOwnedTables = 32;
    private const int MaxCheckpointLength = 600_000;
    private const string StandardDifficulty = "standard";
    private static readonly TimeSpan WaitingLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan GameLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan EndedLifetime = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim _botWorkers = new(4);

    public async Task<BoardTable> CreateAsync(GuestRow guest, string gameId, string variant, int players, bool bots,
        string? botDifficulty, CancellationToken stop)
    {
        var game = FindGame(gameId);
        if (players is < 2 or > MaxPlayers || !game.Definition.PlayerCounts.Contains(players)
            || !game.Definition.Variants.Any(v => v.Id == variant)
            || (botDifficulty is not null && (gameId != "chess" || !bots || botDifficulty is not ("easy" or StandardDifficulty or "hard"))))
            throw new PortalFault(400, "invalid_request");
        var difficulty = gameId == "chess" && bots ? botDifficulty ?? StandardDifficulty : null;

        string start;
        try { start = game.Start(variant, players); }
        catch (ArgumentException) { throw new PortalFault(400, "invalid_request"); }

        var initial = bots ? FinishTurn(game, start, View(game, start, players), players, difficulty, null, stop) : null;

        await using var db = await factory.CreateDbContextAsync(stop);
        await using var transaction = await db.Database.BeginTransactionAsync(stop);
        var utc = time.GetUtcNow();
        if (await db.BoardTables.CountAsync(t => t.EndReason == null && t.ExpiresAtTicks > utc.UtcTicks, stop) >= MaxTables
            || await db.BoardMemberships.CountAsync(m => m.GuestId == guest.Id && m.Seat == 0
                && m.Table.EndReason == null && m.Table.ExpiresAtTicks > utc.UtcTicks, stop) >= MaxOwnedTables)
            throw new PortalFault(429, "table_limit");

        var table = new BoardTableRow
        {
            Id = Guid.NewGuid().ToString("N"), Game = gameId, Variant = variant, Players = players, Bots = bots, BotDifficulty = difficulty,
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

    public async Task<BoardTable> JoinAsync(GuestRow guest, string invite, CancellationToken stop) =>
        (await JoinCoreAsync(guest, invite, null, stop))!;

    public async Task JoinSocialAsync(GuestRow guest, SocialAcceptance admission, CancellationToken stop) =>
        await JoinCoreAsync(guest, null, admission, stop);

    private async Task<BoardTable?> JoinCoreAsync(GuestRow guest, string? invite, SocialAcceptance? admission, CancellationToken stop)
    {
        // Each unsuccessful compare-and-swap can account for at most one added human seat.
        for (var attempt = 0; attempt < MaxPlayers; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(stop);
            var now = time.GetUtcNow().UtcTicks;
            var table = await (admission is { } social ? SocialTargetQuery(db, social.Target, now)
                : WithSeats(db).Where(t => t.InviteCode == invite && t.ExpiresAtTicks > now)).SingleOrDefaultAsync(stop)
                ?? throw (admission is null ? Missing() : SocialLedger.Missing());
            var existing = table.Members.SingleOrDefault(m => m.GuestId == guest.Id);
            var open = admission is null ? table.Checkpoint is null && table.Members.Count < table.Players
                : CanInvite(table, admission.SenderId);
            if (admission is null)
            {
                if (existing is not null) return Read(table, existing.Seat);
                if (!open) throw Missing();
            }

            var seat = table.Members.Count;
            string? checkpoint = null;
            GamePosition? position = null;
            if (open && existing is null && admission?.ReceiptHint != true && seat + 1 == table.Players)
            {
                var game = FindGame(table.Game);
                checkpoint = game.Start(table.Variant, table.Players);
                position = View(game, checkpoint, table.Players);
            }

            await using var transaction = await db.Database.BeginTransactionAsync(stop);
            var utc = time.GetUtcNow();
            SocialInviteRow? receipt = null;
            if (admission is not null)
            {
                receipt = await admission.AuthorizeAsync(db, utc, stop);
                // The same revision must still describe both the roster and the candidate engine start.
                if (!await db.BoardTables.AnyAsync(t => t.Id == table.Id && t.Revision == table.Revision
                        && t.ExpiresAtTicks > utc.UtcTicks, stop)) continue;
                var entitled = existing is not null && table.EndReason != "left";
                if (receipt.Accepted)
                {
                    if (entitled) return null;
                    throw SocialLedger.Missing();
                }
                if (entitled) throw SocialLedger.AlreadySeated();
                if (!open) throw SocialLedger.Missing();
            }
            if (!await CommitAsync(db, table, checkpoint, utc, stop)) continue;
            using var handoff = receipt is null ? null : await admission!.StageAsync(db, receipt, stop);
            var member = NewMember(table, guest.Id, seat, utc);
            db.BoardMemberships.Add(member);
            await db.SaveChangesAsync(stop);
            await transaction.CommitAsync(stop);
            if (handoff is not null)
            {
                handoff.Commit();
                return null;
            }
            member.Guest = guest;
            table.Members.Add(member);
            return Project(table, seat, position, table.Revision + 1);
        }
        throw new BusyException();
    }

    public static async Task<bool> CanInviteAsync(MahjongDb db, SocialTarget target, Guid sender, DateTimeOffset utc,
        CancellationToken stop) => await SocialTargetQuery(db, target, utc.UtcTicks).SingleOrDefaultAsync(stop) is { } table
        && CanInvite(table, sender);

    private static bool CanInvite(BoardTableRow table, Guid sender) => !table.Bots && table.EndReason is null
        && table.Checkpoint is null && table.Members.Count < table.Players && table.Members.Any(m => m.GuestId == sender);

    private static IQueryable<BoardTableRow> SocialTargetQuery(MahjongDb db, SocialTarget target, long now) =>
        WithSeats(db).Where(t => t.Game == target.Game && t.Id == target.TableId && t.ExpiresAtTicks > now);

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
            .Where(m => m.GuestId == guest && m.Table.Game == game && m.Table.EndReason == null && m.Table.ExpiresAtTicks > now)
            .OrderByDescending(m => m.JoinedAtTicks).ThenByDescending(m => m.TableId)
            .Select(m => m.TableId).FirstOrDefaultAsync(stop);
    }

    public async Task<BoardTable> MoveAsync(Guid guest, string id, long revision, string moveId, CancellationToken stop)
    {
        await using var db = await factory.CreateDbContextAsync(stop);
        var table = await ForMemberAsync(db, guest, id, stop);
        if (revision != table.Revision) throw Stale();
        RequireOpen(table);
        if (table.Checkpoint is not { } checkpoint) throw new PortalFault(400, "room_not_started");
        var seat = table.Members.Single(m => m.GuestId == guest).Seat;
        var game = FindGame(table.Game);
        var position = View(game, checkpoint, table.Players);
        if (position.Status == "finished") throw new PortalFault(400, "game_finished");
        if (position.Turn != seat) throw new PortalFault(400, "not_your_turn");
        var move = position.Moves.FirstOrDefault(move => move.Id == moveId) ?? throw new PortalFault(400, "illegal_move");
        try { checkpoint = game.Apply(checkpoint, move.Id); }
        catch (ArgumentException) { throw new PortalFault(400, "illegal_move"); }

        var after = View(game, checkpoint, table.Players);
        var steps = new List<BoardMotionStep>(table.Bots ? table.Players : 1) { MotionStep(move, position, after) };
        var next = table.Bots
            ? FinishTurn(game, checkpoint, after, table.Players, Difficulty(table), steps, stop)
            : new TurnResult(checkpoint, after);
        if (!await CommitAsync(db, table, next.Checkpoint, time.GetUtcNow(), stop)) throw Stale();
        return Project(table, seat, next.Position, table.Revision + 1, new(table.Revision, [.. steps]));
    }

    public async Task<BoardTable> EndAsync(Guid guest, string id, long revision, bool resign, CancellationToken stop)
    {
        await using var db = await factory.CreateDbContextAsync(stop);
        var table = await ForMemberAsync(db, guest, id, stop);
        if (revision != table.Revision) throw Stale();
        RequireOpen(table);
        var position = table.Checkpoint is { } checkpoint ? View(FindGame(table.Game), checkpoint, table.Players) : null;
        if (position?.Status == "finished") throw new PortalFault(400, "game_finished");
        if (resign)
        {
            if (table.Game != "chess") throw new PortalFault(400, "invalid_request");
            if (position is null) throw new PortalFault(400, "room_not_started");
        }
        else if (table.Game == "chess" && position is not null)
            throw new PortalFault(400, "resign_required");

        var reason = resign ? "resignation" : "left";
        var seat = table.Members.Single(m => m.GuestId == guest).Seat;
        var utc = time.GetUtcNow();
        var expires = (utc + EndedLifetime).UtcTicks;
        if (await AtRevision(db, table, utc).ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.Revision, t => t.Revision + 1)
                .SetProperty(t => t.InviteCode, (string?)null)
                .SetProperty(t => t.EndReason, reason)
                .SetProperty(t => t.EndedBySeat, seat)
                .SetProperty(t => t.ExpiresAtTicks, expires), stop) != 1)
            throw Stale();
        table.EndReason = reason;
        table.EndedBySeat = seat;
        return Project(table, seat, position, table.Revision + 1);
    }

    private static void RequireOpen(BoardTableRow table)
    {
        if (table.EndReason is "left") throw new PortalFault(400, "room_closed");
        if (table.EndReason is "resignation") throw new PortalFault(400, "game_finished");
    }

    private IBoardGame FindGame(string id) => registry.Find(id) ?? throw new PortalFault(400, "invalid_request");

    private static PortalFault Missing() => new(404, "room_not_found");
    private static PortalFault Stale() => new(409, "stale_room");

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

    private static IQueryable<BoardTableRow> AtRevision(MahjongDb db, BoardTableRow table, DateTimeOffset utc) =>
        db.BoardTables.Where(t => t.Id == table.Id && t.Revision == table.Revision
            && t.EndReason == null && t.ExpiresAtTicks > utc.UtcTicks);

    private static async Task<bool> CommitAsync(MahjongDb db, BoardTableRow table, string? checkpoint, DateTimeOffset utc,
        CancellationToken stop)
    {
        var expires = ExpiresAt(utc, checkpoint is not null);
        return await AtRevision(db, table, utc)
            .ExecuteUpdateAsync(update => update
                .SetProperty(t => t.Revision, t => t.Revision + 1)
                .SetProperty(t => t.Checkpoint, checkpoint)
                .SetProperty(t => t.ExpiresAtTicks, expires), stop) == 1;
    }

    private BoardTable Read(BoardTableRow table, int seat) => Project(table, seat,
        table.Checkpoint is { } checkpoint ? View(FindGame(table.Game), checkpoint, table.Players) : null, table.Revision);

    private static BoardTable Project(BoardTableRow table, int seat, GamePosition? position, long revision,
        BoardTurnMotion? motion = null)
    {
        var seats = new BoardSeat[table.Players];
        for (var index = 0; index < seats.Length; index++)
            seats[index] = table.Bots && index > 0
                ? new(index, $"Bot {index}", true, true)
                : new(index, "", false, false);
        foreach (var member in table.Members)
            seats[member.Seat] = new(member.Seat, member.Guest.DisplayName, false, true);
        var closed = table.EndReason == "left";
        var result = table.EndReason == "resignation" && table.EndedBySeat is { } loser
            ? new BoardResult("resignation", loser, 1 - loser) : null;
        if (position is not null && table.EndReason is not null)
            position = position with
            {
                Status = "finished", Turn = null, Moves = [],
                Winners = result is null ? [] : [result.WinnerSeat],
                Detail = result is null ? "Table closed." : $"{(result.WinnerSeat == 0 ? "White" : "Black")} wins by resignation."
            };
        return new(table.Id, table.Game, table.Variant, revision, closed ? "closed" : position?.Status ?? "waiting", seat,
            position is null && !closed ? table.InviteCode : null, seats, position, motion, Difficulty(table), closed ? table.EndReason : null,
            closed ? table.EndedBySeat : null, result);
    }

    private static string? Difficulty(BoardTableRow table) =>
        table.Game == "chess" && table.Bots ? table.BotDifficulty ?? StandardDifficulty : null;

    private sealed record TurnResult(string Checkpoint, GamePosition Position);

    private TurnResult FinishTurn(IBoardGame game, string checkpoint, GamePosition position, int players,
        string? difficulty, List<BoardMotionStep>? steps, CancellationToken stop)
    {
        if (position.Turn is not > 0) return new(checkpoint, position);
        if (!_botWorkers.Wait(0)) throw new BusyException();
        try
        {
            for (var ply = 1; ply < players && position.Turn is > 0; ply++)
            {
                stop.ThrowIfCancellationRequested();
                var move = ChooseBotMove(game, checkpoint, position, difficulty);
                try { checkpoint = game.Apply(checkpoint, move.Id); }
                catch (ArgumentException) { throw EngineFault(game, "the bot's legal move was rejected"); }
                var after = View(game, checkpoint, players);
                steps?.Add(MotionStep(move, position, after));
                position = after;
            }
            if (position.Turn is > 0) throw EngineFault(game, "the turn did not return to the human seat");
            return new(checkpoint, position);
        }
        finally
        {
            _botWorkers.Release();
        }
    }

    private GameMove ChooseBotMove(IBoardGame game, string checkpoint, GamePosition position, string? difficulty)
    {
        string? selected;
        try { selected = game is ChessGame chess ? chess.SelectBotMove(checkpoint, difficulty ?? StandardDifficulty)
                : game.SelectBotMove(checkpoint); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { selected = null; }
        if (position.Moves.FirstOrDefault(move => move.Id == selected) is { } legal) return legal;
        log.LogWarning("Board game {Game} bot did not select a legal move; using the first legal move.", game.Definition.Id);
        return position.Moves[0];
    }

    private static BoardMotionStep MotionStep(GameMove move, GamePosition before, GamePosition after)
    {
        var prior = before.Cells.ToDictionary(cell => cell.Id, StringComparer.Ordinal);
        var changes = after.Cells.Where(cell => !prior.TryGetValue(cell.Id, out var old)
            || cell.Owner != old.Owner || cell.Piece != old.Piece).ToArray();
        return new(move, changes);
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

    private PortalFault EngineFault(IBoardGame game, string reason)
    {
        log.LogError("Board game {Game} could not complete the operation: {Reason}.", game.Definition.Id, reason);
        return new(500, "engine_unavailable");
    }
}
