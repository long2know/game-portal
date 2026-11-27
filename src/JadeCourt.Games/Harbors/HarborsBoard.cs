namespace JadeCourt.Games.Harbors;

internal static class HarborsBoard
{
    internal readonly record struct Point(int X, int Y);
    internal readonly record struct Link(int A, int B)
    {
        internal int Other(int node) => A == node ? B : A;
    }

    internal static readonly HarborsTile[] Tiles;
    internal static readonly Point[] Sites;
    internal static readonly Link[] Routes;
    internal static readonly int[][] RoutesAtSite;

    static HarborsBoard()
    {
        (int Q, int R, int? Resource, int? Number)[] layout =
        [
            (0, 0, null, null),
            (1, 0, 0, 6), (0, 1, 2, 4), (-1, 1, 1, 8), (-1, 0, 3, 10), (0, -1, 0, 5), (1, -1, 1, 11),
            (2, 0, 3, 4), (0, 2, 3, 8), (-2, 2, 0, 3), (-2, 0, 1, 9), (0, -2, 2, 10), (2, -2, 2, 6)
        ];
        Point[] corners = [new(0, -2), new(1, -1), new(1, 1), new(0, 2), new(-1, 1), new(-1, -1)];
        var polygons = layout.Select(tile => corners.Select(corner =>
            new Point(2 * tile.Q + tile.R + corner.X, 3 * tile.R + corner.Y)).ToArray()).ToArray();
        Sites = [.. polygons.SelectMany(polygon => polygon).Distinct().OrderBy(site => site.Y).ThenBy(site => site.X)];
        var ids = Sites.Select((site, id) => (site, id)).ToDictionary(row => row.site, row => row.id);
        Tiles = [.. layout.Select((tile, id) => new HarborsTile(id, 2 * tile.Q + tile.R, 3 * tile.R,
            tile.Resource, tile.Number, polygons[id].Select(corner => ids[corner]).ToArray()))];
        Routes = [.. Tiles.SelectMany(tile => Sides(tile.NodeIds)).Distinct().OrderBy(link => link.A).ThenBy(link => link.B)];
        RoutesAtSite = [.. Sites.Select((_, node) => Routes.Select((link, id) => (link, id))
            .Where(row => row.link.A == node || row.link.B == node).Select(row => row.id).ToArray())];
    }

    private static IEnumerable<Link> Sides(int[] polygon)
    {
        for (var i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Length];
            yield return new(Math.Min(a, b), Math.Max(a, b));
        }
    }
}
