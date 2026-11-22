using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JadeCourt.Poker.Persistence;

internal sealed record HoldemReplayAction(int Seat, HoldemActionKind Kind, int To);

internal sealed record HoldemReplay(long HandNumber, long StartRevision, long Revision,
    int SmallBlind, int BigBlind, int ButtonSeat, int[] Stacks, byte[] Deck, HoldemReplayAction[] Actions);

/// <summary>Trusted host storage only. These streams contain the full private deck, not a client view.</summary>
public static class HoldemServerPersistence
{
    private const int FormatVersion = 1;
    private sealed record Frame(int Version, byte[] Payload, byte[] Digest);

    private static readonly JsonSerializerOptions CheckpointJson = new()
    {
        MaxDepth = 8,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    public static void WriteCheckpoint(HoldemTable table, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(destination);
        var payload = JsonSerializer.SerializeToUtf8Bytes(table.CaptureReplay(), CheckpointJson);
        JsonSerializer.Serialize(destination, new Frame(FormatVersion, payload, SHA256.HashData(payload)),
            CheckpointJson);
    }

    public static HoldemTable RestoreCheckpoint(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            var frame = JsonSerializer.Deserialize<Frame>(source, CheckpointJson)
                ?? throw new InvalidDataException("Missing Hold'em checkpoint.");
            if (frame.Version != FormatVersion)
                throw new InvalidDataException("Unsupported Hold'em checkpoint version.");
            if (!CryptographicOperations.FixedTimeEquals(frame.Digest, SHA256.HashData(frame.Payload)))
                throw new InvalidDataException("Hold'em checkpoint checksum mismatch.");
            var replay = JsonSerializer.Deserialize<HoldemReplay>(frame.Payload, CheckpointJson)
                ?? throw new InvalidDataException("Missing Hold'em replay.");
            return HoldemTable.Replay(replay);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or HoldemRejectedException)
        {
            throw new InvalidDataException("Invalid Hold'em checkpoint.", e);
        }
    }
}
