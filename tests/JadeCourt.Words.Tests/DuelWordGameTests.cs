using System.Text.Json.Nodes;

namespace JadeCourt.Words.Tests;

public sealed class DuelWordGameTests
{
    private static readonly WordAction Pass = new("pass");
    private static readonly WordGame Game = new(WordLexicon.Default, _ => { });

    [Fact]
    public void DealUsesThe104EnglishTilesAndTheOriginalBoardWithANeutralCenter()
    {
        var deal = new WordGame().Start();
        var saved = WordCheckpoint.Load(deal);
        var host = Game.View(deal, 0);
        var peer = Game.View(deal, 1);
        var publicView = Game.View(deal, null);
        Assert.Equal(("duel", 15, 15, 0, 7, 90), (host.Variant, host.Rows, host.Columns, host.Turn, host.RackSize, host.BagCount));
        Assert.Equal([7, 7], host.RackSizes);
        Assert.Equal((7, 0), (host.ExchangeLimit, peer.ExchangeLimit));
        Assert.Empty(publicView.Rack);
        Assert.Empty(host.Rack.Select(tile => tile.Id).Intersect(peer.Rack.Select(tile => tile.Id)));
        var tiles = saved.Racks.SelectMany(rack => rack).Concat(saved.Bag).Select(saved.Rules.Get).ToArray();
        Assert.Equal(104, tiles.Select(tile => tile.Id).Distinct().Count());
        Assert.Equal(220, tiles.Sum(tile => tile.Points));
        Assert.Equal(2, tiles.Count(tile => tile is { Letter: "?", Blank: true, Points: 0 }));
        Assert.All(tiles.Where(tile => tile.Letter != "?"), tile => Assert.False(tile.Blank));
        string[] faces =
        [
            "??", string.Concat(new string('A', 9), new string('E', 13), new string('I', 8), new string('O', 8),
                "RRRRRRSSSSSTTTTTTT"),
            "DDDDDLLLLNNNNNUUUU", "GGGHHHHYY", "BBCCFFMMPPWW", "KVV", "", "", "X", "", "JQZ"
        ];
        Assert.Equal(faces, Enumerable.Range(0, faces.Length).Select(points =>
            string.Concat(tiles.Where(tile => tile.Points == points).Select(tile => tile.Letter).Order(StringComparer.Ordinal))));

        Assert.DoesNotContain(publicView.Premiums, cell => (cell.Row, cell.Column) == (7, 7));
        Assert.Equal(WordTiles.Classic.ProjectPremiums().Where(cell => (cell.Row, cell.Column) != (7, 7)),
            publicView.Premiums);
        Assert.Equal(new[] { ("double-letter", 20), ("double-word", 12), ("triple-letter", 20), ("triple-word", 12) },
            publicView.Premiums.GroupBy(cell => cell.Kind).OrderBy(group => group.Key).Select(group => (group.Key, group.Count())));
    }

    [Fact]
    public void CenterMustBeCoveredButIsNotDoubledAndBingoAdds35AfterOtherPremiums()
    {
        var checkpoint = Deal("CABINET", "GLOWRIM");
        var rack = Game.View(checkpoint, 0);
        Assert.Throws<InvalidOperationException>(() => Game.Apply(checkpoint, 0, Line(rack, 7, 8, "CAB")));
        var centered = Game.Apply(checkpoint, 0, Line(rack, 7, 6, "CAB"));
        Assert.Equal("Seat 1 played CAB (+9).", Game.View(centered, null).LastMove);

        var bingo = Game.Apply(checkpoint, 0, Line(rack, 7, 4, "CABINET"));
        var restored = new WordGame().View(bingo, null);
        Assert.Equal("Seat 1 played CABINET (+91).", restored.LastMove); // 14 face points × 2 × 2 + 35
        Assert.Equal([91, 0], restored.Scores);
        Assert.Equal(83, restored.BagCount);
    }

