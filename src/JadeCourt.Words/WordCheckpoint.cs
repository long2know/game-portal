using System.Text.Json;
using System.Text.Json.Serialization;

namespace JadeCourt.Words;

internal sealed record WordCheckpoint(
    int Version, int Turn, int[][] Racks, int[] Bag, WordCheckpoint.PlacedTile[] Board, int[] Scores, int DryTurns,
    WordCheckpoint.TurnNote? Last)
{
    internal const int CurrentVersion = 1;
    private const int MaxLength = 16_384;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 8
    };

    internal readonly record struct PlacedTile(int Row, int Column, int TileId, int Seat);
    internal sealed record TurnNote(string Kind, int Count, string[]? Words, int Points);

    internal bool WentOut => Bag.Length == 0 && (Racks[0].Length == 0 || Racks[1].Length == 0);
    internal bool Finished => WentOut || DryTurns == WordTiles.DryTurnLimit;

    internal string Save() => JsonSerializer.Serialize(this, Options);

    internal static WordCheckpoint Load(string checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.Length > MaxLength)
            throw InvalidCheckpoint();

        WordCheckpoint? state;
        try
        {
            state = JsonSerializer.Deserialize<WordCheckpoint>(checkpoint, Options);
        }
        catch (JsonException error)
        {
            throw InvalidCheckpoint(error);
        }

        if (state is null || state.Version != CurrentVersion || state.Turn is < 0 or > 1 ||
            state.DryTurns is < 0 or > WordTiles.DryTurnLimit ||
            state.Scores is not { Length: 2 } || state.Scores.Any(score => score < 0 ||
                score > WordTiles.Count * (2 * WordTiles.Size * WordTiles.MaxPoints + WordTiles.LinkBonus)) ||
            state.Racks is not { Length: 2 } || state.Racks.Any(rack => rack is null || rack.Length > WordTiles.RackSize) ||
            state.Bag is null || state.Board is null ||
            state.Bag.Length > 0 && state.Racks.Any(rack => rack.Length != WordTiles.RackSize) ||
            state.Racks[0].Length + state.Racks[1].Length + state.Bag.Length + state.Board.Length != WordTiles.Count ||
            !state.HasValidNote())
            throw InvalidCheckpoint();

        var seen = new bool[WordTiles.Count + 1];
        var occupied = new bool[WordTiles.Size * WordTiles.Size];
        foreach (var tile in state.Racks.SelectMany(rack => rack).Concat(state.Bag))
            if (!MarkTile(tile))
                throw InvalidCheckpoint();
        foreach (var cell in state.Board)
        {
            if (!WordBoard.InBounds(cell.Row, cell.Column) || cell.Seat is < 0 or > 1 || !MarkTile(cell.TileId))
                throw InvalidCheckpoint();
            var index = WordBoard.Index(cell.Row, cell.Column);
            if (occupied[index])
                throw InvalidCheckpoint();
            occupied[index] = true;
        }
        if (state.Board.Length == 1 || state.Board.Length > 0 && !occupied[WordBoard.Center] ||
            state.Board.Length == 0 && state.Scores.Any(score => score != 0) ||
            state.WentOut && (state.DryTurns != 0 || state.Racks[state.Turn].Length == 0))
            throw InvalidCheckpoint();
        return state;

        bool MarkTile(int id)
        {
            if (!WordTiles.IsTileId(id) || seen[id])
                return false;
            seen[id] = true;
            return true;
        }
    }

    private bool HasValidNote() => Last switch
    {
        null => DryTurns == 0 && Board.Length == 0 && Turn == 0,
        { Kind: "pass", Count: 0, Words: null, Points: 0 } => DryTurns > 0,
        { Kind: "exchange", Count: >= 1 and <= WordTiles.ExchangeLimit, Words: null, Points: 0 } => DryTurns > 0,
        { Kind: "place", Count: >= 1 and <= WordTiles.RackSize, Words: { Length: >= 1 and <= WordTiles.RackSize + 1 },
            Points: > 0 } move => DryTurns == 0 && Board.Length >= move.Count && Scores[1 - Turn] >= move.Points &&
                                   move.Words.All(word => word is not null && WordLexicon.IsEntry(word)),
        _ => false
    };

    private static InvalidDataException InvalidCheckpoint(Exception? inner = null) =>
        new("Invalid Word Tiles checkpoint.", inner);
}
