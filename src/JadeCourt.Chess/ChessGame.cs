using global::Chess;
using JadeCourt.Games;

namespace JadeCourt.Chess;

public sealed class ChessGame : IBoardGame
{
    private readonly StockfishBot? stockfish;

    public ChessGame(string? stockfishPath = null, Action<string>? warn = null)
    {
        if (!string.IsNullOrWhiteSpace(stockfishPath))
            stockfish = new StockfishBot(stockfishPath, warn);
    }

    public GameDefinition Definition => new(
        "chess", "Chess", "Orthodox chess for two players.", [2], [new("standard", "Standard")]);

    public string Start(string variant, int players)
    {
        if (variant != "standard")
            throw new ArgumentException("Unknown chess variant.", nameof(variant));
        if (players != 2)
            throw new ArgumentException("Chess requires two players.", nameof(players));
        return ChessReplay.Version;
    }

    public GamePosition View(string checkpoint)
    {
        var state = ChessReplay.Load(checkpoint);
        var board = state.Board;
        var turn = Seat(board.Turn);
        var player = turn == 0 ? "White" : "Black";
        var inCheck = turn == 0 ? board.WhiteKingChecked : board.BlackKingChecked;
        var legalMoves = state.LegalMoves().Select(Project)
            .OrderBy(move => move.Id, StringComparer.Ordinal).ToArray();

        var cells = new GameCell[64];
        for (var row = 0; row < 8; row++)
        for (var column = 0; column < 8; column++)
        {
            var square = new Position((short)column, (short)(7 - row));
            var piece = board[square];
            cells[row * 8 + column] = new GameCell(square.ToString(), row, column,
                piece is null ? null : Seat(piece.Color), piece?.Type.Name.ToLowerInvariant());
        }

        var lastMove = state.LastMove is { } last ? Project(last) : null;
        if (legalMoves.Length == 0)
        {
            var winner = 1 - turn;
            var detail = inCheck
                ? $"{(winner == 0 ? "White" : "Black")} wins by checkmate."
                : "Draw by stalemate.";
            return new GamePosition(8, 8, cells, null, "finished", inCheck ? [winner] : [], detail, [], lastMove);
        }
        if (state.AutomaticDraw is { } draw)
            return new GamePosition(8, 8, cells, null, "finished", [], draw, [], lastMove);

        return new GamePosition(8, 8, cells, turn, "playing", [],
            inCheck ? $"{player} is in check." : null, legalMoves, lastMove);
    }

    public string Apply(string checkpoint, string moveId)
    {
        var state = ChessReplay.Load(checkpoint);
        if (!state.TryPlay(moveId))
            throw new ArgumentException("Not a legal move in this chess position.", nameof(moveId));
        return $"{checkpoint} {moveId}";
    }

    public string SelectBotMove(string checkpoint)
    {
        var state = ChessReplay.Load(checkpoint);
        var legal = state.AutomaticDraw is null ? state.LegalMoves().ToArray() : [];
        if (legal.Length == 0)
            throw new ArgumentException("The chess game has finished.", nameof(checkpoint));

        if (stockfish is not null)
        {
            var history = checkpoint.Length == ChessReplay.Version.Length
                ? "" : checkpoint[(ChessReplay.Version.Length + 1)..];
            var ids = legal.Select(ChessReplay.MoveId).ToHashSet(StringComparer.Ordinal);
            if (stockfish.Select(history, ids) is { } id)
                return id;
        }

        return ChessReplay.MoveId(legal.OrderByDescending(BotScore)
            .ThenBy(ChessReplay.MoveId, StringComparer.Ordinal).First());
    }

    private static int Seat(PieceColor color) => color == PieceColor.White ? 0 : 1;

    private static GameMove Project(Move move)
    {
        var from = move.OriginalPosition.ToString();
        var to = move.NewPosition.ToString();
        var promotion = move.Promotion?.Type.Name.ToLowerInvariant();
        var label = move.IsCastling ? (move.NewPosition.X == 6 ? "O-O" : "O-O-O")
            : promotion is null ? $"{from}–{to}" : $"{from}–{to} ({promotion})";
        return new GameMove(ChessReplay.MoveId(move), label, from, to, promotion);
    }

    private static int BotScore(Move move)
    {
        if (move.IsMate && move.IsCheck)
            return 100_000;

        var capture = move.CapturedPiece is { } target
            ? 10 * Value(target.Type.AsChar) - Value(move.Piece.Type.AsChar) : 0;
        var promotion = move.Promotion is { } promoted ? 8 * (Value(promoted.Type.AsChar) - 100) : 0;
        var centralize = DistanceFromCenter(move.OriginalPosition) - DistanceFromCenter(move.NewPosition);
        return capture + promotion + 6 * centralize + (move.IsCheck ? 35 : 0) + (move.IsCastling ? 100 : 0);
    }

    private static int DistanceFromCenter(Position p) => Math.Abs(2 * p.X - 7) + Math.Abs(2 * p.Y - 7);

    private static int Value(char type) => type switch
    {
        'p' => 100,
        'n' => 320,
        'b' => 330,
        'r' => 500,
        'q' => 900,
        _ => 1_000
    };
}
