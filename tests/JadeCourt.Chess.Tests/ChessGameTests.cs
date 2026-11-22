using JadeCourt.Games;

namespace JadeCourt.Chess.Tests;

public sealed class ChessGameTests
{
    private readonly IBoardGame game = new ChessGame();

    [Fact]
    public void OpeningAndRecoveryExposeGenericSquaresTurnsAndMoves()
    {
        Assert.Equal("chess", game.Definition.Id);
        Assert.Equal([2], game.Definition.PlayerCounts);
        Assert.Equal("standard", Assert.Single(game.Definition.Variants).Id);
        Assert.Throws<ArgumentException>(() => game.Start("chess960", 2));
        Assert.Throws<ArgumentException>(() => game.Start("standard", 4));

        var opening = game.Start("standard", 2);
        var start = game.View(opening);
        Assert.Equal((8, 8, 0, "playing"), (start.Rows, start.Columns, start.Turn, start.Status));
        Assert.Equal(64, start.Cells.Length);
        Assert.Equal(new GameCell("a8", 0, 0, 1, "rook"), start.Cells[0]);
        Assert.Equal(new GameCell("h1", 7, 7, 0, "rook"), start.Cells[63]);
        Assert.Equal(20, start.Moves.Length);
        Assert.Null(start.LastMove);
        var played = Assert.Single(start.Moves, m => m.Id == "e2e4");
        Assert.Equal(("e2", "e4", null), (played.From, played.To, played.Promotion));

        var restored = new ChessGame().View(game.Apply(opening, played.Id));
        Assert.Equal(1, restored.Turn);
        Assert.Equal(played, restored.LastMove);
        Assert.Equal(0, At(restored, "e4").Owner);
        Assert.Equal("pawn", At(restored, "e4").Piece);
        Assert.Null(At(restored, "e2").Piece);
        Assert.Equal("pawn", At(game.View(opening), "e2").Piece);
    }

    [Fact]
    public void IllegalMovesAndForgedHistoriesAreRejectedIncludingIgnoringCheck()
    {
        var opening = game.Start("standard", 2);
        Assert.Throws<ArgumentException>(() => game.Apply(opening, "e2e5"));
        Assert.Throws<ArgumentException>(() => game.Apply(opening, "e7e5"));
        Assert.Throws<ArgumentException>(() => game.View(opening + " e2e5"));
        Assert.Throws<ArgumentException>(() => game.View(opening + " e2e4\nquit"));

        var checkedPosition = Play("e2e4 d7d5 f1b5");
        var view = game.View(checkedPosition);
        Assert.Equal(1, view.Turn);
        Assert.Equal("Black is in check.", view.Detail);
        Assert.Contains(view.Moves, m => m.Id == "c7c6");
        Assert.DoesNotContain(view.Moves, m => m.Id == "a7a6");
        Assert.Throws<ArgumentException>(() => game.Apply(checkedPosition, "a7a6"));
    }

    [Fact]
    public void PromotionRequiresChoosingAmongFourNamedMoves()
    {
        var before = Play("a2a4 h7h5 a4a5 h5h4 a5a6 h4h3 a6b7 h3g2");
        var options = game.View(before).Moves.Where(m => m.From == "b7" && m.To == "a8")
            .Select(m => (m.Id, m.Promotion)).ToArray();
        Assert.Equal([("b7a8b", "bishop"), ("b7a8n", "knight"), ("b7a8q", "queen"), ("b7a8r", "rook")], options);
        Assert.Throws<ArgumentException>(() => game.Apply(before, "b7a8"));
        Assert.Throws<ArgumentException>(() => game.Apply(before, "b7a8N"));

        var view = new ChessGame().View(game.Apply(before, "b7a8n"));
        Assert.Equal((0, "knight"), (At(view, "a8").Owner, At(view, "a8").Piece));
        Assert.Null(At(view, "b7").Piece);
        Assert.Equal(("b7a8n", "knight"), (view.LastMove?.Id, view.LastMove?.Promotion));
    }

