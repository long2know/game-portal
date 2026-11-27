using System.Text.Json;
using System.Text.Json.Nodes;
using JadeCourt.Games.Harbors;
using Xunit.Abstractions;
using static JadeCourt.Games.Harbors.HarborsActionKind;
using static JadeCourt.Games.Harbors.HarborsGame;

namespace JadeCourt.Games.Tests;

public sealed class HarborsTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    [Fact]
    public void CanalRingsProjectAConnectedDeduplicatedLatticeWithBalancedTerrain()
    {
        var view = View(Start(1), 0);
        (int X, int Y)[] centers =
        [
            (0, 0), (2, 0), (1, 3), (-1, 3), (-2, 0), (-1, -3), (1, -3),
            (4, 0), (2, 6), (-2, 6), (-4, 0), (-2, -6), (2, -6)
        ];
        (int X, int Y)[] corners = [(0, -2), (1, -1), (1, 1), (0, 2), (-1, 1), (-1, -1)];
        Assert.Equal(centers, view.Tiles.Select(tile => (tile.X, tile.Y)));
        Assert.Equal(Enumerable.Range(0, 13), view.Tiles.Select(tile => tile.Id));
        Assert.Equal(48, view.Nodes.Length);
        Assert.Equal(60, view.Routes.Length);
        Assert.Equal(Enumerable.Range(0, 48), view.Nodes.Select(node => node.Id));
        Assert.Equal(Enumerable.Range(0, 60), view.Routes.Select(route => route.Id));

        var expectedSites = centers.SelectMany(tile => corners.Select(corner => (X: tile.X + corner.X, Y: tile.Y + corner.Y)));
        Assert.Equal(expectedSites.Distinct().OrderBy(site => site.Y).ThenBy(site => site.X), view.Nodes.Select(node => (node.X, node.Y)));
        var expectedRoutes = new HashSet<(int A, int B)>();
        foreach (var tile in view.Tiles)
        {
            Assert.Equal(6, tile.NodeIds.Length);
            Assert.Equal(corners, tile.NodeIds.Select(id => (view.Nodes[id].X - tile.X, view.Nodes[id].Y - tile.Y)));
            for (var i = 0; i < 6; i++)
            {
                var a = tile.NodeIds[i];
                var b = tile.NodeIds[(i + 1) % 6];
                expectedRoutes.Add((Math.Min(a, b), Math.Max(a, b)));
            }
        }
        Assert.Equal(expectedRoutes.Order(), view.Routes.Select(route => (route.A, route.B)));
        var seen = new HashSet<int> { 0 };
        for (var step = 0; step < view.Nodes.Length; step++)
            foreach (var route in view.Routes)
                if (seen.Contains(route.A) || seen.Contains(route.B)) { seen.Add(route.A); seen.Add(route.B); }
        Assert.Equal(view.Nodes.Length, seen.Count);

        var canal = Assert.Single(view.Tiles, tile => tile.Resource is null);
        Assert.Equal(0, canal.Id);
        Assert.Null(canal.Number);
        Assert.Equal(Enumerable.Range(0, 4), view.Tiles.Skip(1).Select(tile => tile.Resource!.Value).Distinct().Order());
        foreach (var terrain in view.Tiles.Skip(1).GroupBy(tile => tile.Resource))
        {
            Assert.Equal(3, terrain.Count());
            Assert.All(terrain, tile => Assert.Contains(tile.Number, new int?[] { 3, 4, 5, 6, 8, 9, 10, 11 }));
            Assert.Equal(11, terrain.Sum(tile => 6 - Math.Abs(7 - tile.Number!.Value)));
        }
    }

    [Fact]
    public void SnakeSetupPlacesAFreePierThenItsIncidentRouteAndGrantsOnlyTheSecondPier()
    {
        var saved = Start(9);
        int[] order = [0, 1, 2, 2, 1, 0];
        var grants = new int[3][];
        for (var i = 0; i < order.Length; i++)
        {
            var seat = order[i];
            var other = (seat + 1) % 3;
            var placing = View(saved, seat);
            Assert.Equal("placePier", placing.Phase);
            Assert.Equal(seat, placing.TurnSeat);
            Assert.Equal(0, placing.Turn);
            Assert.Null(placing.LastRoll);
            Assert.False(placing.LegalActions.CanRoll);
            AssertNoActions(View(saved, other).LegalActions);
            Assert.Equal(placing.Nodes.Where(node => EmptyWithSpacing(placing, node.Id)).Select(node => node.Id), placing.LegalActions.Piers);
            var node = placing.LegalActions.Piers[0];
            Reject(saved, other, new(BuildPier, node), HarborsRejection.NotYourTurn);
            Reject(saved, seat, new(BuildRoute, 0));
            if (i > 0) Reject(saved, seat, new(BuildPier, placing.Nodes.First(site => site.Seat is not null).Id));

            var next = Apply(saved, seat, new(BuildPier, node));
            Assert.Equal(next, Apply(saved, seat, new(BuildPier, node)));
            Assert.Null(View(saved, seat).Nodes[node].Seat);
            saved = next;
            var routing = View(saved, seat);
            Assert.Equal("placeRoute", routing.Phase);
            Assert.Equal(seat, routing.TurnSeat);
            Assert.Equal(seat, routing.Nodes[node].Seat);
            Assert.Equal(1, routing.Nodes[node].Level);
            Assert.Equal(i < 3 ? 1 : 2, routing.Seats[seat].Points);
            Assert.Empty(routing.LegalActions.Piers);
            Assert.Equal(Incident(routing, node).Where(route => route.Seat is null).Select(route => route.Id), routing.LegalActions.Routes);
            Reject(saved, seat, new(BuildRoute, routing.Routes.First(route => route.A != node && route.B != node).Id));
            var grant = i < 3 ? new int[4] : OpeningGrant(routing, node);
            if (i >= 3) grants[seat] = grant;
            Assert.Equal(grant, routing.OwnResources);
            Assert.Equal(grant.Sum(), View(saved, other).Seats[seat].ResourceCount);

            var edge = routing.LegalActions.Routes[0];
            saved = Apply(saved, seat, new(BuildRoute, edge));
            var placed = View(saved, seat);
            Assert.Equal(seat, placed.Routes[edge].Seat);
            Assert.Equal(grant, placed.OwnResources);
        }

        var initial = View(saved, 0);
        Assert.Equal("roll", initial.Phase);
        Assert.Equal(1, initial.Turn);
        Assert.Equal(0, initial.TurnSeat);
        Assert.True(initial.LegalActions.CanRoll);
        Assert.All(initial.Seats, seat => Assert.Equal(2, seat.Points));
        Assert.Equal(new[] { 2, 2, 2 }, initial.Routes.Where(route => route.Seat is not null)
            .GroupBy(route => route.Seat).OrderBy(group => group.Key).Select(group => group.Count()));
        for (var seat = 0; seat < 3; seat++) Assert.Equal(grants[seat], View(saved, seat).OwnResources);
    }

    [Fact]
    public void ConnectedRoutesCanEndAtButNotExtendThroughARivalAndNewPiersNeedSpacingAndARoute()
    {
        var initial = Start(2);
        var board = View(initial, 0);
        var saved = CompleteSetup(initial, (15, Edge(board, 15, 21)), (27, Edge(board, 27, 32)));
        saved = Apply(WithHand(WithFaces(saved, 1, 3, 4), 0, [5, 5, 5, 0]), 0, new(Roll));
        var before = View(saved, 0);
        var blocked = Edge(before, 27, 33);
        var contact = Edge(before, 21, 27);
        Assert.Contains(contact, before.LegalActions.Routes);
        Assert.DoesNotContain(blocked, before.LegalActions.Routes);

        saved = Apply(saved, 0, new(BuildRoute, contact));
        var contacted = View(saved, 0);
        Assert.Equal(new[] { 4, 4, 5, 0 }, contacted.OwnResources);
        Assert.Equal(0, contacted.Routes[contact].Seat);
        Assert.Equal(1, contacted.Nodes[27].Seat);
        Assert.Equal(2, contacted.Seats[0].Points);
        Assert.DoesNotContain(blocked, contacted.LegalActions.Routes);
        Reject(saved, 0, new(BuildRoute, blocked));
        Reject(saved, 0, new(BuildPier, 21)); // 21 touches two existing piers.

        var expansion = contacted.LegalActions.Routes.SelectMany(edge => new[] { contacted.Routes[edge].A, contacted.Routes[edge].B }
            .Select(node => (edge, node))).First(pair => EmptyWithSpacing(contacted, pair.node)
                && !Incident(contacted, pair.node).Any(route => route.Seat == 0));
        Assert.DoesNotContain(expansion.node, contacted.LegalActions.Piers);
        Reject(saved, 0, new(BuildPier, expansion.node));
        saved = Apply(saved, 0, new(BuildRoute, expansion.edge));
        Assert.Contains(expansion.node, View(saved, 0).LegalActions.Piers);
        saved = Apply(saved, 0, new(BuildPier, expansion.node));
        var expanded = View(saved, 0);
        Assert.Equal(0, expanded.Nodes[expansion.node].Seat);
        Assert.Equal(1, expanded.Nodes[expansion.node].Level);
        Assert.Equal(3, expanded.Seats[0].Points);
        Assert.Equal(new[] { 2, 2, 4, 0 }, expanded.OwnResources);
    }

    [Fact]
    public void ProductionPaysEveryAdjacentPierAndWarehouseEvenOffTurnAndCapsEachKind()
    {
        var initial = Start(3);
        var board = View(initial, 0);
        var saved = CompleteSetup(initial, (16, Edge(board, 16, 22)), (27, Edge(board, 27, 32)));
        saved = Apply(WithFaces(WithHand(saved, 0, [0, 0, 1, 2]), 1, 1, 6), 0, new(Roll));
        Assert.Contains(16, View(saved, 0).LegalActions.Upgrades);
        Assert.DoesNotContain(27, View(saved, 0).LegalActions.Upgrades);
        Reject(saved, 0, new(Upgrade, 27));
        saved = Apply(saved, 0, new(Upgrade, 16));
        var improved = View(saved, 0);
        Assert.Equal(new[] { 0, 0, 0, 0 }, improved.OwnResources);
        Assert.Equal(2, improved.Nodes[16].Level);
        Assert.Equal(3, improved.Seats[0].Points);

        saved = WithFaces(Apply(saved, 0, new(EndTurn)), 2, 1, 5);
        var peerBefore = View(saved, 1).OwnResources;
        var paid = Apply(saved, 1, new(Roll));
        Assert.Equal(new[] { 1, 5 }, View(paid, 0).LastRoll);
        Assert.Equal(2, View(paid, 0).OwnResources[0]);
        Assert.Equal(peerBefore[0] + 1, View(paid, 1).OwnResources[0]);
        for (var seat = 0; seat < 3; seat++)
        {
            var before = View(saved, seat);
            var after = View(paid, seat);
            for (var kind = 0; kind < 4; kind++)
            {
                var expected = before.Tiles.Where(tile => tile.Number == 6 && tile.Resource == kind)
                    .Sum(tile => tile.NodeIds.Select(id => before.Nodes[id]).Where(node => node.Seat == seat).Sum(node => node.Level));
                Assert.Equal(before.OwnResources[kind] + expected, after.OwnResources[kind]);
            }
        }

        var capped = Apply(WithHand(saved, 0, [11, 12, 0, 0]), 1, new(Roll));
        Assert.Equal(12, View(capped, 0).OwnResources[0]);
        Assert.Equal(12, View(capped, 0).OwnResources[1]);
        Reject(paid, 1, new(Roll));
    }

    [Fact]
    public void OneMarketExchangeUsesTwoOnSevenOtherwiseThreeAndDoesNotExceedCapacity()
    {
        var opening = WithHand(CompleteSetup(Start(4)), 0, [3, 12, 1, 0]);
        var saved = WithFaces(opening, 1, 2, 5);
        Reject(saved, 0, new(Trade, Give: 0, Take: 3));
        Reject(saved, 0, new(EndTurn));
        Reject(saved, 0, new(Roll, Target: 5));
        var rolled = Apply(saved, 0, new(Roll));
        for (var seat = 0; seat < 3; seat++) Assert.Equal(View(saved, seat).OwnResources, View(rolled, seat).OwnResources);
        var market = View(rolled, 0).LegalActions.Trades;
        Assert.NotEmpty(market);
        Assert.All(market, trade => Assert.Equal(2, trade.Cost));
        Assert.Contains(new HarborsTrade(0, 3, 2), market);
        Assert.DoesNotContain(market, trade => trade.Take == 1 || trade.Give == trade.Take);
        Reject(rolled, 0, new(Trade, Give: 0, Take: 1));
        Reject(rolled, 0, new(Trade, Give: 0, Take: 0));
        Reject(rolled, 0, new(Trade, Give: 4, Take: 0));
        Reject(rolled, 0, new(BuildRoute, int.MaxValue));
        Reject(rolled, 0, new((HarborsActionKind)99));

        var traded = Apply(rolled, 0, new(Trade, Give: 0, Take: 3));
        Assert.Equal(new[] { 1, 12, 1, 1 }, View(traded, 0).OwnResources);
        Assert.Empty(View(traded, 0).LegalActions.Trades);
        Reject(traded, 0, new(Trade, Give: 1, Take: 2));
        Assert.Contains(new HarborsTrade(1, 2, 2), View(rolled, 0).LegalActions.Trades);
        var after = Apply(traded, 0, new(EndTurn));
        Assert.Equal(new[] { 2, 5 }, View(after, 1).LastRoll);

        var next = Apply(WithFaces(WithHand(after, 1, [0, 0, 0, 5]), 2, 1, 1), 1, new(Roll));
        Assert.Equal(new[] { new HarborsTrade(3, 0, 3), new(3, 1, 3), new(3, 2, 3) }, View(next, 1).LegalActions.Trades);
        Assert.Equal(new[] { 0, 1, 0, 2 }, View(Apply(next, 1, new(Trade, Give: 3, Take: 1)), 1).OwnResources);
    }

    [Fact]
    public void ResumedTurnsUseExactPrivateDiceAndSendOnlyRecipientResourcesAndPublicFaces()
    {
        const int seed = 197;
        var start = Start(seed);
        Assert.Equal(start, Start(seed));
        Assert.NotEqual(start, Start(seed + 1));
        Assert.Equal(JsonSerializer.Serialize(View(start, 0), Wire), JsonSerializer.Serialize(View(Start(seed + 1), 0), Wire));
        var random = new Random(seed);
        var tape = Enumerable.Range(0, 240).Select(_ => random.Next(1, 7)).ToArray();
        using var persisted = JsonDocument.Parse(start);
        Assert.Equal(1, persisted.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal(tape, persisted.RootElement.GetProperty("State").GetProperty("DiceTape").EnumerateArray().Select(face => face.GetInt32()));

        var saved = CompleteSetup(start);
        for (var turn = 1; turn <= 9; turn++)
        {
            var active = (turn - 1) % 3;
            var unopened = View(saved, active);
            Assert.Equal(turn, unopened.Turn);
            Assert.Equal(turn == 1 ? null : tape[(2 * turn - 4)..(2 * turn - 2)], unopened.LastRoll);
            var resumed = JsonNode.Parse(saved)!.ToJsonString();
            var rolled = Apply(resumed, active, new(Roll));
            Assert.Equal(Apply(saved, active, new(Roll)), rolled);
            for (var viewer = 0; viewer < 3; viewer++)
            {
                var position = View(rolled, viewer);
                Assert.Equal(tape[(2 * turn - 2)..(2 * turn)], position.LastRoll);
                using var wire = JsonDocument.Parse(JsonSerializer.Serialize(position, Wire));
                Assert.Equal(new[] { "lastRoll", "legalActions", "nodes", "ownResources", "phase", "routes", "seats", "tiles",
                    "turn", "turnSeat", "winnerSeat" }, Fields(wire.RootElement));
                var caller = wire.RootElement.GetProperty("ownResources").EnumerateArray().Select(count => count.GetInt32()).ToArray();
                Assert.Equal(position.OwnResources, caller);
                Assert.Equal(position.Seats[viewer].ResourceCount, caller.Sum());
                Assert.All(wire.RootElement.GetProperty("seats").EnumerateArray(), publicSeat =>
                    Assert.Equal(new[] { "points", "resourceCount", "seat" }, Fields(publicSeat)));
                if (viewer != active) AssertNoActions(position.LegalActions);
            }
            saved = Apply(rolled, active, new(EndTurn));
        }

        var closed = View(saved, 0, playing: false);
        Assert.Equal("roll", closed.Phase);
        Assert.Null(closed.TurnSeat);
        Assert.NotNull(closed.LastRoll);
        AssertNoActions(closed.LegalActions);
        var original = JsonSerializer.Serialize(View(saved, 0), Wire);
        closed.LastRoll[0] = 100;
        closed.OwnResources[0] = 100;
        closed.Tiles[0].NodeIds[0] = 100;
        Assert.Equal(original, JsonSerializer.Serialize(View(saved, 0), Wire));
    }

    [Fact]
    public void VersionOneCheckpointRejectsInvalidFacesSetupResourcesAndDisconnectedRoutes()
    {
        var start = Start(6);
        RejectCheckpoint(start, root => root["Version"] = 2);
        RejectCheckpoint(start, root => State(root)["DiceTape"]![239] = 0);
        RejectCheckpoint(start, root => State(root)["Resources"]![0] = 1);
        RejectCheckpoint(start, root => State(root)["OpeningPiers"] = new JsonArray(900));
        RejectCheckpoint(start, root => State(root).Remove("Sites"));

        var played = CompleteSetup(start);
        var view = View(played, 0);
        var occupied = view.Routes.Where(route => route.Seat == 0).SelectMany(route => new[] { route.A, route.B }).ToHashSet();
        var disconnected = view.Routes.First(route => route.Seat is null && !occupied.Contains(route.A) && !occupied.Contains(route.B));
        RejectCheckpoint(played, root => State(root)["Routes"]![disconnected.Id] = 1);
        RejectCheckpoint(played, root => State(root)["Phase"] = 5);
        RejectCheckpoint(played, root => State(root)["Resources"]![0] = 13);

        static JsonObject State(JsonObject root) => root["State"]!.AsObject();
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, 0)]
    public void LastTurnSettlesByHighestScoreIncludingTies(bool upgrade, int? winner)
    {
        var saved = CompleteSetup(Start(41));
        if (upgrade) saved = WithHand(WithFaces(saved, 1, 4, 3), 0, [0, 0, 1, 2]);
        for (var turn = 1; turn <= 120; turn++)
        {
            var seat = (turn - 1) % 3;
            var current = View(saved, seat);
            Assert.Equal(turn, current.Turn);
            Assert.Equal(seat, current.TurnSeat);
            Assert.Equal("roll", current.Phase);
            saved = Apply(saved, seat, new(Roll));
            if (turn == 1 && upgrade) saved = Apply(saved, seat, new(Upgrade, View(saved, seat).LegalActions.Upgrades[0]));
            Assert.Equal("build", View(saved, seat).Phase);
            saved = Apply(saved, seat, new(EndTurn));
        }
        var final = View(saved, 0);
        Assert.Equal("finished", final.Phase);
        Assert.Equal(120, final.Turn);
        Assert.Null(final.TurnSeat);
        Assert.Equal(winner, final.WinnerSeat);
        Assert.Equal(new[] { upgrade ? 3 : 2, 2, 2 }, final.Seats.Select(seat => seat.Points));
        AssertNoActions(final.LegalActions);
        Reject(saved, 0, new(EndTurn), HarborsRejection.Finished);
    }

    [Fact]
    public void SeededBotTablesReturnToEachHumanSeatAndUsuallyReachEightPoints()
    {
        const int matches = 16;
        var atTarget = 0;
        var totalTurns = 0;
        var longestMatch = 0;
        var winners = new int[3];
        for (var seed = 0; seed < matches; seed++)
        {
            var humanSeat = seed % 3;
            var start = Start(seed);
            var saved = PlayBotsUntil(start, humanSeat);
            Assert.Equal(saved, PlayBotsUntil(start, humanSeat));
            for (var commands = 0; ; commands++)
            {
                var view = View(saved, humanSeat);
                if (view.Phase == "finished")
                {
                    if (view.Seats.Max(seat => seat.Points) == 8)
                    {
                        var winningSeat = Assert.IsType<int>(view.WinnerSeat);
                        Assert.Equal(8, view.Seats[winningSeat].Points);
                        atTarget++;
                        winners[winningSeat]++;
                    }
                    else Assert.Equal(120, view.Turn);
                    totalTurns += view.Turn;
                    longestMatch = Math.Max(longestMatch, view.Turn);
                    Assert.Null(view.TurnSeat);
                    Reject(saved, humanSeat, new(Roll), HarborsRejection.Finished);
                    break;
                }

                Assert.Equal(humanSeat, view.TurnSeat);
                Assert.True(commands < 1_100, $"Seed {seed} did not finish in the human's total action bound.");
                var action = HarborsBot.Choose(view);
                var next = PlayBotsUntil(Apply(saved, humanSeat, action), humanSeat);
                Assert.NotEqual(saved, next);
                saved = next;
            }
        }

        output.WriteLine($"{atTarget}/{matches} reached eight points; mean {totalTurns / (double)matches:F1} turns, max {longestMatch}; "
            + $"eight-point wins by seat: {string.Join(", ", winners)}.");
        Assert.True(atTarget >= matches / 2, $"Only {atTarget}/{matches} matches reached the scoring target.");
    }

    [Fact]
    public void CanceledStartAndBotRunDoNotProduceANewCheckpoint()
    {
        var stop = new CancellationToken(canceled: true);
        Assert.Throws<OperationCanceledException>(() => Start(stop: stop));
        var start = Start(3);
        Assert.Throws<OperationCanceledException>(() => PlayBotsUntil(start, 1, stop));
        Assert.Equal("placePier", View(start, 0).Phase);
        Assert.All(View(start, 0).Nodes, node => Assert.Null(node.Seat));
    }

    private static string CompleteSetup(string saved, params (int Node, int Route)[] firstPlacements)
    {
        for (var i = 0; i < 6; i++)
        {
            var seat = View(saved, 0).TurnSeat!.Value;
            var choices = View(saved, seat);
            var node = i < firstPlacements.Length ? firstPlacements[i].Node : choices.LegalActions.Piers[0];
            saved = Apply(saved, seat, new(BuildPier, node));
            var edge = i < firstPlacements.Length ? firstPlacements[i].Route : View(saved, seat).LegalActions.Routes[0];
            saved = Apply(saved, seat, new(BuildRoute, edge));
        }
        return saved;
    }

    private static IEnumerable<HarborsRoute> Incident(HarborsPosition view, int node) =>
        view.Routes.Where(route => route.A == node || route.B == node);

    private static int Edge(HarborsPosition view, int a, int b) =>
        Assert.Single(Incident(view, a), route => route.A == b || route.B == b).Id;

    private static bool EmptyWithSpacing(HarborsPosition view, int node) =>
        view.Nodes[node].Seat is null && Incident(view, node).All(route => view.Nodes[route.A == node ? route.B : route.A].Seat is null);

    private static int[] OpeningGrant(HarborsPosition view, int node) => Enumerable.Range(0, 4)
        .Select(kind => view.Tiles.Count(tile => tile.Resource == kind && tile.NodeIds.Contains(node))).ToArray();

    private static void AssertNoActions(HarborsLegalActions legal)
    {
        Assert.False(legal.CanRoll);
        Assert.False(legal.CanEndTurn);
        Assert.Empty(legal.Piers);
        Assert.Empty(legal.Routes);
        Assert.Empty(legal.Upgrades);
        Assert.Empty(legal.Trades);
    }

    private static void Reject(string saved, int seat, HarborsAction action, HarborsRejection reason = HarborsRejection.IllegalAction) =>
        Assert.Equal(reason, Assert.Throws<HarborsRejectedException>(() => Apply(saved, seat, action)).Reason);

    private static void RejectCheckpoint(string saved, Action<JsonObject> tamper)
    {
        var root = JsonNode.Parse(saved)!.AsObject();
        tamper(root);
        Assert.Throws<InvalidDataException>(() => View(root.ToJsonString(), 0));
    }

    private static string[] Fields(JsonElement element) => [.. element.EnumerateObject().Select(property => property.Name).Order()];

    private static string WithFaces(string saved, int turn, int first, int second)
    {
        var root = JsonNode.Parse(saved)!;
        var tape = root["State"]!["DiceTape"]!;
        tape[2 * (turn - 1)] = first;
        tape[2 * turn - 1] = second;
        return root.ToJsonString();
    }

    private static string WithHand(string saved, int seat, int[] hand)
    {
        var root = JsonNode.Parse(saved)!;
        var resources = root["State"]!["Resources"]!;
        for (var kind = 0; kind < hand.Length; kind++) resources[seat * 4 + kind] = hand[kind];
        return root.ToJsonString();
    }
}
