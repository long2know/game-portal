using global::Chess;

namespace JadeCourt.Chess;

internal sealed class ChessReplay
{
    internal const string Version = "chess-v1";
    // Even if all 16 pawns made six moves, 30 pieces were captured, and neither overlapped,
    // the fifty-move adjudication bounds game length well below this.
    private const int MaxPlies = 20_000;

    [Flags]
    private enum Castling
    {
        None = 0,
        WhiteKing = 1,
        WhiteQueen = 2,
        BlackKing = 4,
        BlackQueen = 8,
        White = WhiteKing | WhiteQueen,
        Black = BlackKing | BlackQueen,
        All = White | Black
    }

    private readonly Dictionary<string, int> repetitions = new(StringComparer.Ordinal);
    private Castling castling = Castling.All;
    private int reversiblePlies;

    internal ChessBoard Board { get; } = new() { AutoEndgameRules = AutoEndgameRules.None };
    internal Move? LastMove { get; private set; }
    internal string? AutomaticDraw { get; private set; }

    private ChessReplay() => repetitions[PositionKey()] = 1;

    internal static ChessReplay Load(string checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var state = new ChessReplay();
        if (checkpoint == Version)
            return state;

        if (checkpoint.Length > Version.Length + MaxPlies * 6 ||
            !checkpoint.StartsWith(Version + " ", StringComparison.Ordinal))
            throw InvalidCheckpoint();

        var plies = 0;
        var history = checkpoint.AsSpan(Version.Length + 1);
        foreach (var range in history.Split(' '))
        {
            if (++plies > MaxPlies || !state.TryPlay(history[range]))
                throw InvalidCheckpoint();
        }
        return state;
    }

    private static ArgumentException InvalidCheckpoint() => new("Invalid chess move history.", "checkpoint");

    internal bool TryPlay(ReadOnlySpan<char> id)
    {
        if (AutomaticDraw is not null || Board.IsEndGame || !IsMoveId(id))
            return false;

        var from = new Position(id[..2].ToString());
        var to = new Position(id.Slice(2, 2).ToString());
        if (Board[from]?.Color != Board.Turn)
            return false;

        var promotion = id.Length == 5 ? id[4] : '\0';
        foreach (var move in Board.Moves(from, generateSan: false))
        {
            if (move.NewPosition != to || PromotionCode(move) != promotion || !RespectsCastlingRights(move))
                continue;

            if (!Board.Move(move))
                throw new InvalidOperationException("Chess validation and execution disagreed.");
            LastMove = move;
            TrackPosition(move);
            return true;
        }
        return false;
    }

    internal IEnumerable<Move> LegalMoves()
    {
        var turn = Board.Turn;
        for (short y = 0; y < 8; y++)
        for (short x = 0; x < 8; x++)
        {
            if (Board[x, y]?.Color != turn)
                continue;
            foreach (var move in Board.Moves(new Position(x, y), generateSan: false))
            {
                if (RespectsCastlingRights(move))
                    yield return move;
            }
        }
    }

    internal static string MoveId(Move move)
    {
        var coordinates = $"{move.OriginalPosition}{move.NewPosition}";
        var promotion = PromotionCode(move);
        return promotion == '\0' ? coordinates : coordinates + promotion;
    }

    private static char PromotionCode(Move move) => move.Promotion?.Type.AsChar ?? '\0';

    private static bool IsMoveId(ReadOnlySpan<char> id) =>
        id.Length is 4 or 5 &&
        id[0] is >= 'a' and <= 'h' && id[1] is >= '1' and <= '8' &&
        id[2] is >= 'a' and <= 'h' && id[3] is >= '1' and <= '8' &&
        (id.Length == 4 || id[4] is 'q' or 'r' or 'b' or 'n');

    private bool RespectsCastlingRights(Move move)
    {
        if (!move.IsCastling)
            return true;
        var rook = new Position(move.NewPosition.X == 6 ? (short)7 : (short)0, move.NewPosition.Y);
        return (castling & RookRight(rook)) != Castling.None;
    }

    private void TrackPosition(Move move)
    {
        var previousRights = castling;
        if (move.Piece.Type == PieceType.King)
            castling &= ~(move.Piece.Color == PieceColor.White ? Castling.White : Castling.Black);
        // A captured original rook cannot acquire castling rights again when replaced by another rook.
        castling &= ~(RookRight(move.OriginalPosition) | RookRight(move.NewPosition));

        var resetsFiftyMoveCount = move.Piece.Type == PieceType.Pawn || move.CapturedPiece is not null;
        reversiblePlies = resetsFiftyMoveCount ? 0 : reversiblePlies + 1;
        if (resetsFiftyMoveCount || castling != previousRights)
            repetitions.Clear();

        var key = PositionKey();
        repetitions[key] = repetitions.GetValueOrDefault(key) + 1;
        AutomaticDraw = (move.CapturedPiece is not null || move.IsPromotion) && HasInsufficientMaterial()
            ? "Draw by insufficient material."
            : repetitions[key] >= 3 ? "Draw by threefold repetition."
            : reversiblePlies >= 100 ? "Draw by the fifty-move rule."
            : null;
    }

    private static Castling RookRight(Position position) => (position.X, position.Y) switch
    {
        (0, 0) => Castling.WhiteQueen,
        (7, 0) => Castling.WhiteKing,
        (0, 7) => Castling.BlackQueen,
        (7, 7) => Castling.BlackKing,
        _ => Castling.None
    };

    private string PositionKey()
    {
        Span<char> key = stackalloc char[67];
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
            key[y * 8 + x] = Board[x, y]?.ToFenChar() ?? '.';
        key[64] = Board.Turn.AsChar;
        key[65] = (char)('A' + (int)castling);
        key[66] = LegalEnPassantFile();
        return new string(key);
    }

    private char LegalEnPassantFile()
    {
        // Repetition ignores the FEN en passant square unless an actual legal capture exists.
        if (LastMove is not { } last || last.Piece.Type != PieceType.Pawn ||
            Math.Abs(last.OriginalPosition.Y - last.NewPosition.Y) != 2)
            return '-';

        var pawn = last.NewPosition;
        for (var x = pawn.X - 1; x <= pawn.X + 1; x += 2)
        {
            if (x is < 0 or > 7 || Board[x, pawn.Y] is not { } neighbor ||
                neighbor.Type != PieceType.Pawn || neighbor.Color != Board.Turn)
                continue;
            if (Board.Moves(new Position((short)x, pawn.Y), generateSan: false).Any(move => move.IsEnPassant))
                return pawn.File();
        }
        return '-';
    }

    private bool HasInsufficientMaterial()
    {
        var minors = 0;
        int? bishopSquareColor = null;
        var onlySameColorBishops = true;
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
        {
            var piece = Board[x, y];
            if (piece is null || piece.Type == PieceType.King)
                continue;
            minors++;
            if (piece.Type == PieceType.Bishop)
            {
                var squareColor = (x + y) % 2;
                bishopSquareColor ??= squareColor;
                onlySameColorBishops &= bishopSquareColor == squareColor;
            }
            else if (piece.Type == PieceType.Knight)
                onlySameColorBishops = false;
            else
                return false;
        }
        // Two knights can mate a cooperating opponent, so they do not make a dead position.
        return minors <= 1 || onlySameColorBishops;
    }
}
