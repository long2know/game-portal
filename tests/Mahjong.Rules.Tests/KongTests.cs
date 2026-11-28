using System.Text.Json;
using Mahjong.Rules;
using static Mahjong.Rules.Tests.Fixture;

namespace Mahjong.Rules.Tests;

public sealed class KongTests
{
    private static readonly RoundSeed RolledSeed = new(0x0123456789abcdef, 0xfedcba9876543210);
    private const string AddedReadyHand = "b5 d1 d2 d3 c1 c2 c3 b6 b7 b8 d5";
    private const string CanRob = "b3 b4 c4 c5 c6 d4 d5 d6 d7 d8 d9 c5 c5";

    [Theory]
    [InlineData(ChangshaEngine.Profile)]
    [InlineData(ChangshaEngine.TwoTileKongProfile)]
    public void OpenBuTakesTheDiscardAndOneTailTileWithoutRequiringReadyShape(string profile)
    {
        var state = AtTurn(
        [
            "b7", "", "b7 b7 b7 b1 b2 b3 c1 c2 c3 d1 d2 d3 c9", ""
        ], "d7 c7 d9", profile: profile);
        var offered = Select(state, 0, "discard", "b7");
        Assert.True(Has(offered, 2, "bu"));
        Assert.False(Has(offered, 2, "gang"));
        var done = Select(offered, 2, "bu");
        if (done.Window!.Kind == DecisionKind.Discard)
            done = Expire(done);
        var meld = Assert.Single(ChangshaEngine.ViewPublic(done).Seats[2].Melds);
        Assert.Equal("bu", meld.Kind);
        Assert.Equal("open", meld.Method);
        Assert.Equal(0, meld.FromSeat);
        Assert.Equal(new[] { "b7", "b7", "b7", "b7" }, meld.Tiles);
        Assert.Equal("d9", Tiles.Face(done.Seats[2].Drawn!.Value));
        Assert.Equal(new[] { "d7", "c7" }, done.Wall.Select(tile => Tiles.Face(tile.Tile)));
        Assert.DoesNotContain(offered.Window!.Trigger!.Value, done.Seats[0].Discards);
        Assert.Equal(DecisionKind.Turn, done.Window!.Kind);
        Assert.Equal(2, done.Window.Seat);
        Assert.Null(ChangshaEngine.ViewPublic(done).KongRoll);
        Assert.Null(ChangshaEngine.ViewSeat(done, 2).KongReplacements);
    }

    [Theory]
    [InlineData(ChangshaEngine.Profile)]
    [InlineData(ChangshaEngine.TwoTileKongProfile)]
    public void OpenReadyGangCannotBeRobbedAndUsesRolledSupplement(string profile)
    {
        var state = AtTurn(
        [
            "b7", "", "b7 b7 b7 b1 b2 b3 c1 c2 c3 d1 d2 d3 c5", ""
        ], "c9 c5", profile: profile);
        var offered = Select(state, 0, "discard", "b7");
        Assert.Contains("b7", Option(offered, 2, "gang").Faces);
        var done = Select(offered, 2, "gang");
        if (done.Window!.Kind == DecisionKind.Discard)
            done = Expire(done);
        Assert.Equal(DecisionKind.Turn, done.Window!.Kind);
        Assert.Equal(DrawKind.RolledSupplement, done.Window.DrawKind);
        Assert.Equal("gang", Assert.Single(done.Seats[2].Melds).Kind);
        Assert.Equal("open", Assert.Single(done.Seats[2].Melds).Method);
        var own = ChangshaEngine.ViewSeat(done, 2);
        if (profile == ChangshaEngine.TwoTileKongProfile)
        {
            Assert.Null(own.DrawnTileId);
            Assert.Equal(new[] { "c5", "c9" }, own.KongReplacements!.Value.Select(tile => tile.Face));
            Assert.Equal(new[] { "hu", "discardKong" }, own.LegalOptions.Select(option => option.Kind));
        }
        else
            Assert.Equal("c5", Tiles.Face(done.Seats[2].Drawn!.Value));
        Assert.Equal("c5", Assert.Single(Option(done, 2, "hu").Faces));
        Assert.Equal(2, ChangshaEngine.ViewPublic(done).KongRoll!.Seat);
    }

