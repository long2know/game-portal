namespace JadeCourt.Solo;

public sealed record SoloAction(
    string Kind, string? From = null, string? To = null, int? Count = null,
    int? Index = null, int? Value = null);

public sealed record SolitaireCard(int? Rank, string? Suit, bool FaceUp);

public sealed record KlondikeMove(string From, string To, int Count);

public sealed record KlondikePosition(
    bool Finished, int Moves, int StockCount, int WasteCount, SolitaireCard? Waste,
    SolitaireCard?[] Foundations, SolitaireCard[][] Tableau, KlondikeMove[] LegalMoves);

public sealed record SudokuPosition(
    bool Finished, int Moves, int[] Given, int[] Values, int[] Conflicts);
