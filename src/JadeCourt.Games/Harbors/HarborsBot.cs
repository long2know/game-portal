using static JadeCourt.Games.Harbors.HarborsGame;

namespace JadeCourt.Games.Harbors;

internal static class HarborsBot
{
    private const int NoPath = int.MinValue;
    private const int LookAhead = 3;

    internal static HarborsAction Choose(HarborsPosition view)
    {
        var seat = view.TurnSeat ?? throw new InvalidDataException("Jade Harbors bot has no active turn.");
        var legal = view.LegalActions;
        if (legal.CanRoll) return new(HarborsActionKind.Roll);

        var at = HarborsBoard.RoutesAtSite;
        var ownSupply = new int[ResourceKinds];
        var siteSupply = new int[view.Nodes.Length, ResourceKinds];
        foreach (var tile in view.Tiles)
        {
            if (tile is not { Resource: int kind, Number: int number }) continue;
            var weight = 6 - Math.Abs(7 - number);
            foreach (var node in tile.NodeIds)
            {
                siteSupply[node, kind] += weight;
                if (view.Nodes[node].Seat == seat) ownSupply[kind] += weight * view.Nodes[node].Level;
            }
        }

        if (legal.Upgrades.Length != 0) return new(HarborsActionKind.Upgrade, legal.Upgrades.MaxBy(SiteValue));
        if (legal.Piers.Length != 0) return new(HarborsActionKind.BuildPier, legal.Piers.MaxBy(SiteValue));

        var anchors = new bool[view.Nodes.Length];
        var futureSites = new bool[view.Nodes.Length];
        var pierGoal = false;
        var upgradeGoal = false;
        foreach (var node in view.Nodes)
        {
            anchors[node.Id] = node.Seat == seat || node.Seat is null && at[node.Id].Any(edge => view.Routes[edge].Seat == seat);
            futureSites[node.Id] = node.Seat is null && at[node.Id].All(edge => view.Nodes[Other(edge, node.Id)].Seat is null);
            pierGoal |= futureSites[node.Id] && anchors[node.Id];
            upgradeGoal |= node.Seat == seat && node.Level == 1;
        }

        if (view.Phase == "placeRoute" && legal.Routes.Length != 0)
            return new(HarborsActionKind.BuildRoute, legal.Routes.MaxBy(RouteValue));
        if (!legal.CanEndTurn) throw new InvalidDataException("Jade Harbors bot has no legal action.");

        // Look for a buildable site before buying a route, or trading for its materials.
        var bestRoute = -1;
        var bestValue = NoPath;
        var routeGoal = false;
        foreach (var route in view.Routes)
        {
            if (route.Seat is not null || !anchors[route.A] && !anchors[route.B]) continue;
            var value = RouteValue(route.Id);
            if (value == NoPath) continue;
            routeGoal = true;
            if (value > bestValue && legal.Routes.Contains(route.Id)) (bestRoute, bestValue) = (route.Id, value);
        }

        var wanted = default(HarborsTrade);
        var bestTrade = (ScoresNow: false, Value: 0);
        foreach (var trade in legal.Trades)
        {
            if (upgradeGoal) Consider(trade, UpgradeCost, 85, scores: true);
            if (pierGoal) Consider(trade, PierCost, 80, scores: true);
            if (routeGoal) Consider(trade, RouteCost, 55, scores: false);
        }

        if (bestTrade.ScoresNow) return new(HarborsActionKind.Trade, Give: wanted.Give, Take: wanted.Take);
        if (bestRoute >= 0 && (!pierGoal || view.OwnResources[0] > 1 && view.OwnResources[1] > 1))
            return new(HarborsActionKind.BuildRoute, bestRoute);
        return bestTrade.Value > 0
            ? new(HarborsActionKind.Trade, Give: wanted.Give, Take: wanted.Take)
            : new(HarborsActionKind.EndTurn);

        int Other(int edge, int node) => view.Routes[edge].A == node ? view.Routes[edge].B : view.Routes[edge].A;

        int SiteValue(int node)
        {
            var score = 0;
            for (var kind = 0; kind < ResourceKinds; kind++)
            {
                var weight = siteSupply[node, kind];
                if (weight != 0) score += 12 + weight * (8 + 30 / (4 + ownSupply[kind]));
            }
            return score;
        }

        int RouteValue(int edge)
        {
            var route = view.Routes[edge];
            if (anchors[route.A] == anchors[route.B]) return NoPath;
            var first = anchors[route.A] ? route.B : route.A;
            if (view.Nodes[first].Seat is not null) return NoPath;

            var best = NoPath;
            Span<int> queue = stackalloc int[view.Nodes.Length];
            Span<int> distance = stackalloc int[view.Nodes.Length];
            distance.Clear();
            queue[0] = first;
            distance[first] = 1;
            var tail = 1;
            for (var head = 0; head < tail; head++)
            {
                var node = queue[head];
                if (futureSites[node]) best = Math.Max(best, SiteValue(node) - 25 * (distance[node] - 1));
                if (distance[node] == LookAhead) continue;
                foreach (var next in at[node])
                {
                    if (view.Routes[next].Seat is not null) continue;
                    var end = Other(next, node);
                    if (distance[end] != 0 || anchors[end] || view.Nodes[end].Seat is not null) continue;
                    queue[tail++] = end;
                    distance[end] = distance[node] + 1;
                }
            }
            return best;
        }

        void Consider(HarborsTrade trade, ReadOnlySpan<int> cost, int priority, bool scores)
        {
            var hand = view.OwnResources;
            if (hand[trade.Take] >= cost[trade.Take] || hand[trade.Give] - trade.Cost < cost[trade.Give]) return;
            var missing = -1; // The incoming material covers one missing unit.
            for (var kind = 0; kind < ResourceKinds; kind++) missing += Math.Max(0, cost[kind] - hand[kind]);
            var value = (ScoresNow: scores && missing == 0, Value: (missing == 0 ? 100 : -20 * missing)
                + priority + hand[trade.Give] - trade.Cost);
            if (value.CompareTo(bestTrade) > 0) (wanted, bestTrade) = (trade, value);
        }
    }
}
