using System.Text.Json;
using Mahjong.Rules;
using static Mahjong.Rules.Tests.Fixture;

namespace Mahjong.Rules.Tests;

public sealed class TwoTileKongTests
{
    private const string Profile = ChangshaEngine.TwoTileKongProfile;
    private const string OneWait = "b9 b9 b9 b9 b1 b2 b3 c1 c2 c3 d1 d2 d3 c5";
    private const string TwoWaits = "b9 b9 b9 b9 b2 b3 c1 c2 c3 d1 d2 d3 c5 c5";
    private const string CatchBoth = "b5 b5 b5 b8 b8 b8 c8 c8 c8 d8 d8 d8 d5";
    private const string CatchD5 = "d4 d6 c1 c2 c3 d7 d8 d9 b2 b3 b4 c5 c5";
    private const string PengOnly = "d5 d5 b1 b3 b4 b6 b7 c4 c7 d1 d4 d6 d7";
    private const string Stock = "c5 d1 c8 d5 d7 b7 c6 c7 d6 d9 c9 d4";
    private static readonly RoundSeed SumSeed = new(0x0123456789abcdef, 0xfedcba9876543210);

    [Theory]
    [InlineData(true, "c5 d5 b5 d7 d8 c6 b7 b8 d6 d9", "sum", 5, "d5 c5")]
    [InlineData(true, "c5 b5 d7 d8 c6 b7 b8 d6 d9", "sum", 5, "c5")]
    [InlineData(false, "b5 d7 c5 d8 c6 b7 b8 d6 d9 c7", "lower", 4, "d8 c5")]
    [InlineData(false, "c5 d5 b5 d7 d8 c6", "tail", 1, "c6 d8")]
    public void RollCountsOccupiedPhysicalStacksFromOneAndNeverBorrowsFromAnotherStack(
        bool sumSeed, string stock, string method, int count, string faces)
    {
        var start = AtTurn([OneWait, "", "", ""], stock, seed: sumSeed ? SumSeed : null, profile: Profile);
        // Keep real slots, but leave gaps between occupied stacks as earlier rolls can do.
        var tailStack = start.Wall[^1].Slot / 2;
        start = start with
        {
            Wall = [.. start.Wall.Select(entry => entry with { Slot = entry.Slot - 2 * (tailStack - entry.Slot / 2) })]
        };
        var rolledAt = Now.AddSeconds(3);
        var rolled = Select(start, 0, "gang", now: rolledAt);
        var publicView = ChangshaEngine.ViewPublic(rolled);
        var roll = Assert.IsType<PublicKongRoll>(publicView.KongRoll);
        Assert.Equal(sumSeed ? new[] { 4, 1 } : new[] { 6, 4 }, roll.Dice);
        Assert.Equal(sumSeed ? 1 : 4, roll.LowerDie);
        Assert.Equal((0, (int?)count, method), (roll.Seat, roll.StackCount, roll.StackMethod));
        Assert.Equal(("turn", "turn", (int?)0), (publicView.Phase, publicView.Window!.Reason, publicView.TurnSeat));
        Assert.Equal(rolledAt + start.Start.TurnLimit, publicView.Window.ClosesAtGameTime);
        Assert.Null(publicView.Window.Trigger);
        var actual = rolled.Window!.KongTiles!.Value;
        Assert.Equal(faces.Split(' '), actual.Select(Tiles.Face));
        Assert.Equal(start.Wall.Where(entry => !actual.Contains(entry.Tile)), rolled.Wall);
        Assert.Equal(start.WindowNumber + 1, rolled.WindowNumber);
        Assert.Equal(10, publicView.Seats[0].ConcealedCount);
        Assert.Null(rolled.Seats[0].Drawn);
        AssertInventory(rolled);

        var recovered = RoundTrip(rolled);
        AssertInventory(recovered);
        Assert.Equal(Checkpoint(publicView), Checkpoint(ChangshaEngine.ViewPublic(recovered)));
        Assert.Equal(Checkpoint(ChangshaEngine.ViewSeat(rolled, 0)), Checkpoint(ChangshaEngine.ViewSeat(recovered, 0)));
        var discarded = Expire(recovered);
        Assert.Equal(actual, discarded.Seats[0].Discards.TakeLast(actual.Length));
        Assert.Equal(start.DiscardCount + 1, discarded.DiscardCount);
    }

