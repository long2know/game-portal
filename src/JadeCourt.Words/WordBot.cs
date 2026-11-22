namespace JadeCourt.Words;

internal static class WordBot
{
    private const int VisitBudget = 25_000;
    private const int CandidateBudget = 1_000;

    internal static WordAction Select(WordLexicon lexicon, WordView view)
    {
        var pass = new WordAction("pass");
        if (view.Status != "playing" || view.Turn is not (0 or 1) ||
            view.Rows != WordTiles.Size || view.Columns != WordTiles.Size ||
            view.Rack is not { Length: >= 1 and <= WordTiles.RackSize } ||
            view.Board is null || view.Board.Length > WordTiles.Count - view.Rack.Length)
            return pass;

        var board = new WordBoard(lexicon);
        var seen = new HashSet<int>();
        foreach (var cell in view.Board)
        {
            if (cell is null || cell.Seat is < 0 or > 1 || !WordBoard.InBounds(cell.Row, cell.Column) ||
                !AcceptTile(cell.Tile))
                return pass;
            var index = WordBoard.Index(cell.Row, cell.Column);
            if (board.TileAt(index) != 0)
                return pass;
            board.LoadTile(index, cell.Tile.Id);
        }
        foreach (var tile in view.Rack)
            if (!AcceptTile(tile))
                return pass;

        var rack = view.Rack.OrderBy(tile => tile.Id).ToArray();
        var anchors = new bool[WordBoard.Length];
        var crossMasks = new int[WordBoard.Length];
        var path = new (int Index, int TileId)[rack.Length];
        var visitsLeft = VisitBudget;
        var candidatesLeft = CandidateBudget;
        var bestPoints = 0;
        WordPlacement[]? best = null;
        for (var index = 0; index < WordBoard.Length; index++)
            anchors[index] = board.TileAt(index) == 0 && board.IsAnchor(index);

        foreach (var vertical in new[] { false, true })
        {
            if (!CanSearch())
                break;
            for (var index = 0; index < WordBoard.Length; index++)
                if (board.TileAt(index) == 0)
                    crossMasks[index] = board.CrossMask(index, vertical);

            for (var start = 0; start < WordBoard.Length && CanSearch(); start++)
                if (board.TileAt(WordBoard.Offset(start, vertical, -1)) == 0 && ReachesAnchor(start, vertical))
                    Search(start, vertical, lexicon.Root, (1 << rack.Length) - 1, 0, false);
        }
        return best is null ? pass : new WordAction("place", best);

        bool AcceptTile(WordTile? tile) =>
            tile is not null && WordTiles.IsTileId(tile.Id) && WordTiles.Get(tile.Id) == tile && seen.Add(tile.Id);

        bool CanSearch() => visitsLeft > 0 && candidatesLeft > 0;

        bool ReachesAnchor(int start, bool vertical)
        {
            var blanks = 0;
            for (var index = start; index >= 0; index = WordBoard.Offset(index, vertical, 1))
            {
                if (board.TileAt(index) != 0)
                    continue;
                if (++blanks > rack.Length || crossMasks[index] == 0)
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
            if (prefix.IsWord && linked && placed > 0 && existing == 0)
            {
                candidatesLeft--;
                var placements = new WordPlacement[placed];
                for (var i = 0; i < placed; i++)
                {
                    var (position, id) = path[i];
                    placements[i] = new(WordBoard.Row(position), WordBoard.Column(position), id);
                }
                // Paths already meet the board anchor and every cross-check; use the authoritative score.
                var score = board.Score(placements).Points;
                if (score > bestPoints)
                    (best, bestPoints) = (placements, score);
            }

            if (index < 0 || !CanSearch())
                return;
            var next = WordBoard.Offset(index, vertical, 1);
            if (existing != 0)
            {
                if (prefix.Step(WordTiles.Get(existing).Letter[0]) is { } continuation)
                    Search(next, vertical, continuation, remaining, placed, linked);
                return;
            }

            var tried = 0;
            for (var i = 0; i < rack.Length && CanSearch(); i++)
            {
                var slot = 1 << i;
                if ((remaining & slot) == 0)
                    continue;
                var letter = rack[i].Letter[0];
                var mask = 1 << (letter - 'A');
                if ((crossMasks[index] & mask) == 0 || (tried & mask) != 0 || prefix.Step(letter) is not { } child)
                    continue;
                tried |= mask;
                path[placed] = (index, rack[i].Id);
                Search(next, vertical, child, remaining ^ slot, placed + 1, linked || anchors[index]);
            }
        }
    }
}
