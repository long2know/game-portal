namespace JadeCourt.Words;

internal sealed class WordTiles
{
    internal const int MaxSize = 15;
    internal const int MaxRackSize = 7;

    internal static readonly WordTiles Quick = new(1, "quick", 11, 6, 4,
    [
        ('A', 5, 1), ('B', 2, 2), ('C', 2, 2), ('D', 2, 2), ('E', 6, 1), ('F', 1, 3),
        ('G', 1, 2), ('H', 2, 2), ('I', 4, 1), ('J', 1, 4), ('K', 1, 3), ('L', 3, 1),
        ('M', 2, 2), ('N', 3, 1), ('O', 4, 1), ('P', 2, 2), ('Q', 1, 5), ('R', 4, 1),
        ('S', 4, 1), ('T', 4, 1), ('U', 2, 1), ('V', 1, 3), ('W', 1, 3), ('X', 1, 4),
        ('Y', 2, 2), ('Z', 1, 5)
    ]);

    internal static readonly WordTiles Classic = new(2, "classic", MaxSize, MaxRackSize, 6,
    [
        ('A', 9, 1), ('B', 2, 3), ('C', 3, 2), ('D', 4, 2), ('E', 11, 1), ('F', 2, 4),
        ('G', 3, 2), ('H', 3, 3), ('I', 8, 1), ('J', 1, 7), ('K', 1, 5), ('L', 4, 1),
        ('M', 3, 2), ('N', 6, 1), ('O', 7, 1), ('P', 2, 3), ('Q', 1, 9), ('R', 6, 1),
        ('S', 4, 1), ('T', 6, 1), ('U', 4, 2), ('V', 2, 5), ('W', 2, 4), ('X', 1, 7),
        ('Y', 2, 3), ('Z', 1, 9), ('?', 2, 0)
    ], CourtGrid(centerDoubleWord: true));

    internal static readonly WordTiles Duel = new(3, "duel", MaxSize, MaxRackSize, 3,
    [
        ('A', 9, 1), ('B', 2, 4), ('C', 2, 4), ('D', 5, 2), ('E', 13, 1), ('F', 2, 4),
        ('G', 3, 3), ('H', 4, 3), ('I', 8, 1), ('J', 1, 10), ('K', 1, 5), ('L', 4, 2),
        ('M', 2, 4), ('N', 5, 2), ('O', 8, 1), ('P', 2, 4), ('Q', 1, 10), ('R', 6, 1),
        ('S', 5, 1), ('T', 7, 1), ('U', 4, 2), ('V', 2, 5), ('W', 2, 4), ('X', 1, 8),
        ('Y', 2, 3), ('Z', 1, 10), ('?', 2, 0)
    ], CourtGrid(centerDoubleWord: false));

    private readonly WordTile[] tiles;
    private readonly PremiumKind[] grid;
    private readonly WordPremium[] premiums;

    private WordTiles(int version, string variant, int size, int rackSize, int dryTurnLimit,
        (char Letter, int Count, int Points)[] supply, PremiumKind[]? grid = null)
    {
        Version = version;
        Variant = variant;
        Size = size;
        RackSize = rackSize;
        DryTurnLimit = dryTurnLimit;
        this.grid = grid ?? new PremiumKind[size * size];
        tiles = supply.SelectMany(tile => Enumerable.Repeat(tile, tile.Count))
            .Select((tile, index) => new WordTile(index + 1, tile.Letter.ToString(), tile.Points, tile.Letter == '?')).ToArray();
        premiums = this.grid.Select((kind, index) => (kind, index))
            .Where(cell => cell.kind != PremiumKind.None)
            .Select(cell => new WordPremium(cell.index / size, cell.index % size, cell.kind switch
            {
                PremiumKind.DoubleLetter => "double-letter",
                PremiumKind.TripleLetter => "triple-letter",
                PremiumKind.DoubleWord => "double-word",
                _ => "triple-word"
            })).ToArray();

        var maxPoints = tiles.Max(tile => tile.Points);
        MaxTurnScore = UsesPremiumRules
            ? LargestLineScore(maxPoints) + rackSize * ((size + 2) * maxPoints * 3) + RackBonus
            : 2 * size * maxPoints + LinkBonus;
    }

