using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JadeCourt.Games.Farkle;

public enum FarkleActionKind { Roll, Keep, Bank }
public enum FarklePhase { Roll, Keep, Finished }
public readonly record struct FarkleAction(FarkleActionKind Kind, int Mask = 0);
public readonly record struct FarkleChoice(int Mask, int Points);
public sealed record FarkleTurnResult(int Seat, bool Banked, int Points, int[] Dice);

public sealed class FarkleGame
{
    public const int MinPlayers = 2;
    public const int MaxPlayers = 6;
    public const int DiceCount = FarkleScore.MaxDice;
    public const int Goal = 10_000;
    public const int EntryMinimum = 500;
    public const int MaxRolls = 24;

    private const int Version = 1;
    private const int MaxCheckpointLength = 4096;
    private const int MaxTotal = Goal - FarkleScore.MinPoints + MaxRolls * FarkleScore.MaxPoints;
    private static readonly JsonSerializerOptions SaveFormat = new()
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        MaxDepth = 8
    };

    private sealed record Checkpoint(int Version, int[] Scores, int Turn, int RollCount, int[] Dice, int KeptMask,
        int TurnPoints, int? FinalStarter, bool Finished, FarkleTurnResult[] RecentTurns);

    private readonly Checkpoint state;
    private FarkleGame(Checkpoint state) => this.state = state;

    public int? Turn => state.Finished ? null : state.Turn;
    public FarklePhase Phase => state.Finished ? FarklePhase.Finished :
        state.Dice.Length > 0 && state.KeptMask == 0 ? FarklePhase.Keep : FarklePhase.Roll;
    public IReadOnlyList<int> Scores => Array.AsReadOnly(state.Scores);
    public IReadOnlyList<int> Dice => Array.AsReadOnly(state.Dice);
    public int KeptMask => state.KeptMask;
    public int TurnPoints => state.TurnPoints;
    public int? FinalStarter => state.FinalStarter;
    public bool CanRoll => Phase == FarklePhase.Roll && state.RollCount < MaxRolls;
    public bool CanBank => Phase == FarklePhase.Roll &&
        TurnPoints >= (state.Scores[state.Turn] == 0 ? EntryMinimum : FarkleScore.MinPoints);
    public int DiceToRoll
    {
        get
        {
            if (Phase != FarklePhase.Roll) return 0;
            var remaining = state.Dice.Length - BitOperations.PopCount((uint)state.KeptMask);
            return remaining == 0 ? DiceCount : remaining;
        }
    }
    public int[] Winners()
    {
        if (!state.Finished) return [];
        var high = state.Scores.Max();
        return Enumerable.Range(0, state.Scores.Length).Where(seat => state.Scores[seat] == high).ToArray();
    }

    public IEnumerable<FarkleTurnResult> RecentTurns() =>
        state.RecentTurns.Select(result => result with { Dice = (int[])result.Dice.Clone() });

    public IEnumerable<FarkleChoice> Choices()
    {
        if (Phase != FarklePhase.Keep) yield break;
        for (var mask = 1; mask < (1 << state.Dice.Length); mask++)
        {
            var points = FarkleScore.Selection(state.Dice, mask);
            if (points > 0) yield return new(mask, points);
        }
    }

    public static FarkleGame Start(int players)
    {
        if (players is < MinPlayers or > MaxPlayers) throw new ArgumentOutOfRangeException(nameof(players));
        return new(new Checkpoint(Version, new int[players], 0, 0, [], 0, 0, null, false, []));
    }

    public FarkleGame Apply(int seat, FarkleAction action, Func<int> rollDie)
    {
        if (Turn != seat) throw new InvalidOperationException("This seat cannot act now.");
        switch (action.Kind)
        {
            case FarkleActionKind.Roll when action.Mask == 0 && CanRoll:
                var dice = new int[DiceToRoll];
                for (var i = 0; i < dice.Length; i++)
                {
                    dice[i] = rollDie();
                    if (dice[i] is < 1 or > 6) throw new InvalidDataException("Invalid die face from the roller.");
                }
                return FarkleScore.HasChoice(dice)
                    ? new(state with { Dice = dice, KeptMask = 0, RollCount = state.RollCount + 1 })
                    : EndTurn(new(seat, false, TurnPoints, dice));
            case FarkleActionKind.Keep when Phase == FarklePhase.Keep:
                var score = FarkleScore.Selection(state.Dice, action.Mask);
                if (score == 0) throw new InvalidOperationException("Every kept die must score in this roll.");
                return new(state with { KeptMask = action.Mask, TurnPoints = TurnPoints + score });
            case FarkleActionKind.Bank when action.Mask == 0 && CanBank:
                return EndTurn(new(seat, true, TurnPoints, []));
            default:
                throw new InvalidOperationException("That decision is not available now.");
        }
    }

    public FarkleAction BotAction()
    {
        if (Phase == FarklePhase.Keep)
        {
            var best = Choices().OrderByDescending(choice => choice.Points)
                .ThenByDescending(choice =>
                {
                    var remaining = state.Dice.Length - BitOperations.PopCount((uint)choice.Mask);
                    return remaining == 0 ? DiceCount : remaining;
                }).First();
            return new(FarkleActionKind.Keep, best.Mask);
        }
        if (Phase == FarklePhase.Finished) throw new InvalidOperationException("The game is over.");

        var total = state.Scores[state.Turn] + TurnPoints;
        var ahead = FinalStarter is null ? total >= Goal : total > state.Scores.Max();
        if (CanBank && (!CanRoll || ahead || TurnPoints >= 650 || DiceToRoll < 4))
            return new(FarkleActionKind.Bank);
        return new(FarkleActionKind.Roll);
    }

    private FarkleGame EndTurn(FarkleTurnResult result)
    {
        var scores = state.Scores;
        var finalStarter = FinalStarter;
        if (result.Banked)
        {
            scores = (int[])scores.Clone();
            scores[result.Seat] += result.Points;
            if (scores[result.Seat] >= Goal) finalStarter ??= result.Seat;
        }
        var next = (result.Seat + 1) % scores.Length;
        FarkleTurnResult[] recent = [result, .. state.RecentTurns.Take(scores.Length - 1)];
        return new(state with
        {
            Scores = scores, Turn = next, RollCount = 0, Dice = [], KeptMask = 0, TurnPoints = 0,
            FinalStarter = finalStarter, Finished = next == finalStarter, RecentTurns = recent
        });
    }

    public string Save() => JsonSerializer.Serialize(state, SaveFormat);

    public static FarkleGame Restore(string checkpoint)
    {
        if (checkpoint is not { Length: > 0 and <= MaxCheckpointLength }) throw InvalidCheckpoint();
        try
        {
            var restored = JsonSerializer.Deserialize<Checkpoint>(checkpoint, SaveFormat);
            return restored is not null && IsValid(restored) ? new(restored) : throw InvalidCheckpoint();
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid Farkle checkpoint.", ex); }
    }

    private static InvalidDataException InvalidCheckpoint() => new("Invalid Farkle checkpoint.");

    private static bool IsValid(Checkpoint saved)
    {
        if (saved.Version != Version || saved.Scores is not { Length: >= MinPlayers and <= MaxPlayers } scores
            || scores.Any(score => score < 0 || score > MaxTotal || score % FarkleScore.MinPoints != 0
                || (score > 0 && score < EntryMinimum))
            || saved.Turn < 0 || saved.Turn >= scores.Length
            || saved.Dice is not { Length: <= DiceCount } dice || dice.Any(die => die is < 1 or > 6)
            || saved.RollCount is < 0 or > MaxRolls
            || saved.RecentTurns is null || saved.RecentTurns.Length > scores.Length) return false;

        if (saved.FinalStarter is { } starter)
        {
            if (starter < 0 || starter >= scores.Length || scores[starter] < Goal
                || saved.Finished != (saved.Turn == starter)
                || (saved.Finished && (dice.Length != 0 || saved.RecentTurns.Length != scores.Length))) return false;
        }
        else if (saved.Finished || scores.Any(score => score >= Goal)) return false;

        if (saved.RollCount == 0)
        {
            if (dice.Length != 0 || saved.KeptMask != 0 || saved.TurnPoints != 0) return false;
        }
        else
        {
            if ((saved.RollCount == 1 && dice.Length != DiceCount) || !FarkleScore.HasChoice(dice)) return false;
            var kept = FarkleScore.Selection(dice, saved.KeptMask);
            if (saved.KeptMask != 0 && kept == 0) return false;
            var prior = (long)saved.TurnPoints - kept;
            if (!InPointRange(prior, saved.RollCount - 1, (saved.RollCount - 1) * FarkleScore.MinPoints)) return false;
        }

        for (var i = 0; i < saved.RecentTurns.Length; i++)
        {
            var result = saved.RecentTurns[i];
            var seat = (saved.Turn + scores.Length - 1 - i) % scores.Length;
            if (result is null || result.Seat != seat || result.Dice is null) return false;
            if (result.Banked)
            {
                if (result.Dice.Length != 0 || !InPointRange(result.Points, MaxRolls, FarkleScore.MinPoints)
                    || result.Points > scores[seat]) return false;
            }
            else if (result.Dice.Length is < 1 or > DiceCount || result.Dice.Any(die => die is < 1 or > 6)
                || FarkleScore.HasChoice(result.Dice) || !InPointRange(result.Points, MaxRolls - 1)) return false;
        }
        return true;
    }

    private static bool InPointRange(long points, int rolls, int minimum = 0) =>
        points >= minimum && points <= rolls * FarkleScore.MaxPoints && points % FarkleScore.MinPoints == 0;
}
