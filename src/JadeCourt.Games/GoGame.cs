namespace JadeCourt.Games;

internal sealed record GoState(int Size, byte[] Board, int Turn, int Passes, string[] Seen, GameMove? LastMove);

internal sealed class GoGame() : BoardGame<GoState, GoGame.Turn>(
    new("go", "Go (Weiqi)", "Surround territory with stones and score by Chinese area counting.",
        [2], [new("9x9", "9×9"), new("19x19", "19×19")]), 560_000)
{
    internal readonly record struct Turn(int At, byte[]? Board, string? Key, int Captured, int Liberties);
    private readonly record struct Group(int Size, int Liberties, int LastLiberty);
    private sealed record Area(double[] Scores, byte[] Owner, int[] RegionSize);

    private const int Pass = -1;
    private const int PositionLimit = 4097;
    private const double Komi = 7.5;
    private const string KomiDetail = "White receives 7.5 komi.";
    private static readonly (int Row, int Column)[] Directions = [(-1, 0), (0, -1), (0, 1), (1, 0)];
    private static readonly Grid Small = new(9);
    private static readonly Grid Large = new(19);

    private sealed class Grid(int size)
    {
        internal int[][] Neighbors { get; } = Enumerable.Range(0, size * size).Select(index =>
            Directions.Select(direction => Point.At(index, size).Offset(direction.Row, direction.Column, size))
                .Where(next => next >= 0).ToArray()).ToArray();

        internal string Empty { get; } = PositionKey(new byte[size * size]);
    }

    private static Grid ForSize(int size) => size == 9 ? Small : Large;

    protected override GoState InitialState(string variant, int players)
    {
        var size = variant == "9x9" ? 9 : 19;
        return new(size, new byte[size * size], 0, 0, [ForSize(size).Empty], null);
    }

    protected override bool IsValid(GoState state)
    {
        if (state.Size is not (9 or 19) || state.Board?.Length != state.Size * state.Size ||
            Array.Exists(state.Board, stone => stone > 2) || state.Turn is not (0 or 1) ||
            state.Passes is < 0 or > 2 || (state.Passes > 0) != (state.LastMove?.Id == BoardNotation.Pass.Id) ||
            state.Seen?.Length is not (>= 1 and <= PositionLimit) ||
            state.Seen[0] != ForSize(state.Size).Empty || state.Seen[^1] != PositionKey(state.Board))
            return false;

        var unique = new HashSet<string>(StringComparer.Ordinal);
        Span<byte> packed = stackalloc byte[(state.Board.Length + 3) / 4];
        foreach (var key in state.Seen)
        {
            if (key?.Length != ForSize(state.Size).Empty.Length ||
                !Convert.TryFromBase64String(key, packed, out var written) ||
                written != packed.Length || packed[^1] > 2 || !unique.Add(key))
                return false;
            foreach (var value in packed)
                if ((value & (value >> 1) & 0x55) != 0)
                    return false;
        }
        return true;
    }

    private static string PositionKey(byte[] board)
    {
        // Two bits per point give exact superko equality without a hash collision or a large JSON board history.
        Span<byte> packed = stackalloc byte[(board.Length + 3) / 4];
        packed.Clear();
        for (var point = 0; point < board.Length; point++)
            packed[point / 4] |= (byte)(board[point] << (2 * (point % 4)));
        return Convert.ToBase64String(packed);
    }

    protected override IEnumerable<Turn> LegalTurns(GoState state)
    {
        if (state.Passes == 2)
            yield break;
        if (state.Seen.Length < PositionLimit)
        {
            var seen = state.Seen.ToHashSet(StringComparer.Ordinal);
            var grid = ForSize(state.Size);
            for (var at = 0; at < state.Board.Length; at++)
            {
                if (state.Board[at] != 0)
                    continue;
                if (TryPlay(state, grid, at) is Turn play && !seen.Contains(play.Key!))
                    yield return play;
            }
        }
        yield return new(Pass, null, null, 0, 0);
    }

    private static Turn? TryPlay(GoState state, Grid grid, int at)
    {
        var own = (byte)(state.Turn + 1);
        var opponent = (byte)(3 - own);
        var board = (byte[])state.Board.Clone();
        board[at] = own;
        Span<int> stones = stackalloc int[board.Length];
        Span<bool> visited = stackalloc bool[board.Length];
        var captured = 0;
        foreach (var neighbor in grid.Neighbors[at])
        {
            if (board[neighbor] != opponent)
                continue;
            var group = ReadGroup(board, grid, neighbor, stones, visited);
            if (group.Liberties != 0)
                continue;
            captured += group.Size;
            foreach (var stone in stones[..group.Size])
                board[stone] = 0;
        }

        var liberties = ReadGroup(board, grid, at, stones, visited).Liberties;
        return liberties == 0 ? null : new(at, board, PositionKey(board), captured, liberties);
    }

    private static Group ReadGroup(byte[] board, Grid grid, int from, Span<int> stones, Span<bool> visited)
    {
        visited.Clear();
        var color = board[from];
        var size = 1;
        var liberties = 0;
        var lastLiberty = -1;
        stones[0] = from;
        visited[from] = true;
        for (var i = 0; i < size; i++)
            foreach (var neighbor in grid.Neighbors[stones[i]])
            {
                if (visited[neighbor])
                    continue;
                visited[neighbor] = true;
                if (board[neighbor] == color)
                    stones[size++] = neighbor;
                else if (board[neighbor] == 0)
                {
                    liberties++;
                    lastLiberty = neighbor;
                }
            }
        return new(size, liberties, lastLiberty);
    }

    protected override GameMove Describe(GoState state, Turn turn)
    {
        if (turn.At == Pass)
            return BoardNotation.Pass;
        var point = Point.At(turn.At, state.Size);
        var file = (char)('A' + point.Column + (point.Column >= 8 ? 1 : 0));
        return BoardNotation.Place(point, FormattableString.Invariant($"{file}{state.Size - point.Row}"));
    }

    protected override GoState Advance(GoState state, Turn turn, GameMove move) => turn.At == Pass
        ? state with { Turn = 1 - state.Turn, Passes = state.Passes + 1, LastMove = move }
        : new(state.Size, turn.Board!, 1 - state.Turn, 0, [.. state.Seen, turn.Key!], move);

    protected override Turn Choose(GoState state, Turn[] turns)
    {
        var grid = ForSize(state.Size);
        var area = CountArea(state.Board, grid);
        var distressed = AtariStones(state.Board, grid, (byte)(state.Turn + 1));
        var occupied = state.Board.Count(stone => stone != 0);
        return turns.MaxBy(turn =>
        {
            if (turn.At == Pass)
                return 0;
            var point = Point.At(turn.At, state.Size);
            var depth = Math.Min(Math.Min(point.Row, point.Column),
                state.Size - 1 - Math.Max(point.Row, point.Column));
            var shape = depth switch { 0 => -6, 1 => 1, 2 => 7, 3 => 8, _ => 4 };
            if (occupied < 12 && OpeningPoint(point, state.Size))
                shape += 15;

            var friendly = 0;
            var hostile = 0;
            foreach (var neighbor in grid.Neighbors[turn.At])
            {
                if (state.Board[neighbor] == state.Turn + 1)
                    friendly++;
                else if (state.Board[neighbor] != 0)
                    hostile++;
            }

            var pocket = area.Owner[turn.At] != 0 && area.RegionSize[turn.At] <= 8;
            var shapePenalty = pocket ? 20 : 0;
            if (turn.Liberties == 1)
                shapePenalty += 42;
            if (friendly == grid.Neighbors[turn.At].Length)
                shapePenalty += 45;
            var rescue = turn.Liberties >= 2 ? 15 * Math.Min(6, distressed[turn.At]) : 0;
            return 65 * turn.Captured + rescue + 2 * friendly + 4 * hostile +
                   Math.Min(turn.Liberties, 5) + shape - shapePenalty;
        });
    }

    private static bool OpeningPoint(Point point, int size)
    {
        if (size == 9)
            return (point.Row is 2 or 6 && point.Column is 2 or 6) || point is { Row: 4, Column: 4 };
        return point.Row is 3 or 9 or 15 && point.Column is 3 or 9 or 15;
    }

    private static int[] AtariStones(byte[] board, Grid grid, byte color)
    {
        var checkedStones = new bool[board.Length];
        var threatened = new int[board.Length];
        Span<int> stones = stackalloc int[board.Length];
        Span<bool> visited = stackalloc bool[board.Length];
        for (var point = 0; point < board.Length; point++)
        {
            if (board[point] != color || checkedStones[point])
                continue;
            var group = ReadGroup(board, grid, point, stones, visited);
            foreach (var stone in stones[..group.Size])
                checkedStones[stone] = true;
            if (group.Liberties == 1)
                threatened[group.LastLiberty] += group.Size;
        }
        return threatened;
    }

    private static Area CountArea(byte[] board, Grid grid)
    {
        var visited = new bool[board.Length];
        var owner = new byte[board.Length];
        var regionSize = new int[board.Length];
        var scores = new double[] { 0, Komi };
        Span<int> region = stackalloc int[board.Length];
        for (var point = 0; point < board.Length; point++)
        {
            if (board[point] != 0)
            {
                scores[board[point] - 1]++;
                continue;
            }
            if (visited[point])
                continue;

            var size = 1;
            byte border = 0;
            region[0] = point;
            visited[point] = true;
            for (var i = 0; i < size; i++)
                foreach (var neighbor in grid.Neighbors[region[i]])
                {
                    if (board[neighbor] != 0)
                        border |= board[neighbor];
                    else if (!visited[neighbor])
                    {
                        visited[neighbor] = true;
                        region[size++] = neighbor;
                    }
                }
            if (border is not (1 or 2))
                continue;
            scores[border - 1] += size;
            foreach (var empty in region[..size])
            {
                owner[empty] = border;
                regionSize[empty] = size;
            }
        }
        return new(scores, owner, regionSize);
    }

    protected override GamePosition Project(GoState state, GameMove[] moves)
    {
        var cells = state.Board.Select((stone, index) =>
            Point.At(index, state.Size).Cell(stone == 0 ? null : stone - 1, stone == 0 ? null : "stone")).ToArray();
        if (state.Passes != 2)
        {
            var detail = state.Seen.Length == PositionLimit ? $"Placement limit reached; pass is required. {KomiDetail}"
                : state.Passes == 1 ? $"One pass; a second consecutive pass ends the game. {KomiDetail}" : KomiDetail;
            return new(state.Size, state.Size, cells, state.Turn, "playing", [], detail, moves, state.LastMove);
        }

        var scores = CountArea(state.Board, ForSize(state.Size)).Scores;
        return new(state.Size, state.Size, cells, null, "finished", [scores[0] > scores[1] ? 0 : 1],
            $"Chinese area scoring. {KomiDetail}", [], state.LastMove, scores);
    }
}
