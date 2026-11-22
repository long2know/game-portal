using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JadeCourt.Solo.Tests;

public sealed class SudokuTests
{
    private const string Prefix = "sudoku-v1:";
    private readonly SudokuGame game = new();

    [Theory]
    [InlineData("easy", 45)]
    [InlineData("standard", 37)]
    [InlineData("hard", 31)]
    public void StartsFreshSymmetricPuzzlesWithExactlyOneSolution(string variant, int clues)
    {
        var first = game.View(game.Start(variant));
        var second = game.View(game.Start(variant));
        Assert.False(first.Given.SequenceEqual(second.Given));
        foreach (var puzzle in new[] { first, second })
        {
            Assert.False(puzzle.Finished);
            Assert.Equal(0, puzzle.Moves);
            Assert.Equal(81, puzzle.Given.Length);
            Assert.Equal(clues, puzzle.Given.Count(value => value != 0));
            Assert.All(puzzle.Given, value => Assert.InRange(value, 0, 9));
            Assert.Equal(puzzle.Given, puzzle.Values);
            Assert.Empty(puzzle.Conflicts);
            Assert.Equal(puzzle.Given.Select(value => value == 0), puzzle.Given.Reverse().Select(value => value == 0));
            Assert.Single(FindSolutions(puzzle.Given));
        }

        var wire = JsonSerializer.SerializeToElement(first);
        Assert.Equal(new[] { "Conflicts", "Finished", "Given", "Moves", "Values" },
            wire.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(1)] // Row only.
    [InlineData(2)] // Column only.
    [InlineData(4)] // Box only.
    public void ConflictingGuessesAreRecordedAndCanBeCleared(int relationship)
    {
        var checkpoint = game.Start("standard");
        var given = game.View(checkpoint).Given;
        var (cell, peer) = (from empty in Enumerable.Range(0, 81)
                            where given[empty] == 0
                            from filled in Enumerable.Range(0, 81)
                            where given[filled] != 0 && Relationship(empty, filled) == relationship
                            select (empty, filled)).First();

        var guess = given[peer];
        var entered = game.Apply(checkpoint, new("set", Index: cell, Value: guess));
        var view = game.View(entered);
        Assert.Equal(guess, view.Values[cell]);
        Assert.Equal(0, view.Given[cell]);
        Assert.Equal(1, view.Moves);
        Assert.Equal(Enumerable.Range(0, 81)
            .Where(index => index == cell || given[index] == guess && Relationship(index, cell) != 0),
            view.Conflicts);

        var cleared = game.View(game.Apply(entered, new("set", Index: cell, Value: 0)));
        Assert.Equal(given, cleared.Values);
        Assert.Empty(cleared.Conflicts);
        Assert.Equal(2, cleared.Moves);
        Assert.Equal(given, game.View(checkpoint).Values);
    }

    [Fact]
    public void GivensAndInvalidCommandsDoNotAdvanceSudoku()
    {
        Assert.Throws<ArgumentException>(() => game.Start("unknown"));
        var checkpoint = game.Start("easy");
        var before = game.View(checkpoint);
        var clue = Array.FindIndex(before.Given, value => value != 0);
        var blank = Array.IndexOf(before.Given, 0);
        foreach (var action in new SoloAction[]
        {
            new("set", Index: clue, Value: 0), new("hint", Index: clue),
            new("set", Index: 81, Value: 1), new("set", Index: -1, Value: 1),
            new("set", Index: blank, Value: 10), new("set", Index: blank, Value: -1),
            new("set", Index: blank), new("set", Index: blank, Value: 0),
            new("hint", Index: -1), new("hint", Value: 9), new("draw"),
        })
            Assert.Throws<InvalidOperationException>(() => game.Apply(checkpoint, action));

        Assert.Equal(0, game.View(checkpoint).Moves);
        Assert.Equal(before.Values, game.View(checkpoint).Values);
    }

    [Fact]
    public void HintsChangeOneCellAndResumedSudokuFinishesOnlyWhenSolved()
    {
        var checkpoint = game.Start("standard");
        var before = game.View(checkpoint);
        var solution = Assert.Single(FindSolutions(before.Given));
        var blanks = Enumerable.Range(0, 81).Where(index => before.Given[index] == 0).ToArray();
        var wrong = blanks[^1];
        checkpoint = game.Apply(checkpoint, new("set", Index: wrong, Value: solution[wrong] % 9 + 1));
        var guessed = game.View(checkpoint);
        var afterDefaultHint = game.Apply(checkpoint, new("hint"));
        Assert.Equal(new[] { blanks[0] }, Changed(guessed.Values, game.View(afterDefaultHint).Values));
        Assert.Equal(solution[blanks[0]], game.View(afterDefaultHint).Values[blanks[0]]);

        var resumed = new SudokuGame();
        checkpoint = resumed.Apply(afterDefaultHint, new("hint", Index: wrong));
        Assert.Equal(game.Apply(afterDefaultHint, new("hint", Index: wrong)), checkpoint);
        var hinted = resumed.View(checkpoint);
        Assert.Equal(new[] { wrong }, Changed(game.View(afterDefaultHint).Values, hinted.Values));
        Assert.Equal(solution[wrong], hinted.Values[wrong]);
        Assert.Equal(3, hinted.Moves);
        Assert.Equal(before.Given, hinted.Given);

        var last = blanks[^2];
        foreach (var index in blanks[1..^2])
            checkpoint = resumed.Apply(checkpoint, new("set", Index: index, Value: solution[index]));

        checkpoint = resumed.Apply(checkpoint, new("set", Index: last, Value: solution[last] % 9 + 1));
        var fullButWrong = resumed.View(checkpoint);
        Assert.All(fullButWrong.Values, value => Assert.InRange(value, 1, 9));
        Assert.Contains(last, fullButWrong.Conflicts);
        Assert.False(fullButWrong.Finished);

        checkpoint = resumed.Apply(checkpoint, new("set", Index: last, Value: solution[last]));
        var finished = game.View(checkpoint);
        Assert.Equal(solution, finished.Values);
        Assert.Equal(blanks.Length + 2, finished.Moves);
        Assert.Empty(finished.Conflicts);
        Assert.True(finished.Finished);
        Assert.Throws<InvalidOperationException>(() => game.Apply(checkpoint, new("set", Index: last, Value: 0)));
    }

    [Fact]
    public void MalformedVersionsChangedCluesAndAmbiguousPuzzlesAreRejected()
    {
        var checkpoint = game.Start("hard");
        var wrongVersion = "sudoku-v9:" + checkpoint[Prefix.Length..];
        Assert.Contains("version", Assert.Throws<InvalidDataException>(() => game.View(wrongVersion)).Message);

        var changed = JsonNode.Parse(checkpoint[Prefix.Length..])!.AsObject();
        var clue = Array.FindIndex(game.View(checkpoint).Given, value => value != 0);
        changed["Values"]![clue] = 0;

        var complete = Enumerable.Range(0, 81).Select(index =>
            (index / 9 * 3 + index / 27 + index % 9) % 9 + 1).ToArray();
        var ambiguous = new int[81];
        ambiguous[40] = complete[40];
        foreach (var first in Enumerable.Range(0, 40).Where(index => complete[index] > 2 && complete[80 - index] > 2).Take(15))
        {
            ambiguous[first] = complete[first];
            ambiguous[80 - first] = complete[80 - first];
        }
        Assert.Equal(31, ambiguous.Count(value => value != 0));
        Assert.Equal(2, FindSolutions(ambiguous).Count);
        var ambiguousCheckpoint = Prefix + JsonSerializer.Serialize(new
        {
            Variant = "hard", Moves = 0, Given = ambiguous, Values = ambiguous,
        });

        foreach (var corrupt in new[] { wrongVersion, Prefix + "{}", Prefix + changed.ToJsonString(), ambiguousCheckpoint })
        {
            Assert.Throws<InvalidDataException>(() => game.View(corrupt));
            Assert.Throws<InvalidDataException>(() => game.Apply(corrupt, new("hint")));
        }
    }

    private static IEnumerable<int> Changed(int[] before, int[] after) =>
        Enumerable.Range(0, 81).Where(index => before[index] != after[index]);

    private static int Relationship(int left, int right)
    {
        var row = left / 9 == right / 9;
        var column = left % 9 == right % 9;
        var box = left / 27 == right / 27 && left % 9 / 3 == right % 9 / 3;
        return (row ? 1 : 0) | (column ? 2 : 0) | (box ? 4 : 0);
    }

    private static List<int[]> FindSolutions(int[] clues)
    {
        var grid = (int[])clues.Clone();
        var found = new List<int[]>();
        var visits = 0;
        Search();
        return found;

        void Search()
        {
            Assert.True(++visits <= 100_000, "Independent Sudoku check exceeded its search budget.");
            var next = -1;
            var best = 10;
            var allowed = 0;
            for (var index = 80; index >= 0; index--)
            {
                if (grid[index] != 0)
                    continue;

                var row = index / 9;
                var column = index % 9;
                var corner = 27 * (row / 3) + 3 * (column / 3);
                var blocked = 0;
                for (var offset = 0; offset < 9; offset++)
                    blocked |= 1 << grid[row * 9 + offset] | 1 << grid[offset * 9 + column]
                        | 1 << grid[corner + 9 * (offset / 3) + offset % 3];
                var options = ~blocked & 0x3fe;
                var size = BitOperations.PopCount((uint)options);
                if (size == 0)
                    return;
                if (size < best)
                    (next, best, allowed) = (index, size, options);
            }

            if (next == -1)
            {
                found.Add((int[])grid.Clone());
                return;
            }

            for (var digit = 9; digit >= 1 && found.Count < 2; digit--)
            {
                if ((allowed & (1 << digit)) == 0)
                    continue;
                grid[next] = digit;
                Search();
                grid[next] = 0;
            }
        }
    }
}