    [Fact]
    public void EachPrivateCandidateCanBloomWithoutJoiningTheBaseHandOrBecomingAnotherBird()
    {
        var start = AtTurn([TwoWaits, "", "", ""], "b5 d1 b1 b4 d7 d8 c6 c7 d6 d9 c8 d4",
            dealer: 2, seed: SumSeed, profile: Profile);
        var rolled = Select(start, 0, "gang");
        var own = ChangshaEngine.ViewSeat(rolled, 0);
        var privateTiles = own.KongReplacements!.Value;
        var rack = own.Hand.Select(tile => tile.TileId).ToArray();
        Assert.Equal(new[] { "b4", "b1" }, privateTiles.Select(tile => tile.Face));
        Assert.Equal(2, privateTiles.Select(tile => tile.TileId).Distinct().Count());
        Assert.Null(own.DrawnTileId);
        Assert.Equal(new[] { "hu", "hu", "discardKong" }, own.LegalOptions.Select(option => option.Kind));
        Assert.Equal(privateTiles.Select(tile => tile.TileId), Option(rolled, 0, "discardKong").TileIds);
        Assert.Equal(new[] { "b4", "b1" }, Option(rolled, 0, "discardKong").Faces);
        var sameView = ChangshaEngine.ViewPublic(rolled);
        var publicJson = JsonSerializer.SerializeToElement(sameView, CheckpointJson);
        Assert.Equal(new[] { "dice", "lowerDie", "seat", "stackCount", "stackMethod" },
            publicJson.GetProperty("kongRoll").EnumerateObject().Select(field => field.Name).Order());
        Assert.False(publicJson.TryGetProperty("wall", out _));
        Assert.DoesNotContain("\"seed\"", publicJson.GetRawText());
        Assert.Equal(JsonValueKind.Array, JsonSerializer.SerializeToElement(own, CheckpointJson)
            .GetProperty("kongReplacements").ValueKind);
        foreach (var other in Enumerable.Range(1, 3).Select(seat => ChangshaEngine.ViewSeat(rolled, seat)))
        {
            Assert.Null(other.KongReplacements);
            Assert.Empty(other.LegalOptions);
            var otherJson = JsonSerializer.SerializeToElement(other, CheckpointJson);
            Assert.False(otherJson.TryGetProperty("kongReplacements", out _));
            Assert.All(rack.Concat(privateTiles.Select(tile => tile.TileId)), id =>
                Assert.DoesNotContain(id, otherJson.GetRawText()));
        }
        Assert.All(rack.Concat(privateTiles.Select(tile => tile.TileId)), id =>
            Assert.DoesNotContain(id, publicJson.GetRawText()));
        Assert.Equal(own.LegalOptions[0].OptionId, BotPolicy.Pick(sameView, own));

        var restored = RoundTrip(rolled);
        AssertInventory(restored);
        foreach (var choice in privateTiles)
        {
            Assert.DoesNotContain(choice.TileId, rack);
            var hu = Option(rolled, 0, "hu", choice.Face);
            Assert.Equal(choice.TileId, Assert.Single(hu.TileIds));
            Assert.Equal(choice.Face, Assert.Single(hu.Faces));
            var won = Select(restored, 0, "hu", choice.Face);
            Assert.Equal(Checkpoint(Select(rolled, 0, "hu", choice.Face)), Checkpoint(won));
            var settlement = Assert.IsType<Settlement>(won.Settlement);
            var winner = Assert.Single(settlement.Winners);
            Assert.Equal((0, "selfDraw", choice.Face), (winner.Seat, winner.WinBy, Assert.Single(winner.WinningTiles)));
            Assert.Equal("kongBloom", Assert.Single(winner.Patterns).Code);
            Assert.Equal(choice.Face, Tiles.Face(won.Seats[0].Drawn!.Value));
            Assert.Equal(Assert.Single(privateTiles, tile => tile != choice).Face, Tiles.Face(won.UnchosenKongTile!.Value));
            Assert.Equal(rolled.Seats[0].Discards.AsEnumerable(), won.Seats[0].Discards);
            Assert.Equal(new[] { "b5", "d1" }, settlement.Birds);
            Assert.Equal(start.Wall.Take(2).Select(entry => entry.Tile), won.Birds);
            AssertBalanced(settlement, 18, -3, -12, -3);
            Assert.Equal(won.Birds.Length, rolled.Wall.Length - won.Wall.Length);

            var terminal = ChangshaEngine.ViewPublic(won);
            Assert.Null(terminal.KongRoll);
            Assert.Null(ChangshaEngine.ViewSeat(won, 0).KongReplacements);
            Assert.Equal(own.Hand.Select(tile => tile.Face),
                Assert.Single(terminal.Settlement!.Winners).WinningHand.ConcealedTiles);
            var recovered = RoundTrip(won);
            AssertInventory(recovered);
            Assert.Equal(Checkpoint(won), Checkpoint(recovered));
            Assert.Equal(Checkpoint(terminal), Checkpoint(ChangshaEngine.ViewPublic(recovered)));
        }

        var decline = new Choose(0, rolled.Window!.Id, Option(rolled, 0, "discardKong").OptionId);
        Reject(rolled, decline, rolled.Window.ClosesAt, MoveStatus.WindowElapsed);
        Reject(rolled, new Expire(rolled.Window.Id), rolled.Window.ClosesAt.AddTicks(-1), MoveStatus.NotDue);
        Reject(rolled, decline with { Seat = 1 }, rolled.AdvancedAtGameTime, MoveStatus.IllegalChoice);
        Reject(rolled, decline, rolled.AdvancedAtGameTime.AddTicks(-1), MoveStatus.IllegalChoice);
        var timedOut = Expire(restored, rolled.Window.ClosesAt.AddMinutes(2));
        Assert.Equal(rolled.Window.ClosesAt, timedOut.AdvancedAtGameTime);
        var manual = Move(rolled, decline, rolled.Window.ClosesAt.AddTicks(-1));
        Assert.Equal(Checkpoint(manual.Seats[0]), Checkpoint(timedOut.Seats[0]));
        Assert.Equal(manual.Wall.AsEnumerable(), timedOut.Wall);
        Assert.Equal(manual.Window?.Id, timedOut.Window?.Id);
        Assert.Equal(rolled.Window.KongTiles!.Value, timedOut.Seats[0].Discards.TakeLast(2));
        Assert.Equal(rolled.DiscardCount + 1, timedOut.DiscardCount);
        Reject(timedOut, decline, timedOut.AdvancedAtGameTime, MoveStatus.StaleWindow);
    }