    [Fact]
    public void HuOnTheOriginalDiscardBeatsOpenGangBeforeReplacement()
    {
        var state = AtTurn(
        [
            "b7",
            "b6 b8 c1 c2 c3 d1 d2 d3 d7 d8 d9 c2 c2",
            "b7 b7 b7 b1 b2 b3 c1 c2 c3 d1 d2 d3 c5",
            ""
        ], "c9 c5");
        var offered = Select(state, 0, "discard", "b7");
        var both = Select(Select(offered, 2, "gang"), 1, "hu");
        if (both.Window is not null)
            both = Expire(both);
        Assert.Empty(both.Seats[2].Melds);
        Assert.Equal("discard", Assert.Single(both.Settlement!.Winners).WinBy);
        Assert.Equal("c5", Tiles.Face(Assert.Single(both.Wall).Tile));
    }

    [Fact]
    public void ConcealedBuDoesNotOfferRobAndKeepsFacesRedacted()
    {
        var state = AtTurn(
        [
            "b9 b9 b9 b9 b1 b2 b3 c1 c2 c3 d1 d2 d3 c5",
            "b7 b8 c1 c2 c3 d1 d2 d3 d4 d5 d6 c8 c8", "", ""
        ], "d9 c5", melds: [new(2, "bu", "d8 d8 d8 d8", null, "concealed")]);
        var old = Checkpoint(state);
        var done = Select(state, 0, "bu", "b9");
        Assert.Equal(old, Checkpoint(state));
        Assert.Equal(DecisionKind.Turn, done.Window!.Kind);
        var ongoing = ChangshaEngine.ViewPublic(done);
        Assert.Null(ongoing.Settlement);
        var meld = Assert.Single(ongoing.Seats[0].Melds);
        Assert.Equal("concealed", meld.Method);
        Assert.Null(meld.FromSeat);
        Assert.Equal(new string?[] { null, null, null, null }, meld.Tiles);
        Assert.Empty(ChangshaEngine.ViewSeat(done, 1).LegalOptions);
        var won = Select(done, 0, "hu");
        var settlement = won.Settlement!;
        Assert.Equal("plain", Assert.Single(Assert.Single(settlement.Winners).Patterns).Code);

        var saved = Checkpoint(won);
        var json = JsonSerializer.SerializeToElement(ChangshaEngine.ViewPublic(won), CheckpointJson);
        var winner = Assert.Single(json.GetProperty("settlement").GetProperty("winners").EnumerateArray());
        Assert.Equal("c5", Assert.Single(winner.GetProperty("winningTiles").EnumerateArray()).GetString());
        var hand = winner.GetProperty("winningHand");
        Assert.Equal(new[] { "b1", "b2", "b3", "c1", "c2", "c3", "c5", "d1", "d2", "d3" },
            hand.GetProperty("concealedTiles").EnumerateArray().Select(tile => tile.GetString()));
        Assert.Equal(new[] { "concealedTiles", "melds" }, hand.EnumerateObject().Select(field => field.Name).Order());
        var shownMeld = Assert.Single(hand.GetProperty("melds").EnumerateArray());
        Assert.Equal("bu", shownMeld.GetProperty("kind").GetString());
        Assert.Equal("concealed", shownMeld.GetProperty("method").GetString());
        Assert.Equal(JsonValueKind.Null, shownMeld.GetProperty("fromSeat").ValueKind);
        Assert.Equal(new[] { "b9", "b9", "b9", "b9" }, shownMeld.GetProperty("tiles").EnumerateArray().Select(tile => tile.GetString()));
        Assert.Equal(new[] { "fromSeat", "kind", "method", "tiles" }, shownMeld.EnumerateObject().Select(field => field.Name).Order());
        foreach (var seat in new[] { 0, 2 })
        {
            var tableMeld = Assert.Single(json.GetProperty("seats")[seat].GetProperty("melds").EnumerateArray());
            Assert.Equal("[null,null,null,null]", tableMeld.GetProperty("tiles").GetRawText());
        }

        Assert.DoesNotContain("\"winningHand\"", saved);
        Assert.Equal(saved, Checkpoint(won));
        Assert.Equal(json.GetRawText(), Checkpoint(ChangshaEngine.ViewPublic(RoundTrip(won))));
    }

