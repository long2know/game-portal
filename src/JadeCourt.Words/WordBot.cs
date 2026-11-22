namespace JadeCourt.Words;

internal static class WordBot
{
    private const int VisitBudget = 25_000;
    private const int CandidateBudget = 1_000;

    internal static WordAction Select(WordLexicon lexicon, WordView view)
    {
        var pass = new WordAction("pass");
        if (view.Status != "playing" || view.Turn is not (0 or 1) ||
            (view.ViewerSeat is { } viewer && viewer != view.Turn) || WordTiles.ForVariant(view.Variant) is not { } rules ||
            view.Rows != rules.Size || view.Columns != rules.Size || view.RackSize != rules.RackSize ||
            view.Rack is not { Length: >= 1 } || view.Rack.Length > rules.RackSize ||
            view.RackSizes is not { Length: 2 } || view.RackSizes.Any(size => size < 1 || size > rules.RackSize) ||
            view.RackSizes[view.Turn.Value] != view.Rack.Length || view.BagCount < 0 || view.BagCount > rules.Count ||
            view.Board is null || view.Board.Length > rules.Count ||
            view.Board.Length + view.BagCount + view.RackSizes.Sum() != rules.Count ||
            view.ExchangeLimit != rules.ExchangeLimit(view.Rack.Length, view.BagCount))
            return pass;

        var board = new WordBoard(rules, lexicon);
        var seen = new HashSet<int>();
        foreach (var cell in view.Board)
        {
            if (cell is null || cell.Seat is < 0 or > 1 || !rules.InBounds(cell.Row, cell.Column) ||
                !AcceptTile(cell.Tile, onBoard: true))
                return pass;
            var index = board.Index(cell.Row, cell.Column);
            if (board.TileAt(index) is not null)
                return pass;
            board.LoadTile(index, cell.Tile);
        }
        if (!board.IsEmpty && (view.Board.Length == 1 || board.TileAt(board.Center) is null))
            return pass;
        foreach (var tile in view.Rack)
            if (!AcceptTile(tile, onBoard: false))
                return pass;

        var rack = view.Rack.OrderBy(tile => tile.Id).ToArray();
        var anchors = new bool[board.Length];
        var crossMasks = new int[board.Length];
        var path = new (int Index, int TileId, string? Letter)[rack.Length];
        var visitsLeft = VisitBudget;
        var candidatesLeft = CandidateBudget;
        var bestPoints = 0;
        WordPlacement[]? best = null;
        for (var index = 0; index < board.Length; index++)
            anchors[index] = board.TileAt(index) is null && board.IsAnchor(index);

        foreach (var vertical in new[] { false, true })
        {
            if (!CanSearch())
                break;
            for (var index = 0; index < board.Length; index++)
                if (board.TileAt(index) is null)
                    crossMasks[index] = board.CrossMask(index, vertical);

            for (var start = 0; start < board.Length && CanSearch(); start++)
                if (board.TileAt(board.Offset(start, vertical, -1)) is null && ReachesAnchor(start, vertical))
                    Search(start, vertical, lexicon.Root, (1 << rack.Length) - 1, 0, false);
        }
        return best is null ? pass : new WordAction("place", best);

        bool AcceptTile(WordTile? tile, bool onBoard)
        {
            if (tile is null || !rules.IsTileId(tile.Id) || !seen.Add(tile.Id))
                return false;
            var canonical = rules.Get(tile.Id);
            return onBoard && canonical.Blank
                ? tile.Blank && tile.Points == 0 && WordTiles.IsLetter(tile.Letter)
                : canonical == tile;
        }

        bool CanSearch() => visitsLeft > 0 && candidatesLeft > 0;

        bool ReachesAnchor(int start, bool vertical)
        {
            var spaces = 0;
            for (var index = start; index >= 0; index = board.Offset(index, vertical, 1))
            {
                if (board.TileAt(index) is not null)
                    continue;
                if (++spaces > rack.Length || crossMasks[index] == 0)
                    return false;
                if (anchors[index])
                    return true;
            }
            return false;
        }

        void Search(int index, bool vertical, WordLexicon.Node prefix, int remaining, int placed, bool linked)
        {
            if (!CanSearch())
                return;
            visitsLeft--;
            var existing = board.TileAt(index);
            if (prefix.IsWord && linked && placed > 0 && existing is null)
            {
                candidatesLeft--;
                var placements = new WordPlacement[placed];
                for (var i = 0; i < placed; i++)
                {
                    var (position, id, letter) = path[i];
                    placements[i] = new(board.Row(position), board.Column(position), id, letter);
                }
                // Paths already meet the board anchor and every cross-check; use the authoritative score.
                var score = board.Score(placements).Points;
                if (best is null || score > bestPoints)
                    (best, bestPoints) = (placements, score);
            }

            if (index < 0 || !CanSearch())
                return;
            var next = board.Offset(index, vertical, 1);
            if (existing is not null)
            {
                if (prefix.Step(existing.Letter[0], rules.Size) is { } continuation)
                    Search(next, vertical, continuation, remaining, placed, linked);
                return;
            }

            var tried = 0;
            var triedBlank = false;
            for (var i = 0; i < rack.Length && CanSearch(); i++)
            {
                var slot = 1 << i;
                if ((remaining & slot) == 0)
                    continue;
                if (rack[i].Blank)
                {
                    if (triedBlank)
                        continue;
                    triedBlank = true;
                    for (var letterIndex = 0; letterIndex < 26 && CanSearch(); letterIndex++)
                    {
                        var assigned = (char)('A' + letterIndex);
                        if ((crossMasks[index] & (1 << letterIndex)) == 0 || prefix.Step(assigned, rules.Size) is not { } wildcard)
                            continue;
                        path[placed] = (index, rack[i].Id, assigned.ToString());
                        Search(next, vertical, wildcard, remaining ^ slot, placed + 1, linked || anchors[index]);
                    }
                    continue;
                }
                var letter = rack[i].Letter[0];
                var mask = 1 << (letter - 'A');
                if ((crossMasks[index] & mask) == 0 || (tried & mask) != 0 || prefix.Step(letter, rules.Size) is not { } child)
                    continue;
                tried |= mask;
                path[placed] = (index, rack[i].Id, null);
                Search(next, vertical, child, remaining ^ slot, placed + 1, linked || anchors[index]);
            }
        }
    }
}
