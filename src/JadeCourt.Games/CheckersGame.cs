namespace JadeCourt.Games;

internal sealed record CheckersState(byte[] Board, int Turn, string[] QuietPositions, GameMove? LastMove);

internal sealed class CheckersGame() : BoardGame<CheckersState, CheckersGame.Turn>(
    new("checkers", "English Checkers", "Take forced jumps, crown kings, and leave your opponent no move.",
        [2], [new("english", "English 8×8")]), 16_384)
{
    internal sealed record Turn(int From, int[] Landings, int[] Captures, bool Crown)
    {
        internal int To => Landings[^1];
    }

    private const int Size = 8;
    private const int QuietLimit = 80;
    private static readonly (int Row, int Column)[] Diagonals = [(-1, -1), (-1, 1), (1, -1), (1, 1)];

    protected override CheckersState InitialState(string variant, int players)
    {
        var board = new byte[Size * Size];
        for (var index = 0; index < board.Length; index++)
            if (IsDark(index))
                board[index] = index / Size < 3 ? (byte)2 : index / Size >= 5 ? (byte)1 : (byte)0;
        return new(board, 0, [PositionKey(board, 0)], null);
    }

    private static bool IsDark(int index) => ((index / Size + index % Size) & 1) != 0;
    private static int Owner(byte piece) => (piece - 1) & 1;
    private static bool IsKing(byte piece) => piece > 2;
    private static int Forward(int seat) => seat == 0 ? -1 : 1;
    private static bool AtCrown(int index, int seat) => index / Size == (seat == 0 ? 0 : Size - 1);

    protected override bool IsValid(CheckersState state)
    {
        if (state.Turn is not (0 or 1) || state.Board?.Length != Size * Size ||
            state.QuietPositions?.Length is not (>= 1 and <= QuietLimit + 1) ||
            state.QuietPositions[^1] != PositionKey(state.Board, state.Turn) ||
            Array.Exists(state.QuietPositions, key => key?.Length != 89 || key[0] is not ('0' or '1')))
            return false;

        for (var index = 0; index < state.Board.Length; index++)
        {
            var piece = state.Board[index];
            if (piece != 0 && (!IsDark(index) || piece > 4 || (!IsKing(piece) && AtCrown(index, Owner(piece)))))
                return false;
        }
        return state.Board.Count(piece => piece is 1 or 3) <= 12 &&
               state.Board.Count(piece => piece is 2 or 4) <= 12;
    }

    private static string PositionKey(byte[] board, int seat) =>
        string.Concat(seat == 0 ? "0" : "1", Convert.ToBase64String(board));

    private static bool IsDraw(CheckersState state) =>
        state.QuietPositions.Length > QuietLimit ||
        state.QuietPositions.Count(key => key == state.QuietPositions[^1]) >= 3;

    protected override IEnumerable<Turn> LegalTurns(CheckersState state) =>
        IsDraw(state) ? [] : Generate(state.Board, state.Turn);

    private static IEnumerable<Turn> Generate(byte[] board, int seat)
    {
        var scratch = (byte[])board.Clone();
        var canCapture = false;
        for (var from = 0; from < scratch.Length; from++)
        {
            var piece = scratch[from];
            if (piece == 0 || Owner(piece) != seat)
                continue;
            foreach (var jump in Jumps(scratch, seat, piece, from, from, [], []))
            {
                canCapture = true;
                yield return jump;
            }
        }
        if (canCapture)
            yield break;

        for (var from = 0; from < board.Length; from++)
        {
            var piece = board[from];
            if (piece == 0 || Owner(piece) != seat)
                continue;
            foreach (var (dy, dx) in Diagonals)
            {
                if (!IsKing(piece) && dy != Forward(seat))
                    continue;
                var to = Point.At(from, Size).Offset(dy, dx, Size);
                if (to >= 0 && board[to] == 0)
                    yield return new(from, [to], [], !IsKing(piece) && AtCrown(to, seat));
            }
        }
    }

    private static IEnumerable<Turn> Jumps(
        byte[] board, int seat, byte piece, int from, int at, List<int> landings, List<int> captured)
    {
        var crown = !IsKing(piece) && AtCrown(at, seat);
        var continued = false;
        if (!crown)
        {
            foreach (var (dy, dx) in Diagonals)
            {
                if (!IsKing(piece) && dy != Forward(seat))
                    continue;
                var point = Point.At(at, Size);
                var to = point.Offset(2 * dy, 2 * dx, Size);
                if (to < 0 || board[to] != 0)
                    continue;
                var over = point.Offset(dy, dx, Size);
                var enemy = board[over];
                if (enemy == 0 || Owner(enemy) == seat)
                    continue;

                board[at] = board[over] = 0;
                board[to] = piece;
                landings.Add(to);
                captured.Add(over);
                foreach (var next in Jumps(board, seat, piece, from, to, landings, captured))
                    yield return next;
                captured.RemoveAt(captured.Count - 1);
                landings.RemoveAt(landings.Count - 1);
                board[at] = piece;
                board[over] = enemy;
                board[to] = 0;
                continued = true;
            }
        }

        if (!continued && captured.Count != 0)
            yield return new(from, [.. landings], [.. captured], crown);
    }

    protected override GameMove Describe(CheckersState state, Turn turn) =>
        BoardNotation.Travel(Point.At(turn.From, Size),
            turn.Landings.Select(index => Point.At(index, Size)).ToArray(), turn.Crown ? "king" : null);

    protected override CheckersState Advance(CheckersState state, Turn turn, GameMove move)
    {
        var board = After(state.Board, turn);
        var next = 1 - state.Turn;
        return new(board, next, NextHistory(state, turn, board, next), move);
    }

    private static string[] NextHistory(CheckersState state, Turn turn, byte[] board, int next)
    {
        var key = PositionKey(board, next);
        return turn.Captures.Length == 0 && IsKing(state.Board[turn.From])
            ? [.. state.QuietPositions, key] : [key];
    }

    private static byte[] After(byte[] board, Turn turn)
    {
        var result = (byte[])board.Clone();
        var piece = result[turn.From];
        result[turn.From] = 0;
        foreach (var captured in turn.Captures)
            result[captured] = 0;
        result[turn.To] = turn.Crown ? (byte)(piece + 2) : piece;
        return result;
    }

    protected override Turn Choose(CheckersState state, Turn[] turns) => turns.MaxBy(turn =>
    {
        var board = After(state.Board, turn);
        var opponent = 1 - state.Turn;
        var replies = Generate(board, opponent).ToArray();
        if (replies.Length == 0)
            return 10_000;
        var next = new CheckersState(board, opponent, NextHistory(state, turn, board, opponent), null);
        if (IsDraw(next))
            return 0;
        return replies.Min(reply =>
        {
            var afterReply = After(board, reply);
            if (!Generate(afterReply, state.Turn).Any())
                return -10_000;
            return Evaluate(afterReply, state.Turn);
        });
    })!;

    private static int Evaluate(byte[] board, int seat)
    {
        var score = 0;
        for (var index = 0; index < board.Length; index++)
        {
            var piece = board[index];
            if (piece == 0)
                continue;
            var point = Point.At(index, Size);
            var owner = Owner(piece);
            var progress = owner == 0 ? Size - 1 - point.Row : point.Row;
            var centre = Math.Min(point.Column, Size - 1 - point.Column);
            var value = (IsKing(piece) ? 175 : 100 + 3 * progress) + 2 * centre;
            score += owner == seat ? value : -value;
        }
        return score;
    }

    protected override GamePosition Project(CheckersState state, GameMove[] moves)
    {
        var cells = new List<GameCell>(32);
        for (var index = 0; index < state.Board.Length; index++)
        {
            if (!IsDark(index))
                continue;
            var piece = state.Board[index];
            cells.Add(Point.At(index, Size).Cell(piece == 0 ? null : Owner(piece),
                piece == 0 ? null : IsKing(piece) ? "king" : "man"));
        }

        if (moves.Length != 0)
            return new(Size, Size, [.. cells], state.Turn, "playing", [], null, moves, state.LastMove);

        var draw = IsDraw(state) && Generate(state.Board, state.Turn).Any();
        var detail = !draw ? "The side to move has no legal move."
            : state.QuietPositions.Count(key => key == state.QuietPositions[^1]) >= 3
                ? "Draw: threefold repetition." : "Draw: 40 moves per side without a capture or a man advancing.";
        return new(Size, Size, [.. cells], null, "finished", draw ? [] : [1 - state.Turn], detail, [], state.LastMove);
    }
}
