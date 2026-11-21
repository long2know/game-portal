using System.Numerics;

namespace JadeCourt.Games;

internal sealed record ReversiState(ulong Black, ulong White, int Turn, GameMove? LastMove);

internal sealed class ReversiGame() : BoardGame<ReversiState, ReversiGame.Turn>(
    new("reversi", "Reversi", "Outflank opposing discs and finish with the majority.",
        [2], [new("standard", "Standard")]), 4096)
{
    internal readonly record struct Turn(int Index, ulong Flips);

    private const int Size = 8;
    private const int Pass = -1;
    private static readonly (int Row, int Column)[] Directions =
        [(-1, -1), (-1, 0), (-1, 1), (0, -1), (0, 1), (1, -1), (1, 0), (1, 1)];

    protected override ReversiState InitialState(string variant, int players) =>
        new((1UL << 28) | (1UL << 35), (1UL << 27) | (1UL << 36), 0, null);

    protected override bool IsValid(ReversiState state) =>
        state.Turn is 0 or 1 && (state.Black & state.White) == 0 &&
        BitOperations.PopCount(state.Black | state.White) >= 4;

    protected override IEnumerable<Turn> LegalTurns(ReversiState state)
    {
        var (own, other) = Sides(state);
        var hasPlacement = false;
        foreach (var placement in Placements(own, other))
        {
            hasPlacement = true;
            yield return placement;
        }
        if (!hasPlacement && Placements(other, own).Any())
            yield return new(Pass, 0);
    }

    private static (ulong Own, ulong Other) Sides(ReversiState state) =>
        state.Turn == 0 ? (state.Black, state.White) : (state.White, state.Black);

    private static IEnumerable<Turn> Placements(ulong own, ulong other)
    {
        var empty = ~(own | other);
        while (empty != 0)
        {
            var index = BitOperations.TrailingZeroCount(empty);
            empty &= empty - 1;
            var point = Point.At(index, Size);
            ulong flips = 0;
            foreach (var (dy, dx) in Directions)
            {
                ulong line = 0;
                for (var next = point.Offset(dy, dx, Size); next >= 0;
                    next = Point.At(next, Size).Offset(dy, dx, Size))
                {
                    var bit = 1UL << next;
                    if ((other & bit) != 0)
                    {
                        line |= bit;
                        continue;
                    }
                    if ((own & bit) != 0)
                        flips |= line;
                    break;
                }
            }
            if (flips != 0)
                yield return new(index, flips);
        }
    }

    protected override GameMove Describe(ReversiState state, Turn turn) =>
        turn.Index == Pass ? BoardNotation.Pass : BoardNotation.Place(Point.At(turn.Index, Size));

    protected override ReversiState Advance(ReversiState state, Turn turn, GameMove move)
    {
        var (own, other) = After(Sides(state), turn);
        return state.Turn == 0 ? new(own, other, 1, move) : new(other, own, 0, move);
    }

    private static (ulong Own, ulong Other) After((ulong Own, ulong Other) sides, Turn turn) =>
        turn.Index == Pass ? sides : (sides.Own | turn.Flips | (1UL << turn.Index), sides.Other & ~turn.Flips);

    protected override Turn Choose(ReversiState state, Turn[] turns) => turns.MaxBy(turn =>
    {
        var (own, other) = After(Sides(state), turn);
        var replies = Placements(other, own).ToArray();
        var corners = BitOperations.PopCount(own & 0x8100000000000081UL);
        var material = BitOperations.PopCount(own) - BitOperations.PopCount(other);
        if (replies.Length == 0 && !Placements(own, other).Any())
            return Math.Sign(material) * 10_000 + material;

        var exposedCorners = replies.Count(reply => IsCorner(reply.Index));
        var late = BitOperations.PopCount(own | other) >= 52;
        return 70 * corners - 55 * exposedCorners - 6 * replies.Length + (late ? 4 : 1) * material;
    });

    private static bool IsCorner(int index) => index is 0 or 7 or 56 or 63;

    protected override GamePosition Project(ReversiState state, GameMove[] moves)
    {
        var cells = new GameCell[Size * Size];
        for (var index = 0; index < cells.Length; index++)
        {
            var bit = 1UL << index;
            int? owner = (state.Black & bit) != 0 ? 0 : (state.White & bit) != 0 ? 1 : null;
            cells[index] = Point.At(index, Size).Cell(owner, owner is null ? null : "disc");
        }

        if (moves.Length != 0)
            return new(Size, Size, cells, state.Turn, "playing", [],
                moves[0].Id == BoardNotation.Pass.Id ? "No legal placement. Pass is required." : null,
                moves, state.LastMove);

        var black = BitOperations.PopCount(state.Black);
        var white = BitOperations.PopCount(state.White);
        return new(Size, Size, cells, null, "finished", black == white ? [] : [black > white ? 0 : 1],
            black == white ? "Draw: equal discs." : "Neither player can place another disc.",
            [], state.LastMove, [black, white]);
    }
}