    [Theory]
    [InlineData("d5", 18, 1)]
    [InlineData("c8", 12, 2)]
    public void CannonSettlesExactTilesAndStructuralUnitsInSeatOrder(string firstFace, int firstPoints, int caughtCount)
    {
        var rolled = RollCannon();
        var current = ChangshaEngine.ViewSeat(rolled, 0);
        var throwBoth = Assert.Single(current.LegalOptions);
        Assert.Equal("discardKong", throwBoth.Kind);
        Assert.Equal(throwBoth.OptionId, BotPolicy.Pick(ChangshaEngine.ViewPublic(rolled), current));
        var claim = Select(rolled, 0, "discardKong");
        var table = ChangshaEngine.ViewPublic(claim);
        Assert.Equal(("claim", "kong", (int?)null), (table.Phase, table.Window!.Reason, table.TurnSeat));
        Assert.Equal(0, table.Window.Trigger!.Seat);
        Assert.Equal(new[] { "d5", "c8" }, table.Window.Trigger.Faces);
        Assert.Equal(table.Window.Trigger.Faces.AsEnumerable(), table.Seats[0].Discards.TakeLast(2));
        Assert.Null(table.KongRoll);
        Assert.Equal(claim.AdvancedAtGameTime + rolled.Start.ClaimLimit, table.Window.ClosesAtGameTime);
        Assert.Equal(rolled.WindowNumber + 1, claim.WindowNumber);
        Assert.Equal(rolled.DiscardCount + 1, claim.DiscardCount);
        Assert.Equal(rolled.Window!.KongTiles!.Value, claim.Seats[0].Discards.TakeLast(2));
        Assert.Empty(ChangshaEngine.ViewSeat(claim, 3).LegalOptions);
        foreach (var seat in Enumerable.Range(0, 4))
        {
            var self = ChangshaEngine.ViewSeat(claim, seat);
            Assert.Null(self.KongReplacements);
            Assert.All(self.LegalOptions, option => Assert.Empty(option.TileIds));
        }
        Assert.Equal(new[] { "hu:d5", "hu:c8", "pass:" }, OptionSummary(claim, 1));
        Assert.Equal(new[] { "hu:d5", "pass:" }, OptionSummary(claim, 2));
        var second = Select(claim, 2, "hu", "d5");
        Assert.Null(second.Settlement);
        Assert.Equal(Checkpoint(table), Checkpoint(ChangshaEngine.ViewPublic(second)));
        Assert.Empty(ChangshaEngine.ViewSeat(second, 2).LegalOptions);
        var duplicate = new Choose(2, second.Window!.Id, Option(claim, 2, "hu", "d5").OptionId);
        Reject(second, duplicate, second.AdvancedAtGameTime, MoveStatus.IllegalChoice);
        var recovered = RoundTrip(second);
        AssertInventory(recovered);
        var first = new Choose(1, recovered.Window!.Id, Option(claim, 1, "hu", firstFace).OptionId);
        Reject(recovered, first, recovered.Window.ClosesAt, MoveStatus.WindowElapsed);
        var won = Move(recovered, first, recovered.AdvancedAtGameTime);
        Assert.Equal(Checkpoint(Select(Select(claim, 1, "hu", firstFace), 2, "hu", "d5")), Checkpoint(won));

        var settlement = Assert.IsType<Settlement>(won.Settlement);
        Assert.Equal(0, settlement.SourceSeat);
        Assert.Equal(0, settlement.NextDealerSeat);
        Assert.Equal(new[] { (1, "discard", firstFace), (2, "discard", "d5") },
            settlement.Winners.Select(winner => (winner.Seat, winner.WinBy, Assert.Single(winner.WinningTiles))));
        Assert.Equal(firstFace == "d5" ? new[] { "allPungs", "allGenerals", "kongCannon" } : ["allGenerals", "kongCannon"],
            settlement.Winners[0].Patterns.Select(pattern => pattern.Code));
        Assert.Equal("kongCannon", Assert.Single(settlement.Winners[1].Patterns).Code);
        Assert.Equal(new[] { new PayerTransfer(0, 1, firstPoints, "win"), new(0, 2, 21, "win") }, settlement.Transfers);
        AssertBalanced(settlement, -firstPoints - 21, firstPoints, 21, 0);
        Assert.Equal(new[] { "c5", "d1" }, settlement.Birds);
        Assert.Equal(claim.Wall.Take(2).Select(tile => tile.Tile), won.Birds);
        Assert.Equal(claim.Wall.Skip(2), won.Wall);
        Assert.Equal(rolled.Window.KongTiles.Value.Take(caughtCount), won.ClaimedKongWinTiles!.Value);
        Assert.Equal(claim.Seats[0].Discards.RemoveRange(won.ClaimedKongWinTiles.Value).AsEnumerable(), won.Seats[0].Discards);
        if (caughtCount == 1)
            Assert.Contains(rolled.Window.KongTiles.Value[1], won.Seats[0].Discards);
        var terminal = RoundTrip(won);
        AssertInventory(terminal);
        Assert.Equal(Checkpoint(won), Checkpoint(terminal));
        Assert.Equal(Checkpoint(ChangshaEngine.ViewPublic(won)), Checkpoint(ChangshaEngine.ViewPublic(terminal)));
        Reject(terminal, first, terminal.AdvancedAtGameTime, MoveStatus.StaleWindow);
    }