    [Theory]
    [InlineData("bu", "plain", ChangshaEngine.Profile, "d5", 1)]
    [InlineData("gang", "robKong", ChangshaEngine.Profile, "d5", 6)]
    [InlineData("bu", "plain", ChangshaEngine.TwoBirdProfile, "d5 c4", 2)]
    [InlineData("gang", "robKong", ChangshaEngine.TwoBirdProfile, "d5 c4", 12)]
    [InlineData("bu", "plain", ChangshaEngine.TwoTileKongProfile, "d5 c4", 2)]
    [InlineData("gang", "robKong", ChangshaEngine.TwoTileKongProfile, "d5 c4", 12)]
    public void RobbingAnAddedKongKeepsOriginalPengAndOnlyTakesTheAddedTile(string kind, string code,
        string profile, string birds, int points)
    {
        var state = profile == ChangshaEngine.Profile ? Added() :
            AtTurn([AddedReadyHand, CanRob, "", ""], "d5 c4 c8 d6 d7 c9", dealer: 2, drawn: "b5",
                melds: [new(0, "peng", "b5 b5 b5", 3)], profile: profile);
        var pending = Select(state, 0, kind, "b5");
        var view = ChangshaEngine.ViewPublic(pending);
        Assert.Null(view.Settlement);
        Assert.Null(view.KongRoll);
        Assert.Null(ChangshaEngine.ViewSeat(pending, 0).KongReplacements);
        Assert.Equal("kong", view.Window!.Reason);
        Assert.Equal("b5", Assert.Single(view.Window.Trigger!.Faces));
        Assert.Equal("peng", Assert.Single(view.Seats[0].Melds).Kind);
        Assert.Equal(state.Wall.Length, pending.Wall.Length);
        Assert.Equal(state.Seats[0].Hand, pending.Seats[0].Hand);
        Assert.Equal(new[] { "hu", "pass" }, ChangshaEngine.ViewSeat(pending, 1).LegalOptions.Select(option => option.Kind));
        var resumed = RoundTrip(pending);
        var robbed = Select(resumed, 1, "hu");
        if (robbed.Window is not null)
            robbed = Expire(robbed);
        var settlement = robbed.Settlement!;
        var winner = Assert.Single(settlement.Winners);
        Assert.Equal("robKong", winner.WinBy);
        Assert.Equal(code, Assert.Single(winner.Patterns).Code);
        Assert.Equal(0, settlement.SourceSeat);
        Assert.Equal(1, settlement.NextDealerSeat);
        var expectedBirds = birds.Split(' ');
        Assert.Equal(expectedBirds, settlement.Birds);
        Assert.Equal(state.Wall.Length - expectedBirds.Length, robbed.Wall.Length);
        Assert.Equal("peng", Assert.Single(robbed.Seats[0].Melds).Kind);
        Assert.Equal(pending.Window!.Trigger, robbed.ClaimedWinTile);
        Assert.DoesNotContain(pending.Window.Trigger!.Value, robbed.Seats[0].Hand);
        AssertBalanced(settlement, -points, points, 0, 0);

        var terminal = ChangshaEngine.ViewPublic(robbed);
        Assert.Null(terminal.KongRoll);
        var projected = Assert.Single(terminal.Settlement!.Winners);
        Assert.Equal("b5", Assert.Single(projected.WinningTiles));
        Assert.Equal(new[] { "b3", "b4", "c4", "c5", "c5", "c5", "c6", "d4", "d5", "d6", "d7", "d8", "d9" },
            projected.WinningHand.ConcealedTiles);
        Assert.Empty(projected.WinningHand.Melds);
    }

