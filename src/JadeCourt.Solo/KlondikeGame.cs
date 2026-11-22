using System.Security.Cryptography;

namespace JadeCourt.Solo;

public sealed class KlondikeGame
{
    private const string Name = "klondike";
    private const int Ranks = 13;
    private const int Suits = 4;
    private const int Columns = 7;
    private const int DeckSize = Suits * Ranks;
    private const int InitialStockSize = DeckSize - Columns * (Columns + 1) / 2;
    private static readonly string[] SuitNames = ["clubs", "diamonds", "hearts", "spades"];
    private static readonly SolitaireCard CoveredCard = new(null, null, false);
    private static readonly Pile[] Piles =
    [
        new(PileKind.Waste, 0),
        .. Enumerable.Range(0, Columns).Select(index => new Pile(PileKind.Tableau, index)),
        .. Enumerable.Range(0, Suits).Select(index => new Pile(PileKind.Foundation, index)),
    ];

    public string Start(string variant)
    {
        if (variant != "draw-one")
            throw new ArgumentException("Klondike variant must be draw-one.", nameof(variant));

        var deck = Enumerable.Range(0, DeckSize).ToArray();
        RandomNumberGenerator.Shuffle(deck.AsSpan());
        var tableau = Enumerable.Range(0, Columns).Select(_ => new List<int>()).ToArray();
        var dealt = 0;
        for (var pass = 0; pass < Columns; pass++)
            for (var column = pass; column < Columns; column++)
                tableau[column].Add(deck[dealt++]);

        var state = new State(0, [.. deck.AsSpan(dealt)], [], new int[Suits], tableau,
            Enumerable.Range(0, Columns).ToArray());
        return SoloCheckpoint.Write(Name, state);
    }

    public string Apply(string checkpoint, SoloAction action)
    {
        var state = Read(checkpoint);
        if (Finished(state))
            throw new InvalidOperationException("This Klondike game is finished.");
        if (state.Moves == int.MaxValue)
            throw new InvalidOperationException("This Klondike game cannot record another action.");

        switch (action?.Kind)
        {
            case "draw":
                if (action is not { From: null, To: null, Count: null, Index: null, Value: null })
                    throw new InvalidOperationException("Draw takes no other fields.");
                Draw(state);
                break;
            case "move":
                if (action is not { Count: int count, Index: null, Value: null }
                    || Pile.Parse(action.From) is not { } from || Pile.Parse(action.To) is not { } to)
                    throw new InvalidOperationException("Move requires valid from and to piles and a count.");
                if (MoveError(state, from, to, count) is { } error)
                    throw new InvalidOperationException(error);
                Move(state, from, to, count);
                break;
            default:
                throw new InvalidOperationException("Klondike action must be draw or move.");
        }

        return SoloCheckpoint.Write(Name, state with { Moves = state.Moves + 1 });
    }

    public KlondikePosition View(string checkpoint)
    {
        var state = Read(checkpoint);
        var finished = Finished(state);
        return new KlondikePosition(finished, state.Moves, state.Stock.Count, state.Waste.Count,
            state.Waste.Count == 0 ? null : Face(state.Waste[^1]),
            state.Foundations.Select((height, suit) => height == 0 ? null : Face(suit * Ranks + height - 1)).ToArray(),
            state.Tableau.Select((column, i) => column.Select((card, j) =>
                j < state.Covered[i] ? CoveredCard : Face(card)).ToArray()).ToArray(),
            finished ? [] : LegalMoves(state).ToArray());
    }

    private static void Draw(State state)
    {
        if (state.Stock.Count > 0)
        {
            state.Waste.Add(state.Stock[^1]);
            state.Stock.RemoveAt(state.Stock.Count - 1);
        }
        else if (state.Waste.Count > 0)
        {
            state.Waste.Reverse();
            state.Stock.AddRange(state.Waste);
            state.Waste.Clear();
        }
        else
        {
            throw new InvalidOperationException("There are no stock or waste cards to draw.");
        }
    }

    private static IEnumerable<KlondikeMove> LegalMoves(State state)
    {
        foreach (var from in Piles)
        {
            var available = MovableCount(state, from);
            for (var count = 1; count <= available; count++)
                foreach (var to in Piles)
                    if (MoveError(state, from, to, count) is null)
                        yield return new KlondikeMove(from.Id, to.Id, count);
        }
    }

    private static string? MoveError(State state, Pile from, Pile to, int count)
    {
        if (from == to || to.Kind == PileKind.Waste)
            return "Move must go to a different tableau or foundation pile.";
        if (count < 1 || count > MovableCount(state, from))
            return "Move only a face-up tableau tail or the single top card of a waste or foundation pile.";

        var card = MovingCard(state, from, count);
        if (to.Kind == PileKind.Foundation)
        {
            if (from.Kind == PileKind.Foundation || count != 1)
                return "Move one waste or tableau card at a time to a foundation.";
            return Suit(card) == to.Index && Rank(card) == state.Foundations[to.Index] + 1
                ? null : "A foundation builds from Ace upward in its own suit.";
        }

        var column = state.Tableau[to.Index];
        if (column.Count == 0)
            return Rank(card) == Ranks ? null : "Only a King can fill an empty tableau column.";
        return CanStack(card, column[^1]) ? null : "Tableau builds descend one rank in alternating colors.";
    }

