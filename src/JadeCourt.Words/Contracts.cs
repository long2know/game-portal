using System.Text.Json.Serialization;

namespace JadeCourt.Words;

public sealed record WordAction(string Kind, WordPlacement[]? Placements = null, int[]? Exchange = null);

public sealed record WordPlacement(int Row, int Column, int TileId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Letter = null);

public sealed record WordTile(int Id, string Letter, int Points, bool Blank = false);

public sealed record WordCell(int Row, int Column, WordTile Tile, int Seat);

public sealed record WordPremium(int Row, int Column, string Kind);

public sealed record WordView(
    int Rows, int Columns, int? Turn, string Status, int[] Winners, string? Detail,
    WordCell[] Board, WordTile[] Rack, int[] Scores, int[] RackSizes, int BagCount, string? LastMove,
    string Variant, int RackSize, WordPremium[] Premiums, int ExchangeLimit)
{
    internal int? ViewerSeat { get; init; }
}
