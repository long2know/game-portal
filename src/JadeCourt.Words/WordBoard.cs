namespace JadeCourt.Words;

internal sealed class WordBoard(WordTiles rules, WordLexicon lexicon)
{
    internal const int AllLetters = (1 << 26) - 1;
    private readonly WordTile?[] tiles = new WordTile?[rules.Size * rules.Size];

    internal int Length => tiles.Length;
    internal int Center => Length / 2;
    internal bool IsEmpty { get; private set; } = true;
    internal int Index(int row, int column) => row * rules.Size + column;
    internal int Row(int index) => index / rules.Size;
    internal int Column(int index) => index % rules.Size;

    internal int Offset(int index, bool vertical, int distance)
    {
        var (row, column) = (Row(index), Column(index));
        if (vertical)
            row += distance;
        else
            column += distance;
        return rules.InBounds(row, column) ? Index(row, column) : -1;
    }

    internal WordTile? TileAt(int index) => index < 0 ? null : tiles[index];

    internal void LoadTile(int index, WordTile tile)
    {
        tiles[index] = tile;
        IsEmpty = false;
    }

    internal bool IsAnchor(int index) => IsEmpty ? index == Center :
        TileAt(Offset(index, false, -1)) is not null || TileAt(Offset(index, false, 1)) is not null ||
        TileAt(Offset(index, true, -1)) is not null || TileAt(Offset(index, true, 1)) is not null;

    internal MoveScore Score(WordPlacement[] placements)
    {
        Span<int> added = stackalloc int[placements.Length];
        var sameRow = true;
        var sameColumn = true;
        var linked = false;
        for (var i = 0; i < placements.Length; i++)
        {
            var placement = placements[i];
            if (!rules.InBounds(placement.Row, placement.Column))
                throw Illegal("A tile is outside the board.");
            var index = Index(placement.Row, placement.Column);
            if (tiles[index] is not null || added[..i].Contains(index))
                throw Illegal("Each tile must occupy a different empty cell.");
            if (!rules.CanAssign(placement.TileId, placement.Letter))
                throw Illegal(rules.Get(placement.TileId).Blank
                    ? "Assign an uppercase A–Z letter to each blank."
                    : "Only blank tiles can be assigned a letter.");

            sameRow &= placement.Row == placements[0].Row;
            sameColumn &= placement.Column == placements[0].Column;
            linked |= IsAnchor(index);
            added[i] = index;
        }

        if (!sameRow && !sameColumn)
            throw Illegal("Place tiles in a single row or column.");
        if (!linked)
            throw Illegal(IsEmpty ? "The first word must cross the center." : "Link your play to the existing board.");

        // A single tile may make a word in either direction. Report the horizontal word first if it makes both.
        var vertical = !sameRow || (placements.Length == 1 &&
            TileAt(Offset(added[0], false, -1)) is null && TileAt(Offset(added[0], false, 1)) is null);
        for (var i = 0; i < added.Length; i++)
            tiles[added[i]] = rules.Placed(placements[i].TileId, placements[i].Letter);
        added.Sort();

        try
        {
            var main = ReadWord(added[0], vertical, added, out var end, out var points);
            if (end < added[^1])
                throw Illegal("There may not be a gap in the new word.");
            RequireWord(main);

            var words = new List<string> { main };
            foreach (var index in added)
            {
                var cross = ReadWord(index, !vertical, added, out _, out var crossPoints);
                if (cross.Length == 1)
                    continue;
                RequireWord(cross);
                words.Add(cross);
                points += crossPoints + rules.LinkBonus;
            }
            if (placements.Length == rules.RackSize)
                points += rules.RackBonus;
            return new MoveScore(words.ToArray(), points);
        }
        finally
        {
            foreach (var index in added)
                tiles[index] = null;
        }
    }

    internal int CrossMask(int index, bool vertical)
    {
        var crossDirection = !vertical;
        Span<char> candidate = stackalloc char[WordTiles.MaxSize];
        var length = 0;
        var blank = 0;
        for (var position = Start(index, crossDirection); position >= 0 &&
            (position == index || tiles[position] is not null); position = Offset(position, crossDirection, 1))
        {
            if (position == index)
                blank = length++;
            else
                candidate[length++] = tiles[position]!.Letter[0];
        }
        if (length == 1)
            return AllLetters;

        var allowed = 0;
        for (var letter = 0; letter < 26; letter++)
        {
            candidate[blank] = (char)('A' + letter);
            if (lexicon.Contains(candidate[..length]))
                allowed |= 1 << letter;
        }
        return allowed;
    }

    private int Start(int index, bool vertical)
    {
        while (TileAt(Offset(index, vertical, -1)) is not null)
            index = Offset(index, vertical, -1);
        return index;
    }

    private string ReadWord(int index, bool vertical, ReadOnlySpan<int> added, out int end, out int points)
    {
        Span<char> letters = stackalloc char[WordTiles.MaxSize];
        var length = 0;
        var wordMultiplier = 1;
        points = 0;
        end = index;
        for (var position = Start(index, vertical); TileAt(position) is { } tile; position = Offset(position, vertical, 1))
        {
            letters[length++] = tile.Letter[0];
            var premium = added.Contains(position) ? rules.PremiumAt(position) : PremiumKind.None;
            points += tile.Points * (premium is PremiumKind.DoubleLetter ? 2 : premium is PremiumKind.TripleLetter ? 3 : 1);
            wordMultiplier *= premium is PremiumKind.DoubleWord ? 2 : premium is PremiumKind.TripleWord ? 3 : 1;
            end = position;
        }
        points *= wordMultiplier;
        return new string(letters[..length]);
    }

    private void RequireWord(string word)
    {
        if (word.Length < 2)
            throw Illegal("A move must form at least one word of two or more letters.");
        if (!lexicon.Contains(word))
            throw Illegal($"{word} is not in the Word Tiles lexicon.");
    }

    private static InvalidOperationException Illegal(string message) => new(message);

    internal readonly record struct MoveScore(string[] Words, int Points);
}
