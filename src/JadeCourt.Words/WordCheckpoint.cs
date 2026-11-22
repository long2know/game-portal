using System.Text.Json;
using System.Text.Json.Serialization;

namespace JadeCourt.Words;

internal sealed record WordCheckpoint(
    int Version, int Turn, int[][] Racks, int[] Bag, WordCheckpoint.PlacedTile[] Board, int[] Scores, int DryTurns,
    WordCheckpoint.TurnNote? Last)
{
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

    internal sealed record PlacedTile(int Row, int Column, int TileId, int Seat,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Letter = null);
    internal sealed record TurnNote(string Kind, int Count, string[]? Words, int Points);

    internal WordTiles Rules => WordTiles.ForVersion(Version)!;
    internal bool WentOut => Bag.Length == 0 && (Racks[0].Length == 0 || Racks[1].Length == 0);
    internal bool Finished => WentOut || DryTurns == Rules.DryTurnLimit;

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

        if (state is null || WordTiles.ForVersion(state.Version) is not { } rules ||
            state.Turn is < 0 or > 1 || state.DryTurns < 0 || state.DryTurns > rules.DryTurnLimit ||
            state.Bag is null || state.Board is null ||
            state.Scores is not { Length: 2 } || state.Scores.Any(score => score < 0 ||
                score > (rules.IsClassic ? state.Board.Length : rules.Count) * rules.MaxTurnScore) ||
            state.Racks is not { Length: 2 } || state.Racks.Any(rack => rack is null || rack.Length > rules.RackSize) ||
            state.Bag.Length > 0 && state.Racks.Any(rack => rack.Length != rules.RackSize) ||
            state.Racks[0].Length + state.Racks[1].Length + state.Bag.Length + state.Board.Length != rules.Count ||
            !state.HasValidNote(rules))
            throw InvalidCheckpoint();

        var seen = new bool[rules.Count + 1];
        var occupied = new bool[rules.Size * rules.Size];
        foreach (var tile in state.Racks.SelectMany(rack => rack).Concat(state.Bag))
            if (!MarkTile(tile))
                throw InvalidCheckpoint();
        foreach (var cell in state.Board)
        {
            if (cell is null || !rules.InBounds(cell.Row, cell.Column) || cell.Seat is < 0 or > 1 ||
                !MarkTile(cell.TileId) || !rules.CanAssign(cell.TileId, cell.Letter))
                throw InvalidCheckpoint();
            var index = cell.Row * rules.Size + cell.Column;
            if (occupied[index])
                throw InvalidCheckpoint();
            occupied[index] = true;
        }
        if (state.Board.Length == 1 || state.Board.Length > 0 && !occupied[occupied.Length / 2] ||
            state.Board.Length == 0 && state.Scores.Any(score => score != 0) ||
            rules.IsClassic && state.Board.Length > 0 && !ConnectedToCenter(occupied, rules.Size, state.Board.Length) ||
            state.WentOut && (state.Racks[state.Turn].Length == 0 ||
                (rules.IsClassic ? state.Last?.Kind != "place" : state.DryTurns != 0)))
            throw InvalidCheckpoint();
        return state;

        bool MarkTile(int id)
        {
            if (!rules.IsTileId(id) || seen[id])
                return false;
            seen[id] = true;
            return true;
        }
    }

    private static bool ConnectedToCenter(bool[] occupied, int size, int count)
    {
        var queue = new int[count];
        var end = 0;
        Visit(occupied.Length / 2);
        for (var start = 0; start < end; start++)
        {
            var index = queue[start];
            if (index >= size) Visit(index - size);
            if (index < occupied.Length - size) Visit(index + size);
            if (index % size > 0) Visit(index - 1);
            if (index % size < size - 1) Visit(index + 1);
        }
        return end == count;

        void Visit(int index)
        {
            if (!occupied[index])
                return;
            occupied[index] = false;
            queue[end++] = index;
        }
    }

    private bool HasValidNote(WordTiles rules) => Last switch
    {
        null => DryTurns == 0 && Board.Length == 0 && Turn == 0,
        { Kind: "pass", Count: 0, Words: null, Points: 0 } => DryTurns > 0,
        { Kind: "exchange", Count: >= 1, Words: null, Points: 0 } note =>
            DryTurns > 0 && note.Count <= rules.MaxExchange && (!rules.IsClassic || Bag.Length >= rules.RackSize),
        { Kind: "place", Count: >= 1, Words: { Length: >= 1 } words, Points: >= 0 } move =>
            move.Count <= rules.RackSize && move.Count <= Board.Length && Scores[1 - Turn] >= move.Points &&
            (rules.IsClassic
                ? words.Length <= move.Count + 1 && move.Points <= rules.MaxTurnScore &&
                    (move.Points == 0 ? DryTurns > 0 : DryTurns == 0)
                : words.Length <= rules.RackSize + 1 && move.Points > 0 && DryTurns == 0) &&
            words.All(word => word is not null && word.Length <= rules.Size && WordLexicon.IsEntry(word)),
        _ => false
    };

    private static InvalidDataException InvalidCheckpoint(Exception? inner = null) =>
        new("Invalid Word Tiles checkpoint.", inner);
}
