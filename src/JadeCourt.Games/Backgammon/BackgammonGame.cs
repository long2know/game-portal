namespace JadeCourt.Games.Backgammon;

public readonly record struct BackgammonMove(int From, int To, int Die);
public enum BackgammonActionKind { Roll = 1, Pass, Move }
public readonly record struct BackgammonAction(BackgammonActionKind Kind, BackgammonMove Move = default);
public enum BackgammonRejection { NotYourTurn, IllegalAction, Finished }
public sealed class BackgammonRejectedException(BackgammonRejection reason) : Exception(reason.ToString())
{
    public BackgammonRejection Reason { get; } = reason;
}

public sealed record BackgammonTurn(int Seat, int[] Dice, int[] Unused);
public sealed record BackgammonLegalActions(bool CanRoll, bool CanPass, BackgammonMove[] Moves);
public sealed record BackgammonView(int[] Points, int[] Bar, int[] Off, int? Turn, int? Winner,
    int[] Dice, int[] Remaining, int[] OpeningRoll, BackgammonTurn? LastTurn, BackgammonLegalActions? LegalActions);

internal sealed record BackgammonState(int[] Points, int[] Bar, int[] Off, int? Turn, int? Winner,
    int[] Dice, int[] Remaining, int[] OpeningRoll, BackgammonTurn? LastTurn);

public static class BackgammonGame
{
    public const int Bar = 0;
    public const int Off = 25;
    private const int Checkers = 15;
    private const int CheckpointLimit = 4_096;
    private const string Game = "backgammon";

    public static string Start(Func<int> die, CancellationToken stop = default)
    {
        var points = new int[24];
        foreach (var (point, count) in new[] { (24, 2), (13, 5), (8, 3), (6, 5) })
        {
            points[point - 1] = count;
            points[24 - point] = -count;
        }

        for (var attempt = 0; attempt < 128; attempt++)
        {
            stop.ThrowIfCancellationRequested();
            var dice = ThrowDice(die);
            if (dice[0] != dice[1])
                return Save(new(points, [0, 0], [0, 0], OpeningSeat(dice), null, dice, dice, dice, null));
        }
        throw new InvalidDataException("The Backgammon opening roll did not produce a winner.");
    }

    public static BackgammonView View(string checkpoint, int viewer, bool playing = true)
    {
        var state = Load(checkpoint);
        BackgammonLegalActions? legal = null;
        if (playing && state.Turn == viewer)
        {
            if (state.Dice.Length == 0)
                legal = new(true, false, []);
            else
            {
                var moves = Search(state, viewer).Moves;
                legal = new(false, moves.Length == 0, moves);
            }
        }
        return new(state.Points, state.Bar, state.Off, playing ? state.Turn : null, state.Winner,
            state.Dice, state.Remaining, state.OpeningRoll, state.LastTurn, legal);
    }

    public static string Apply(string checkpoint, int seat, BackgammonAction action, Func<int> die)
    {
        var state = Load(checkpoint);
        if (state.Winner.HasValue) throw new BackgammonRejectedException(BackgammonRejection.Finished);
        if (state.Turn != seat) throw new BackgammonRejectedException(BackgammonRejection.NotYourTurn);
        if (state.Dice.Length == 0 && action.Kind == BackgammonActionKind.Roll && action.Move == default)
            return Save(Roll(state, die));
        if (state.Dice.Length != 0)
        {
            var moves = Search(state, seat).Moves;
            if (action.Kind == BackgammonActionKind.Pass && action.Move == default && moves.Length == 0)
                return Save(EndTurn(state, seat));
            if (action.Kind == BackgammonActionKind.Move && moves.Contains(action.Move))
            {
                var next = Step(state, seat, action.Move);
                return Save(next.Off[seat] == Checkers || !Steps(next, seat).Any() ? EndTurn(next, seat) : next);
            }
        }
        throw new BackgammonRejectedException(BackgammonRejection.IllegalAction);
    }

