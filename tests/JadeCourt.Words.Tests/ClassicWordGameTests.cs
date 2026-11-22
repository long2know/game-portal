using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JadeCourt.Words.Tests;

public sealed class ClassicWordGameTests
{
    private static readonly WordTiles Classic = WordTiles.Classic;
    private static readonly WordAction Pass = new("pass");

    [Fact]
    public void ClassicDealAndOriginalPremiumMapArePublicButOnlyTheViewerRackIsRevealed()
    {
        var (game, start) = Start("CATERS?", "PIONDB?");
        var opener = game.View(start, 0);
        var nonacting = game.View(start, 1);
        var spectator = game.View(start, null);
        Assert.Equal(("classic", 15, 15, 7, 86), (opener.Variant, opener.Rows, opener.Columns, opener.RackSize, opener.BagCount));
        Assert.Equal([7, 7], opener.RackSizes);
        Assert.Equal([7, 0, 0], new[] { opener.ExchangeLimit, nonacting.ExchangeLimit, spectator.ExchangeLimit });
        Assert.Equal(("?", 0, true), (opener.Rack[^1].Letter, opener.Rack[^1].Points, opener.Rack[^1].Blank));
        Assert.True(nonacting.Rack[^1].Blank);
        Assert.Empty(opener.Rack.Select(tile => tile.Id).Intersect(nonacting.Rack.Select(tile => tile.Id)));
        Assert.Empty(spectator.Rack);

        var premiums = opener.Premiums.ToDictionary(cell => (cell.Row, cell.Column), cell => cell.Kind);
        Assert.Equal(["double-letter", "double-word", "triple-letter", "triple-word"],
            premiums.Values.Distinct().Order(StringComparer.Ordinal));
        Assert.Equal("double-word", premiums[(7, 7)]);
        Assert.Equal("triple-word", premiums[(0, 5)]);
        Assert.Equal("double-letter", premiums[(0, 0)]);
        Assert.Equal(premiums.Keys.OrderBy(cell => cell.Row).ThenBy(cell => cell.Column),
            spectator.Premiums.Select(cell => (cell.Row, cell.Column)));
        foreach (var ((row, column), kind) in premiums)
        {
            Assert.Equal(kind, premiums[(14 - row, column)]);
            Assert.Equal(kind, premiums[(row, 14 - column)]);
            Assert.Equal(kind, premiums[(column, row)]);
        }
        opener.Premiums[0] = new(7, 7, "triple-word");
        Assert.Equal(nonacting.Premiums, game.View(start, 0).Premiums);
    }

    [Fact]
    public void EachNewWordUsesTheSharedNewPremiumButNeverPreviouslyUsedPremiums()
    {
        var (game, start) = Start("BAREINL", "MOWSCTU");
        var first = game.Apply(start, 0, Across(game.View(start, 0), 7, 7, "BAR"));
        Assert.Equal("Seat 1 played BAR (+10).", game.View(first, null).LastMove);
        var reply = game.Apply(first, 1, Across(game.View(first, 1), 6, 8, "MOW"));
        Assert.Equal("Seat 2 played MOW, MA, OR (+20).", game.View(reply, null).LastMove);

        var shared = game.Apply(reply, 0, Across(game.View(reply, 0), 7, 10, "E"));
        var crossing = game.View(shared, 1);
        Assert.Equal("Seat 1 played BARE, WE (+22).", crossing.LastMove);
        Assert.Equal([32, 20], crossing.Scores);
        Assert.Equal("double-word", Assert.Single(crossing.Premiums, p => (p.Row, p.Column) == (7, 10)).Kind);

        var later = game.Apply(shared, 1, Across(crossing, 7, 11, "S"));
        Assert.Equal("Seat 2 played BARES (+7).", game.View(later, null).LastMove);
        Assert.Equal([32, 27], game.View(later, null).Scores);
    }