    [Theory]
    [InlineData("bu", "c9", ChangshaEngine.Profile)]
    [InlineData("gang", "c2", ChangshaEngine.Profile)]
    [InlineData("bu", "c9", ChangshaEngine.TwoTileKongProfile)]
    [InlineData("gang", "d6 c8", ChangshaEngine.TwoTileKongProfile)]
    public void AddedKongFinishesAfterPassAndRetainsSourceOfTheOriginalMeld(string kind, string replacement, string profile)
    {
        var pending = Select(Added(profile), 0, kind, "b5");
        Assert.Null(ChangshaEngine.ViewPublic(pending).KongRoll);
        var done = Expire(pending);
        Assert.Equal(Checkpoint(done), Checkpoint(Expire(RoundTrip(pending))));
        var view = ChangshaEngine.ViewPublic(done);
        var meld = Assert.Single(view.Seats[0].Melds);
        Assert.Equal(kind, meld.Kind);
        Assert.Equal("added", meld.Method);
        Assert.Equal(3, meld.FromSeat);
        Assert.Equal(new[] { "b5", "b5", "b5", "b5" }, meld.Tiles);
        Assert.Equal(DecisionKind.Turn, done.Window!.Kind);
        if (kind == "gang" && profile == ChangshaEngine.TwoTileKongProfile)
        {
            Assert.Null(done.Seats[0].Drawn);
            Assert.Equal(replacement.Split(' '), ChangshaEngine.ViewSeat(done, 0).KongReplacements!.Value.Select(tile => tile.Face));
            Assert.Equal(new[] { "d5", "c2", "d7", "c9" }, done.Wall.Select(tile => Tiles.Face(tile.Tile)));
        }
        else
            Assert.Equal(replacement, Tiles.Face(done.Seats[0].Drawn!.Value));
        if (kind == "gang")
        {
            var roll = Assert.IsType<PublicKongRoll>(view.KongRoll);
            Assert.Equal(new[] { 5, 2 }, roll.Dice);
            Assert.Equal(2, roll.LowerDie);
        }
        else
            Assert.Null(view.KongRoll);
    }

    [Fact]
    public void APreviouslyHeldFourthTileCannotUpgradeAPeng()
    {
        var state = AtTurn([AddedReadyHand, "", "", ""], "d5 c2 c8 d6 d7 c9", drawn: "d5",
            melds: [new(0, "peng", "b5 b5 b5", 3)]);
        Assert.False(Has(state, 0, "bu"));
        Assert.False(Has(state, 0, "gang"));
    }

    [Theory]
    [InlineData(ChangshaEngine.Profile, "b8", false, false)]
    [InlineData(ChangshaEngine.TwoBirdProfile, "b8", false, false)]
    [InlineData(ChangshaEngine.TwoTileKongProfile, "b8", true, true)]
    [InlineData(ChangshaEngine.TwoTileKongProfile, "b1", true, false)]
    public void RetainedFourthCanOnlyUpgradeOnAnEligibleDrawAndOnlyGangRequiresReady(
        string profile, string drawn, bool canBu, bool canGang)
    {
        var (state, fourth) = DrawAfterPeng(profile, drawn);
        var view = ChangshaEngine.ViewSeat(state, 0);
        var kongs = view.LegalOptions.Where(option => option.Kind is "bu" or "gang").ToArray();
        Assert.Equal(canGang ? new[] { "bu", "gang" } : canBu ? ["bu"] : [],
            kongs.Select(option => option.Kind));
        Assert.All(kongs, option =>
        {
            Assert.Equal(fourth.TileId, Assert.Single(option.TileIds));
            Assert.Equal(new[] { "b5", "b5", "b5", "b5" }, option.Faces);
        });
        var restored = RoundTrip(state);
        AssertInventory(restored);
        Assert.Equal(Checkpoint(view), Checkpoint(ChangshaEngine.ViewSeat(restored, 0)));
    }

