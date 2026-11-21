namespace JadeCourt.Games;

internal readonly record struct Point(int Row, int Column)
{
    internal static Point At(int index, int columns) => new(index / columns, index % columns);

    internal int Index(int columns) => Row * columns + Column;

    internal int Offset(int row, int column, int size)
    {
        var y = Row + row;
        var x = Column + column;
        return (uint)y < size && (uint)x < size ? y * size + x : -1;
    }

    internal string Id => FormattableString.Invariant($"r{Row}c{Column}");
    internal string Label => FormattableString.Invariant($"{(char)('A' + Column)}{Row + 1}");

    internal GameCell Cell(int? owner, string? piece) => new(Id, Row, Column, owner, piece);
}

internal static class BoardNotation
{
    internal static readonly GameMove Pass = new("pass", "Pass", null, null);

    internal static GameMove Place(Point destination, string? label = null)
    {
        var id = destination.Id;
        return new(id, label ?? destination.Label, null, id);
    }

    internal static GameMove Travel(Point from, Point[] landings, string? promotion = null)
    {
        var fromId = from.Id;
        var ids = landings.Select(point => point.Id).ToArray();
        var label = $"{from.Label} → {string.Join(" → ", landings.Select(point => point.Label))}";
        if (promotion is not null)
            label += $" ({promotion})";
        return new($"{fromId}>{string.Join('>', ids)}", label, fromId, ids[^1], promotion,
            ids.Length > 1 ? ids[..^1] : null);
    }
}
