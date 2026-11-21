namespace JadeCourt.Games;

internal sealed record ChineseCheckersState(
    byte[] Board, int Players, int Turn, int Passes, int[] LastOrigins, GameMove? LastMove);

internal sealed class ChineseCheckersGame() : BoardGame<ChineseCheckersState, ChineseCheckersGame.Turn>(
    new("chinese-checkers", "Chinese Checkers", "Race ten pegs to the opposite point of the star.",
        [2, 3, 4, 6], [new("standard", "Standard")]), 8192)
{
    internal sealed record Turn(int From, int[] Landings)
    {
        internal int To => Landings[^1];
    }

    private readonly record struct Hole(Point Point, int Camp);
    private readonly record struct Edge(int Step, int Hop);

    private const int Rows = 17;
    private const int Columns = 25;
    private const int CampSize = 10;
    private const int Center = -1;
    private const int Pass = -1;

    private static readonly (int Row, int Column)[] Directions =
        [(-1, -1), (-1, 1), (0, -2), (0, 2), (1, -1), (1, 1)];

    private static readonly Hole[] Holes = MakeHoles();
    private static readonly int[][] Camps = Enumerable.Range(0, 6)
        .Select(camp => Enumerable.Range(0, Holes.Length).Where(index => Holes[index].Camp == camp).ToArray())
        .ToArray();
    private static readonly Edge[][] Edges = MakeEdges();
    private static readonly int[,] Distances = MakeDistances();
    private static readonly int[] Tips = Camps.Select(camp =>
        camp.MaxBy(hole => Distance(Holes[hole].Point, new(8, 12)))).ToArray();

    private static Hole[] MakeHoles()
    {
        var holes = new List<Hole>(121);
        for (var row = 0; row < Rows; row++)
        {
            var halfWidth = row < 4 ? row : row > 12 ? 16 - row : 8 + Math.Abs(row - 8);
            for (var column = 12 - halfWidth; column <= 12 + halfWidth; column += 2)
            {
                var camp = row < 4 ? 0 : row > 12 ? 3
                    : Math.Abs(column - 12) <= 8 - Math.Abs(row - 8) ? Center
                    : row < 8 ? (column > 12 ? 1 : 5) : (column > 12 ? 2 : 4);
                holes.Add(new(new(row, column), camp));
            }
        }
        return [.. holes];
    }

    private static Edge[][] MakeEdges()
    {
        var index = Holes.Select((hole, number) => (hole.Point, number))
            .ToDictionary(pair => pair.Point, pair => pair.number);
        return Holes.Select(hole =>
        {
            var (row, column) = hole.Point;
            return Directions.Select(direction => new Edge(
                    index.GetValueOrDefault(new(row + direction.Row, column + direction.Column), -1),
                    index.GetValueOrDefault(new(row + 2 * direction.Row, column + 2 * direction.Column), -1)))
                .Where(edge => edge.Step >= 0).ToArray();
        }).ToArray();
    }

    private static int[,] MakeDistances()
    {
        var distances = new int[Holes.Length, Holes.Length];
        for (var from = 0; from < Holes.Length; from++)
            for (var to = 0; to < Holes.Length; to++)
                distances[from, to] = Distance(Holes[from].Point, Holes[to].Point);
        return distances;
    }

    private static int Distance(Point from, Point to)
    {
        var rows = Math.Abs(from.Row - to.Row);
        var columns = Math.Abs(from.Column - to.Column);
        return rows + Math.Max(0, columns - rows) / 2;
    }

    private static int StartCamp(int players, int seat) => players switch
    {
        2 => 3 * seat,
        3 => 2 * seat,
        4 => seat + seat / 2,
        6 => seat,
        _ => throw new InvalidOperationException("Invalid Chinese Checkers seat configuration.")
    };

    private static int GoalCamp(int startCamp) => (startCamp + 3) % 6;

    protected override ChineseCheckersState InitialState(string variant, int players)
    {
        var board = new byte[Holes.Length];
        for (var seat = 0; seat < players; seat++)
            foreach (var hole in Camps[StartCamp(players, seat)])
                board[hole] = (byte)(seat + 1);
        return new(board, players, 0, 0, Enumerable.Repeat(-1, players).ToArray(), null);
    }

    protected override bool IsValid(ChineseCheckersState state)
    {
        if (state.Players is not (2 or 3 or 4 or 6) || state.Turn < 0 || state.Turn >= state.Players ||
            state.Passes < 0 || state.Passes > state.Players || state.Board?.Length != Holes.Length ||
            state.LastOrigins?.Length != state.Players ||
            Array.Exists(state.LastOrigins, origin => origin < -1 || origin >= Holes.Length))
            return false;

        Span<int> counts = stackalloc int[7];
        counts.Clear();
        foreach (var peg in state.Board)
        {
            if (peg > state.Players)
                return false;
            counts[peg]++;
        }
        for (var seat = 1; seat <= state.Players; seat++)
            if (counts[seat] != CampSize)
                return false;
        return true;
    }

    private static int? Winner(ChineseCheckersState state)
    {
        for (var seat = 0; seat < state.Players; seat++)
            if (Camps[GoalCamp(StartCamp(state.Players, seat))].All(hole => state.Board[hole] == seat + 1))
                return seat;
        return null;
    }

    protected override IEnumerable<Turn> LegalTurns(ChineseCheckersState state)
    {
        if (state.Passes == state.Players || Winner(state) is not null)
            yield break;
        var start = StartCamp(state.Players, state.Turn);
        var goal = GoalCamp(start);
        var found = false;
        for (var from = 0; from < Holes.Length; from++)
        {
            if (state.Board[from] != state.Turn + 1)
                continue;
            foreach (var (step, _) in Edges[from])
                if (state.Board[step] == 0 && CanEnter(from, step, goal) && CanRest(step, start, goal))
                {
                    found = true;
                    yield return new(from, [step]);
                }
            foreach (var jump in Jumps(state.Board, from, start, goal))
            {
                found = true;
                yield return jump;
            }
        }
        if (!found)
            yield return new(Pass, []);
    }

    private static bool CanEnter(int from, int to, int goal) => Holes[from].Camp != goal || Holes[to].Camp == goal;
    private static bool CanRest(int at, int start, int goal) => Holes[at].Camp == Center ||
        Holes[at].Camp == start || Holes[at].Camp == goal;

    private static IEnumerable<Turn> Jumps(byte[] board, int from, int start, int goal)
    {
        // Lifting the moving peg leaves a fixed hop graph; one shortest route per endpoint suffices.
        var previous = new int[Holes.Length];
        Array.Fill(previous, -1);
        previous[from] = from;
        var frontier = new Queue<int>();
        frontier.Enqueue(from);
        while (frontier.TryDequeue(out var at))
        {
            foreach (var (step, hop) in Edges[at])
            {
                if (hop < 0 || previous[hop] >= 0 || step == from || board[step] == 0 ||
                    board[hop] != 0 || !CanEnter(at, hop, goal))
                    continue;

                previous[hop] = at;
                frontier.Enqueue(hop);
                if (!CanRest(hop, start, goal))
                    continue;
                var landings = new List<int>();
                for (var point = hop; point != from; point = previous[point])
                    landings.Add(point);
                landings.Reverse();
                yield return new(from, [.. landings]);
            }
        }
    }

    protected override GameMove Describe(ChineseCheckersState state, Turn turn) => turn.From == Pass
        ? BoardNotation.Pass
        : BoardNotation.Travel(Holes[turn.From].Point, turn.Landings.Select(hole => Holes[hole].Point).ToArray());

    protected override ChineseCheckersState Advance(ChineseCheckersState state, Turn turn, GameMove move)
    {
        var next = (state.Turn + 1) % state.Players;
        if (turn.From == Pass)
            return state with { Turn = next, Passes = state.Passes + 1, LastMove = move };

        var board = (byte[])state.Board.Clone();
        board[turn.From] = 0;
        board[turn.To] = (byte)(state.Turn + 1);
        var origins = (int[])state.LastOrigins.Clone();
        origins[state.Turn] = turn.From;
        return new(board, state.Players, next, 0, origins, move);
    }

    protected override Turn Choose(ChineseCheckersState state, Turn[] turns)
    {
        var goal = GoalCamp(StartCamp(state.Players, state.Turn));
        var pegs = Enumerable.Range(0, Holes.Length).Where(hole => state.Board[hole] == state.Turn + 1).ToArray();
        return turns.MaxBy(turn =>
        {
            var index = Array.IndexOf(pegs, turn.From);
            pegs[index] = turn.To;
            var cost = AssignmentDistance(pegs, Camps[goal]);
            pegs[index] = turn.From;

            var progress = Distances[turn.From, Tips[goal]] - Distances[turn.To, Tips[goal]];
            var entered = Holes[turn.From].Camp != goal && Holes[turn.To].Camp == goal;
            var backtrack = turn.To == state.LastOrigins[state.Turn];
            return -100 * cost + 8 * progress + (entered ? 40 : 0) - (backtrack ? 35 : 0);
        })!;
    }

    private static int AssignmentDistance(int[] pegs, int[] goals)
    {
        // Minimum-cost matching keeps the bot from sending every peg to the same near corner.
        Span<int> pegPotential = stackalloc int[CampSize + 1];
        Span<int> goalPotential = stackalloc int[CampSize + 1];
        Span<int> matched = stackalloc int[CampSize + 1];
        Span<int> parent = stackalloc int[CampSize + 1];
        Span<int> reduced = stackalloc int[CampSize + 1];
        Span<bool> visited = stackalloc bool[CampSize + 1];
        pegPotential.Clear();
        goalPotential.Clear();
        matched.Clear();

        for (var peg = 1; peg <= CampSize; peg++)
        {
            matched[0] = peg;
            reduced.Fill(int.MaxValue);
            visited.Clear();
            var goal = 0;
            do
            {
                visited[goal] = true;
                var searchPeg = matched[goal];
                var cheapest = int.MaxValue;
                var next = 0;
                for (var option = 1; option <= CampSize; option++)
                {
                    if (visited[option])
                        continue;
                    var cost = Distances[pegs[searchPeg - 1], goals[option - 1]] -
                               pegPotential[searchPeg] - goalPotential[option];
                    if (cost < reduced[option])
                    {
                        reduced[option] = cost;
                        parent[option] = goal;
                    }
                    if (reduced[option] < cheapest)
                    {
                        cheapest = reduced[option];
                        next = option;
                    }
                }
                for (var option = 0; option <= CampSize; option++)
                    if (visited[option])
                    {
                        pegPotential[matched[option]] += cheapest;
                        goalPotential[option] -= cheapest;
                    }
                    else
                        reduced[option] -= cheapest;
                goal = next;
            } while (matched[goal] != 0);

            while (goal != 0)
            {
                var prior = parent[goal];
                matched[goal] = matched[prior];
                goal = prior;
            }
        }
        return -goalPotential[0];
    }

    protected override GamePosition Project(ChineseCheckersState state, GameMove[] moves)
    {
        var cells = Holes.Select((hole, index) =>
        {
            var peg = state.Board[index];
            return hole.Point.Cell(peg == 0 ? null : peg - 1, peg == 0 ? null : "peg");
        }).ToArray();

        if (moves.Length != 0)
            return new(Rows, Columns, cells, state.Turn, "playing", [],
                moves[0].Id == BoardNotation.Pass.Id ? "No legal peg move. Pass is required." : null,
                moves, state.LastMove);

        var winner = Winner(state);
        return new(Rows, Columns, cells, null, "finished", winner is int seat ? [seat] : [],
            winner is null ? "Draw: no player can move." : "The winning player filled their target camp.",
            [], state.LastMove);
    }
}