    [Fact]
    public void LetterAndWordMultipliersStackOnTheMainWordAndTheCrossUsesItsNewTripleWord()
    {
        var game = new WordGame();
        var vertical = "LAUGHTER".Select((letter, offset) => (Row: 1 + offset, Column: 5, Letter: letter));
        var checkpoint = BoardPosition("CHAIRSN", "PIONDB?", [.. vertical, (7, 6, 'A'), (7, 7, 'R')]);
        var action = Across(game.View(checkpoint, 0), 0, 0, "CHAIRS");
        var played = game.View(game.Apply(checkpoint, 0, action), null);
        // CHAIRS: (2×2 + 3 + 1 + 1 + 1 + 1)×2×3 = 66; SLAUGHTER: 13×3 = 39.
        Assert.Equal("Seat 1 played CHAIRS, SLAUGHTER (+105).", played.LastMove);
        Assert.Equal([114, 20], played.Scores);
    }

    [Fact]
    public void LayingSevenAddsFiftyAfterAllWordMultipliersAndLongWordsReachTheEdge()
    {
        var (game, start) = Start("CABINET", "PORSUD?");
        var acting = game.View(start, 0);
        var bingo = Across(acting, 7, 4, "CABINET");
        var overflow = bingo with { Placements = [.. bingo.Placements!, new(7, 11, acting.Rack[0].Id)] };
        Assert.Contains("1 and 7", Assert.Throws<InvalidOperationException>(() => game.Apply(start, 0, overflow)).Message);
        var played = game.View(game.Apply(start, 0, bingo), 0);
        Assert.Equal("Seat 1 played CABINET (+130).", played.LastMove); // 10×2×2×2 + 50
        Assert.Equal([130, 0], played.Scores);
        Assert.Equal((7, 79, 7), (played.Board.Length, played.BagCount, played.Rack.Length));

        var longWord = "ACKNOWLEDGMENT".Select((letter, column) => (7, column, letter)).ToArray();
        var edge = BoardPosition("SEATIRN", "PIONDB?", longWord);
        var extended = game.View(game.Apply(edge, 0, Across(game.View(edge, 0), 7, 14, "S")), null);
        Assert.Equal("Seat 1 played ACKNOWLEDGMENTS (+26).", extended.LastMove);
        Assert.Equal(15, extended.Board.Length);
    }

