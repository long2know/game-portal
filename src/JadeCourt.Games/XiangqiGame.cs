namespace JadeCourt.Games;

internal sealed record XiangqiState(byte[] Anchor, int Seat, byte[] Trail)
{
    internal XiangqiGame.Position Restored { get; set; } = null!;
}

internal sealed class XiangqiGame() : BoardGame<XiangqiState, XiangqiGame.Turn>(
    new("xiangqi", "Xiangqi (Chinese Chess)", "Use palaces, river crossings and cannon screens to trap the opposing general.",
        [2], [new("standard", "Standard 10×9")]), 4096)
{
    internal enum Piece { General = 1, Advisor, Elephant, Horse, Chariot, Cannon, Soldier }
    internal enum Ending { None, Repetition, QuietLimit, RedChecks, BlackChecks }
    internal readonly record struct Turn(int From, int To);
    internal readonly record struct Occurrence(string Board, bool Check);
    internal sealed record Position(byte[] Board, int Seat, Turn? Last, List<Occurrence> Quiet, Ending Ending);
    private readonly record struct Step(int To, int Blocker);

    private const int Rows = 10;
    private const int Columns = 9;
    private const int CellCount = Rows * Columns;
    private const int QuietPlyLimit = 120;
    private const int Win = 100_000;
    private const int NoStep = -2;
    private const int ClearStep = -1;
    private static readonly string[] Names = ["", "general", "advisor", "elephant", "horse", "chariot", "cannon", "soldier"];
    private static readonly int[] PieceLimits = [0, 1, 2, 2, 2, 2, 2, 5];
    private static readonly Piece[] BackRank =
        [Piece.Chariot, Piece.Horse, Piece.Elephant, Piece.Advisor, Piece.General,
            Piece.Advisor, Piece.Elephant, Piece.Horse, Piece.Chariot];
    private static readonly (int Row, int Column)[] Orthogonal = [(-1, 0), (0, -1), (0, 1), (1, 0)];
    private static readonly string[] Ids = Enumerable.Range(0, CellCount).Select(at =>
        FormattableString.Invariant($"{(char)('a' + at % Columns)}{Rows - at / Columns}")).ToArray();
    private static readonly Step[][][] Steps = Enumerable.Range(0, 15).Select(BuildSteps).ToArray();

    protected override XiangqiState InitialState(string variant, int players)
    {
        var board = new byte[CellCount];
        for (var seat = 0; seat < 2; seat++)
        {
            for (var file = 0; file < Columns; file++)
            {
                board[Index(HomeRow(0, seat), file)] = Encode(BackRank[file], seat);
                if (file is 1 or 7)
                    board[Index(HomeRow(2, seat), file)] = Encode(Piece.Cannon, seat);
                if (file % 2 == 0)
                    board[Index(HomeRow(3, seat), file)] = Encode(Piece.Soldier, seat);
            }
        }
        return new(board, 0, []);
    }

    internal static byte Encode(Piece kind, int seat) => (byte)((int)kind + 7 * seat);
    private static int Owner(byte piece) => (piece - 1) / 7;
    private static Piece Kind(byte piece) => (Piece)((piece - 1) % 7 + 1);
    private static int HomeRow(int row, int seat) => seat == 0 ? Rows - 1 - row : row;
    private static int Index(int row, int column) => row * Columns + column;
    private static bool InPalace(int row, int col, int seat) => HomeRow(row, seat) <= 2 && col is >= 3 and <= 5;

    protected override bool IsValid(XiangqiState state)
    {
        if (state.Seat is not (0 or 1) || state.Anchor?.Length != CellCount ||
            state.Trail is null || state.Trail.Length % 2 != 0 ||
            state.Trail.Length > 2 * (QuietPlyLimit + 1) || !ValidAnchor(state.Anchor, state.Seat))
            return false;

        var board = (byte[])state.Anchor.Clone();
        var seat = state.Seat;
        var quiet = new List<Occurrence> { Mark(board, seat) };
        var ending = Ending.None;
        Turn? last = null;
        for (var i = 0; i < state.Trail.Length; i += 2)
        {
            var turn = new Turn(state.Trail[i], state.Trail[i + 1]);
            if (ending != Ending.None || !IsLegal(board, seat, turn))
                return false;
            if (MakesProgress(board, turn))
            {
                // The anchor is either the opening or the position immediately before the last progress move.
                if (i != 0)
                    return false;
                quiet.Clear();
            }
            Make(board, turn);
            seat = 1 - seat;
            quiet.Add(Mark(board, seat));
            ending = Adjudicate(quiet, 0, seat);
            last = turn;
        }
        state.Restored = new(board, seat, last, quiet, ending);
        return true;
    }

    private static bool ValidAnchor(byte[] board, int seat)
    {
        Span<int> counts = stackalloc int[15];
        Span<int> homeSoldiers = stackalloc int[2];
        counts.Clear();
        homeSoldiers.Clear();
        for (var at = 0; at < board.Length; at++)
        {
            var piece = board[at];
            if (piece == 0)
                continue;
            if (piece > 14 || ++counts[piece] > PieceLimits[(int)Kind(piece)])
                return false;
            var owner = Owner(piece);
            var row = HomeRow(at / Columns, owner);
            var col = at % Columns;
            switch (Kind(piece))
            {
                case Piece.General when !InPalace(at / Columns, col, owner):
                case Piece.Advisor when row > 2 || col is < 3 or > 5 || ((row + col) & 1) == 0:
                case Piece.Elephant when !(row is 0 or 4 && col is 2 or 6 || row == 2 && col is 0 or 4 or 8):
                case Piece.Soldier when row < 3:
                    return false;
                case Piece.Soldier when row < 5:
                    var file = 1 << col;
                    if ((col & 1) != 0 || (homeSoldiers[owner] & file) != 0)
                        return false;
                    homeSoldiers[owner] |= file;
                    break;
            }
        }
        return counts[Encode(Piece.General, 0)] == 1 && counts[Encode(Piece.General, 1)] == 1
            && !InCheck(board, 1 - seat);
    }

    private static Step[][] BuildSteps(int code) =>
        code == 0 || Kind((byte)code) is Piece.Chariot or Piece.Cannon ? [] :
            Enumerable.Range(0, CellCount).Select(from => Enumerable.Range(0, CellCount)
                .Select(to => new Step(to, StepBlocker((byte)code, from, to)))
                .Where(step => step.Blocker != NoStep).ToArray()).ToArray();

    private static int StepBlocker(byte piece, int from, int to)
    {
        var (row, col) = Point.At(from, Columns);
        var (y, x) = Point.At(to, Columns);
        var dy = y - row;
        var dx = x - col;
        var vertical = Math.Abs(dy);
        var horizontal = Math.Abs(dx);
        var seat = Owner(piece);
        return Kind(piece) switch
        {
            Piece.General when vertical + horizontal == 1 && InPalace(y, x, seat) => ClearStep,
            Piece.Advisor when vertical == 1 && horizontal == 1 && InPalace(y, x, seat) => ClearStep,
            Piece.Elephant when vertical == 2 && horizontal == 2 && HomeRow(y, seat) < 5 =>
                Index(row + dy / 2, col + dx / 2),
            Piece.Horse when vertical == 2 && horizontal == 1 => Index(row + dy / 2, col),
            Piece.Horse when vertical == 1 && horizontal == 2 => Index(row, col + dx / 2),
            Piece.Soldier when dy == (seat == 0 ? -1 : 1) && dx == 0 => ClearStep,
            Piece.Soldier when dy == 0 && horizontal == 1 && HomeRow(row, seat) >= 5 => ClearStep,
            _ => NoStep
        };
    }

    private static bool CanLand(byte piece, int seat) => piece == 0 || (Owner(piece) != seat && Kind(piece) != Piece.General);

    private static IEnumerable<int> Destinations(byte[] board, int from, int seat)
    {
        var piece = board[from];
        var kind = Kind(piece);
        if (kind is not (Piece.Chariot or Piece.Cannon))
        {
            foreach (var (to, blocker) in Steps[piece][from])
                if ((blocker == ClearStep || board[blocker] == 0) && CanLand(board[to], seat))
                    yield return to;
            yield break;
        }

        var (row, col) = Point.At(from, Columns);
        foreach (var (dy, dx) in Orthogonal)
        {
            var screen = false;
            for (int y = row + dy, x = col + dx; (uint)y < Rows && (uint)x < Columns; y += dy, x += dx)
            {
                var to = Index(y, x);
                if (board[to] == 0)
                {
                    if (!screen)
                        yield return to;
                }
                else if (kind == Piece.Cannon && !screen)
                    screen = true;
                else
                {
                    if (CanLand(board[to], seat))
                        yield return to;
                    break;
                }
            }
        }
    }

    private static int Screens(byte[] board, int from, int to)
    {
        var (row, col) = Point.At(from, Columns);
        var (y, x) = Point.At(to, Columns);
        if ((row == y) == (col == x))
            return -1;
        var step = row == y ? Math.Sign(x - col) : Math.Sign(y - row) * Columns;
        var screens = 0;
        for (var at = from + step; at != to; at += step)
            if (board[at] != 0 && ++screens == 2)
                break;
        return screens;
    }

    private static bool Reaches(byte[] board, int from, int to)
    {
        var piece = board[from];
        var kind = Kind(piece);
        if (kind is Piece.Chariot or Piece.Cannon)
            return Screens(board, from, to) == (kind == Piece.Cannon && board[to] != 0 ? 1 : 0);
        var blocker = StepBlocker(piece, from, to);
        return blocker == ClearStep || (blocker >= 0 && board[blocker] == 0);
    }

    private static bool InCheck(byte[] board, int seat) => InCheck(board, seat, Array.IndexOf(board, Encode(Piece.General, seat)));

    private static bool InCheck(byte[] board, int seat, int general)
    {
        for (var from = 0; from < board.Length; from++)
        {
            var piece = board[from];
            if (piece == 0 || Owner(piece) == seat)
                continue;
            if (Reaches(board, from, general) ||
                (Kind(piece) == Piece.General && from % Columns == general % Columns && Screens(board, from, general) == 0))
                return true;
        }
        return false;
    }

    private static bool IsLegal(byte[] board, int seat, Turn turn) =>
        (uint)turn.From < CellCount && (uint)turn.To < CellCount &&
        board[turn.From] != 0 && Owner(board[turn.From]) == seat && CanLand(board[turn.To], seat) &&
        Reaches(board, turn.From, turn.To) && KeepsGeneralSafe(board, seat, Array.IndexOf(board, Encode(Piece.General, seat)), turn);

    private static bool KeepsGeneralSafe(byte[] board, int seat, int general, Turn turn)
    {
        var taken = Make(board, turn);
        var safe = !InCheck(board, seat, turn.From == general ? turn.To : general);
        Undo(board, turn, taken);
        return safe;
    }

    private static IEnumerable<Turn> Generate(byte[] board, int seat)
    {
        var general = Array.IndexOf(board, Encode(Piece.General, seat));
        for (var from = 0; from < board.Length; from++)
            if (board[from] != 0 && Owner(board[from]) == seat)
                foreach (var to in Destinations(board, from, seat))
                {
                    var turn = new Turn(from, to);
                    if (KeepsGeneralSafe(board, seat, general, turn))
                        yield return turn;
                }
    }

    private static byte Make(byte[] board, Turn turn)
    {
        var captured = board[turn.To];
        board[turn.To] = board[turn.From];
        board[turn.From] = 0;
        return captured;
    }

    private static void Undo(byte[] board, Turn turn, byte captured)
    {
        board[turn.From] = board[turn.To];
        board[turn.To] = captured;
    }

    private static bool MakesProgress(byte[] board, Turn turn) =>
        board[turn.To] != 0 || (Kind(board[turn.From]) == Piece.Soldier && turn.From / Columns != turn.To / Columns);

    private static Occurrence Mark(byte[] board, int seat) => new(Convert.ToBase64String(board), InCheck(board, seat));

    private static Ending Adjudicate(List<Occurrence> quiet, int start, int seat)
    {
        // Even offsets have the same side to move. Each check was delivered by the seat that just moved.
        var last = quiet.Count - 1;
        var repeated = 0;
        for (var i = last - 2; i >= start; i -= 2)
        {
            if (quiet[i].Board != quiet[last].Board || ++repeated < 2)
                continue;

            var previousSeatAlwaysChecks = true;
            var nextSeatAlwaysChecks = true;
            for (var j = last; j > i; j -= 2)
            {
                previousSeatAlwaysChecks &= quiet[j].Check;
                nextSeatAlwaysChecks &= quiet[j - 1].Check;
            }
            if (previousSeatAlwaysChecks == nextSeatAlwaysChecks)
                return Ending.Repetition;
            return (nextSeatAlwaysChecks ? seat : 1 - seat) == 0 ? Ending.RedChecks : Ending.BlackChecks;
        }
        return last - start >= QuietPlyLimit ? Ending.QuietLimit : Ending.None;
    }

    protected override IEnumerable<Turn> LegalTurns(XiangqiState state) => state.Restored.Ending == Ending.None
        ? Generate(state.Restored.Board, state.Restored.Seat) : [];

    protected override GameMove Describe(XiangqiState state, Turn turn) => Move(turn);

    private static GameMove Move(Turn turn)
    {
        var from = Ids[turn.From];
        var to = Ids[turn.To];
        return new($"{from}>{to}", $"{from} → {to}", from, to);
    }

    protected override XiangqiState Advance(XiangqiState state, Turn turn, GameMove move)
    {
        var position = state.Restored;
        return MakesProgress(position.Board, turn)
            ? new(position.Board, position.Seat, [(byte)turn.From, (byte)turn.To])
            : new(state.Anchor, state.Seat, [.. state.Trail, (byte)turn.From, (byte)turn.To]);
    }

    private static string Color(int seat) => seat == 0 ? "Red" : "Black";

    private static int? PerpetualChecker(Ending ending) => ending switch
    {
        Ending.RedChecks => 0,
        Ending.BlackChecks => 1,
        _ => null
    };

    protected override GamePosition Project(XiangqiState state, GameMove[] moves)
    {
        var position = state.Restored;
        var cells = position.Board.Select((piece, at) => new GameCell(Ids[at], at / Columns, at % Columns,
            piece == 0 ? null : Owner(piece), piece == 0 ? null : Names[(int)Kind(piece)])).ToArray();
        var last = position.Last is Turn turn ? Move(turn) : null;
        var checkedSeat = position.Quiet[^1].Check;
        var color = Color(position.Seat);
        if (moves.Length > 0)
            return new(Rows, Columns, cells, position.Seat, "playing", [],
                checkedSeat ? $"{color} is in check." : null, moves, last);

        if (position.Ending == Ending.None || !Generate(position.Board, position.Seat).Any())
            return new(Rows, Columns, cells, null, "finished", [1 - position.Seat],
                checkedSeat ? $"Checkmate: {color} loses." : $"Stalemate: {color} loses.", [], last);

        var checker = PerpetualChecker(position.Ending);
        var detail = checker is int loser ? $"{Color(loser)} loses by perpetual check."
            : position.Ending == Ending.Repetition ? "Draw: threefold repetition."
            : "Draw: 60 moves per side without a capture or a soldier advancing.";
        return new(Rows, Columns, cells, null, "finished", checker is int seat ? [1 - seat] : [], detail, [], last);
    }

    protected override Turn Choose(XiangqiState state, Turn[] turns)
    {
        var (board, seat, _, quiet, _) = state.Restored;
        var best = turns[0];
        var bestScore = int.MinValue;
        foreach (var turn in turns)
        {
            var start = MakesProgress(board, turn) ? quiet.Count : 0;
            var captured = Make(board, turn);
            quiet.Add(Mark(board, 1 - seat));
            var ending = Adjudicate(quiet, start, 1 - seat);
            var score = ScoreReplies(board, seat, quiet, start, ending, bestScore);
            quiet.RemoveAt(quiet.Count - 1);
            Undo(board, turn, captured);
            if (score == Win)
                return turn;
            if (score > bestScore)
                (best, bestScore) = (turn, score);
        }
        return best;
    }

    private static int ScoreReplies(byte[] board, int seat, List<Occurrence> quiet, int start, Ending ending, int best)
    {
        var worst = Win;
        foreach (var reply in Generate(board, 1 - seat))
        {
            if (ending != Ending.None)
                return EndScore(ending, seat, Win);

            var nextStart = MakesProgress(board, reply) ? quiet.Count : start;
            var captured = Make(board, reply);
            quiet.Add(Mark(board, seat));
            var end = Adjudicate(quiet, nextStart, seat);
            var score = !Generate(board, seat).Any() ? 1 - Win
                : end != Ending.None ? EndScore(end, seat, Win - 1)
                : Evaluate(board, seat) - (quiet[^1].Check ? 22 : 0);
            quiet.RemoveAt(quiet.Count - 1);
            Undo(board, reply, captured);
            worst = Math.Min(worst, score);
            if (worst <= best)
                break;
        }
        return worst;
    }

    private static int EndScore(Ending ending, int seat, int win) =>
        PerpetualChecker(ending) is int checker ? checker == seat ? -win : win : 0;

    private static int Evaluate(byte[] board, int seat)
    {
        var score = 0;
        for (var at = 0; at < board.Length; at++)
        {
            var piece = board[at];
            if (piece == 0)
                continue;
            var owner = Owner(piece);
            var rank = HomeRow(at / Columns, owner);
            var center = 4 - Math.Abs(at % Columns - 4);
            var value = Kind(piece) switch
            {
                Piece.Chariot => 900 + 3 * center,
                Piece.Cannon => 470 + 5 * center,
                Piece.Horse => 420 + 8 * center + rank,
                Piece.Advisor or Piece.Elephant => 190,
                Piece.Soldier => 100 + 10 * rank + (rank >= 5 ? 50 + 4 * center : 0),
                _ => 0
            };
            score += owner == seat ? value : -value;
        }
        return score;
    }
}