    [Fact]
    public void ANewPremiumScoresInEachCrossButIsSpentForLaterTurns()
    {
        var checkpoint = Deal("BAREINL", "MOWSCTU");
        checkpoint = Play(0, 7, 7, "BAR", "BAR", 6);
        checkpoint = Play(1, 6, 8, "MOW", "MOW, MA, OR", 24);
        checkpoint = Play(0, 7, 10, "E", "BARE, WE", 24);
        Assert.Equal([30, 24], Game.View(checkpoint, null).Scores);
        Assert.All(Game.View(Game.Apply(checkpoint, 1, Pass), 0).Board, cell => Assert.False(cell.Recent));

        string Play(int seat, int row, int column, string word, string formed, int points)
        {
            var action = Line(Game.View(checkpoint, seat), row, column, word);
            var next = Game.Apply(checkpoint, seat, action with { Placements = action.Placements!.Reverse().ToArray() });
            var restored = new WordGame().View(next, null);
            Assert.Equal($"Seat {seat + 1} played {formed} (+{points}).", restored.LastMove);
            Assert.Equal(action.Placements!.Select(step => (step.Row, step.Column)),
                restored.Board.Where(cell => cell.Recent).Select(cell => (cell.Row, cell.Column)));
            return next;
        }
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("F", 1)]
    [InlineData("FOXGUEA", 7)]
    public void ExchangeIsLimitedByAvailableReplacementsAndTheyAreDrawnBeforeReturns(string draws, int limit)
    {
        var checkpoint = NearEnd("CATERS?", "PIONDBL", draws, 52);
        var original = Game.View(checkpoint, 0);
        Assert.Equal((limit, 0), (original.ExchangeLimit, Game.View(checkpoint, 1).ExchangeLimit));
        var ids = original.Rack.Select(tile => tile.Id).ToArray();
        if (limit < ids.Length)
            Assert.Throws<InvalidOperationException>(() => Game.Apply(checkpoint, 0, new("exchange", Exchange: ids)));
        if (limit == 0)
            return;

        var next = Game.Apply(checkpoint, 0, new("exchange", Exchange: ids[..limit]));
        Assert.Equal(ids[..limit].Order(), WordCheckpoint.Load(next).Bag.Order());
        var after = Game.View(next, 0);
        Assert.Equal(draws, string.Concat(after.Rack.TakeLast(limit).Select(tile => tile.Letter)));
        Assert.Empty(ids[..limit].Intersect(after.Rack.Select(tile => tile.Id)));
        Assert.Equal(limit, after.BagCount);
        Assert.Equal(1, after.Turn);

        if (limit == 1)
        {
            var spent = Game.View(Game.Apply(checkpoint, 0, Line(original, 7, 9, "S")), null);
            Assert.Equal((0, "playing"), (spent.BagCount, spent.Status));
        }
    }

    [Fact]
    public void ScorelessTurnsDoNotEndZeroZeroAndAPositivePlayResetsTheStreak()
    {
        var checkpoint = Deal("??ARETN", "FAMOBUX");
        for (var turn = 0; turn < 8; turn++)
        {
            checkpoint = Game.Apply(checkpoint, turn % 2, Pass);
            Assert.Equal("playing", Game.View(checkpoint, null).Status);
        }
        var blanks = Game.View(checkpoint, 0).Rack.Where(tile => tile.Blank).ToArray();
        var zeroPlay = Game.Apply(checkpoint, 0, new("place", [new(7, 7, blanks[0].Id, "A"), new(7, 8, blanks[1].Id, "T")]));
        Assert.Equal("Seat 1 played AT (+0).", Game.View(zeroPlay, null).LastMove);
        Assert.Equal("playing", Game.View(zeroPlay, null).Status);

        var restored = new WordGame();
        var scoring = restored.Apply(zeroPlay, 1, Line(restored.View(zeroPlay, 1), 6, 7, "M"));
        Assert.Equal("Seat 2 played MA (+4).", restored.View(scoring, null).LastMove);
        var first = restored.Apply(scoring, 0, Pass);
        var second = restored.Apply(first, 1, new("exchange", Exchange: [restored.View(first, 1).Rack[0].Id]));
        Assert.Equal("playing", restored.View(second, null).Status);
        var finalCheckpoint = restored.Apply(second, 0, Pass);
        var final = restored.View(finalCheckpoint, null);
        Assert.Equal(("finished", null, "Three consecutive scoreless turns."), (final.Status, final.Turn, final.Detail));
        Assert.Equal([0, 4], final.Scores);
        Assert.Equal([1], final.Winners);
        Assert.Throws<InvalidOperationException>(() => restored.Apply(finalCheckpoint, 1, Pass));
    }