    [Fact]
    public void EnPassantSurvivesRecoveryThenExpiresAndRemovesThePassedPawn()
    {
        var before = Play("e2e4 a7a6 e4e5 d7d5");
        var recovered = new ChessGame();
        Assert.Contains(recovered.View(before).Moves, m => m.Id == "e5d6");
        var captured = recovered.View(recovered.Apply(before, "e5d6"));
        Assert.Null(At(captured, "d5").Piece);
        Assert.Equal((0, "pawn"), (At(captured, "d6").Owner, At(captured, "d6").Piece));

        var expired = game.Apply(game.Apply(before, "g1f3"), "g8f6");
        Assert.DoesNotContain(game.View(expired).Moves, m => m.Id == "e5d6");
    }

    [Fact]
    public void CastlingUsesKingCoordinatesAndCannotPassThroughAnAttack()
    {
        var before = Play("e2e4 e7e5 g1f3 b8c6 f1c4 g8f6");
        var recovered = new ChessGame();
        Assert.Contains(recovered.View(before).Moves, m => m.Id == "e1g1");
        Assert.DoesNotContain(recovered.View(before).Moves, m => m.Id == "e1h1");
        var castled = recovered.View(recovered.Apply(before, "e1g1"));
        Assert.Equal("king", At(castled, "g1").Piece);
        Assert.Equal("rook", At(castled, "f1").Piece);
        Assert.Null(At(castled, "h1").Piece);

        var attackedTransit = Play("e2e4 d7d5 g1f3 c8g4 f1c4 g4h3 g2g3 a7a6");
        Assert.Null(game.View(attackedTransit).Detail);
        Assert.DoesNotContain(game.View(attackedTransit).Moves, m => m.Id == "e1g1");
    }

    [Fact]
    public void AReplacementRookDoesNotRestoreCastlingRights()
    {
        var replacement = Play(
            "e2e4 d7d5 f1c4 c8g4 h2h4 g4h3 g1e2 h3g2 a2a4 g2h1 a1a3 a7a6 a3h3 a6a5 h3h1 b7b6");
        var recovered = new ChessGame();
        var view = recovered.View(replacement);
        var rook = At(view, "h1");
        Assert.Equal((0, "rook"), (rook.Owner, rook.Piece));
        Assert.DoesNotContain(view.Moves, m => m.Id == "e1g1");
        Assert.Throws<ArgumentException>(() => recovered.Apply(replacement, "e1g1"));
    }

    [Fact]
    public void CheckmateStopsPlayAndReportsTheWinningSeat()
    {
        var mate = Play("f2f3 e7e5 g2g4 d8h4");
        AssertFinished(game.View(mate), "Black wins by checkmate.", [1]);
        Assert.Equal("d8h4", game.View(mate).LastMove?.Id);
        Assert.Throws<ArgumentException>(() => game.Apply(mate, "e1f2"));
        Assert.Throws<ArgumentException>(() => game.SelectBotMove(mate));
        Assert.Throws<ArgumentException>(() => new ChessGame().SelectBotMove(mate, "easy"));
        Assert.Throws<ArgumentException>(() => new ChessGame().SelectBotMove(mate, "hard"));
    }

    [Fact]
    public void StalemateHasNoWinner()
    {
        var stalemate = Play(
            "e2e3 a7a5 d1h5 a8a6 h5a5 h7h5 a5c7 a6h6 h2h4 f7f6 c7d7 e8f7 d7b7 d8d3 b7b8 d3h7 b8c8 f7g6 c8e6");
        AssertFinished(game.View(stalemate), "Draw by stalemate.", []);
    }

    [Fact]
    public void RepetitionCountsNonconsecutivePositionsAcrossDifferentLengthCycles()
    {
        var twice = Play("e2e4 e7e5 g1f3 g8f6 f3g1 f6g8");
        Assert.Equal("playing", game.View(twice).Status);

        var threeTimes = Play("g1f3 g8f6 f3d4 f6d5 d4e2 d5e7 e2g1 e7g8", twice);
        var recovered = new ChessGame();
        AssertFinished(recovered.View(threeTimes), "Draw by threefold repetition.", []);
        Assert.Throws<ArgumentException>(() => recovered.Apply(threeTimes, "g1f3"));
        Assert.Throws<ArgumentException>(() => recovered.View(threeTimes + " g1f3"));
    }

