using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JadeCourt.Words.Tests;

public sealed class WordGameTests
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);
    private static readonly WordAction Pass = new("pass");

    [Fact]
    public void ProductionStartShufflesACompleteIndependentSupply()
    {
        var game = new WordGame();
        var first = game.Start();
        var second = game.Start();
        Assert.NotEqual(first, second);
        foreach (var checkpoint in new[] { first, second })
        {
            var state = WordCheckpoint.Load(checkpoint);
            Assert.Equal(Enumerable.Range(1, WordTiles.Count), state.Racks[0]
                .Concat(state.Racks[1]).Concat(state.Bag).Order());
        }
    }

    [Fact]
    public void ProjectsOnlyTheViewerRackOnTheExactWireAndRestoresWithoutSharedState()
    {
        var game = Deal("CATERS", "PIONDB", "FOX");
        var start = game.Start();
        var zero = game.View(start, 0);
        var one = game.View(start, 1);
        var spectator = game.View(start, null);
        Assert.Equal((11, 11, 0, "playing", 50), (zero.Rows, zero.Columns, zero.Turn, zero.Status, zero.BagCount));
        Assert.Equal([6, 6], zero.RackSizes);
        Assert.Equal([0, 0], zero.Scores);
        Assert.Empty(zero.Board);
        Assert.Empty(zero.Winners);
        Assert.Null(zero.LastMove);
        Assert.Null(zero.Detail);
        Assert.Equal("CATERS", Letters(zero));
        Assert.Equal("PIONDB", Letters(one));
        Assert.Empty(spectator.Rack);

        var firstMove = Along(zero, 5, 4, "CAT");
        Assert.Equal("place", firstMove.Kind);
        var wireAction = JsonSerializer.SerializeToNode(firstMove, Wire)!.AsObject();
        Assert.Equal(["exchange", "kind", "placements"], wireAction.Select(field => field.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["column", "row", "tileId"],
            wireAction["placements"]![0]!.AsObject().Select(field => field.Key).Order(StringComparer.Ordinal));

        var restored = JsonSerializer.Deserialize<WordAction>(wireAction, Wire)!;
        var checkpoint = game.Apply(start, 0, restored);
        var recovered = new WordGame();
        var after = recovered.View(checkpoint, 0);
        Assert.Equal("ERSFOX", Letters(after));
        Assert.Equal("PIONDB", Letters(recovered.View(checkpoint, 1)));
        Assert.Equal((1, 47, "Seat 1 played CAT (+4)."), (after.Turn, after.BagCount, after.LastMove));
        Assert.Equal([4, 0], after.Scores);
        Assert.Equal(new WordCell(5, 4, zero.Rack[0], 0), after.Board[0]);
        Assert.Equal([4, 5, 6], after.Board.Select(cell => cell.Column));

        foreach (var viewer in new int?[] { null, 0, 1 })
        {
            var projection = recovered.View(checkpoint, viewer);
            var json = JsonSerializer.SerializeToNode(projection, Wire)!.AsObject();
            Assert.Equal(
                ["bagCount", "board", "columns", "detail", "lastMove", "rack", "rackSizes", "rows", "scores",
                    "status", "turn", "winners"], json.Select(field => field.Key).Order(StringComparer.Ordinal));
            Assert.Equal(["column", "row", "seat", "tile"],
                json["board"]![0]!.AsObject().Select(field => field.Key).Order(StringComparer.Ordinal));
            Assert.Equal(["id", "letter", "points"],
                json["board"]![0]!["tile"]!.AsObject().Select(field => field.Key).Order(StringComparer.Ordinal));

            var rackIds = json["rack"]!.AsArray().Select(tile => tile!["id"]!.GetValue<int>()).ToArray();
            Assert.Equal(viewer switch { 0 => after.Rack.Select(tile => tile.Id),
                1 => one.Rack.Select(tile => tile.Id), _ => [] }, rackIds);
            Assert.Empty(rackIds.Intersect(WordCheckpoint.Load(checkpoint).Bag));
            Assert.Empty(rackIds.Intersect(viewer == 1 ? after.Rack.Select(tile => tile.Id) : one.Rack.Select(tile => tile.Id)));
        }

        after.Rack[0] = one.Rack[0];
        after.Scores[0] = -100;
        Assert.Equal("ERSFOX", Letters(recovered.View(checkpoint, 0)));
        Assert.Equal([4, 0], recovered.View(checkpoint, 0).Scores);
        Assert.Equal("CATERS", Letters(game.View(start, 0)));
    }

    [Fact]
    public void ScoresEveryNewWordAndJunctionAndRejectsAnInvalidCrossword()
    {
        var (game, start) = Start("CATERS", "PIONDB", "FOX");
        var opening = game.Apply(start, 0, Along(game.View(start, 0), 5, 4, "CAT"));
        var reply = game.View(opening, 1);
        Assert.Contains("AO", Assert.Throws<InvalidOperationException>(
            () => game.Apply(opening, 1, Along(reply, 6, 5, "ON"))).Message);
        Assert.Throws<InvalidOperationException>(() => game.Apply(opening, 1, Along(reply, 0, 0, "PI")));

        var linked = game.Apply(opening, 1, Along(reply, 4, 5, "PI"));
        var scores = game.View(linked, 0);
        Assert.Equal([4, 12], scores.Scores);
        Assert.Equal("Seat 2 played PI, PA, IT (+12).", scores.LastMove);
        Assert.Equal(5, scores.Board.Length);
        Assert.Equal(2, scores.Board.Count(cell => cell.Seat == 1));

        var extended = game.View(game.Apply(linked, 0, Along(scores, 5, 7, "S")), null);
        Assert.Equal([9, 12], extended.Scores);
        Assert.Equal("Seat 1 played CATS (+5).", extended.LastMove);
        Assert.Equal("Seat 1 played CAT (+4).", game.View(opening, null).LastMove);
    }

    [Fact]
    public void AcceptsABridgeOverOldTilesAndASingleCrossingTile()
    {
        var (game, start) = Start("ATNERS", "CSLPIE");
        var opening = game.Apply(start, 0, Along(game.View(start, 0), 5, 5, "AT"));
        var rack = game.View(opening, 1).Rack;
        var bridge = new WordAction("place",
        [
            new(5, 7, rack.Single(tile => tile.Letter == "S").Id),
            new(5, 4, rack.Single(tile => tile.Letter == "C").Id)
        ]);
        var bridged = game.Apply(opening, 1, bridge);
        Assert.Equal("Seat 2 played CATS (+5).", game.View(bridged, null).LastMove);

        var single = game.Apply(bridged, 0, Along(game.View(bridged, 0), 6, 5, "N"));
        var result = game.View(single, null);
        Assert.Equal("Seat 1 played AN (+2).", result.LastMove);
        Assert.Equal([4, 5], result.Scores);
    }

    [Fact]
    public void RejectsMalformedMovesAndStaleOrForeignTiles()
    {
        var (game, start) = Start("CATZRQ", "PIONDB");
        var view = game.View(start, 0);
        var cat = Along(view, 5, 4, "CAT");
        var first = cat.Placements![0];
        var second = cat.Placements[1];
        var wrongRack = game.View(start, 1).Rack[0].Id;
        WordAction[] invalid =
        [
            new("PLACE", cat.Placements),
            new("place"),
            new("place", []),
            new("place", [null!]),
            new("place", cat.Placements, []),
            new("pass", []),
            new("pass", Exchange: []),
            Along(view, 3, 4, "CAT"),
            Along(view, 5, 5, "A"),
            Along(view, 5, 5, "ZQ"),
            new("place", [first, second with { Column = 6 }, cat.Placements[2] with { Column = 7 }]),
            new("place", [first, second with { Row = 4 }]),
            new("place", [first, second with { Column = first.Column }]),
            new("place", [first, second with { TileId = first.TileId }]),
            new("place", [first with { TileId = wrongRack }, second]),
            new("place", [first with { TileId = int.MaxValue }, second]),
            new("place", [first with { Column = int.MaxValue }, second])
        ];
        foreach (var action in invalid)
            Assert.Throws<InvalidOperationException>(() => game.Apply(start, 0, action));
        Assert.Throws<InvalidOperationException>(() => game.Apply(start, 1, cat));
        Assert.Throws<ArgumentOutOfRangeException>(() => game.Apply(start, -1, Pass));
        Assert.Throws<ArgumentOutOfRangeException>(() => game.View(start, 2));
        Assert.Throws<ArgumentNullException>(() => game.Apply(start, 0, null!));

        var after = game.Apply(start, 0, cat);
        Assert.Throws<InvalidOperationException>(() => game.Apply(after, 0, cat));
        var again = game.Apply(after, 1, Pass);
        Assert.Throws<InvalidOperationException>(() => game.Apply(again, 0, cat));
        Assert.Throws<InvalidOperationException>(() => game.Apply(again, 0, Along(game.View(again, 0), 5, 5, "R")));
        Assert.Equal(3, game.View(again, null).Board.Length);
        Assert.Empty(game.View(start, null).Board);
    }

    [Fact]
    public void ExchangeRequiresDistinctOwnedTilesAndDrawsBeforeReturningThem()
    {
        var (game, checkpoint) = Start("CATERS", "PIONDB", "FOX");
        var before = game.View(checkpoint, 0);
        var ids = before.Rack.Select(tile => tile.Id).ToArray();
        var foreign = game.View(checkpoint, 1).Rack[0].Id;
        var bagId = WordCheckpoint.Load(checkpoint).Bag[0];
        foreach (var exchange in new int[][] { [], ids[..4], [foreign], [bagId], [ids[0], ids[0]] })
            Assert.Throws<InvalidOperationException>(() =>
                game.Apply(checkpoint, 0, new WordAction("exchange", Exchange: exchange)));
        Assert.Throws<InvalidOperationException>(() =>
            game.Apply(checkpoint, 0, new WordAction("exchange", [], [ids[0]])));

        var next = game.Apply(checkpoint, 0, new WordAction("exchange", Exchange: [ids[2], ids[0]]));
        var exchanged = game.View(next, 0);
        Assert.Equal("AERSFO", Letters(exchanged));
        Assert.Equal(before.BagCount, exchanged.BagCount);
        Assert.Equal(1, exchanged.Turn);
        Assert.Equal("Seat 1 exchanged 2 tiles.", game.View(next, null).LastMove);
        Assert.Equal([0, 0], exchanged.Scores);
        Assert.Equal("PIONDB", Letters(game.View(next, 1)));
        Assert.Contains(ids[0], WordCheckpoint.Load(next).Bag);
        Assert.Contains(ids[2], WordCheckpoint.Load(next).Bag);
    }

    [Fact]
    public void PlacementBreaksTheStreakButPassingAndExchangingCountTowardTheEnd()
    {
        var (game, checkpoint) = Start("CATERS", "PIONDB", "FOX");
        checkpoint = game.Apply(checkpoint, 0, Pass);
        checkpoint = game.Apply(checkpoint, 1, Pass);
        checkpoint = game.Apply(checkpoint, 0, new("exchange", Exchange: [game.View(checkpoint, 0).Rack[0].Id]));
        var used = game.View(checkpoint, 1);
        var after = game.Apply(checkpoint, 1, Along(used, 5, 4, "PIN"));
        Assert.Equal("playing", game.View(after, null).Status);
        for (var turn = 0; turn < 3; turn++)
        {
            after = game.Apply(after, turn % 2, Pass);
            Assert.Equal("playing", game.View(after, null).Status);
        }
        var one = game.View(after, 1);
        var ended = game.Apply(after, 1, new("exchange", Exchange: [one.Rack[0].Id]));
        var final = game.View(ended, null);
        Assert.Null(final.Turn);
        Assert.Equal("finished", final.Status);
        Assert.Equal("Four turns without a placement.", final.Detail);
        Assert.Equal("Seat 2 exchanged 1 tile.", final.LastMove);
        Assert.Equal(47, final.BagCount);
        Assert.Throws<InvalidOperationException>(() => game.Apply(ended, 0, Pass));
    }

    [Fact]
    public void FourPassesEndATieAndDeductOnlyEachSeatsOwnUnplayedPoints()
    {
        var (game, checkpoint) = Start("ABCEST", "ABCEST");
        for (var ply = 0; ply < 4; ply++)
            checkpoint = game.Apply(checkpoint, ply % 2, Pass);

        var ended = new WordGame().View(checkpoint, null);
        Assert.Equal("finished", ended.Status);
        Assert.Null(ended.Turn);
        Assert.Empty(ended.Winners);
        Assert.Equal([-8, -8], ended.Scores);
        Assert.Empty(ended.Rack);
        Assert.Equal([6, 6], ended.RackSizes);
        Assert.Equal([-8, -8], new WordGame().View(checkpoint, 0).Scores);
    }

    [Fact]
    public void AShortBagCapsExchangeAndDrawingTheLastTileAloneDoesNotFinish()
    {
        var game = new WordGame();
        var checkpoint = NearEnd("CATERS", "PIONDB", "X");
        var view = game.View(checkpoint, 0);
        Assert.Throws<InvalidOperationException>(() =>
            game.Apply(checkpoint, 0, new WordAction("exchange", Exchange: [view.Rack[0].Id, view.Rack[1].Id])));

        var emptiedBag = game.Apply(checkpoint, 0, Along(view, 5, 4, "C"));
        var after = game.View(emptiedBag, 0);
        Assert.Equal((0, "playing", 1, "ATERSX"), (after.BagCount, after.Status, after.Turn, Letters(after)));
        Assert.Throws<InvalidOperationException>(() =>
            game.Apply(emptiedBag, 1, new WordAction("exchange", Exchange: [game.View(emptiedBag, 1).Rack[0].Id])));
    }

    [Fact]
    public void EmptyingARackWithAnEmptyBagEndsAndDoesNotTransferThePenalty()
    {
        var game = new WordGame();
        var checkpoint = NearEnd("C", "NE", "");
        var before = game.View(checkpoint, 0);
        Assert.Equal(("playing", 0), (before.Status, before.BagCount));

        var end = game.Apply(checkpoint, 0, Along(before, 5, 4, "C"));
        var final = game.View(end, null);
        Assert.Null(final.Turn);
        Assert.Equal("finished", final.Status);
        Assert.Equal("A seat emptied its rack with no tiles left to draw.", final.Detail);
        Assert.Equal([0, 2], final.RackSizes);
        Assert.Equal([13, 18], final.Scores);
        Assert.Equal([1], final.Winners);
        Assert.Throws<InvalidOperationException>(() => game.Apply(end, 1, Pass));
    }

    [Fact]
    public void RejectsAlteredCheckpointVersionAndInventoryBeforeProjectionOrMoves()
    {
        var (game, checkpoint) = Start("CATERS", "PIONDB");
        var version = JsonNode.Parse(checkpoint)!;
        version["version"] = 2;
        Assert.Throws<InvalidDataException>(() => game.View(version.ToJsonString(), null));

        var inventory = JsonNode.Parse(checkpoint)!;
        inventory["bag"]![0] = game.View(checkpoint, 0).Rack[0].Id;
        Assert.Throws<InvalidDataException>(() => game.Apply(inventory.ToJsonString(), 0, Pass));
        Assert.Throws<InvalidDataException>(() => game.View(new string(' ', 20_000), 0));
        Assert.Throws<InvalidDataException>(() => game.View("{}", 0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("CAT\n\n")]
    [InlineData("CATS\nCAT\n")]
    [InlineData("CAT\nCAT\n")]
    [InlineData("C4T\n")]
    [InlineData("cat\n")]
    [InlineData("I\n")]
    public void EmptyOrInvalidLexiconsFailExplicitly(string list)
    {
        using var source = new StringReader(list);
        Assert.Contains("lexicon", Assert.Throws<InvalidDataException>(() => WordLexicon.Load(source)).Message);
    }

    [Fact]
    public void BotDependsOnlyOnTheProjectedRackAndPublicBoardAndCanPass()
    {
        var (game, checkpoint) = Start("CATERS", "PIONDB", "FOX");
        var (otherGame, otherCheckpoint) = Start("CATERS", "JQZVWK", "HUM");
        Assert.NotEqual(checkpoint, otherCheckpoint);
        var input = game.View(checkpoint, 0);
        var otherInput = otherGame.View(otherCheckpoint, 0);
        Assert.Equal(JsonSerializer.Serialize(input, Wire), JsonSerializer.Serialize(otherInput, Wire));
        var bot = new WordGame(WordLexicon.Default, _ => throw new InvalidOperationException("Bot cannot draw."));
        var move = bot.SelectBotMove(input);
        Assert.Equal("place", move.Kind);
        Assert.Equal(JsonSerializer.Serialize(move, Wire), JsonSerializer.Serialize(bot.SelectBotMove(otherInput), Wire));
        Assert.Equal(JsonSerializer.Serialize(move, Wire),
            JsonSerializer.Serialize(bot.SelectBotMove(input with { Rack = input.Rack.Reverse().ToArray() }), Wire));
        Assert.Empty(bot.SelectBotMove(game.View(checkpoint, null)).Placements ?? []);
        Assert.Equal(Pass, bot.SelectBotMove(game.View(checkpoint, null)));

        var (unplayableGame, unplayable) = Start("JQZVWK", "PIONDB");
        var fallback = bot.SelectBotMove(unplayableGame.View(unplayable, 0));
        Assert.Equal(Pass, fallback);
        Assert.Equal(1, game.View(unplayableGame.Apply(unplayable, 0, fallback), null).Turn);
    }

    [Fact]
    public void BotRepliesAreBoundedLegalAndPlayAFullGameThroughRecovery()
    {
        var (game, checkpoint) = Start("CATERS", "PIONDB", "FOX");
        var recovered = new WordGame();
        var watch = Stopwatch.StartNew();
        var placements = 0;
        var turns = 0;
        var view = game.View(checkpoint, null);
        while (view.Turn is { } seat && turns < 80)
        {
            var acting = recovered.View(checkpoint, seat);
            var move = recovered.SelectBotMove(acting);
            Assert.Contains(move.Kind, new[] { "pass", "place" });
            checkpoint = recovered.Apply(checkpoint, seat, move);
            var after = recovered.View(checkpoint, null);
            Assert.Equal(WordTiles.Count, after.BagCount + after.Board.Length + after.RackSizes.Sum());
            if (move.Placements is { } played)
            {
                placements++;
                Assert.Equal(view.Board.Length + played.Length, after.Board.Length);
                foreach (var placement in played)
                {
                    var cell = Assert.Single(after.Board, cell => (cell.Row, cell.Column) == (placement.Row, placement.Column));
                    Assert.Equal((seat, placement.TileId), (cell.Seat, cell.Tile.Id));
                    Assert.Contains(acting.Rack, tile => tile.Id == placement.TileId);
                }
            }
            if (after.Status == "playing")
                Assert.Equal(1 - seat, after.Turn);
            view = after;
            turns++;
        }
        Assert.True(placements >= 4, $"Only {placements} placements in {turns} turns.");
        Assert.Equal("finished", view.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Playthrough took {watch.Elapsed}.");
    }

    private static (WordGame Game, string Checkpoint) Start(string first, string second, string draws = "")
    {
        var game = Deal(first, second, draws);
        return (game, game.Start());
    }

    private static WordGame Deal(string first, string second, string draws)
    {
        Assert.Equal((6, 6), (first.Length, second.Length));
        var available = AllTiles();
        var known = Take(available, first + second + draws);
        var tail = available.ToArray();
        new Random(281).Shuffle(tail);
        int[] deal = [.. known, .. tail];
        return new WordGame(WordLexicon.Default, tiles =>
        {
            if (tiles.Length == deal.Length)
                deal.CopyTo(tiles, 0);
        });
    }

    private static string NearEnd(string first, string second, string draw)
    {
        var available = AllTiles();
        var racks = new[] { Take(available, first), Take(available, second) };
        var bag = Take(available, draw);
        var at = Take(available, "AT");
        // Keep the CAT extension clear while accounting for the rest of the supply.
        var cells = Enumerable.Range(0, 121).Where(index => index / 11 is not (4 or 5 or 6));
        WordCheckpoint.PlacedTile[] board =
        [
            new(5, 5, at[0], 1), new(5, 6, at[1], 1),
            .. available.Zip(cells, (id, index) => new WordCheckpoint.PlacedTile(index / 11, index % 11, id, 1))
        ];
        return new WordCheckpoint(1, 0, racks, bag, board, [9, 20], 1, new("pass", 0, null, 0)).Save();
    }

    private static List<int> AllTiles() => Enumerable.Range(1, WordTiles.Count).ToList();

    private static int[] Take(List<int> available, string letters) => letters.Select(letter =>
    {
        var id = available.First(id => WordTiles.Get(id).Letter[0] == letter);
        available.Remove(id);
        return id;
    }).ToArray();

    private static WordAction Along(WordView view, int row, int column, string word)
    {
        var available = view.Rack.Select(tile => tile.Id).ToList();
        return new WordAction("place", Take(available, word)
            .Select((id, offset) => new WordPlacement(row, column + offset, id)).ToArray());
    }

    private static string Letters(WordView view) => string.Concat(view.Rack.Select(tile => tile.Letter));
}
