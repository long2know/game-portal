using System.Text.Json;
using System.Text.Json.Nodes;

namespace JadeCourt.Solo.Tests;

public sealed class KlondikeTests
{
    private const string Prefix = "klondike-v1:";
    private static readonly SoloAction Draw = new("draw");
    private readonly KlondikeGame game = new();

    [Fact]
    public void DealProjectsOnlyFaceUpCardsAndStockCount()
    {
        var view = game.View(game.Start("draw-one"));

        Assert.False(view.Finished);
        Assert.Equal(0, view.Moves);
        Assert.Equal(24, view.StockCount);
        Assert.Equal(0, view.WasteCount);
        Assert.Null(view.Waste);
        Assert.Equal(new SolitaireCard?[4], view.Foundations);
        Assert.Equal(Enumerable.Range(1, 7), view.Tableau.Select(column => column.Length));
        foreach (var column in view.Tableau)
        {
            Assert.All(column[..^1], card => Assert.Equal(new SolitaireCard(null, null, false), card));
            Assert.True(column[^1].FaceUp);
            Assert.InRange(column[^1].Rank!.Value, 1, 13);
            Assert.Contains(column[^1].Suit, new[] { "clubs", "diamonds", "hearts", "spades" });
        }

        var wire = JsonSerializer.SerializeToElement(view);
        Assert.Equal(new[]
        {
            "Finished", "Foundations", "LegalMoves", "Moves", "StockCount", "Tableau", "Waste", "WasteCount",
        }, wire.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("Tableau")[6][0].GetProperty("Rank").ValueKind);
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("Tableau")[6][0].GetProperty("Suit").ValueKind);
    }

    [Fact]
    public void DrawsOneAtATimeAndRecyclesInOrderAcrossEngineInstances()
    {
        var initial = game.Start("draw-one");
        var checkpoint = initial;
        var wasteOrder = new List<SolitaireCard>();
        for (var index = 0; index < 24; index++)
        {
            checkpoint = game.Apply(checkpoint, Draw);
            var position = game.View(checkpoint);
            Assert.Equal(23 - index, position.StockCount);
            Assert.Equal(index + 1, position.WasteCount);
            Assert.Equal(index + 1, position.Moves);
            wasteOrder.Add(Assert.IsType<SolitaireCard>(position.Waste));
        }
        Assert.Equal(24, wasteOrder.Distinct().Count());
        Assert.Empty(wasteOrder.Intersect(game.View(initial).Tableau.Select(column => column[^1])));

        var resumed = new KlondikeGame();
        checkpoint = resumed.Apply(checkpoint, Draw);
        var recycled = resumed.View(checkpoint);
        Assert.Equal(24, recycled.StockCount);
        Assert.Equal(0, recycled.WasteCount);
        Assert.Null(recycled.Waste);
        Assert.Equal(25, recycled.Moves);
        foreach (var card in wasteOrder)
        {
            var next = resumed.Apply(checkpoint, Draw);
            Assert.Equal(game.Apply(checkpoint, Draw), next);
            Assert.Equal(card, resumed.View(next).Waste);
            checkpoint = next;
        }
    }

    [Fact]
    public void OnlyAnAlternatingDescendingFaceUpTailMovesAndExposesTheCoveredTop()
    {
        var checkpoint = Arrange([5, 5, 5, 5],
        [
            [Card(0, 10)], [Card(1, 13), Card(2, 9), Card(3, 8)], [Card(1, 10)], [Card(3, 11)],
            [], [Card(3, 13)], [Card(1, 12)],
        ], covered: [0, 1, 0, 0, 0, 0, 0]);

        foreach (var invalid in new KlondikeMove[]
        {
            new("t1", "t4", 3), // Includes a covered King.
            new("t1", "t2", 2), // Same color.
            new("t1", "t3", 2), // Skips a rank.
            new("t1", "t4", 2), // Non-King into an empty column.
            new("t1", "t0", 0),
        })
        {
            Assert.DoesNotContain(invalid, game.View(checkpoint).LegalMoves);
            Assert.Throws<InvalidOperationException>(() => game.Apply(checkpoint, Action(invalid)));
        }

        var legal = new KlondikeMove("t1", "t0", 2);
        Assert.Contains(legal, game.View(checkpoint).LegalMoves);
        var moved = game.Apply(checkpoint, Action(legal));
        var view = game.View(moved);
        Assert.Equal(new SolitaireCard[]
        {
            new(10, "clubs", true), new(9, "hearts", true), new(8, "spades", true),
        }, view.Tableau[0]);
        var revealed = Assert.Single(view.Tableau[1]);
        Assert.Equal(new SolitaireCard(13, "diamonds", true), revealed);
        Assert.Equal(1, view.Moves);
        Assert.Equal(new SolitaireCard(null, null, false), game.View(checkpoint).Tableau[1][0]);

        moved = game.Apply(moved, new("move", "t6", "t5", 1));
        moved = game.Apply(moved, new("move", "t5", "t4", 2));
        var kingTail = game.View(moved);
        Assert.Empty(kingTail.Tableau[5]);
        Assert.Equal(new SolitaireCard[] { new(13, "spades", true), new(12, "diamonds", true) }, kingTail.Tableau[4]);
    }

    [Fact]
    public void WasteOnlyOffersItsTopAndFoundationsStartAtAceWithoutRankWrap()
    {
        var checkpoint = Arrange([0, 12, 12, 0],
        [
            [Card(3, 2)], [Card(0, 2)], [Card(2, 13)], [Card(1, 13)], [Card(3, 13)], [], [],
        ], waste: [Card(0, 1), Card(3, 1)]);

        Assert.Equal(new SolitaireCard(1, "spades", true), game.View(checkpoint).Waste);
        Assert.Throws<InvalidOperationException>(() => game.Apply(checkpoint, new("move", "waste", "f0", 1)));
        Assert.Throws<InvalidOperationException>(() => game.Apply(checkpoint, new("move", "waste", "f3", 2)));
        Assert.Throws<InvalidOperationException>(() => game.Apply(checkpoint, new("move", "waste", "t5", 1)));
        Assert.Throws<InvalidOperationException>(() => game.Apply(checkpoint, new("move", "waste", "t2", 1)));
        Assert.Throws<InvalidOperationException>(() => game.Apply(checkpoint, new("move", "t0", "f3", 1)));

        checkpoint = game.Apply(checkpoint, new("move", "waste", "f3", 1));
        var ace = game.View(checkpoint);
        Assert.Equal(1, ace.WasteCount);
        Assert.Equal(new SolitaireCard(1, "clubs", true), ace.Waste);
        Assert.Equal(new SolitaireCard(1, "spades", true), ace.Foundations[3]);
        checkpoint = game.Apply(checkpoint, new("move", "t0", "f3", 1));
        var two = game.View(checkpoint);
        Assert.Equal(new SolitaireCard(2, "spades", true), two.Foundations[3]);
        Assert.Empty(two.Tableau[0]);
    }

    [Fact]
    public void RecycledWasteOmitsFoundationCardsAndFourKingTopsFinishTheGame()
    {
        var checkpoint = Arrange([12, 13, 13, 12], [[], [], [], [], [], [], []],
            waste: [Card(0, 13), Card(3, 13)]);
        Assert.Throws<InvalidOperationException>(() => game.Apply(checkpoint, new("move", "waste", "f0", 1)));
        checkpoint = game.Apply(checkpoint, new("move", "waste", "f3", 1));
        checkpoint = game.Apply(checkpoint, Draw);
        Assert.Equal(1, game.View(checkpoint).StockCount);
        checkpoint = game.Apply(checkpoint, Draw);
        Assert.Equal(new SolitaireCard(13, "clubs", true), game.View(checkpoint).Waste);

        checkpoint = game.Apply(checkpoint, new("move", "f3", "t0", 1));
        var backedOut = game.View(checkpoint);
        Assert.Equal(new SolitaireCard(12, "spades", true), backedOut.Foundations[3]);
        Assert.Equal(new SolitaireCard(13, "spades", true), Assert.Single(backedOut.Tableau[0]));
        checkpoint = game.Apply(checkpoint, new("move", "waste", "f0", 1));
        Assert.False(game.View(checkpoint).Finished);
        Assert.Throws<InvalidOperationException>(() => game.Apply(checkpoint, Draw));

        checkpoint = game.Apply(checkpoint, new("move", "t0", "f3", 1));
        var won = new KlondikeGame().View(checkpoint);
        Assert.True(won.Finished);
        Assert.Equal(6, won.Moves);
        Assert.Equal(new SolitaireCard?[]
        {
            new(13, "clubs", true), new(13, "diamonds", true), new(13, "hearts", true), new(13, "spades", true),
        }, won.Foundations);
        Assert.All(won.Tableau, Assert.Empty);
        Assert.Empty(won.LegalMoves);
        Assert.Throws<InvalidOperationException>(() => game.Apply(checkpoint, new("move", "f3", "t0", 1)));
    }

    [Fact]
    public void MalformedCommandsAndCorruptCheckpointsAreRejectedOnBothReadPaths()
    {
        Assert.Throws<ArgumentException>(() => game.Start("draw-three"));
        var checkpoint = game.Start("draw-one");
        Assert.Throws<InvalidOperationException>(() => game.Apply(checkpoint, new("move", "t7", "t0", 1)));
        Assert.Throws<InvalidOperationException>(() => game.Apply(checkpoint, new("move", "stock", "t0", 1)));
        Assert.Throws<InvalidOperationException>(() => game.Apply(checkpoint, new("move", "t1", "t0")));
        Assert.Throws<InvalidOperationException>(() => game.Apply(checkpoint, new("draw", Count: 2)));
        Assert.Equal(0, game.View(checkpoint).Moves);

        var wrongVersion = "klondike-v2:" + checkpoint[Prefix.Length..];
        Assert.Contains("version", Assert.Throws<InvalidDataException>(() => game.View(wrongVersion)).Message);
        var duplicate = JsonNode.Parse(checkpoint[Prefix.Length..])!.AsObject();
        var stock = duplicate["Stock"]!.AsArray();
        stock[0] = stock[1]!.DeepClone();

        foreach (var corrupt in new[] { wrongVersion, Prefix + "{", Prefix + duplicate.ToJsonString(), Prefix + new string(' ', 2048) })
        {
            Assert.Throws<InvalidDataException>(() => game.View(corrupt));
            Assert.Throws<InvalidDataException>(() => game.Apply(corrupt, Draw));
        }
    }

    private static SoloAction Action(KlondikeMove move) => new("move", move.From, move.To, move.Count);

    private static int Card(int suit, int rank) => suit * 13 + rank - 1;

    private static string Arrange(int[] foundations, int[][] tableau, int[]? covered = null, int[]? waste = null)
    {
        waste ??= [];
        var occupied = Enumerable.Range(0, 4)
            .SelectMany(suit => Enumerable.Range(suit * 13, foundations[suit]))
            .Concat(tableau.SelectMany(column => column))
            .Concat(waste);
        return Prefix + JsonSerializer.Serialize(new
        {
            Moves = 0, Stock = Enumerable.Range(0, 52).Except(occupied).ToArray(), Waste = waste,
            Foundations = foundations, Tableau = tableau, Covered = covered ?? new int[7],
        });
    }
}