    [Fact]
    public void RepetitionDistinguishesOnlyAnEnPassantCaptureThatIsActuallyLegal()
    {
        const string cycle = "g1f3 g8f6 f3g1 f6g8 g1f3 g8f6 f3g1 f6g8";
        var legal = Play("e2e4 a7a6 e4e5 d7d5");
        Assert.Contains(game.View(legal).Moves, m => m.Id == "e5d6");
        Assert.Equal("playing", game.View(Play(cycle, legal)).Status);

        var pinned = Play("d2d4 e7e5 d4e5 d7d5 e2e3 d5d4 e3d4 d8e7 b1c3 f7f5");
        Assert.DoesNotContain(game.View(pinned).Moves, m => m.Id == "e5f6");
        Assert.Throws<ArgumentException>(() => game.Apply(pinned, "e5f6"));
        AssertFinished(game.View(Play(cycle, pinned)), "Draw by threefold repetition.", []);
    }

    [Fact]
    public void FiftyMoveDrawStartsAtOneHundredConsecutiveQuietPlies()
    {
        var before = Play("""
            e2e4 e7e5 d1h5 g8e7 h5h3 e7d5 f1a6 d8f6 h3g3 h8g8 a6b5 f6f3 g3h3 f3a3 b5a4 a3c3 g1f3
            f8a3 f3h4 c3a5 h4f3 a5b5 h3g3 e8f8 g3h3 b5a6 f3g5 a3c5 g5e6 f8e7 h3f5 c5d4 f5g4 a6d3
            e6c5 g8e8 g4g3 e8d8 a4b5 d3b3 g3c3 b3b4 c3b3 d4e3 e1e2 b4c4 c5d3 d5f4 e2f1 c4e6 b1a3
            e6a6 b3e6 e7f8 b5c6 a6a5 h1g1 f4d5 d3c5 f8g8 c6a4 d5e7 f1e2 e7c6 e2d1 a5b5 c5b3 c6b4
            e6c4 b5a5 c4b5 b4c6 b5e2 a5a6 g1f1 c6e7 e2b5 e3h6 b5c6 d8e8 c6f6 a6d6 f6g6 e8f8 g6f5
            e7g6 f5f6 d6b4 f1g1 b4d6 a4c6 f8e8 b3c5 h6e3 c5d3 g6h4 f6g6 g8f8 c6b5 d6c5 d3e1
            """);
        Assert.Equal("playing", game.View(before).Status);
        AssertFinished(game.View(game.Apply(before, "b8a6")), "Draw by the fifty-move rule.", []);
    }

    [Fact]
    public void TakingTheLastMaterialThatCanMateFinishesTheGame()
    {
        var before = Play("""
            g2g4 b7b6 h2h4 c7c6 f2f3 g7g5 h4g5 c8a6 h1h7 a6e2 h7h8 e2d1 e1d1 d7d5 h8g8 b6b5
            f1b5 c6b5 g8f8 e8f8 g1h3 f8e8 a2a4 b5a4 a1a4 b8c6 a4a7 c6a7 b2b4 a8b8 b1c3 b8b4
            c3d5 d8d5 f3f4 d5g5 h3g5 b4f4 g5f7 e8f7 d1e2 f4g4 e2f1 g4g5 d2d3 f7e8 f1e1 e7e5
            c1g5 e8f7 d3d4 e5d4 g5e3 d4e3 e1e2 a7c8 e2e3 f7e7 c2c4 c8b6 e3f3
            """);
        Assert.Equal("playing", game.View(before).Status);
        var result = game.View(game.Apply(before, "b6c4"));
        AssertFinished(result, "Draw by insufficient material.", []);
        Assert.Equal(["knight"], result.Cells.Where(c => c.Piece is not null and not "king").Select(c => c.Piece));
    }

    private string Play(string moves, string? checkpoint = null) => moves
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Aggregate(checkpoint ?? game.Start("standard", 2), game.Apply);

    private static GameCell At(GamePosition position, string id) => Assert.Single(position.Cells, c => c.Id == id);

    private static void AssertFinished(GamePosition position, string detail, int[] winners)
    {
        Assert.Equal("finished", position.Status);
        Assert.Null(position.Turn);
        Assert.Equal(detail, position.Detail);
        Assert.Equal(winners, position.Winners);
        Assert.Empty(position.Moves);
    }
}
