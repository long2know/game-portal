namespace JadeCourt.Games;

public sealed record GameDefinition(
    string Id, string Name, string Description, int[] PlayerCounts, GameVariant[] Variants);

public sealed record GameVariant(string Id, string Label);

public sealed record GameCell(string Id, int Row, int Column, int? Owner, string? Piece);

public sealed record GameMove(
    string Id, string Label, string? From, string? To, string? Promotion = null, string[]? Via = null);

public sealed record GamePosition(
    int Rows, int Columns, GameCell[] Cells, int? Turn, string Status, int[] Winners,
    string? Detail, GameMove[] Moves, GameMove? LastMove, double[]? Scores = null);

public interface IBoardGame
{
    GameDefinition Definition { get; }
    string Start(string variant, int players);
    GamePosition View(string checkpoint);
    string Apply(string checkpoint, string moveId);
    string SelectBotMove(string checkpoint);
}