    [Fact]
    public void DealersFirstBatchIsOneDiscardEventForEarthlyCannon()
    {
        var dealt = Deal([OneWait, CatchBoth, CatchD5, PengOnly], front: "c5 d1",
            rearDrawOrder: "d4 c9 d9 d6 c7 c6 b7 d7 d2 d3 c4 c6 d5 c8", profile: Profile);
        AssertInventory(dealt);
        var rolled = Select(Expire(dealt), 0, "gang");
        var claim = Expire(rolled);
        Assert.Equal(1, claim.DiscardCount);
        Assert.Equal(new[] { "d5", "c8" }, ChangshaEngine.ViewPublic(claim).Window!.Trigger!.Faces);
        var won = Expire(Select(claim, 2, "hu", "d5"));
        Assert.Equal(new[] { "earthly", "kongCannon" },
            Assert.Single(won.Settlement!.Winners).Patterns.Select(pattern => pattern.Code));
        Assert.Equal(new[] { "c5", "d1" }, won.Settlement.Birds);
        AssertBalanced(won.Settlement, -42, 0, 42, 0);
    }

    [Fact]
    public void IdenticalFacesOfferDistinctPhysicalChoicesAndOnlyRemoveEachCaughtTileOnce()
    {
        var rolled = RollCannon(Stock.Replace("c8 d5", "d5 d5", StringComparison.Ordinal), identical: true);
        var candidates = rolled.Window!.KongTiles!.Value;
        var claim = Select(rolled, 0, "discardKong");
        Assert.Equal(new[] { "d5", "d5" }, ChangshaEngine.ViewPublic(claim).Window!.Trigger!.Faces);
        var choices = new[] { 1, 2 }.Select(seat => ChangshaEngine.ViewSeat(claim, seat).LegalOptions
            .Where(option => option.Kind == "hu").ToArray()).ToArray();
        foreach (var options in choices)
        {
            Assert.Equal(2, options.Length);
            Assert.NotEqual(options[0].OptionId, options[1].OptionId);
            Assert.All(options, option =>
            {
                Assert.Equal("d5", Assert.Single(option.Faces));
                Assert.Empty(option.TileIds);
            });
        }
        var second = Move(claim, new Choose(2, claim.Window!.Id, choices[1][1].OptionId), claim.AdvancedAtGameTime);
        for (var index = 0; index < 2; index++)
        {
            var won = Move(RoundTrip(second), new Choose(1, claim.Window.Id, choices[0][index].OptionId), second.AdvancedAtGameTime);
            Assert.All(won.Settlement!.Winners, winner => Assert.Equal("d5", Assert.Single(winner.WinningTiles)));
            AssertBalanced(won.Settlement, -39, 18, 21, 0);
            Assert.Equal(index == 0 ? candidates.AsEnumerable() : [candidates[1]], won.ClaimedKongWinTiles!.Value);
            var river = index == 0 ? claim.Seats[0].Discards.RemoveRange(candidates) : claim.Seats[0].Discards.Remove(candidates[1]);
            Assert.Equal(river.AsEnumerable(), won.Seats[0].Discards);
        }

        var passed = Expire(second);
        Assert.Equal(Tiles.Parse("d5"), Assert.Single(passed.Seats[1].PassedWinFaces));
        Assert.Equal(2, passed.Settlement!.NextDealerSeat);
    }

