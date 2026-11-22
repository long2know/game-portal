using JadeCourt.Solo;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Server.Solo;

internal sealed class SoloSaveRow
{
    public Guid GuestId { get; init; }
    public required string Game { get; init; }
    public required string Variant { get; set; }
    public required string Checkpoint { get; set; }
    public long Revision { get; set; }
}

internal sealed record SoloTable(string Game, string Variant, long Revision, object Position);

internal sealed class SoloTables(IDbContextFactory<MahjongDb> factory)
{
    private const int MaxCheckpointLength = 16_384;
    private sealed record SoloRules(Func<string, string> Start, Func<string, SoloAction, string> Apply, Func<string, object> View);
    private static readonly KlondikeGame Klondike = new();
    private static readonly SudokuGame Sudoku = new();
    private static readonly SoloRules SolitaireRules = new(Klondike.Start, Klondike.Apply, Klondike.View);
    private static readonly SoloRules SudokuRules = new(Sudoku.Start, Sudoku.Apply, Sudoku.View);
    private readonly SemaphoreSlim _dealWorkers = new(4);

    public async Task<SoloTable?> GetAsync(Guid guest, string game, CancellationToken stop)
    {
        var rules = Find(game);
        await using var db = await factory.CreateDbContextAsync(stop);
        var save = await Saves(db, guest, game).AsNoTracking().SingleOrDefaultAsync(stop);
        return save is null ? null : new(game, save.Variant, save.Revision, rules.View(CheckedCheckpoint(save.Checkpoint)));
    }

    public async Task<SoloTable> DealAsync(Guid guest, string game, string variant, CancellationToken stop)
    {
        var rules = Find(game);
        string checkpoint;
        object position;
        if (!_dealWorkers.Wait(0)) throw new BusyException();
        try
        {
            try { checkpoint = rules.Start(variant); }
            catch (ArgumentException) { throw new PortalFault(400, "invalid_request"); }
            position = rules.View(CheckedCheckpoint(checkpoint));
        }
        finally
        {
            _dealWorkers.Release();
        }

        await using var db = await factory.CreateDbContextAsync(stop);
        await using var transaction = await db.Database.BeginTransactionAsync(stop);
        var save = await Saves(db, guest, game).SingleOrDefaultAsync(stop);
        if (save is null)
        {
            save = new SoloSaveRow { GuestId = guest, Game = game, Variant = variant, Checkpoint = checkpoint };
            db.SoloSaves.Add(save);
        }
        else
        {
            save.Variant = variant;
            save.Checkpoint = checkpoint;
            save.Revision++;
        }
        await db.SaveChangesAsync(stop);
        await transaction.CommitAsync(stop);
        return new(game, variant, save.Revision, position);
    }

    public async Task<SoloTable> MoveAsync(Guid guest, string game, long revision, SoloAction action, CancellationToken stop)
    {
        var rules = Find(game);
        await using var db = await factory.CreateDbContextAsync(stop);
        var saves = Saves(db, guest, game);
        var save = await saves.AsNoTracking().SingleOrDefaultAsync(stop) ?? throw new PortalFault(404, "room_not_found");
        if (revision != save.Revision) throw Stale();
        var before = CheckedCheckpoint(save.Checkpoint);
        string after;
        try { after = rules.Apply(before, action); }
        catch (InvalidOperationException)
        {
            throw rules.View(before) is KlondikePosition { Finished: true } or SudokuPosition { Finished: true }
                ? new PortalFault(400, "game_finished") : new PortalFault(400, "illegal_action");
        }
        var position = rules.View(CheckedCheckpoint(after));
        if (await saves.Where(s => s.Revision == revision).ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.Revision, s => s.Revision + 1)
                .SetProperty(s => s.Checkpoint, after), stop) != 1)
            throw Stale();
        return new(game, save.Variant, revision + 1, position);
    }

    private static IQueryable<SoloSaveRow> Saves(MahjongDb db, Guid guest, string game) =>
        db.SoloSaves.Where(s => s.GuestId == guest && s.Game == game);

    private static SoloRules Find(string game) => game switch
    {
        "solitaire" => SolitaireRules, "sudoku" => SudokuRules, _ => throw new PortalFault(400, "invalid_request")
    };

    private static string CheckedCheckpoint(string checkpoint) =>
        checkpoint is { Length: > 0 and <= MaxCheckpointLength } ? checkpoint : throw new InvalidDataException();

    private static PortalFault Stale() => new(409, "stale_room");
}
