using System.Security.Cryptography;

namespace JadeCourt.Solo;

public sealed class SudokuGame
{
    private const string Name = "sudoku";
    private const int Width = SudokuSolver.Width;
    private const int Cells = SudokuSolver.Cells;
    private const int Center = Cells / 2;
    private static readonly int[] FallbackPairs = [1, 3, 10, 11, 15, 16, 17, 22, 26, 27, 28, 29, 32, 33, 39];

    public string Start(string variant)
    {
        var clueCount = ClueCount(variant);
        if (clueCount == 0)
            throw new ArgumentException("Sudoku variant must be easy, standard, or hard.", nameof(variant));

        var given = Generate(clueCount);
        return SoloCheckpoint.Write(Name, new State(variant, 0, given, (int[])given.Clone()));
    }

    public string Apply(string checkpoint, SoloAction action)
    {
        var (state, solution) = Read(checkpoint);
        if (state.Values.AsSpan().SequenceEqual(solution))
            throw new InvalidOperationException("This Sudoku game is finished.");
        if (state.Moves == int.MaxValue)
            throw new InvalidOperationException("This Sudoku game cannot record another action.");

        int index;
        int value;
        switch (action?.Kind)
        {
            case "set":
                if (action is not
                    { Index: >= 0 and < Cells, Value: >= 0 and <= Width, From: null, To: null, Count: null })
                    throw new InvalidOperationException("Set requires an index from 0 to 80 and a value from 0 to 9.");
                index = action.Index.Value;
                value = action.Value.Value;
                break;
            case "hint":
                if (action is not { Value: null, From: null, To: null, Count: null }
                    || action.Index is < 0 or >= Cells)
                    throw new InvalidOperationException("Hint takes only an optional index from 0 to 80.");
                index = action.Index ?? FirstHintCell(state, solution);
                value = solution[index];
                break;
            default:
                throw new InvalidOperationException("Sudoku action must be set or hint.");
        }

        if (state.Given[index] != 0)
            throw new InvalidOperationException("Sudoku clues cannot be changed.");
        if (state.Values[index] == value)
            throw new InvalidOperationException("That Sudoku cell would not change.");

        state.Values[index] = value;
        return SoloCheckpoint.Write(Name, state with { Moves = state.Moves + 1 });
    }

    public SudokuPosition View(string checkpoint)
    {
        var (state, solution) = Read(checkpoint);
        return new SudokuPosition(state.Values.AsSpan().SequenceEqual(solution), state.Moves,
            state.Given, state.Values, Conflicts(state.Values));
    }

    private static int[] Generate(int target)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var solution = SudokuSolver.GenerateSolution();
            if (solution is null)
                continue;

            var given = (int[])solution.Clone();
            var pairs = Enumerable.Range(0, Center).ToArray();
            RandomNumberGenerator.Shuffle(pairs.AsSpan());
            var remaining = Cells;
            foreach (var first in pairs)
            {
                var last = Cells - 1 - first;
                given[first] = given[last] = 0;
                if (SudokuSolver.UniqueSolution(given) is null)
                {
                    given[first] = solution[first];
                    given[last] = solution[last];
                }
                else if ((remaining -= 2) == target)
                {
                    return given;
                }
            }
        }

        return Fallback(target);
    }

    private static int[] Fallback(int target)
    {
        // This symmetric mask on the base grid is unique; adding clue pairs preserves uniqueness.
        var given = new int[Cells];
        var extras = Enumerable.Range(0, Center).Except(FallbackPairs).ToArray();
        var digits = Enumerable.Range(1, Width).ToArray();
        RandomNumberGenerator.Shuffle(extras.AsSpan());
        RandomNumberGenerator.Shuffle(digits.AsSpan());
        var symmetry = RandomNumberGenerator.GetInt32(8);

        Add(Center);
        foreach (var first in FallbackPairs.Concat(extras.Take((target - 1) / 2 - FallbackPairs.Length)))
        {
            Add(first);
            Add(Cells - 1 - first);
        }
        return given;

        void Add(int index)
        {
            var row = index / Width;
            var column = index % Width;
            var value = digits[(row * 3 + row / 3 + column) % Width];
            if ((symmetry & 1) != 0)
                (row, column) = (column, row);
            if ((symmetry & 2) != 0)
                row = Width - 1 - row;
            if ((symmetry & 4) != 0)
                column = Width - 1 - column;
            given[row * Width + column] = value;
        }
    }

    private static int FirstHintCell(State state, int[] solution)
    {
        for (var index = 0; index < Cells; index++)
            if (state.Given[index] == 0 && state.Values[index] != solution[index])
                return index;
        throw new InvalidOperationException("There is no Sudoku cell left to hint.");
    }

    private static int[] Conflicts(int[] values)
    {
        var conflicts = new bool[Cells];
        for (var first = 0; first < Cells; first++)
        {
            if (values[first] == 0)
                continue;
            for (var last = first + 1; last < Cells; last++)
                if (values[first] == values[last] && (first / Width == last / Width
                    || first % Width == last % Width || SudokuSolver.Box(first) == SudokuSolver.Box(last)))
                    conflicts[first] = conflicts[last] = true;
        }
        return Enumerable.Range(0, Cells).Where(index => conflicts[index]).ToArray();
    }

    private static (State, int[] Solution) Read(string checkpoint)
    {
        var state = SoloCheckpoint.Read<State>(Name, checkpoint);
        var expected = ClueCount(state.Variant);
        if (state.Moves < 0 || expected == 0 || state.Given is not { Length: Cells }
            || state.Values is not { Length: Cells })
            throw SoloCheckpoint.Invalid(Name);

        var clues = 0;
        for (var index = 0; index < Cells; index++)
        {
            var clue = state.Given[index];
            var value = state.Values[index];
            if (clue is < 0 or > Width || value is < 0 or > Width || (clue != 0 && clue != value)
                || (clue == 0) != (state.Given[Cells - 1 - index] == 0))
                throw SoloCheckpoint.Invalid(Name);
            if (clue != 0)
                clues++;
        }

        if (clues != expected || SudokuSolver.UniqueSolution(state.Given) is not { } solution)
            throw SoloCheckpoint.Invalid(Name);
        return (state, solution);
    }

    private static int ClueCount(string variant) => variant switch
    {
        "easy" => 45,
        "standard" => 37,
        "hard" => 31,
        _ => 0,
    };

    private sealed record State(string Variant, int Moves, int[] Given, int[] Values);
}
