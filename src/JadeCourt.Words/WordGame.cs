using System.Security.Cryptography;

namespace JadeCourt.Words;

public sealed class WordGame
{
    private readonly WordLexicon lexicon;
    private readonly Action<int[]> shuffle;

    public WordGame() : this(WordLexicon.Default, ShuffleSecure) { }

    internal WordGame(WordLexicon lexicon, Action<int[]> shuffle)
    {
        this.lexicon = lexicon;
        this.shuffle = shuffle;
    }

    public string Start()
    {
        var rules = WordTiles.Duel;
        var tiles = Enumerable.Range(1, rules.Count).ToArray();
        shuffle(tiles);
        return new WordCheckpoint(rules.Version, 0,
            [tiles[..rules.RackSize], tiles[rules.RackSize..(2 * rules.RackSize)]],
            tiles[(2 * rules.RackSize)..], [], [0, 0], 0, null).Save();
    }

    public WordView View(string checkpoint, int? viewerSeat)
    {
        if (viewerSeat is { } seat)
            ValidateSeat(seat);
        var state = WordCheckpoint.Load(checkpoint);
        var rules = state.Rules;
        var finished = state.Finished;
        int[] winners = [];
        string? detail = null;
        if (finished)
        {
            if (!rules.IsDuel || state.WentOut)
            {
                var left = state.Racks.Select(rack => rack.Sum(id => rules.Get(id).Points)).ToArray();
                for (var i = 0; i < 2; i++)
                    state.Scores[i] -= left[i];
                if (rules.UsesPremiumRules && state.WentOut)
                    state.Scores[1 - state.Turn] += left[state.Turn];
            }
            winners = state.Scores[0].CompareTo(state.Scores[1]) switch
            {
                > 0 => [0],
                < 0 => [1],
                _ => []
            };
            detail = state.WentOut
                ? "A seat emptied its rack with no tiles left to draw."
                : rules.IsDuel ? "Three consecutive scoreless turns."
                : rules.UsesPremiumRules ? "Six consecutive scoreless turns." : "Four turns without a placement.";
        }

        // Checkpoints append placements in turn order, before projection sorts them for display.
        var recentStart = state.Board.Length - (rules.IsDuel && state.Last is { Kind: "place" } move ? move.Count : 0);
        return new WordView(rules.Size, rules.Size, finished ? null : state.Turn,
            finished ? "finished" : "playing", winners, detail,
            state.Board.Select((cell, index) => new WordCell(cell.Row, cell.Column, rules.Placed(cell.TileId, cell.Letter), cell.Seat)
                { Recent = index >= recentStart }).OrderBy(cell => cell.Row).ThenBy(cell => cell.Column).ToArray(),
            viewerSeat is { } viewer ? state.Racks[viewer].Select(rules.Get).ToArray() : [],
            state.Scores, state.Racks.Select(rack => rack.Length).ToArray(), state.Bag.Length, DescribeLast(state),
            rules.Variant, rules.RackSize, rules.ProjectPremiums(),
            !finished && viewerSeat == state.Turn ? rules.ExchangeLimit(state.Racks[state.Turn].Length, state.Bag.Length) : 0)
        { ViewerSeat = viewerSeat };
    }

    public string Apply(string checkpoint, int seat, WordAction action)
    {
        ValidateSeat(seat);
        ArgumentNullException.ThrowIfNull(action);
        var state = WordCheckpoint.Load(checkpoint);
        if (state.Finished)
            throw new InvalidOperationException("The Word Tiles game has finished.");
        if (state.Turn != seat)
            throw new InvalidOperationException("It is not this seat's turn.");

        var next = action switch
        {
            { Kind: "place", Placements: { } placements, Exchange: null }
                => Place(state, seat, placements),
            { Kind: "exchange", Placements: null, Exchange: { } exchange }
                => Exchange(state, seat, exchange),
            { Kind: "pass", Placements: null, Exchange: null } => state with
                { DryTurns = state.NextDryTurn, Last = new("pass", 0, null, 0) },
            _ => throw new InvalidOperationException("Invalid Word Tiles action or payload.")
        };
        return (next with { Turn = 1 - seat }).Save();
    }

