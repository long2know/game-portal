using System.Globalization;
using static JadeCourt.Games.XiangqiGame.Piece;

namespace JadeCourt.Games.Tests;

public sealed class XiangqiTests
{
    private readonly IBoardGame game = new XiangqiGame();

    [Fact]
    public void OpeningUsesAlgebraicCellsAndMatchesTheReferenceMoveTree()
    {
        var checkpoint = game.Start("standard", 2);
        var opening = game.View(checkpoint);
        Assert.Equal(new GameCell("a10", 0, 0, 1, "chariot"), opening.Cells[0]);
        Assert.Equal(new GameCell("i1", 9, 8, 0, "chariot"), opening.Cells[^1]);
        Assert.Equal(new GameCell("e10", 0, 4, 1, "general"), Cell(opening, "e10"));
        Assert.Equal(new GameCell("e1", 9, 4, 0, "general"), Cell(opening, "e1"));
        Assert.Equal("cannon", Cell(opening, "b3").Piece);
        Assert.Equal("soldier", Cell(opening, "i7").Piece);
        Assert.Equal(16, opening.Cells.Count(cell => cell.Owner == 0));
        Assert.Equal(16, opening.Cells.Count(cell => cell.Owner == 1));
        Assert.Contains(new GameMove("b1>c3", "b1 → c3", "b1", "c3"), opening.Moves);
        Assert.Equal(44, opening.Moves.Length);
        // Chess Programming Wiki, Chinese Chess Perft Results, position 1.
        Assert.Equal(1_920, opening.Moves.Sum(move => game.View(game.Apply(checkpoint, move.Id)).Moves.Length));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PalaceRiverHorseLegAndElephantEyeConstrainBothColors(int seat)
    {
        var checkpoint = Position(seat, (Mirror("d3"), seat, General), (Mirror("e10"), 1 - seat, General),
            (Mirror("f3"), seat, Advisor), (Mirror("c5"), seat, Elephant), (Mirror("d4"), seat, Cannon),
            (Mirror("b1"), seat, Horse), (Mirror("b2"), seat, Chariot), (Mirror("e4"), seat, Soldier),
            (Mirror("a6"), seat, Soldier));
        var view = game.View(checkpoint);
        AssertMoves("d3", "d2", "e3");
        AssertMoves("f3", "e2");
        AssertMoves("c5", "a3");
        AssertMoves("b1", "d2");
        AssertMoves("e4", "e5");
        AssertMoves("a6", "a7", "b6");
        Assert.Contains(view.Moves, move => move.From == Mirror("b2") && move.To == Mirror("a2"));
        Assert.DoesNotContain(view.Moves, move => move.From == Mirror("b2") && move.To == Mirror("b1"));
        Assert.Throws<ArgumentException>("moveId", () => game.Apply(checkpoint, $"{Mirror("c5")}>{Mirror("e3")}"));

        string Mirror(string cell) => seat == 0 ? cell
            : FormattableString.Invariant($"{cell[0]}{11 - Rank(cell)}");
        void AssertMoves(string from, params string[] to) =>
            Assert.Equal(to.Select(Mirror).Order(StringComparer.Ordinal),
                view.Moves.Where(move => move.From == Mirror(from)).Select(move => move.To).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CannonCapturesOverOneScreenOfEitherColorButDoesNotJumpToAnEmptyPoint(int screenSeat)
    {
        var checkpoint = Position(0, ("e1", 0, General), ("f10", 1, General), ("a4", 0, Cannon),
            ("c4", screenSeat, Soldier), ("e4", 1, Horse), ("g4", 1, Chariot), ("a7", 1, Horse));
        var opening = game.View(checkpoint);
        Assert.Contains(opening.Moves, move => move.Id == "a4>b4");
        foreach (var illegal in new[] { "a4>c4", "a4>d4", "a4>g4", "a4>a7" })
        {
            Assert.DoesNotContain(opening.Moves, move => move.Id == illegal);
            Assert.Throws<ArgumentException>("moveId", () => game.Apply(checkpoint, illegal));
        }

        var next = new XiangqiGame().View(game.Apply(checkpoint, "a4>e4"));
        Assert.Equal(1, next.Turn);
        Assert.Equal("a4>e4", next.LastMove?.Id);
        Assert.Null(Cell(next, "a4").Piece);
        Assert.Equal((0, "cannon"), (Cell(next, "e4").Owner, Cell(next, "e4").Piece));
        Assert.Equal((screenSeat, "soldier"), (Cell(next, "c4").Owner, Cell(next, "c4").Piece));
    }

    [Fact]
    public void GeneralsCannotFaceAndACannonCheckCanBeBrokenByAddingOrRemovingAScreen()
    {
        var pinned = Position(0, ("e1", 0, General), ("e10", 1, General), ("e5", 0, Horse));
        Assert.DoesNotContain(game.View(pinned).Moves, move => move.From == "e5");
        Assert.Throws<ArgumentException>("moveId", () => game.Apply(pinned, "e5>d7"));
        Assert.Contains(game.View(pinned).Moves, move => move.Id == "e1>e2");
        Assert.DoesNotContain(game.View(Position(0, ("d1", 0, General), ("e10", 1, General))).Moves,
            move => move.Id == "d1>e1");

        var check = Position(0, ("e1", 0, General), ("d10", 1, General), ("e8", 1, Cannon),
            ("e4", 0, Horse), ("a2", 0, Chariot));
        Assert.Equal("Red is in check.", game.View(check).Detail);
        Assert.DoesNotContain(game.View(check).Moves, move => move.Id is "a2>a3" or "e1>e2");
        Assert.Throws<ArgumentException>("moveId", () => game.Apply(check, "a2>a3"));
        foreach (var escape in new[] { "e4>c3", "a2>e2" })
        {
            var next = game.View(game.Apply(check, escape));
            Assert.Equal(1, next.Turn);
            Assert.Null(next.Detail);
        }
    }

    [Theory]
    [InlineData(true, "f9>f10", "Checkmate: Black loses.")]
    [InlineData(false, "a8>a9", "Stalemate: Black loses.")]
    public void CheckmateAndStalemateBothLoseAndTheBotTakesAnImmediateWin(bool mate, string move, string detail)
    {
        var checkpoint = Position(0, ("d1", 0, General), ("e10", 1, General), ("g10", 0, Soldier),
            (mate ? "f9" : "d8", 0, Chariot), (mate ? "a9" : "a8", 0, Chariot));
        var bot = game.SelectBotMove(checkpoint);
        Assert.Contains(game.View(checkpoint).Moves, option => option.Id == bot);
        Assert.Equal(new[] { 0 }, game.View(game.Apply(checkpoint, bot)).Winners);

        var saved = game.Apply(checkpoint, move);
        var final = new XiangqiGame().View(saved);
        Assert.Equal("finished", final.Status);
        Assert.Equal(detail, final.Detail);
        Assert.Equal(new[] { 0 }, final.Winners);
        Assert.Equal(move, final.LastMove?.Id);
        Assert.NotNull(Cell(final, "e10").Owner);
        Assert.Empty(final.Moves);
        Assert.Null(final.Turn);
        Assert.Throws<InvalidOperationException>(() => game.SelectBotMove(saved));
        Assert.Throws<ArgumentException>("moveId", () => game.Apply(saved, "e10>e9"));
    }

    [Fact]
    public void BotAvoidsAnOpposingMateInOneWhenItCanDefend()
    {
        var checkpoint = Position(1, ("f1", 0, General), ("e10", 1, General), ("g10", 0, Soldier),
            ("f9", 0, Chariot), ("a9", 0, Chariot), ("a8", 1, Chariot));
        var careless = game.View(Play(checkpoint, "a8>b8", "f9>f10"));
        Assert.Equal("Checkmate: Black loses.", careless.Detail);

        var defended = game.Apply(checkpoint, game.SelectBotMove(checkpoint));
        var next = game.View(defended);
        Assert.Equal(0, next.Turn);
        Assert.DoesNotContain(next.Moves,
            reply => game.View(game.Apply(defended, reply.Id)).Winners.Contains(0));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyUnbrokenOneSidedCheckingAcrossBothRepeatsLoses(bool perpetual)
    {
        var checkpoint = Position(0, ("d1", 0, General), ("e10", 1, General), ("f8", 0, Chariot));
        checkpoint = Play(checkpoint, "f8>e8", "e10>f10", "e8>f8", "f10>e10");
        Assert.Equal("playing", game.View(checkpoint).Status);
        checkpoint = Play(checkpoint, perpetual ? "f8>e8" : "f8>h8", "e10>f10",
            perpetual ? "e8>f8" : "h8>f8", "f10>e10");

        var final = new XiangqiGame().View(checkpoint);
        Assert.Equal("finished", final.Status);
        Assert.Null(final.Turn);
        Assert.Empty(final.Moves);
        Assert.Equal(perpetual ? new[] { 1 } : [], final.Winners);
        Assert.Equal(perpetual ? "Red loses by perpetual check." : "Draw: threefold repetition.", final.Detail);
        var state = CheckpointCodec.Load<XiangqiState>("xiangqi", checkpoint, 4096);
        Assert.Throws<InvalidDataException>(() => game.View(Save(state with { Trail = [.. state.Trail, .. Trail("f8", "e8")] })));
    }

    [Fact]
    public void SixtyQuietMovesPerSideDrawButForwardSoldierProgressResetsTheCount()
    {
        string[] red = ["a2", "b2", "b3", "c3", "c4", "b4", "a4", "a3"];
        string[] black = ["g6", "h6", "i6", "i7", "i8", "i9", "h9", "g9", "g8", "g7"];
        var checkpoint = Position(0, ("d1", 0, General), ("f10", 1, General), ("a2", 0, Chariot),
            ("g6", 1, Chariot), ("e6", 1, Soldier), ("c5", 1, Soldier));
        for (var n = 0; n < 60; n++)
        {
            checkpoint = game.Apply(checkpoint, $"{red[n % red.Length]}>{red[(n + 1) % red.Length]}");
            if (n == 59)
            {
                Assert.Equal("playing", game.View(checkpoint).Status);
                var progressed = new XiangqiGame().View(game.Apply(checkpoint, "e6>e5"));
                Assert.Equal("playing", progressed.Status);
                Assert.Equal(0, progressed.Turn);
                Assert.Equal("e6>e5", progressed.LastMove?.Id);

                var sideways = game.View(game.Apply(checkpoint, "c5>d5"));
                Assert.Equal("finished", sideways.Status);
                Assert.Empty(sideways.Winners);
            }
            checkpoint = game.Apply(checkpoint, $"{black[n % black.Length]}>{black[(n + 1) % black.Length]}");
        }
        var final = new XiangqiGame().View(checkpoint);
        Assert.Equal("finished", final.Status);
        Assert.Empty(final.Winners);
        Assert.Contains("60 moves per side", final.Detail);
    }

    [Fact]
    public void BadAnchorsAndTrailsFailWithoutInventingMovesOrResults()
    {
        Assert.Throws<ArgumentException>("variant", () => game.Start("freestyle", 2));
        Assert.Throws<ArgumentException>("players", () => game.Start("standard", 3));
        Assert.Throws<InvalidDataException>(() => game.View(new GomokuGame().Start("freestyle", 2)));
        var opening = CheckpointCodec.Load<XiangqiState>("xiangqi", game.Start("standard", 2), 4096);
        Assert.Throws<InvalidDataException>(() => game.View(Save(opening with { Anchor = new byte[89] })));
        Assert.Throws<InvalidDataException>(() => game.View(Position(0, ("e1", 0, General), ("e10", 1, General))));
        Assert.Throws<InvalidDataException>(() => game.View(Position(0, ("d1", 0, General), ("e10", 1, General),
            ("c6", 0, Elephant))));
        Assert.Throws<InvalidDataException>(() => game.View(Position(0, ("d1", 0, General), ("e10", 1, General),
            ("e7", 0, Chariot))));
        Assert.Throws<InvalidDataException>(() => game.Apply(Save(opening with { Trail = Trail("b1", "b3") }), "a4>a5"));
        Assert.Throws<InvalidDataException>(() => game.SelectBotMove(Save(opening with { Trail = [255, 0] })));
        Assert.Throws<InvalidDataException>(() => game.View(Save(opening with { Trail = [0] })));
    }

    private string Play(string checkpoint, params string[] moves) => moves.Aggregate(checkpoint, game.Apply);

    private static GameCell Cell(GamePosition view, string id) => Assert.Single(view.Cells, cell => cell.Id == id);
    private static int Rank(string id) => int.Parse(id.AsSpan(1), CultureInfo.InvariantCulture);
    private static int Index(string id) => (10 - Rank(id)) * 9 + id[0] - 'a';
    private static byte[] Trail(string from, string to) => [(byte)Index(from), (byte)Index(to)];

    private static string Position(int seat, params (string Cell, int Owner, XiangqiGame.Piece Piece)[] pieces)
    {
        var board = new byte[90];
        foreach (var piece in pieces)
            board[Index(piece.Cell)] = XiangqiGame.Encode(piece.Piece, piece.Owner);
        return Save(new(board, seat, []));
    }

    private static string Save(XiangqiState state) => CheckpointCodec.Save("xiangqi", state, 4096);
}