    [Theory]
    [InlineData("bu", "plain", 1)]
    [InlineData("gang", "robKong", 6)]
    public void RetainedFourthIsTheRobTileAndPassingCommitsTheSelectedMethod(string kind, string robPattern, int robPoints)
    {
        var (state, fourth) = DrawAfterPeng(ChangshaEngine.TwoTileKongProfile, "b8");
        var held = Assert.Single(state.Seats[0].Hand, tile => Tiles.Face(tile) == fourth.Face);
        var unrelatedDraw = state.Seats[0].Drawn!.Value;
        var peng = Assert.Single(state.Seats[0].Melds);
        var pending = Select(state, 0, kind, "b5");
        var table = ChangshaEngine.ViewPublic(pending);
        Assert.Equal("kong", table.Window!.Reason);
        Assert.Equal("b5", Assert.Single(table.Window.Trigger!.Faces));
        Assert.Equal(held, pending.Window!.Trigger);
        Assert.Null(table.KongRoll);
        Assert.Null(ChangshaEngine.ViewSeat(pending, 0).KongReplacements);
        Assert.Equal("peng", Assert.Single(table.Seats[0].Melds).Kind);
        Assert.Contains(held, pending.Seats[0].Hand);
        Assert.Equal(unrelatedDraw, pending.Seats[0].Drawn);
        Assert.Equal(state.Wall.AsEnumerable(), pending.Wall);
        var rob = Option(pending, 1, "hu");
        Assert.Equal("b5", Assert.Single(rob.Faces));
        Assert.Empty(rob.TileIds);

        var restored = RoundTrip(pending);
        AssertInventory(restored);
        var robbed = Select(restored, 1, "hu");
        if (robbed.Window is not null)
            robbed = Expire(robbed);
        var winner = Assert.Single(robbed.Settlement!.Winners);
        Assert.Equal((1, "robKong", "b5"), (winner.Seat, winner.WinBy, Assert.Single(winner.WinningTiles)));
        Assert.Equal(robPattern, Assert.Single(winner.Patterns).Code);
        Assert.Equal(held, robbed.ClaimedWinTile);
        Assert.Equal("peng", Assert.Single(robbed.Seats[0].Melds).Kind);
        Assert.DoesNotContain(held, robbed.Seats[0].Hand);
        Assert.Contains(unrelatedDraw, robbed.Seats[0].Hand);
        Assert.Equal(new[] { "c4", "d9" }, robbed.Settlement.Birds);
        AssertBalanced(robbed.Settlement, -robPoints, robPoints, 0, 0);

        var passed = Expire(restored);
        var upgraded = Assert.Single(passed.Seats[0].Melds);
        Assert.Equal((kind, "added", (int?)3), (upgraded.Kind, upgraded.Method, upgraded.FromSeat));
        Assert.Equal(Tiles.Sort(peng.Tiles.Add(held)).AsEnumerable(), upgraded.Tiles);
        Assert.DoesNotContain(held, passed.Seats[0].Hand);
        Assert.Contains(unrelatedDraw, passed.Seats[0].Hand);
        Assert.Equal(Checkpoint(Expire(pending)), Checkpoint(passed));
        var own = ChangshaEngine.ViewSeat(passed, 0);
        if (kind == "bu")
        {
            Assert.Equal(state.Wall.Take(state.Wall.Length - 1), passed.Wall);
            Assert.Equal(state.Wall[^1].Tile, passed.Seats[0].Drawn);
            Assert.Null(ChangshaEngine.ViewPublic(passed).KongRoll);
            Assert.Null(own.KongReplacements);
            var won = Select(passed, 0, "hu").Settlement!;
            var selfDraw = Assert.Single(won.Winners);
            Assert.Equal(("selfDraw", "d5"), (selfDraw.WinBy, Assert.Single(selfDraw.WinningTiles)));
            Assert.Equal("plain", Assert.Single(selfDraw.Patterns).Code);
            Assert.Equal(new[] { "c4", "d9" }, won.Birds);
            AssertBalanced(won, 7, -1, -2, -4);
        }
        else
        {
            Assert.Equal("sum", ChangshaEngine.ViewPublic(passed).KongRoll!.StackMethod);
            Assert.Null(own.DrawnTileId);
            var candidates = own.KongReplacements!.Value;
            Assert.Equal(2, candidates.Length);
            Assert.Equal(candidates.Select(tile => tile.TileId), Option(passed, 0, "discardKong").TileIds);
            Assert.Equal(candidates.Select(tile => tile.Face), Option(passed, 0, "discardKong").Faces);
            Assert.Equal(state.Wall.Length - 2, passed.Wall.Length);
            var discarded = Expire(passed);
            Assert.Equal(candidates.Select(tile => tile.Face), discarded.Seats[0].Discards.TakeLast(2).Select(Tiles.Face));
        }
    }

