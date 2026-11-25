using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JadeCourt.Games.Tests;

public sealed class DailyXiangqiTests
{
    private const string Checkmate = "Checkmate: Black loses.";
    private sealed record PlacedPiece(string Cell, int Owner, string Piece);
    private sealed record Puzzle(string Id, PlacedPiece[] Pieces, bool InCheck, string[] Legal, string[] Mate);
    private static readonly Puzzle[] Puzzles = JsonSerializer.Deserialize<Puzzle[]>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "daily-xiangqi.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        })!;

    public static TheoryData<string> PuzzleIds => new(Puzzles.Select(puzzle => puzzle.Id));

    [Theory]
    [MemberData(nameof(PuzzleIds))]
    public void PublishedMovesAndEveryMatingAnswerMatchTheCanonicalEngine(string id)
    {
        var puzzle = Assert.Single(Puzzles, puzzle => puzzle.Id == id);
        var game = new XiangqiGame();
        var checkpoint = Checkpoint(puzzle);
        var opening = game.View(checkpoint);
        Assert.Equal("playing", opening.Status);
        Assert.Equal(0, opening.Turn);
        Assert.Empty(opening.Winners);
        Assert.Equal(puzzle.InCheck ? "Red is in check." : null, opening.Detail);
        Assert.Equal(opening.Moves.Select(move => move.Id), puzzle.Legal);

        var mate = new List<string>();
        foreach (var move in opening.Moves)
        {
            var played = game.Apply(checkpoint, move.Id);
            var next = game.View(played);
            if (next is not { Status: "finished", Detail: Checkmate })
                continue;
            Assert.Equal([0], next.Winners);
            Assert.Null(next.Turn);
            Assert.Empty(next.Moves);
            Assert.Equal(move.Id, next.LastMove?.Id);
            Assert.Throws<ArgumentException>("moveId", () => game.Apply(played, move.Id));
            mate.Add(move.Id);
        }
        Assert.NotEmpty(mate);
        Assert.Equal(mate, puzzle.Mate);
        Assert.True(opening.Moves.Length > mate.Count, "Each tactic must also have a legal non-mating try.");
    }

    [Fact]
    public void OriginalsAndReflectionsAreDistinctAndCarryTheReflectedAnswers()
    {
        Assert.True(Puzzles.Length >= 14);
        Assert.Equal(Puzzles.Length, Puzzles.Select(puzzle => puzzle.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Puzzles.Length, Puzzles.Select(puzzle => string.Join(' ', puzzle.Pieces
            .OrderBy(piece => piece.Cell, StringComparer.Ordinal))).Distinct(StringComparer.Ordinal).Count());
        var originals = Puzzles.Where(puzzle => !puzzle.Id.EndsWith("-reflected", StringComparison.Ordinal)).ToArray();
        Assert.Equal(Puzzles.Length, originals.Length * 2);
        foreach (var original in originals)
        {
            var reflected = Assert.Single(Puzzles, puzzle => puzzle.Id == original.Id + "-reflected");
            Assert.Equal(original.Pieces.Select(piece => piece with { Cell = Reflect(piece.Cell) })
                .OrderBy(piece => piece.Cell, StringComparer.Ordinal), reflected.Pieces.OrderBy(piece => piece.Cell, StringComparer.Ordinal));
            Assert.Equal(original.Mate.Select(move => string.Join('>', move.Split('>').Select(Reflect)))
                .Order(StringComparer.Ordinal), reflected.Mate.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void StalematingBlackIsNotCheckmateEvenThoughRedWins()
    {
        var puzzle = Assert.Single(Puzzles, puzzle => puzzle.Id == "rank-and-file");
        var game = new XiangqiGame();
        const string tryMove = "b9>b8";
        var result = game.View(game.Apply(Checkpoint(puzzle), tryMove));
        Assert.Equal([0], result.Winners);
        Assert.Equal("Stalemate: Black loses.", result.Detail);
        Assert.DoesNotContain(tryMove, puzzle.Mate);
        Assert.Equal(2, puzzle.Mate.Length);
    }

    private static string Reflect(string cell) => (char)('i' - (cell[0] - 'a')) + cell[1..];

    private static string Checkpoint(Puzzle puzzle)
    {
        var board = new byte[90];
        foreach (var piece in puzzle.Pieces)
        {
            var file = piece.Cell[0] - 'a';
            var rank = int.Parse(piece.Cell.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture);
            Assert.InRange(file, 0, 8);
            Assert.InRange(rank, 1, 10);
            Assert.InRange(piece.Owner, 0, 1);
            Assert.Equal(FormattableString.Invariant($"{(char)('a' + file)}{rank}"), piece.Cell);
            var at = (10 - rank) * 9 + file;
            Assert.Equal(0, board[at]);
            var kind = Enum.Parse<XiangqiGame.Piece>(piece.Piece, ignoreCase: true);
            Assert.Equal(kind.ToString().ToLowerInvariant(), piece.Piece);
            board[at] = XiangqiGame.Encode(kind, piece.Owner);
        }
        return CheckpointCodec.Save("xiangqi", new XiangqiState(board, 0, []), 4096);
    }
}