    private static int MovableCount(State state, Pile pile) => pile.Kind switch
    {
        PileKind.Tableau => state.Tableau[pile.Index].Count - state.Covered[pile.Index],
        PileKind.Foundation => state.Foundations[pile.Index] == 0 ? 0 : 1,
        _ => state.Waste.Count == 0 ? 0 : 1,
    };

    private static int MovingCard(State state, Pile from, int count) => from.Kind switch
    {
        PileKind.Tableau => state.Tableau[from.Index][^count],
        PileKind.Foundation => from.Index * Ranks + state.Foundations[from.Index] - 1,
        _ => state.Waste[^1],
    };

    private static void Move(State state, Pile from, Pile to, int count)
    {
        List<int> cards;
        if (from.Kind == PileKind.Tableau)
        {
            var column = state.Tableau[from.Index];
            var start = column.Count - count;
            cards = column.GetRange(start, count);
            column.RemoveRange(start, count);
            if (start > 0 && start == state.Covered[from.Index])
                state.Covered[from.Index]--;
        }
        else
        {
            cards = [MovingCard(state, from, count)];
            if (from.Kind == PileKind.Foundation)
                state.Foundations[from.Index]--;
            else
                state.Waste.RemoveAt(state.Waste.Count - 1);
        }

        if (to.Kind == PileKind.Foundation)
            state.Foundations[to.Index]++;
        else
            state.Tableau[to.Index].AddRange(cards);
    }

    private static State Read(string checkpoint)
    {
        var state = SoloCheckpoint.Read<State>(Name, checkpoint);
        if (state.Moves < 0 || state.Stock is null || state.Waste is null
            || state.Stock.Count + state.Waste.Count > InitialStockSize
            || state.Foundations is not { Length: Suits } || state.Tableau is not { Length: Columns }
            || state.Covered is not { Length: Columns })
            throw SoloCheckpoint.Invalid(Name);

        ulong seen = 0;
        foreach (var card in state.Stock)
            Add(card);
        foreach (var card in state.Waste)
            Add(card);

        for (var suit = 0; suit < Suits; suit++)
        {
            var height = state.Foundations[suit];
            if (height is < 0 or > Ranks)
                throw SoloCheckpoint.Invalid(Name);
            for (var offset = 0; offset < height; offset++)
                Add(suit * Ranks + offset);
        }

        for (var i = 0; i < Columns; i++)
        {
            var column = state.Tableau[i];
            var covered = state.Covered[i];
            if (column is null || covered < 0 || covered > i || covered > Math.Max(0, column.Count - 1))
                throw SoloCheckpoint.Invalid(Name);
            for (var j = 0; j < column.Count; j++)
            {
                Add(column[j]);
                if (j > covered && !CanStack(column[j], column[j - 1]))
                    throw SoloCheckpoint.Invalid(Name);
            }
        }

        if (seen != (1UL << DeckSize) - 1)
            throw SoloCheckpoint.Invalid(Name);
        return state;

        void Add(int card)
        {
            if (card is < 0 or >= DeckSize || (seen & (1UL << card)) != 0)
                throw SoloCheckpoint.Invalid(Name);
            seen |= 1UL << card;
        }
    }

    private static bool Finished(State state) => state.Foundations.All(height => height == Ranks);
    private static int Rank(int card) => card % Ranks + 1;
    private static int Suit(int card) => card / Ranks;
    private static bool IsRed(int card) => Suit(card) is 1 or 2;
    private static bool CanStack(int card, int below) => Rank(card) + 1 == Rank(below) && IsRed(card) != IsRed(below);
    private static SolitaireCard Face(int card) => new(Rank(card), SuitNames[Suit(card)], true);

    private sealed record State(
        int Moves, List<int> Stock, List<int> Waste, int[] Foundations, List<int>[] Tableau, int[] Covered);

    private enum PileKind { Waste, Tableau, Foundation }

    private readonly record struct Pile(PileKind Kind, int Index)
    {
        public string Id => Kind switch
        {
            PileKind.Tableau => $"t{Index}",
            PileKind.Foundation => $"f{Index}",
            _ => "waste",
        };

        public static Pile? Parse(string? id)
        {
            if (id == "waste")
                return new Pile(PileKind.Waste, 0);
            if (id is not { Length: 2 })
                return null;

            var index = id[1] - '0';
            return id[0] switch
            {
                't' when index is >= 0 and < Columns => new Pile(PileKind.Tableau, index),
                'f' when index is >= 0 and < Suits => new Pile(PileKind.Foundation, index),
                _ => null,
            };
        }
    }
}