    [Fact]
    public void LockOrEmptyWallPreventsAnUpgradeEvenWhenTheFourthIsStillHeld()
    {
        string[] hands = ["b9 b9 b9 b9 b5 b6 b7 d1 d2 d3 c5", "", "", ""];
        TestMeld[] melds = [new(0, "peng", "b5 b5 b5", 3)];
        var state = AtTurn(hands, "c5 d1 c8 d5 d7 b7 c6 c7 d6 d9 c9 d4",
            melds: melds, seed: RolledSeed, profile: ChangshaEngine.TwoTileKongProfile);
        var held = Assert.Single(state.Seats[0].Hand, tile => Tiles.Face(tile) == "b5");
        Assert.Equal(Determinism.TileId(state.Start, 0, held), Assert.Single(Option(state, 0, "bu", "b5").TileIds));
        var rolled = Select(state, 0, "gang", "b9");
        var later = Expire(rolled);
        for (var step = 0; step < 15 && ChangshaEngine.ViewPublic(later).TurnSeat != 0; step++)
            later = Expire(later);
        Assert.True(later.Seats[0].FrozenAfterGang);
        Assert.Contains(held, later.Seats[0].Hand);
        Assert.NotEmpty(later.Wall);
        Assert.Equal((0, DrawKind.Ordinary), (later.Window!.Seat, later.Window.DrawKind));
        var locked = ChangshaEngine.ViewSeat(later, 0);
        Assert.Equal(locked.DrawnTileId, Assert.Single(Assert.Single(locked.LegalOptions,
            option => option.Kind == "discard").TileIds));
        Assert.DoesNotContain(locked.LegalOptions, option => option.Kind is "bu" or "gang");

        var shortWall = AtTurn(hands, "c5", melds: melds, profile: ChangshaEngine.TwoTileKongProfile);
        var lastBu = Select(shortWall, 0, "bu", "b9");
        Assert.False(lastBu.Seats[0].FrozenAfterGang);
        Assert.Empty(lastBu.Wall);
        Assert.Contains(held, lastBu.Seats[0].Hand);
        Assert.NotNull(lastBu.Seats[0].Drawn);
        Assert.DoesNotContain(ChangshaEngine.ViewSeat(lastBu, 0).LegalOptions, option => option.Kind is "bu" or "gang");
    }

    [Theory]
    [InlineData(ChangshaEngine.Profile, 14, -3, -8)]
    [InlineData(ChangshaEngine.TwoBirdProfile, 24, -6, -12)]
    public void SeededDiceChooseTheReplacementStackAndScoreKongBloom(string profile, int win, int other, int dealer)
    {
        var state = AtTurn(
        [
            "b9 b9 b9 b9 b1 b2 b3 c1 c2 c3 d1 d2 d3 c5", "", "", ""
        ], "b5 d7 d8 c5 b7 b8", dealer: 2, seed: RolledSeed, profile: profile);
        Assert.Null(ChangshaEngine.ViewPublic(state).KongRoll);
        var rolledAt = Now.AddSeconds(3);
        var after = Select(state, 0, "gang", "b9", now: rolledAt);
        var view = ChangshaEngine.ViewPublic(after);
        var json = JsonSerializer.SerializeToElement(view, CheckpointJson);
        var roll = json.GetProperty("kongRoll");
        Assert.Equal(0, roll.GetProperty("seat").GetInt32());
        Assert.Equal(new[] { 4, 1 }, roll.GetProperty("dice").EnumerateArray().Select(die => die.GetInt32()));
        Assert.Equal(1, roll.GetProperty("lowerDie").GetInt32());
        Assert.Equal(new[] { "dice", "lowerDie", "seat" }, roll.EnumerateObject().Select(field => field.Name).Order());
        Assert.Equal(rolledAt + state.Start.TurnLimit, view.Window!.ClosesAtGameTime);
        Assert.Null(view.Window.Trigger);
        var checkpoint = Checkpoint(after);
        Assert.DoesNotContain("\"kongRoll\"", checkpoint);
        Assert.DoesNotContain("\"kongTiles\"", checkpoint);
        Assert.DoesNotContain("\"kongStackCount\"", checkpoint);
        Assert.DoesNotContain("\"claimedKongWinTiles\"", checkpoint);
        Assert.DoesNotContain("\"unchosenKongTile\"", checkpoint);
        Assert.DoesNotContain("\"kongReplacements\"", Checkpoint(ChangshaEngine.ViewSeat(after, 0)));
        Assert.Equal(json.GetRawText(), Checkpoint(ChangshaEngine.ViewPublic(RoundTrip(after))));

        Assert.Equal("c5", Tiles.Face(after.Seats[0].Drawn!.Value));
        Assert.Equal(new[] { "b5", "d7", "d8", "b7", "b8" }, after.Wall.Select(tile => Tiles.Face(tile.Tile)));
        Assert.Equal(new[] { "hu", "discard" }, ChangshaEngine.ViewSeat(after, 0).LegalOptions.Select(option => option.Kind));
        var won = Select(after, 0, "hu");
        Assert.Null(ChangshaEngine.ViewPublic(won).KongRoll);
        var settle = won.Settlement!;
        Assert.Equal("kongBloom", Assert.Single(Assert.Single(settle.Winners).Patterns).Code);
        AssertBalanced(settle, win, other, dealer, other);
    }

