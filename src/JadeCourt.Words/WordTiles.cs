namespace JadeCourt.Words;

internal static class WordTiles
{
    internal const int Size = 11;
    internal const int RackSize = 6;
    internal const int ExchangeLimit = 3;
    internal const int DryTurnLimit = 4;
    internal const int LinkBonus = 2;
    internal const int MaxPoints = 5;

    private static readonly (char Letter, int Count, int Points)[] Supply =
    [
        ('A', 5, 1), ('B', 2, 2), ('C', 2, 2), ('D', 2, 2), ('E', 6, 1), ('F', 1, 3),
        ('G', 1, 2), ('H', 2, 2), ('I', 4, 1), ('J', 1, 4), ('K', 1, 3), ('L', 3, 1),
        ('M', 2, 2), ('N', 3, 1), ('O', 4, 1), ('P', 2, 2), ('Q', 1, 5), ('R', 4, 1),
        ('S', 4, 1), ('T', 4, 1), ('U', 2, 1), ('V', 1, 3), ('W', 1, 3), ('X', 1, 4),
        ('Y', 2, 2), ('Z', 1, 5)
    ];

    private static readonly WordTile[] Tiles = Supply
        .SelectMany(tile => Enumerable.Repeat(tile, tile.Count))
        .Select((tile, index) => new WordTile(index + 1, tile.Letter.ToString(), tile.Points)).ToArray();

    internal static int Count => Tiles.Length;
    internal static bool IsTileId(int id) => id >= 1 && id <= Tiles.Length;
    internal static WordTile Get(int id) => Tiles[id - 1];
}