    public static string PlayBotTurn(string checkpoint, int seat, Func<int> die, CancellationToken stop = default)
    {
        var state = Load(checkpoint);
        if (state.Turn != seat) return checkpoint;
        stop.ThrowIfCancellationRequested();
        if (state.Dice.Length == 0) state = Roll(state, die);
        var play = Search(state, seat, position => BackgammonBot.Score(position, seat), stop);
        return Save(EndTurn(play.Best, seat));
    }

    private static BackgammonState Load(string checkpoint)
    {
        var state = CheckpointCodec.Load<BackgammonState>(Game, checkpoint, CheckpointLimit);
        return IsValid(state) ? state : throw new InvalidDataException("Invalid Backgammon state.");
    }

    private static string Save(BackgammonState state) => CheckpointCodec.Save(Game, state, CheckpointLimit);

    private static bool IsValid(BackgammonState state)
    {
        if (state.Points is not { Length: 24 } || state.Points.Any(count => count is < -Checkers or > Checkers)
            || !ValidCounts(state.Bar) || !ValidCounts(state.Off) || !ValidRoll(state.OpeningRoll)
            || state.OpeningRoll[0] == state.OpeningRoll[1] || state.Dice is null || state.Remaining is null)
            return false;
        for (var seat = 0; seat < 2; seat++)
            if (state.Points.Sum(count => Math.Max(0, count * Sign(seat))) + state.Bar[seat] + state.Off[seat] != Checkers)
                return false;

        if (state.LastTurn is { } last
            && (last.Seat is < 0 or > 1 || !ValidRoll(last.Dice) || !ValidRemaining(last.Dice, last.Unused)))
            return false;
        if (state.Winner is int winner)
            return winner is 0 or 1 && state.Off[winner] == Checkers && state.Off[1 - winner] < Checkers
                && state.Turn is null && state.Dice.Length == 0 && state.Remaining.Length == 0
                && state.LastTurn?.Seat == winner;
        if (state.Turn is not (0 or 1) || state.Off.Contains(Checkers))
            return false;
        if (state.LastTurn is null)
        {
            if (state.Turn != OpeningSeat(state.OpeningRoll) || !state.Dice.SequenceEqual(state.OpeningRoll)) return false;
        }
        else if (state.LastTurn.Seat == state.Turn) return false;
        return state.Dice.Length == 0 ? state.Remaining.Length == 0 :
            ValidRoll(state.Dice) && state.Remaining.Length > 0 && ValidRemaining(state.Dice, state.Remaining);
    }

    private static bool ValidCounts(int[]? counts) => counts is { Length: 2 } && counts.All(count => count is >= 0 and <= Checkers);
    private static bool ValidRoll(int[]? dice) => dice is { Length: 2 } && dice.All(ValidDie);
    private static bool ValidDie(int die) => die is >= 1 and <= 6;
    private static bool ValidRemaining(int[] dice, int[]? remaining) => remaining is { Length: <= 4 } && remaining.All(face =>
        face == dice[0] || face == dice[1]) && (dice[0] == dice[1] || remaining.Distinct().Count() == remaining.Length);
    private static int OpeningSeat(int[] dice) => dice[0] > dice[1] ? 0 : 1;
    private static int Sign(int seat) => seat == 0 ? 1 : -1;
    private static int PointAt(int seat, int distance) => seat == 0 ? distance : Off - distance;

    private static int[] ThrowDice(Func<int> die)
    {
        var first = die();
        var second = die();
        return ValidDie(first) && ValidDie(second) ? [first, second]
            : throw new InvalidDataException("The Backgammon dice source returned an invalid face.");
    }

    private static BackgammonState Roll(BackgammonState state, Func<int> die)
    {
        var dice = ThrowDice(die);
        return state with { Dice = dice, Remaining = dice[0] == dice[1] ? [dice[0], dice[0], dice[0], dice[0]] : dice };
    }