    [Fact]
    public void RolledKongMissForcesOnlyItsReplacementAndRestrictsFutureDraws()
    {
        var state = AtTurn(
        [
            "b9 b9 b9 b9 b1 b2 b3 c1 c2 c3 d1 d2 d3 d5",
            "b4 b5 b6 c4 c6 d4 d5 d6 d7 d8 d9 c8 c8", "c5 c5", ""
        ], "b5 d7 d8 c5 b7 b8", dealer: 2, seed: RolledSeed);
        var missed = Select(state, 0, "gang", "b9");
        Assert.NotNull(ChangshaEngine.ViewPublic(missed).KongRoll);
        Assert.False(Has(missed, 0, "hu"));
        Assert.Equal("c5", Assert.Single(Assert.Single(ChangshaEngine.ViewSeat(missed, 0).LegalOptions).Faces));
        var cannon = Select(missed, 0, "discard");
        var claim = ChangshaEngine.ViewPublic(cannon);
        Assert.Equal("kong", claim.Window!.Reason);
        Assert.Null(claim.KongRoll);
        Assert.Equal(new[] { "hu", "pass" }, ChangshaEngine.ViewSeat(cannon, 1).LegalOptions.Select(option => option.Kind));
        Assert.Empty(ChangshaEngine.ViewSeat(cannon, 2).LegalOptions);
        var win = Select(cannon, 1, "hu");
        if (win.Window is not null)
            win = Expire(win);
        Assert.Equal("kongCannon", Assert.Single(Assert.Single(win.Settlement!.Winners).Patterns).Code);
        AssertBalanced(win.Settlement, -6, 6, 0, 0);

        var continued = Expire(cannon);
        for (var step = 0; step < 20 && ChangshaEngine.ViewPublic(continued).TurnSeat != 0; step++)
            continued = Expire(continued);
        var continuedView = ChangshaEngine.ViewPublic(continued);
        Assert.Equal(0, continuedView.TurnSeat);
        Assert.Null(continuedView.KongRoll);
        Assert.True(continued.Seats[0].FrozenAfterGang);
        Assert.Single(ChangshaEngine.ViewSeat(continued, 0).LegalOptions, option => option.Kind == "discard");
    }

    [Fact]
    public void EarthlyWinStacksWhenTheDealersFirstDiscardIsKongCannon()
    {
        var dealt = Deal(
        [
            "b9 b9 b9 b9 b1 b2 b3 c1 c2 c3 d1 d2 d3 d5",
            "b4 b5 b6 c4 c6 d4 d5 d6 d7 d8 d9 c8 c8", "", ""
        ], front: "c2", rearDrawOrder: "b7 b8 d8 d7 c5");
        var after = Select(Expire(dealt), 0, "gang", "b9");
        Assert.Equal("c5", Tiles.Face(after.Seats[0].Drawn!.Value));
        var claim = Select(after, 0, "discard");
        var settled = Select(claim, 1, "hu");
        if (settled.Window is not null)
            settled = Expire(settled);
        Assert.Equal(new[] { "earthly", "kongCannon" },
            Assert.Single(settled.Settlement!.Winners).Patterns.Select(pattern => pattern.Code));
        AssertBalanced(settled.Settlement, -28, 28, 0, 0);
    }

