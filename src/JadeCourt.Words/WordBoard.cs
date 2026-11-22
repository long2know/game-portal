namespace JadeCourt.Words;

internal sealed class WordBoard(WordLexicon lexicon)
{
    internal const int Length = WordTiles.Size * WordTiles.Size;
    internal const int Center = Length / 2;
    internal const int AllLetters = (1 << 26) - 1;
    private readonly int[] tiles = new int[Length];

    internal bool IsEmpty { get; private set; } = true;
    internal static int Index(int row, int column) => row * WordTiles.Size + column;
    internal static int Row(int index) => index / WordTiles.Size;
    internal static int Column(int index) => index % WordTiles.Size;

    internal static bool InBounds(int row, int column) =>
        (uint)row < WordTiles.Size && (uint)column < WordTiles.Size;

    internal static int Offset(int index, bool vertical, int distance)
    {
        var (row, column) = (Row(index), Column(index));
        if (vertical)
            row += distance;
        else
            column += distance;
        return InBounds(row, column) ? Index(row, column) : -1;
    }

    internal int TileAt(int index) => index < 0 ? 0 : tiles[index];

    internal void LoadTile(int index, int id)
    {
        tiles[index] = id;
        IsEmpty = false;
    }

    internal bool IsAnchor(int index) => IsEmpty ? index == Center :
        TileAt(Offset(index, false, -1)) != 0 || TileAt(Offset(index, false, 1)) != 0 ||
        TileAt(Offset(index, true, -1)) != 0 || TileAt(Offset(index, true, 1)) != 0;

    internal MoveScore Score(WordPlacement[] placements)
    {
        Span<int> added = stackalloc int[placements.Length];
        var sameRow = true;
        var sameColumn = true;
        var linked = false;
        for (var i = 0; i < placements.Length; i++)
        {
            var placement = placements[i];
            if (!InBounds(placement.Row, placement.Column))
                throw Illegal("A tile is outside the board.");
            var index = Index(placement.Row, placement.Column);
            if (tiles[index] != 0 || added[..i].Contains(index))
                throw Illegal("Each tile must occupy a different empty cell.");

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
            TileAt(Offset(added[0], false, -1)) == 0 && TileAt(Offset(added[0], false, 1)) == 0);
        for (var i = 0; i < added.Length; i++)
            tiles[added[i]] = placements[i].TileId;
        added.Sort();

        try
        {
            var main = ReadWord(added[0], vertical, out var end, out var points);
            if (end < added[^1])
                throw Illegal("There may not be a gap in the new word.");
            RequireWord(main);

            var words = new List<string> { main };
            foreach (var index in added)
            {
                var cross = ReadWord(index, !vertical, out _, out var crossPoints);
                if (cross.Length == 1)
                    continue;
                RequireWord(cross);
                words.Add(cross);
                points += crossPoints + WordTiles.LinkBonus;
            }
            return new MoveScore(words.ToArray(), points);
        }
        finally
        {
            foreach (var index in added)
                tiles[index] = 0;
        }
    }

    internal int CrossMask(int index, bool vertical)
    {
        var crossDirection = !vertical;
        Span<char> candidate = stackalloc char[WordTiles.Size];
        var length = 0;
        var blank = 0;
        for (var position = Start(index, crossDirection); position >= 0 &&
            (position == index || tiles[position] != 0); position = Offset(position, crossDirection, 1))
        {
            if (position == index)
                blank = length++;
            else
                candidate[length++] = WordTiles.Get(tiles[position]).Letter[0];
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
        while (TileAt(Offset(index, vertical, -1)) != 0)
            index = Offset(index, vertical, -1);
        return index;
    }

    private string ReadWord(int index, bool vertical, out int end, out int points)
    {
        Span<char> letters = stackalloc char[WordTiles.Size];
        var length = 0;
        points = 0;
        end = index;
        for (var position = Start(index, vertical); TileAt(position) != 0; position = Offset(position, vertical, 1))
        {
            var tile = WordTiles.Get(tiles[position]);
            letters[length++] = tile.Letter[0];
            points += tile.Points;
            end = position;
        }
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