    [Fact]
    public void PassedBatchFacesStayMissedUntilTheSeatDrawsAndTheThrowerStaysLocked()
    {
        var rolled = RollCannon();
        var claim = Select(rolled, 0, "discardKong");
        var won = Expire(Select(claim, 2, "hu", "d5"));
        Assert.Equal(new[] { Tiles.Parse("d5"), Tiles.Parse("c8") }, won.Seats[1].PassedWinFaces);
        AssertBalanced(won.Settlement!, -21, 0, 21, 0);

        var next = Expire(RoundTrip(Select(claim, 2, "pass")));
        Assert.Equal(claim.Window!.ClosesAt, next.AdvancedAtGameTime);
        Assert.Equal((1, DrawKind.Ordinary), (next.Window!.Seat, next.Window.DrawKind));
        Assert.Equal(claim.WindowNumber + 1, next.WindowNumber);
        Assert.Equal(claim.Wall[0].Tile, next.Seats[1].Drawn);
        Assert.Equal(claim.Wall.Skip(1), next.Wall);
        Assert.Equal(claim.Seats[0].Discards.AsEnumerable(), next.Seats[0].Discards);
        Assert.Empty(next.Seats[1].PassedWinFaces);
        Assert.Equal(Tiles.Parse("d5"), Assert.Single(next.Seats[2].PassedWinFaces));
        var another = Select(next, 1, "discard", "d5");
        Assert.DoesNotContain("hu:d5", OptionSummary(another, 2));
        if (another.Window!.Kind == DecisionKind.Discard)
            another = Expire(another);
        Assert.Equal(2, another.Window!.Seat);
        Assert.Empty(another.Seats[2].PassedWinFaces);

        for (var step = 0; step < 15 && ChangshaEngine.ViewPublic(another).TurnSeat != 0; step++)
            another = Expire(another);
        Assert.Equal(0, ChangshaEngine.ViewPublic(another).TurnSeat);
        Assert.True(another.Seats[0].FrozenAfterGang);
        var holder = ChangshaEngine.ViewSeat(another, 0);
        Assert.Equal(holder.DrawnTileId, Assert.Single(Option(another, 0, "discard").TileIds));
        Assert.All(holder.LegalOptions, option => Assert.Contains(option.Kind, new[] { "hu", "discard" }));
        Assert.Single(holder.LegalOptions, option => option.Kind == "discard");
    }