    [Fact]
    public void FourRealConcealedKongsCanWinWithoutCreatingExtraTiles()
    {
        var state = Deal(["b1 b1 b1 b1 b3 b3 b3 b3 c4 c4 c4 c4 d8 d8", "", "", ""],
            rearDrawOrder: "d8 d8 c7 c7");
        state = Expire(state);
        foreach (var face in new[] { "b1", "b3", "c4", "d8" })
            state = Select(state, 0, "bu", face);
        Assert.Equal(4, state.Seats[0].Melds.Length);
        Assert.Equal(51, state.Wall.Length);
        Assert.All(ChangshaEngine.ViewPublic(state).Seats[0].Melds, meld => Assert.All(meld.Tiles, Assert.Null));
        Assert.Equal(new[] { "c7", "c7" }, ChangshaEngine.ViewSeat(state, 0).Hand.Select(tile => tile.Face));
        var complete = Select(state, 0, "hu");
        Assert.Equal("allPungs", Assert.Single(Assert.Single(complete.Settlement!.Winners).Patterns).Code);
        Assert.Equal("c7", Assert.Single(Assert.Single(complete.Settlement.Winners).WinningTiles));
    }

    private static GameState Added(string profile = ChangshaEngine.Profile) =>
        AtTurn([AddedReadyHand, CanRob, "", ""], "d5 c2 c8 d6 d7 c9",
            dealer: 2, drawn: "b5", melds: [new(0, "peng", "b5 b5 b5", 3)], profile: profile);

    private static (GameState Turn, HandTile Fourth) DrawAfterPeng(string profile, string laterDraw)
    {
        var state = Deal(["b5 b5 b5 d1 d2 d3 c1 c2 c3 b6 b7 d5 d9", CanRob, "", "b5"],
            front: $"d4 c7 b9 {laterDraw} c4 d9", rearDrawOrder: "d5", dealer: 3, profile: profile);
        if (state.Window!.Kind == DecisionKind.Opening)
            state = Expire(state);
        var ownCopies = ChangshaEngine.ViewSeat(state, 0).Hand.Where(tile => tile.Face == "b5").ToArray();
        Assert.Equal(3, ownCopies.Length);
        var claim = Select(state, 3, "discard", "b5");
        var peng = Option(claim, 0, "peng", "b5");
        Assert.Equal(2, peng.TileIds.Length);
        var fourth = Assert.Single(ownCopies, tile => !peng.TileIds.Contains(tile.TileId));
        Assert.True(Has(claim, 1, "hu"));
        state = Expire(Select(claim, 0, "peng", "b5"));
        Assert.Equal(DrawKind.None, state.Window!.DrawKind);
        var noDraw = ChangshaEngine.ViewSeat(state, 0);
        Assert.Null(noDraw.DrawnTileId);
        Assert.Equal(fourth, Assert.Single(noDraw.Hand, tile => tile.Face == "b5"));
        Assert.All(noDraw.LegalOptions, option => Assert.Equal("discard", option.Kind));
        var meld = Assert.Single(state.Seats[0].Melds);
        Assert.Equal(("peng", (int?)3), (meld.Kind, meld.FromSeat));
        Assert.Contains(claim.Window!.Trigger!.Value, meld.Tiles);

        state = Select(state, 0, "discard", "d9");
        for (var seat = 1; seat <= 3; seat++)
        {
            if (state.Window!.Kind == DecisionKind.Discard)
                state = Expire(state);
            Assert.Equal((seat, DrawKind.Ordinary), (state.Window!.Seat, state.Window.DrawKind));
            Assert.Empty(ChangshaEngine.ViewSeat(state, 0).LegalOptions);
            state = Expire(state);
        }
        if (state.Window!.Kind == DecisionKind.Discard)
            state = Expire(state);
        Assert.Equal((0, DrawKind.Ordinary), (state.Window!.Seat, state.Window.DrawKind));
        var drawn = ChangshaEngine.ViewSeat(state, 0);
        Assert.Equal(laterDraw, Assert.Single(drawn.Hand, tile => tile.TileId == drawn.DrawnTileId).Face);
        Assert.Equal(fourth, Assert.Single(drawn.Hand, tile => tile.Face == "b5"));
        return (state, fourth);
    }
}