    internal static WordTiles? ForVersion(int version) => version switch { 1 => Quick, 2 => Classic, 3 => Duel, _ => null };
    internal static WordTiles? ForVariant(string variant) => variant switch { "quick" => Quick, "classic" => Classic, "duel" => Duel, _ => null };
    internal int Version { get; }
    internal string Variant { get; }
    internal int Size { get; }
    internal int RackSize { get; }
    internal int DryTurnLimit { get; }
    internal int MaxTurnScore { get; }
    internal bool UsesPremiumRules => Version is 2 or 3;
    internal bool IsDuel => Version == 3;
    internal int MinExchangeBag => Version == 2 ? RackSize : 0;
    internal int LinkBonus => UsesPremiumRules ? 0 : 2;
    internal int RackBonus => Version switch { 2 => 50, 3 => 35, _ => 0 };
    internal int MaxExchange => UsesPremiumRules ? RackSize : 3;
    internal int Count => tiles.Length;
    internal bool IsTileId(int id) => id >= 1 && id <= tiles.Length;
    internal WordTile Get(int id) => tiles[id - 1];
    internal static bool IsLetter(string? letter) => letter is { Length: 1 } && letter[0] is >= 'A' and <= 'Z';
    internal bool CanAssign(int id, string? letter) => Get(id).Blank ? IsLetter(letter) : letter is null;
    internal WordTile Placed(int id, string? letter) => letter is null ? Get(id) : Get(id) with { Letter = letter };
    internal bool InBounds(int row, int column) => (uint)row < (uint)Size && (uint)column < (uint)Size;
    internal PremiumKind PremiumAt(int index) => grid[index];
    internal WordPremium[] ProjectPremiums() => (WordPremium[])premiums.Clone();

    internal int ExchangeLimit(int rackCount, int bagCount) =>
        bagCount < MinExchangeBag ? 0 : Math.Min(MaxExchange, Math.Min(rackCount, bagCount));

    private int LargestLineScore(int maxPoints)
    {
        var maximum = 0;
        foreach (var vertical in new[] { false, true })
        for (var fixedCoordinate = 0; fixedCoordinate < Size; fixedCoordinate++)
        {
            var sum = 0;
            var multiplier = 1;
            for (var offset = 0; offset < Size; offset++)
            {
                var index = vertical ? offset * Size + fixedCoordinate : fixedCoordinate * Size + offset;
                var premium = grid[index];
                sum += maxPoints * (premium is PremiumKind.DoubleLetter ? 2 : premium is PremiumKind.TripleLetter ? 3 : 1);
                multiplier *= premium is PremiumKind.DoubleWord ? 2 : premium is PremiumKind.TripleWord ? 3 : 1;
            }
            maximum = Math.Max(maximum, sum * multiplier);
        }
        return maximum;
    }

    private static PremiumKind[] CourtGrid(bool centerDoubleWord)
    {
        var grid = new PremiumKind[MaxSize * MaxSize];
        Add(PremiumKind.TripleWord, (0, 5), (2, 2));
        Add(PremiumKind.DoubleWord, (0, 3), (4, 7));
        Add(PremiumKind.TripleLetter, (1, 1), (2, 6), (4, 6));
        Add(PremiumKind.DoubleLetter, (0, 0), (1, 4), (3, 3), (5, 5));
        if (centerDoubleWord)
            grid[grid.Length / 2] = PremiumKind.DoubleWord;
        return grid;

        // Reflect in both axes and across the diagonal for the same opportunities in every quadrant.
        void Add(PremiumKind kind, params (int Row, int Column)[] seeds)
        {
            foreach (var (row, column) in seeds)
            foreach (var r in new[] { row, MaxSize - 1 - row })
            foreach (var c in new[] { column, MaxSize - 1 - column })
                grid[r * MaxSize + c] = grid[c * MaxSize + r] = kind;
        }
    }
}

internal enum PremiumKind { None, DoubleLetter, TripleLetter, DoubleWord, TripleWord }