    [Theory]
    [InlineData(58, 38, 0)]
    [InlineData(59, 39, -1)]
    [InlineData(60, 40, 1)]
    public void GoingOutTransfersTheOpponentsRackValueButTheHigherFinalTotalWins(int opponent, int adjusted, int winner)
    {
        var checkpoint = NearEnd("S", "JZ", "", opponent);
        var ended = Game.Apply(checkpoint, 0, Line(Game.View(checkpoint, 0), 7, 9, "S"));
        var final = new WordGame().View(ended, null);
        Assert.Equal(("finished", null, 0), (final.Status, final.Turn, final.ExchangeLimit));
        Assert.Equal("A seat emptied its rack with no tiles left to draw.", final.Detail);
        Assert.Equal("Seat 1 played CATS (+7).", final.LastMove);
        Assert.Equal([0, 2], final.RackSizes);
        Assert.Equal([39, adjusted], final.Scores);
        Assert.Equal(winner < 0 ? [] : new[] { winner }, final.Winners);
    }

    [Fact]
    public void RestoredBlankStaysZeroAndCorruptDuelCheckpointsAreRejected()
    {
        var checkpoint = Deal("C?TERSI", "POLANDG");
        var acting = Game.View(checkpoint, 0);
        Assert.Throws<InvalidOperationException>(() => Game.Apply(checkpoint, 0, Line(acting, 7, 6, "C?T")));
        var played = Game.Apply(checkpoint, 0, Line(acting, 7, 6, "C?T", blankLetter: "A"));
        var restored = new WordGame();
        Assert.Equal("Seat 1 played CAT (+5).", restored.View(played, null).LastMove);
        var crossed = restored.Apply(played, 1, Line(restored.View(played, 1), 6, 7, "P"));
        var savedBlank = Assert.Single(restored.View(crossed, null).Board, cell => cell.Tile.Blank);
        Assert.Equal(("A", 0, false), (savedBlank.Tile.Letter, savedBlank.Tile.Points, savedBlank.Recent));
        Assert.Equal("Seat 2 played PA (+4).", restored.View(crossed, null).LastMove);

        Action<JsonNode>[] corruptions =
        [
            node => node["board"]![1]!.AsObject().Remove("letter"),
            node => node["board"]![0]!["row"] = 0,
            node => node["bag"]![0] = node["board"]![0]!["tileId"]!.GetValue<int>(),
            node => node["dryTurns"] = 4,
            node => node["version"] = 2
        ];
        foreach (var corrupt in corruptions)
        {
            var invalid = JsonNode.Parse(crossed)!;
            corrupt(invalid);
            Assert.Throws<InvalidDataException>(() => restored.View(invalid.ToJsonString(), 0));
            Assert.Throws<InvalidDataException>(() => restored.Apply(invalid.ToJsonString(), 0, Pass));
        }
    }

    private static string Deal(string first, string second)
    {
        var available = Inventory();
        int[] ordered = [.. Take(available, first + second), .. available];
        return new WordGame(WordLexicon.Default, tiles => ordered.CopyTo(tiles, 0)).Start();
    }

    private static string NearEnd(string first, string second, string draws, int opponent)
    {
        var available = Inventory();
        int[][] racks = [Take(available, first), Take(available, second)];
        var bag = Take(available, draws);
        var anchor = Take(available, "CAT");
        // Reserve row 7 column 9 for a final S with no perpendicular word.
        var filler = Enumerable.Range(8 * 15, 9).Concat(Enumerable.Range(9 * 15, 90));
        WordCheckpoint.PlacedTile[] board =
        [
            .. anchor.Select((id, index) => new WordCheckpoint.PlacedTile(7, 6 + index, id, 1)),
            .. available.Zip(filler, (id, index) => new WordCheckpoint.PlacedTile(index / 15, index % 15, id, 1,
                WordTiles.Duel.Get(id).Blank ? "E" : null))
        ];
        return new WordCheckpoint(3, 0, racks, bag, board, [12, opponent], 1, new("pass", 0, null, 0)).Save();
    }

    private static List<int> Inventory() => Enumerable.Range(1, 104).ToList();

    private static int[] Take(List<int> available, string letters) => letters.Select(letter =>
    {
        var id = available.First(id => WordTiles.Duel.Get(id).Letter[0] == letter);
        available.Remove(id);
        return id;
    }).ToArray();

    private static WordAction Line(WordView view, int row, int column, string word, string? blankLetter = null) =>
        new("place", word.Select((letter, offset) =>
        {
            var tile = view.Rack.First(tile => tile.Letter[0] == letter);
            return new WordPlacement(row, column + offset, tile.Id, tile.Blank ? blankLetter : null);
        }).ToArray());
}
