using System.Text.Json;
using System.Text.Json.Serialization;

namespace JadeCourt.Games;

internal abstract class BoardGame<TState, TTurn>(GameDefinition definition, int checkpointLimit) : IBoardGame
    where TState : class
{
    public GameDefinition Definition => definition with
    {
        PlayerCounts = [.. definition.PlayerCounts],
        Variants = [.. definition.Variants]
    };

    public string Start(string variant, int players)
    {
        if (!definition.PlayerCounts.Contains(players))
            throw new ArgumentException($"Unsupported player count for {definition.Id}.", nameof(players));
        if (!definition.Variants.Any(option => option.Id == variant))
            throw new ArgumentException($"Unsupported variant for {definition.Id}.", nameof(variant));
        return Save(InitialState(variant, players));
    }

    public GamePosition View(string checkpoint)
    {
        var state = Load(checkpoint);
        return Project(state, [.. LegalTurns(state).Select(turn => Describe(state, turn))]);
    }

    public string Apply(string checkpoint, string moveId)
    {
        var state = Load(checkpoint);
        foreach (var turn in LegalTurns(state))
        {
            var move = Describe(state, turn);
            if (move.Id == moveId)
                return Save(Advance(state, turn, move));
        }
        throw new ArgumentException("The move is not legal in this position.", nameof(moveId));
    }

    public string SelectBotMove(string checkpoint)
    {
        var state = Load(checkpoint);
        var turns = LegalTurns(state).ToArray();
        if (turns.Length == 0)
            throw new InvalidOperationException("The game is finished.");
        return Describe(state, turns.Length == 1 ? turns[0] : Choose(state, turns)).Id;
    }

    protected abstract TState InitialState(string variant, int players);
    protected abstract bool IsValid(TState state);
    protected abstract IEnumerable<TTurn> LegalTurns(TState state);
    protected abstract GameMove Describe(TState state, TTurn turn);
    protected abstract TState Advance(TState state, TTurn turn, GameMove move);
    protected abstract TTurn Choose(TState state, TTurn[] turns);
    protected abstract GamePosition Project(TState state, GameMove[] moves);

    private string Save(TState state) => CheckpointCodec.Save(definition.Id, state, checkpointLimit);

    private TState Load(string checkpoint)
    {
        var state = CheckpointCodec.Load<TState>(definition.Id, checkpoint, checkpointLimit);
        return IsValid(state) ? state : throw new InvalidDataException($"Invalid {definition.Id} state.");
    }
}

internal static class CheckpointCodec
{
    private sealed record Envelope<T>(int Version, string Game, T State);

    private static readonly JsonSerializerOptions Json = new()
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static string Save<T>(string game, T state, int limit)
    {
        var checkpoint = JsonSerializer.Serialize(new Envelope<T>(1, game, state), Json);
        if (checkpoint.Length > limit)
            throw new InvalidDataException($"The {game} checkpoint exceeded its size limit.");
        return checkpoint;
    }

    internal static T Load<T>(string game, string checkpoint, int limit) where T : class
    {
        if (string.IsNullOrEmpty(checkpoint) || checkpoint.Length > limit)
            throw new InvalidDataException($"Invalid {game} checkpoint length.");
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope<T>>(checkpoint, Json);
            return envelope is { Version: 1, State: not null } && envelope.Game == game
                ? envelope.State
                : throw new InvalidDataException($"Invalid {game} checkpoint version, game, or state.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Malformed {game} checkpoint.", exception);
        }
    }
}
