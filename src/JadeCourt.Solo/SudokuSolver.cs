using System.Numerics;
using System.Security.Cryptography;

namespace JadeCourt.Solo;

internal static class SudokuSolver
{
    public const int Width = 9;
    public const int Cells = Width * Width;
    private const int FullMask = (1 << Width) - 1;
    private const int NodeLimit = 12_000;

    public static int[]? GenerateSolution() => Search(new int[Cells], 1, randomize: true).Solution;

    public static int[]? UniqueSolution(int[] given) =>
        Search(given, 2, randomize: false) is { Count: 1, Capped: false } result ? result.Solution : null;

    public static int Box(int index) => index / (Width * 3) * 3 + index % Width / 3;

    private static SearchResult Search(int[] given, int stopAt, bool randomize)
    {
        var grid = (int[])given.Clone();
        var rows = new int[Width];
        var columns = new int[Width];
        var boxes = new int[Width];
        for (var index = 0; index < Cells; index++)
        {
            if (grid[index] == 0)
                continue;
            var bit = 1 << (grid[index] - 1);
            var row = index / Width;
            var column = index % Width;
            var box = Box(index);
            if (((rows[row] | columns[column] | boxes[box]) & bit) != 0)
                return default;
            rows[row] |= bit;
            columns[column] |= bit;
            boxes[box] |= bit;
        }

        var visited = 0;
        var count = 0;
        var capped = false;
        int[]? solution = null;
        Visit();
        return new SearchResult(count, capped, solution);

        void Visit()
        {
            if (visited++ >= NodeLimit)
            {
                capped = true;
                return;
            }

            var target = -1;
            var choices = 0;
            var best = Width + 1;
            for (var index = 0; index < Cells; index++)
            {
                if (grid[index] != 0)
                    continue;
                var candidates = FullMask & ~(rows[index / Width] | columns[index % Width] | boxes[Box(index)]);
                var remaining = BitOperations.PopCount((uint)candidates);
                if (remaining == 0)
                    return;
                if (remaining >= best)
                    continue;
                (target, choices, best) = (index, candidates, remaining);
                if (best == 1)
                    break;
            }

            if (target < 0)
            {
                count++;
                solution ??= (int[])grid.Clone();
                return;
            }

            var row = target / Width;
            var column = target % Width;
            var box = Box(target);
            while (choices != 0 && count < stopAt && !capped)
            {
                var candidates = choices;
                if (randomize && (choices & (choices - 1)) != 0)
                {
                    var skip = RandomNumberGenerator.GetInt32(BitOperations.PopCount((uint)choices));
                    for (var i = 0; i < skip; i++)
                        candidates &= candidates - 1;
                }

                var bit = candidates & -candidates;
                choices ^= bit;
                grid[target] = BitOperations.TrailingZeroCount((uint)bit) + 1;
                rows[row] |= bit;
                columns[column] |= bit;
                boxes[box] |= bit;
                Visit();
                grid[target] = 0;
                rows[row] ^= bit;
                columns[column] ^= bit;
                boxes[box] ^= bit;
            }
        }
    }

    private readonly record struct SearchResult(int Count, bool Capped, int[]? Solution);
}