    private static BackgammonState EndTurn(BackgammonState state, int seat)
    {
        var won = state.Off[seat] == Checkers;
        return state with
        {
            Turn = won ? null : 1 - seat, Winner = won ? seat : null, Dice = [], Remaining = [],
            LastTurn = new(seat, state.Dice, state.Remaining)
        };
    }

    private static IEnumerable<BackgammonMove> Steps(BackgammonState state, int seat)
    {
        if (state.Remaining.Length == 0) yield break;
        var sign = Sign(seat);
        if (state.Bar[seat] > 0)
        {
            foreach (var die in state.Remaining.Distinct())
            {
                var target = PointAt(seat, Off - die);
                if (state.Points[target - 1] * sign >= -1) yield return new(Bar, target, die);
            }
            yield break;
        }

        var farthest = 24;
        while (farthest > 0 && state.Points[PointAt(seat, farthest) - 1] * sign <= 0) farthest--;
        foreach (var die in state.Remaining.Distinct())
            for (var distance = farthest; distance > 0; distance--)
            {
                var origin = PointAt(seat, distance);
                if (state.Points[origin - 1] * sign <= 0) continue;
                if (distance > die)
                {
                    var target = PointAt(seat, distance - die);
                    if (state.Points[target - 1] * sign >= -1) yield return new(origin, target, die);
                }
                else if (farthest <= 6 && (distance == die || distance == farthest))
                    yield return new(origin, Off, die);
            }
    }

    private static BackgammonState Step(BackgammonState state, int seat, BackgammonMove move)
    {
        var sign = Sign(seat);
        var points = (int[])state.Points.Clone();
        var bar = state.Bar;
        var off = state.Off;
        if (move.From == Bar)
        {
            bar = (int[])bar.Clone();
            bar[seat]--;
        }
        else points[move.From - 1] -= sign;
        if (move.To == Off)
        {
            off = (int[])off.Clone();
            off[seat]++;
        }
        else if (points[move.To - 1] == -sign)
        {
            if (bar == state.Bar) bar = (int[])bar.Clone();
            bar[1 - seat]++;
            points[move.To - 1] = sign;
        }
        else points[move.To - 1] += sign;

        var dieIndex = Array.IndexOf(state.Remaining, move.Die);
        return state with { Points = points, Bar = bar, Off = off,
            Remaining = [.. state.Remaining.AsSpan(0, dieIndex), .. state.Remaining.AsSpan(dieIndex + 1)] };
    }

    private static (BackgammonMove[] Moves, BackgammonState Best) Search(BackgammonState start, int seat,
        Func<BackgammonState, int>? score = null, CancellationToken stop = default)
    {
        var bestLength = -1;
        var bestSingle = 0;
        var bestScore = int.MinValue;
        var best = start;
        var moves = new HashSet<BackgammonMove>();
        Visit(start, 0, default);
        return ([.. moves.OrderBy(move => move.From).ThenBy(move => move.To).ThenByDescending(move => move.Die)], best);

        // Depth never exceeds four dice. Keep only first steps of maximal paths, not the full path tree.
        void Visit(BackgammonState state, int length, BackgammonMove first)
        {
            stop.ThrowIfCancellationRequested();
            var extended = false;
            if (state.Off[seat] != Checkers)
                foreach (var step in Steps(state, seat))
                {
                    extended = true;
                    Visit(Step(state, seat, step), length + 1, length == 0 ? step : first);
                }
            if (extended) return;
            var single = length == 1 ? first.Die : 0;
            if (length < bestLength || (length == bestLength && single < bestSingle)) return;
            if (length > bestLength || single > bestSingle)
            {
                moves.Clear();
                bestLength = length;
                bestSingle = single;
                bestScore = int.MinValue;
            }
            if (length > 0) moves.Add(first);
            if (score is null) return;
            var value = score(state);
            if (value <= bestScore) return;
            best = state;
            bestScore = value;
        }
    }
}
