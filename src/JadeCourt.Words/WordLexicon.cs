using System.Text;

namespace JadeCourt.Words;

internal sealed class WordLexicon
{
    private const string ResourceName = "JadeCourt.Words.Lexicon.words.txt";
    private static readonly Lazy<WordLexicon> Embedded = new(LoadEmbedded);

    private WordLexicon() { }

    internal static WordLexicon Default => Embedded.Value;
    internal Node Root { get; } = new();

    internal static WordLexicon Load(TextReader source)
    {
        var lexicon = new WordLexicon();
        string? previous = null;
        while (source.ReadLine() is { } word)
        {
            if (!IsEntry(word) || string.CompareOrdinal(previous, word) >= 0)
                throw new InvalidDataException("The Word Tiles lexicon must contain sorted, unique A–Z words of length 2–11.");
            var node = lexicon.Root;
            foreach (var letter in word)
                node = node.Add(letter);
            node.MarkWord();
            previous = word;
        }
        return previous is not null ? lexicon : throw new InvalidDataException("The Word Tiles lexicon is empty.");
    }

    internal static bool IsEntry(string word) =>
        word.Length is >= 2 and <= WordTiles.Size && word.All(letter => letter is >= 'A' and <= 'Z');

    internal bool Contains(ReadOnlySpan<char> word)
    {
        var node = Root;
        foreach (var letter in word)
        {
            if (node.Step(letter) is not { } next)
                return false;
            node = next;
        }
        return node.IsWord;
    }

    private static WordLexicon LoadEmbedded()
    {
        using var stream = typeof(WordLexicon).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The Word Tiles lexicon resource is missing.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        try
        {
            return Load(reader);
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("The Word Tiles lexicon contains invalid UTF-8.", error);
        }
    }

    internal sealed class Node
    {
        private Node?[]? children;
        internal bool IsWord { get; private set; }
        internal Node? Step(char letter) => children?[letter - 'A'];
        internal Node Add(char letter) => (children ??= new Node?[26])[letter - 'A'] ??= new Node();
        internal void MarkWord() => IsWord = true;
    }
}
