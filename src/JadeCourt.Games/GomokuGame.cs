namespace JadeCourt.Games;

internal sealed record GomokuState(byte[] Placements)
{
    internal byte[] Board { get; set; } = null!;
    internal bool Won { get; set; }
}

internal sealed class GomokuGame() : BoardGame<GomokuState, int>(
    new("gomoku", "Gomoku", "Place stones to make an unbroken line of five or more.",
        [2], [new("freestyle", "Freestyle 15×15")]), 512)
{
    private const int Size = 15;
    private const int CellCount = Size * Size;
    private const int LineLength = 5;
    private static readonly (int Row, int Column)[] Axes = [(0, 1), (1, 0), (1, 1), (1, -1)];
    private static readonly int[] LineValues = [0, 1, 4, 18, 100];
    private static readonly int[][][] Lines = Enumerable.Range(0, CellCount).Select(LinesThrough).ToArray();

    protected override GomokuState InitialState(string variant, int players) => new([]);

    protected override bool IsValid(GomokuState state)
    {
        if (state.Placements is null || state.Placements.Length > CellCount)
            return false;

        var board = new byte[CellCount];
        var won = false;
        for (var ply = 0; ply < state.Placements.Length; ply++)
        {
            var at = state.Placements[ply];
            if (won || at >= CellCount || board[at] != 0)
                return false;
            var color = (byte)(1 + (ply & 1));
            board[at] = color;
            won = CompletesLine(board, at, color);
        }

        state.Board = board;
        state.Won = won;
        return true;
    }

    protected override IEnumerable<int> LegalTurns(GomokuState state) => state.Won
        ? [] : Enumerable.Range(0, CellCount).Where(at => state.Board[at] == 0);

    protected override GameMove Describe(GomokuState state, int turn) => Placement(turn);

    private static GameMove Placement(int at)
    {
        var point = Point.At(at, Size);
        var file = (char)('A' + point.Column + (point.Column >= 8 ? 1 : 0));
        return BoardNotation.Place(point, FormattableString.Invariant($"{file}{Size - point.Row}"));
    }

    protected override GomokuState Advance(GomokuState state, int turn, GameMove move) =>
        new([.. state.Placements, (byte)turn]);

    private static int[][] LinesThrough(int at)
    {
        var point = Point.At(at, Size);
        var result = new List<int[]>();
        foreach (var (dy, dx) in Axes)
            for (var offset = 0; offset < LineLength; offset++)
            {
                var start = point.Offset(-offset * dy, -offset * dx, Size);
                var end = point.Offset((LineLength - 1 - offset) * dy, (LineLength - 1 - offset) * dx, Size);
                if (start >= 0 && end >= 0)
                    result.Add(Enumerable.Range(0, LineLength).Select(i => start + i * (dy * Size + dx)).ToArray());
            }
        return [.. result];
    }

    private static bool CompletesLine(byte[] board, int at, byte color)
    {
        foreach (var line in Lines[at])
        {
            var complete = true;
            foreach (var point in line)
                if (point != at && board[point] != color)
                {
                    complete = false;
                    break;
                }
            if (complete)
                return true;
        }
        return false;
    }

    protected override int Choose(GomokuState state, int[] turns)
    {
        var own = (byte)(1 + (state.Placements.Length & 1));
        var opponent = (byte)(3 - own);
        var blocks = new List<int>();
        foreach (var at in turns)
        {
            if (CompletesLine(state.Board, at, own))
                return at;
            if (CompletesLine(state.Board, at, opponent))
                blocks.Add(at);
        }

        var candidates = blocks.Count == 0 ? (IEnumerable<int>)turns : blocks;
        return candidates.MaxBy(at =>
        {
            var attack = Shape(state.Board, at, own);
            var defence = Shape(state.Board, at, opponent);
            var point = Point.At(at, Size);
            var distance = Math.Abs(point.Row - Size / 2) + Math.Abs(point.Column - Size / 2);
            return (attack.Fork ? 100_000 : 0) + (defence.Fork ? 10_000 : 0)
                   + 4 * attack.Value + 5 * defence.Value - distance;
        });
    }

    private static (int Value, bool Fork) Shape(byte[] board, int at, byte color)
    {
        var value = 0;
        var firstFinish = -1;
        var fork = false;
        foreach (var line in Lines[at])
        {
            var stones = 1;
            var gap = -1;
            foreach (var point in line)
            {
                if (point == at)
                    continue;
                if (board[point] == color)
                    stones++;
                else if (board[point] == 0)
                    gap = point;
                else
                {
                    stones = 0;
                    break;
                }
            }

            if (stones == 0 || gap < 0)
                continue;
            value += LineValues[stones];
            if (stones == LineLength - 1)
            {
                if (firstFinish < 0)
                    firstFinish = gap;
                else if (gap != firstFinish)
                    fork = true;
            }
        }
        return (value, fork);
    }

    protected override GamePosition Project(GomokuState state, GameMove[] moves)
    {
        var cells = state.Board.Select((stone, at) =>
            Point.At(at, Size).Cell(stone == 0 ? null : stone - 1, stone == 0 ? null : "stone")).ToArray();
        var turn = state.Placements.Length & 1;
        var last = state.Placements.Length == 0 ? null : Placement(state.Placements[^1]);
        if (moves.Length != 0)
            return new(Size, Size, cells, turn, "playing", [], null, moves, last);
        return new(Size, Size, cells, null, "finished", state.Won ? [1 - turn] : [],
            state.Won ? "Five or more in a row." : "Draw: the board is full.", [], last);
    }
}
