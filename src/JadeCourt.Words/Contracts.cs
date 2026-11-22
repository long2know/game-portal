namespace JadeCourt.Words;

public sealed record WordAction(string Kind, WordPlacement[]? Placements = null, int[]? Exchange = null);

public sealed record WordPlacement(int Row, int Column, int TileId);

public sealed record WordTile(int Id, string Letter, int Points);

public sealed record WordCell(int Row, int Column, WordTile Tile, int Seat);

public sealed record WordView(
    int Rows, int Columns, int? Turn, string Status, int[] Winners, string? Detail,
    WordCell[] Board, WordTile[] Rack, int[] Scores, int[] RackSizes, int BagCount, string? LastMove);