    public WordAction SelectBotMove(WordView actingSeatView)
    {
        ArgumentNullException.ThrowIfNull(actingSeatView);
        return WordBot.Select(lexicon, actingSeatView);
    }

    private WordCheckpoint Place(WordCheckpoint state, int seat, WordPlacement[] placements)
    {
        var rules = state.Rules;
        if (placements.Length < 1 || placements.Length > rules.RackSize)
            throw new InvalidOperationException($"Place between 1 and {rules.RackSize} tiles.");
        if (placements.Any(placement => placement is null))
            throw new InvalidOperationException("A placement is missing its tile or coordinates.");
        var used = RequireOwnedTiles(state.Racks[seat], placements.Select(placement => placement.TileId));

        var board = new WordBoard(rules, lexicon);
        foreach (var cell in state.Board)
            board.LoadTile(board.Index(cell.Row, cell.Column), rules.Placed(cell.TileId, cell.Letter));
        var play = board.Score(placements);

        var remaining = state.Racks[seat].Where(id => !used.Contains(id)).ToArray();
        var draw = Math.Min(rules.RackSize - remaining.Length, state.Bag.Length);
        state.Racks[seat] = [.. remaining, .. state.Bag[..draw]];
        state.Scores[seat] += play.Points;
        return state with
        {
            Board = [.. state.Board, .. placements.Select(p => new WordCheckpoint.PlacedTile(p.Row, p.Column, p.TileId, seat, p.Letter))],
            Bag = state.Bag[draw..],
            DryTurns = play.Points > 0 ? 0 : state.NextDryTurn,
            Last = new("place", placements.Length, play.Words, play.Points)
        };
    }

    private WordCheckpoint Exchange(WordCheckpoint state, int seat, int[] exchange)
    {
        var rules = state.Rules;
        var rack = state.Racks[seat];
        if (exchange.Length < 1 || exchange.Length > rules.MaxExchange)
            throw new InvalidOperationException($"Exchange between 1 and {rules.MaxExchange} tiles.");
        if (exchange.Length > rules.ExchangeLimit(rack.Length, state.Bag.Length))
            throw new InvalidOperationException("There are not enough tiles in the bag or rack for that exchange.");
        var returned = RequireOwnedTiles(rack, exchange);
        state.Racks[seat] = [.. rack.Where(id => !returned.Contains(id)), .. state.Bag[..exchange.Length]];
        int[] bag = [.. state.Bag[exchange.Length..], .. rack.Where(returned.Contains)];
        shuffle(bag);
        return state with
        {
            Bag = bag,
            DryTurns = state.NextDryTurn,
            Last = new("exchange", exchange.Length, null, 0)
        };
    }

    private static HashSet<int> RequireOwnedTiles(int[] rack, IEnumerable<int> ids)
    {
        var used = new HashSet<int>();
        foreach (var id in ids)
        {
            if (!rack.Contains(id) || !used.Add(id))
                throw new InvalidOperationException("Choose distinct tiles from your current rack.");
        }
        return used;
    }

    private static void ValidateSeat(int seat)
    {
        if (seat is not (0 or 1))
            throw new ArgumentOutOfRangeException(nameof(seat), "Word Tiles has seats 0 and 1.");
    }

    private static string? DescribeLast(WordCheckpoint state)
    {
        var player = $"Seat {2 - state.Turn}";
        return state.Last switch
        {
            { Kind: "place", Words: { } words, Points: var points } => $"{player} played {string.Join(", ", words)} (+{points}).",
            { Kind: "exchange", Count: var count } => $"{player} exchanged {count} {(count == 1 ? "tile" : "tiles")}.",
            { Kind: "pass" } => $"{player} passed.",
            _ => null
        };
    }

    private static void ShuffleSecure(int[] tiles)
    {
        for (var i = tiles.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (tiles[i], tiles[j]) = (tiles[j], tiles[i]);
        }
    }
}
