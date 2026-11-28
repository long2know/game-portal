using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Mahjong.Rules;

// Server-private checkpoint. None of these types is a recipient projection.
public sealed record GameState(int CheckpointVersion, int ShuffleVersion, HandStart Start,
    ImmutableArray<WallTile> Wall, ImmutableArray<SeatState> Seats,
    DecisionWindow? Window, int WindowNumber, DateTimeOffset AdvancedAtGameTime,
    int DiscardCount, int? LastTileSeat, PhysicalTile? ClaimedWinTile,
    ImmutableArray<PhysicalTile> Birds, Settlement? Settlement)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<PhysicalTile>? ClaimedKongWinTiles { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PhysicalTile? UnchosenKongTile { get; init; }
}

public readonly record struct WallTile(PhysicalTile Tile, int Slot);

public readonly record struct PhysicalTile(int Number)
{
    internal int FaceIndex => Number / 4;
}

public sealed record SeatState(ImmutableArray<PhysicalTile> Hand, PhysicalTile? Drawn,
    ImmutableArray<Meld> Melds, ImmutableArray<PhysicalTile> Discards,
    ImmutableArray<PhysicalTile> Revealed, ImmutableArray<OpeningAward> OpeningAwards,
    ImmutableArray<int> PassedWinFaces, bool FrozenAfterGang);

public sealed record Meld(string Kind, string Method, int? FromSeat, ImmutableArray<PhysicalTile> Tiles);

public sealed record OpeningAward(string Code, ImmutableArray<PhysicalTile> Evidence);

public sealed record DecisionWindow(string Id, DecisionKind Kind, int Seat, DateTimeOffset ClosesAt,
    PhysicalTile? Trigger, int LastTileOffers, DrawKind DrawKind, string? PendingKongKind,
    ImmutableArray<SeatDecision> Decisions)
{
    // Owns the replacements in a turn; references the river in a KongDiscard window.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<PhysicalTile>? KongTiles { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? KongStackCount { get; init; }
}

public enum DecisionKind
{
    Opening = 1,
    Turn = 2,
    Discard = 3,
    RobKong = 4,
    LastTileOffer = 5,
    KongDiscard = 6
}

public enum DrawKind
{
    None = 0,
    Initial = 1,
    Ordinary = 2,
    Supplement = 3,
    RolledSupplement = 4,
    LastTile = 5
}

public sealed record SeatDecision(int Seat, ImmutableArray<DecisionOption> Options, string? Response);

public sealed record DecisionOption(string Id, string Kind, ImmutableArray<PhysicalTile> OwnTiles,
    ImmutableArray<PhysicalTile> Preview, string? Method, ImmutableArray<OpeningAward> OpeningAwards);