    [Fact]
    public void ABlankBelongsToItsRackThenKeepsItsLetterAndZeroValueOnEveryCross()
    {
        var (game, checkpoint) = Start("C?TERS?", "PIONDBL");
        var view = game.View(checkpoint, 0);
        var action = Across(view, 7, 6, "C?T", blankLetter: "A");
        var (ordinary, blank) = (action.Placements![0], action.Placements[1]);
        foreach (var letter in new string?[] { null, "a", "AA", "?", "\u00C9" })
            Assert.Contains("uppercase", Assert.Throws<InvalidOperationException>(() =>
                game.Apply(checkpoint, 0, new("place", [ordinary, blank with { Letter = letter }]))).Message);
        Assert.Contains("Only blank", Assert.Throws<InvalidOperationException>(() =>
            game.Apply(checkpoint, 0, new("place", [ordinary with { Letter = "C" }, blank]))).Message);
        Assert.Throws<InvalidOperationException>(() => game.Apply(checkpoint, 1, action));

        var played = game.Apply(checkpoint, 0, action);
        var restored = new WordGame();
        Assert.Equal("Seat 1 played CAT (+6).", restored.View(played, null).LastMove);
        var assignment = Assert.Single(restored.View(played, 1).Board, cell => cell.Tile.Blank);
        Assert.Equal((0, 7, 7, blank.TileId, "A", 0),
            (assignment.Seat, assignment.Row, assignment.Column, assignment.Tile.Id, assignment.Tile.Letter, assignment.Tile.Points));
        var foreign = Across(restored.View(played, 1), 6, 7, "PI");
        Assert.Contains("current rack", Assert.Throws<InvalidOperationException>(() => restored.Apply(played, 1,
            foreign with { Placements = [foreign.Placements![0] with { TileId = blank.TileId, Letter = "P" }, foreign.Placements[1]] })).Message);
        var crossed = restored.Apply(played, 1, foreign);
        Assert.Equal("Seat 2 played PI, PA, IT (+9).", restored.View(crossed, null).LastMove);
        var extended = restored.Apply(crossed, 0, Across(restored.View(crossed, 0), 7, 9, "?", blankLetter: "S"));
        var end = game.View(extended, null);
        Assert.Equal("Seat 1 played CATS (+3).", end.LastMove);
        Assert.Equal(["A", "S"], end.Board.Where(cell => cell.Tile.Blank).Select(cell => cell.Tile.Letter));
        Assert.Equal(assignment, end.Board.Single(cell => cell.Tile.Id == blank.TileId));

        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(action, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var placements = wire.RootElement.GetProperty("placements");
        Assert.False(placements[0].TryGetProperty("letter", out _));
        Assert.Equal("A", placements[1].GetProperty("letter").GetString());
    }

    [Fact]
    public void ClassicExchangeLimitIsSevenButBelowSevenInTheBagNoneIsAllowed()
    {
        var game = new WordGame(WordLexicon.Default, _ => { });
        var exactlySeven = NearEnd("CATERS?", "PIONDBL", "FOXGUEA");
        var before = game.View(exactlySeven, 0);
        var requested = before.Rack.Select(tile => tile.Id).ToArray();
        Assert.Equal(7, before.ExchangeLimit);
        Assert.Equal(0, game.View(exactlySeven, 1).ExchangeLimit);
        foreach (var bad in new[] { new[] { requested[0], requested[0] }, [game.View(exactlySeven, 1).Rack[0].Id], [.. requested, requested[0]] })
            Assert.Throws<InvalidOperationException>(() => game.Apply(exactlySeven, 0, new("exchange", Exchange: bad)));
        var after = game.Apply(exactlySeven, 0, new("exchange", Exchange: requested));
        var view = game.View(after, 0);
        Assert.Equal("FOXGUEA", string.Concat(view.Rack.Select(tile => tile.Letter)));
        Assert.Equal("Seat 1 exchanged 7 tiles.", view.LastMove);
        Assert.Equal(7, view.BagCount);
        Assert.Equal(requested.Order(), WordCheckpoint.Load(after).Bag.Order());
        Assert.Equal(100, view.Board.Length + view.BagCount + view.RackSizes.Sum());

        var shortBag = NearEnd("CATERS?", "PIONDBL", "FOXGUE");
        Assert.Equal(0, game.View(shortBag, 0).ExchangeLimit);
        Assert.Throws<InvalidOperationException>(() => game.Apply(shortBag, 0, new("exchange", Exchange: [requested[0]])));
    }

    [Fact]
    public void AZeroPointBlankPlayIsLegalAndCountsAsTheSixthScorelessTurn()
    {
        var (game, checkpoint) = Start("JQZVWKX", "??ARETN", "DUA");
        for (var turn = 0; turn < 4; turn++)
            checkpoint = game.Apply(checkpoint, turn % 2, Pass);
        checkpoint = game.Apply(checkpoint, 0, new("exchange", Exchange: [game.View(checkpoint, 0).Rack[0].Id]));
        var acting = game.View(checkpoint, 1);
        Assert.Equal("playing", acting.Status);
        var scoring = game.Apply(checkpoint, 1, Across(acting, 7, 7, "AT"));
        Assert.Equal("Seat 2 played AT (+4).", game.View(scoring, null).LastMove);
        for (var turn = 0; turn < 5; turn++)
        {
            scoring = game.Apply(scoring, turn % 2, Pass);
            Assert.Equal("playing", game.View(scoring, null).Status);
        }
        var blanks = acting.Rack.Where(tile => tile.Blank).ToArray();
        var settled = game.Apply(checkpoint, 1, new("place", [new(7, 7, blanks[0].Id, "A"), new(7, 8, blanks[1].Id, "T")]));
        var final = new WordGame().View(settled, null);
        Assert.Equal(("finished", null, 0), (final.Status, final.Turn, final.ExchangeLimit));
        Assert.Equal("Six consecutive scoreless turns.", final.Detail);
        Assert.Equal("Seat 2 played AT (+0).", final.LastMove);
        Assert.Equal([-41, -8], final.Scores);
        Assert.Equal([1], final.Winners);
        Assert.Throws<InvalidOperationException>(() => game.Apply(settled, 0, Pass));
    }

    [Fact]
    public void AZeroPointRackOutTransfersTheOtherRemainderEvenOnTheSixthScorelessTurn()
    {
        var game = new WordGame();
        var before = NearEnd("?", "NE", "", anchorBlank: true, dryTurns: 5);
        var acting = game.View(before, 0);
        var end = game.Apply(before, 0, Across(acting, 6, 7, "?", blankLetter: "P"));
        var final = game.View(end, null);
        Assert.Equal(("finished", "A seat emptied its rack with no tiles left to draw."), (final.Status, final.Detail));
        Assert.Equal("Seat 1 played PA (+0).", final.LastMove);
        Assert.Equal([11, 18], final.Scores);
        Assert.Equal([0, 2], final.RackSizes);
        Assert.Equal([1], final.Winners);
    }

    [Fact]
    public void ClassicRestoreRejectsInvalidInventoryBoundsAndAssignmentsBeforeViewOrApply()
    {
        var (game, start) = Start("C?TERS?", "PIONDBL");
        var checkpoint = game.Apply(start, 0, Across(game.View(start, 0), 7, 6, "C?T", blankLetter: "A"));
        Action<JsonNode>[] corruptions =
        [
            node => node["board"]![1]!["letter"] = null,
            node => node["board"]![1]!["letter"] = "a",
            node => node["board"]![1]!["letter"] = "AB",
            node => node["board"]![0]!["letter"] = "C",
            node => node["board"]![0]!["row"] = 15,
            node => node["board"]![0]!["row"] = 0,
            node => node["board"]![0]!["column"] = 7,
            node => node["board"]![0]!["seat"] = 2,
            node => node["board"]![0]!.AsObject().Remove("row"),
            node => node["board"]![0] = null,
            node => node["bag"]![0] = node["board"]![1]!["tileId"]!.GetValue<int>(),
            node => node["bag"]![0] = 101,
            node => node["racks"]![1]![0] = node["racks"]![0]![0]!.GetValue<int>(),
            node => node["turn"] = -1,
            node => node["dryTurns"] = 7,
            node => node["scores"]![0] = int.MaxValue,
            node => node["last"]!["points"] = 0,
            node => node["last"]!["count"] = 8
        ];
        foreach (var corrupt in corruptions)
        {
            var invalid = JsonNode.Parse(checkpoint)!;
            corrupt(invalid);
            AssertInvalid(invalid.ToJsonString());
        }
        AssertInvalid(checkpoint.Insert(1, "\"turn\":0,"));

        void AssertInvalid(string invalid)
        {
            Assert.Contains("checkpoint", Assert.Throws<InvalidDataException>(() => game.View(invalid, null)).Message);
            Assert.Throws<InvalidDataException>(() => game.Apply(invalid, 1, Pass));
        }
    }

    [Fact]
    public void BoundedBotFindsABlankOnlyWordFromTheActingView()
    {
        var lexicon = WordLexicon.Load(new StringReader("AT\n"));
        var (game, start) = Start("??JQZWK", "PIONDBL", lexicon: lexicon);
        var (other, alternate) = Start("??JQZWK", "ABCDEFG", lexicon: lexicon);
        var bot = new WordGame(lexicon, _ => throw new InvalidOperationException("Bot cannot shuffle or draw."));
        var acting = game.View(start, 0);
        var move = bot.SelectBotMove(acting);
        Assert.Equal("place", move.Kind);
        Assert.Equal(move.Placements, bot.SelectBotMove(other.View(alternate, 0)).Placements);
        Assert.Equal(["A", "T"], move.Placements!.Select(p => p.Letter));
        Assert.Equal(2, move.Placements!.Select(p => p.TileId).Distinct().Count());
        Assert.All(move.Placements!, p => Assert.Contains(acting.Rack, tile => tile.Id == p.TileId && tile.Blank));
        Assert.Equal(Pass, bot.SelectBotMove(game.View(start, 1)));
        Assert.Equal(Pass, bot.SelectBotMove(game.View(start, null)));
        Assert.Equal(Pass, bot.SelectBotMove(acting with { Rack = [acting.Rack[0] with { Letter = "I" }, .. acting.Rack[1..]] }));
        Assert.Equal("Seat 1 played AT (+0).", game.View(game.Apply(start, 0, move), null).LastMove);
    }

    [Fact]
    public void ClassicBotsPlayLegalRecoverableTurnsWithBlanksOnAFifteenSquareBoard()
    {
        var (_, checkpoint) = Start("??AEIST", "POUNDBL");
        var recovered = new WordGame();
        var watch = Stopwatch.StartNew();
        var moves = 0;
        var view = recovered.View(checkpoint, null);
        for (var turn = 0; turn < 120 && view.Turn is { } seat; turn++)
        {
            var acting = recovered.View(checkpoint, seat);
            var action = recovered.SelectBotMove(acting);
            checkpoint = recovered.Apply(checkpoint, seat, action);
            view = recovered.View(checkpoint, null);
            Assert.Equal(100, view.Board.Length + view.BagCount + view.RackSizes.Sum());
            if (action.Placements is not { } placed)
                continue;
            moves++;
            Assert.All(placed, p =>
            {
                var tile = Assert.Single(acting.Rack, tile => tile.Id == p.TileId);
                var cell = Assert.Single(view.Board, cell => (cell.Row, cell.Column) == (p.Row, p.Column));
                Assert.Equal((tile.Id, tile.Points, tile.Blank, p.Letter ?? tile.Letter),
                    (cell.Tile.Id, cell.Tile.Points, cell.Tile.Blank, cell.Tile.Letter));
            });
        }
        Assert.True(moves >= 5, $"Only {moves} placements.");
        Assert.Equal("finished", view.Status);
        Assert.Contains(view.Board, cell => cell.Tile.Blank);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Classic playthrough took {watch.Elapsed}.");
    }

    private static (WordGame Game, string Checkpoint) Start(string first, string second, string draws = "", WordLexicon? lexicon = null)
    {
        Assert.Equal((7, 7), (first.Length, second.Length));
        var available = AllTiles();
        var known = Take(available, first + second + draws);
        var tail = available.ToArray();
        new Random(421).Shuffle(tail);
        int[] deal = [.. known, .. tail];
        var game = new WordGame(lexicon ?? WordLexicon.Default, _ => { });
        return (game, new WordCheckpoint(2, 0, [deal[..7], deal[7..14]], deal[14..], [], [0, 0], 0, null).Save());
    }

    private static string BoardPosition(string first, string second, (int Row, int Column, char Letter)[] cells)
    {
        var available = AllTiles();
        int[][] racks = [Take(available, first), Take(available, second)];
        var board = cells.Select(cell => new WordCheckpoint.PlacedTile(cell.Row, cell.Column, Take(available, cell.Letter.ToString())[0], 1)).ToArray();
        return new WordCheckpoint(2, 0, racks, available.ToArray(), board, [9, 20], 1, new("pass", 0, null, 0)).Save();
    }

    private static string NearEnd(string first, string second, string draws, bool anchorBlank = false, int dryTurns = 1)
    {
        var available = AllTiles();
        int[][] racks = [Take(available, first), Take(available, second)];
        var bag = Take(available, draws);
        var center = Take(available, anchorBlank ? "?T" : "AT");
        // Keep the intended extension clear and account for every other tile on the board.
        var filler = Enumerable.Range(8 * 15 + 8, 6).Concat(Enumerable.Range(9 * 15, 6 * 15));
        WordCheckpoint.PlacedTile[] board =
        [
            new(7, 7, center[0], 1, anchorBlank ? "A" : null), new(7, 8, center[1], 1),
            .. available.Zip(filler, (id, index) => new WordCheckpoint.PlacedTile(index / 15, index % 15, id, 1,
                Classic.Get(id).Blank ? "E" : null))
        ];
        return new WordCheckpoint(2, 0, racks, bag, board, [9, 20], dryTurns, new("pass", 0, null, 0)).Save();
    }

    private static List<int> AllTiles() => Enumerable.Range(1, 100).ToList();

    private static int[] Take(List<int> available, string letters) => letters.Select(letter =>
    {
        var id = available.First(id => Classic.Get(id).Letter[0] == letter);
        available.Remove(id);
        return id;
    }).ToArray();

    private static WordAction Across(WordView view, int row, int column, string pattern, string? blankLetter = null)
    {
        var available = view.Rack.Select(tile => tile.Id).ToList();
        var placements = Take(available, pattern).Select((id, offset) =>
            new WordPlacement(row, column + offset, id, Classic.Get(id).Blank ? blankLetter : null)).ToArray();
        return new("place", placements);
    }
}