    [Theory]
    [InlineData("c5", "c5", "", 10, true)]
    [InlineData("d5 c5", "c5 d5", "", 10, true)]
    [InlineData("d1 d5 c5", "c5 d5", "d1", 14, false)]
    public void TakingTheLastStackDoesNotCreateSeabedOrPhantomBirds(string stock, string faces,
        string birds, int paidToWinner, bool takesLast)
    {
        var start = AtTurn([OneWait, "", "", ""], stock, dealer: 2, seed: SumSeed, profile: Profile);
        var rolled = Select(start, 0, "gang");
        Assert.Equal(faces.Split(' '), ChangshaEngine.ViewSeat(rolled, 0).KongReplacements!.Value.Select(tile => tile.Face));
        Assert.Equal(takesLast ? (int?)0 : null, rolled.LastTileSeat);
        var won = Select(RoundTrip(rolled), 0, "hu", "c5");
        Assert.Equal("kongBloom", Assert.Single(Assert.Single(won.Settlement!.Winners).Patterns).Code);
        Assert.Equal(birds.Split(' ', StringSplitOptions.RemoveEmptyEntries), won.Settlement.Birds);
        Assert.Empty(won.Wall);
        AssertBalanced(won.Settlement, paidToWinner, -3, paidToWinner == 10 ? -4 : -8, -3);
        if (!takesLast)
            return;

        var draw = TimeoutHand(Expire(rolled));
        Assert.Equal(("draw", 0), (draw.Settlement!.Reason, draw.Settlement.NextDealerSeat));
        Assert.Empty(draw.Settlement.Birds);
        Assert.Equal(rolled.Window!.KongTiles!.Value, draw.Seats[0].Discards.TakeLast(faces.Split(' ').Length));
        AssertBalanced(draw.Settlement, 0, 0, 0, 0);
    }

    [Theory]
    [InlineData("d5 c5 d1 d7 b7 c6 c7 d6 d9", "d5", "c5 d1", 21)]
    [InlineData("c8 d5", "d5 c8", "", 7)]
    public void CatchOfPartialOrFinalStackUsesOnlyActualReplacementsAndLiveBirds(string stock, string faces,
        string birds, int points)
    {
        var rolled = RollCannon(stock);
        var claim = Expire(rolled);
        Assert.Equal(faces.Split(' '), ChangshaEngine.ViewPublic(claim).Window!.Trigger!.Faces);
        var won = Expire(Select(claim, 2, "hu", "d5"));
        Assert.Equal("kongCannon", Assert.Single(Assert.Single(won.Settlement!.Winners).Patterns).Code);
        Assert.Equal(birds.Split(' ', StringSplitOptions.RemoveEmptyEntries), won.Settlement.Birds);
        Assert.Equal(rolled.Window!.KongTiles!.Value[0], Assert.Single(won.ClaimedKongWinTiles!.Value));
        AssertBalanced(won.Settlement, -points, 0, points, 0);
    }

    private static GameState RollCannon(string stock = Stock, bool identical = false) => Select(AtTurn(
        [OneWait, CatchBoth, CatchD5, identical ? "b6 b6 c6 b1 b3 b4 b6 b7 c4 c7 d1 d4 d6" :
            PengOnly], stock, dealer: 2, seed: SumSeed, profile: Profile),
        0, "gang");

    private static string[] OptionSummary(GameState state, int seat) =>
        [.. ChangshaEngine.ViewSeat(state, seat).LegalOptions.Select(option => $"{option.Kind}:{string.Join(',', option.Faces)}")];

    private static void Reject(GameState state, GameAction action, DateTimeOffset at, MoveStatus status)
    {
        var result = ChangshaEngine.Apply(state, action, at);
        Assert.Equal(status, result.Status);
        Assert.Same(state, result.State);
    }
}
