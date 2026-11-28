using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mahjong.Rules;
using Microsoft.AspNetCore.WebUtilities;

namespace Mahjong.Server;

internal sealed record Chair(Guid? GuestId, string? DisplayName, bool Ready, bool IsBot)
{
    public bool IsOpen => GuestId is null && !IsBot;
    public static Chair Open { get; } = new(null, null, false, false);
    public static Chair Human(Guid guestId, string displayName) => new(guestId, displayName, false, false);
    public static Chair Bot(int seat) => new(null, $"Bot {seat + 1}", true, true);
}

internal sealed record ChatPost(Guid GuestId, string MessageId, int Seat, string DisplayName, string Text,
    DateTimeOffset SentAtUtc);

internal sealed class RoomCheckpoint
{
    private const int BotAdmissionReleaseLimit = 32;
    private int[]? _tableTotals;

    public int CheckpointVersion { get; init; } = 1;
    public string RulesVersion { get; init; } = ChangshaEngine.Profile;
    public required string Id { get; init; }
    public required string Visibility { get; init; }
    public string? InviteCode { get; init; }
    public int MatchHands { get; init; }
    public int HostSeat { get; set; }
    public string Status { get; set; } = "waiting";
    public long RoomRevision { get; set; } = 1;
    public long GameRevision { get; set; }
    public long? WindowAnchor { get; set; }
    public DateTimeOffset? PauseStartedUtc { get; set; }
    public DateTimeOffset? AutoResumeAtUtc { get; set; }
    public long PausedTicks { get; set; }
    public DateTimeOffset? NextBotGameTime { get; set; }
    public required Chair[] Seats { get; init; }
    public int[] Totals { get; set; } = new int[4];
    public List<ChatPost> Chat { get; init; } = [];
    public GameState? Game { get; set; }
    public int[] TableTotals
    {
        // A legacy complete room may already have an open seat's historical match score.
        get => _tableTotals ??= Totals.Select((total, seat) => Seats[seat].IsOpen ? 0 : total).ToArray();
        init => _tableTotals = value;
    }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NextDealerSeat { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid[]? ReleasedGuests { get; set; }

    public DateTimeOffset GameTime(DateTimeOffset utc)
    {
        var clock = (PauseStartedUtc ?? utc).AddTicks(-PausedTicks);
        return Game is { AdvancedAtGameTime: var last } && clock < last ? last : clock;
    }
    public DateTimeOffset ToUtc(DateTimeOffset gameTime) => gameTime.AddTicks(PausedTicks);
    public string PublicWindowId(PublicWindow window) =>
        string.Concat(WindowAnchor!.Value.ToString(CultureInfo.InvariantCulture), ".", window.WindowId);

    public void TouchRevision() => RoomRevision = Increment(RoomRevision);
    public void AdvanceGameRevision() => GameRevision = Increment(GameRevision);

    public void ReplaceChair(int seat, Chair chair)
    {
        TableTotals[seat] = 0;
        Seats[seat] = chair;
    }

    public bool BotAdmissionCapped() => ReleasedGuests is { Length: >= BotAdmissionReleaseLimit };

    public void RecordRelease(Guid guest) => ReleasedGuests = ReleasedGuests is { } guests
        ? [.. guests, guest] : [guest];

    public void ResetReadiness()
    {
        for (var seat = 0; seat < Seats.Length; seat++)
            if (Seats[seat].GuestId.HasValue)
                Seats[seat] = Seats[seat] with { Ready = false };
    }

    private static long Increment(long revision) => revision < Wire.MaxRevision
        ? revision + 1 : throw new InvalidOperationException("The protocol revision limit was reached.");

    public static RoomCheckpoint Read(RoomRow row) => Read(row.Id, row.Checkpoint);

    public static RoomCheckpoint Read(string roomId, string checkpoint)
    {
        var room = JsonSerializer.Deserialize<RoomCheckpoint>(checkpoint, Wire.Json)
            ?? throw new InvalidOperationException("Missing room checkpoint.");
        if (room.CheckpointVersion != 1 || !ChangshaEngine.SupportsProfile(room.RulesVersion)
            || room.Id != roomId || room.Seats.Length != 4 || room.Totals.Length != 4 || room.TableTotals.Length != 4
            || room.NextDealerSeat is < 0 or > 3
            || room.Game is { } game && (game.CheckpointVersion != ChangshaEngine.CheckpointVersion
                || game.ShuffleVersion != ChangshaEngine.ShuffleVersion || game.Start.Profile != room.RulesVersion)
            || room.Status is not ("waiting" or "playing" or "betweenHands" or "complete")
            || room.HostSeat is < 0 or > 3 || !room.Seats[room.HostSeat].GuestId.HasValue
            || !room.ValidReleasedGuests())
            throw new InvalidOperationException("Unsupported or inconsistent room checkpoint.");
        return room;
    }

    private bool ValidReleasedGuests()
    {
        if (ReleasedGuests is not { Length: > 0 } guests) return true;
        // When the cap is reached at most three humans remain, and the last cannot release.
        return guests.Length <= BotAdmissionReleaseLimit + 2 && Game is not null && Status != "waiting"
            && (!BotAdmissionCapped() || guests.Length + Seats.Count(chair => chair.GuestId.HasValue) <= BotAdmissionReleaseLimit + 3)
            && guests.Distinct().Count() == guests.Length && !guests.Contains(Guid.Empty)
            && !Seats.Any(chair => chair.GuestId is { } guest && guests.Contains(guest));
    }

    public void Store(RoomRow row)
    {
        row.Checkpoint = JsonSerializer.Serialize(this, Wire.Json);
        row.OccupiedSeats = Seats.Count(seat => !seat.IsOpen);
        row.Waiting = Status == "waiting";
    }
}

internal sealed class RoomGame(ServerOptions options, ILogger<RoomGame> log)
{
    public void StartHand(RoomCheckpoint room, DateTimeOffset utc)
    {
        var previous = room.Game is null ? null : ChangshaEngine.ViewPublic(room.Game);
        var number = previous is null ? 1 : previous.HandNumber + 1;
        var dealer = previous?.Settlement?.NextDealerSeat ?? room.NextDealerSeat ?? 0;
        Span<byte> seedBytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(seedBytes);
        var seed = new RoundSeed(BinaryPrimitives.ReadUInt64LittleEndian(seedBytes),
            BinaryPrimitives.ReadUInt64LittleEndian(seedBytes[8..]));
        var gameTime = room.GameTime(utc);
        var game = ChangshaEngine.StartHand(
            new HandStart(number, dealer, seed, room.RulesVersion, options.TurnLimit, options.ClaimLimit), gameTime);
        room.NextDealerSeat = null;
        room.ReleasedGuests = null;
        SetGame(room, game, gameTime, previous);
    }

    public string? Choose(RoomCheckpoint room, Intent intent, int seat, DateTimeOffset utc)
    {
        if (room.Status != "playing" || room.Game is null)
            return "stale_window";
        var table = ChangshaEngine.ViewPublic(room.Game);
        if (table.Window is not { } window || room.WindowAnchor != intent.Revision || room.PublicWindowId(window) != intent.WindowId)
            return "stale_window";
        if (room.PauseStartedUtc.HasValue) return "paused";

        var now = room.GameTime(utc);
        var move = ChangshaEngine.Apply(room.Game, new Choose(seat, window.WindowId, intent.OptionId!), now);
        if (move.Status == MoveStatus.Accepted)
        {
            SetGame(room, move.State, now, table);
            return null;
        }
        return move.Status == MoveStatus.IllegalChoice ? "illegal_option" : "stale_window";
    }

    public void SetPaused(RoomCheckpoint room, bool paused, DateTimeOffset utc)
    {
        if (room.PauseStartedUtc.HasValue == paused) return;
        if (paused)
        {
            room.PauseStartedUtc = utc;
            room.AutoResumeAtUtc = utc + options.PauseLimit;
        }
        else
        {
            room.PausedTicks += Math.Max(0, (utc - room.PauseStartedUtc!.Value).Ticks);
            room.PauseStartedUtc = null;
            room.AutoResumeAtUtc = null;
        }
        room.TouchRevision();
    }

    public static void Rematch(RoomCheckpoint room)
    {
        ResetMatch(room);
        room.ResetReadiness();
        room.TouchRevision();
        room.AdvanceGameRevision();
    }

    public void RestartMatch(RoomCheckpoint room, DateTimeOffset utc)
    {
        ResetMatch(room);
        // The waiting baseline is not committed; StartHand advances each revision once.
        StartHand(room, utc);
    }

    private static void ResetMatch(RoomCheckpoint room)
    {
        room.NextDealerSeat = room.Game?.Settlement?.NextDealerSeat ?? room.Game?.Start.DealerSeat ?? room.NextDealerSeat;
        room.Game = null;
        room.Status = "waiting";
        room.WindowAnchor = null;
        room.NextBotGameTime = null;
        room.PauseStartedUtc = null;
        room.AutoResumeAtUtc = null;
        room.PausedTicks = 0;
        room.Totals = new int[4];
        room.ReleasedGuests = null;
    }

    public long DueTicks(RoomRow row, RoomCheckpoint room)
    {
        var next = row.ExpiresAtTicks;
        if (room.Status != "playing" || room.Game is null) return next;
        if (room.AutoResumeAtUtc is { } resume) return Math.Min(next, resume.UtcTicks);
        if (ChangshaEngine.ViewPublic(room.Game).Window is { } window)
            next = Math.Min(next, room.ToUtc(window.ClosesAtGameTime).UtcTicks);
        if (room.NextBotGameTime is { } bot)
            next = Math.Min(next, room.ToUtc(bot).UtcTicks);
        return next;
    }

    public bool AdvanceDue(RoomCheckpoint room, DateTimeOffset utc)
    {
        if (room.Status != "playing" || room.Game is null) return false;
        var resumed = false;
        if (room.AutoResumeAtUtc is { } autoResume)
        {
            if (autoResume > utc) return false;
            SetPaused(room, false, autoResume);
            resumed = true;
        }

        var table = ChangshaEngine.ViewPublic(room.Game);
        if (table.Window is not { } window) return resumed;
        var now = room.GameTime(utc);
        if (now >= window.ClosesAtGameTime)
        {
            var move = ChangshaEngine.Apply(room.Game, new Expire(window.WindowId), now);
            if (move.Status != MoveStatus.Accepted)
                throw new InvalidOperationException("Rules did not accept a due decision expiry.");
            SetGame(room, move.State, now, table);
            return true;
        }

        if (room.NextBotGameTime is not { } botAt || botAt > now) return resumed;
        if (ApplyBot(room, table, window, now)) return true;
        room.NextBotGameTime = null;
        return true;
    }

    private bool ApplyBot(RoomCheckpoint room, PublicView table, PublicWindow window, DateTimeOffset gameTime)
    {
        for (var seat = 0; seat < room.Seats.Length; seat++)
        {
            if (!room.Seats[seat].IsBot) continue;
            var view = ChangshaEngine.ViewSeat(room.Game!, seat);
            if (view.LegalOptions.IsEmpty) continue;

            string? picked = null;
            try
            {
                picked = BotPolicy.Pick(table, view);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                log.LogWarning("A bot could not select an option; attempting a legal fallback.");
            }
            var option = view.LegalOptions.FirstOrDefault(x => x.OptionId == picked)
                ?? view.LegalOptions.FirstOrDefault(x => x.Kind is "pass" or "draw" or "discard")
                ?? view.LegalOptions[0];
            var move = ChangshaEngine.Apply(room.Game!, new Choose(seat, window.WindowId, option.OptionId), gameTime);
            if (move.Status != MoveStatus.Accepted)
            {
                log.LogWarning("Rules rejected a projected bot option; leaving that decision to its normal deadline.");
                return false;
            }
            SetGame(room, move.State, gameTime, table);
            return true;
        }
        return false;
    }

    public void RefreshBotSchedule(RoomCheckpoint room, DateTimeOffset utc)
    {
        if (room.Game is not { } game || !HasLegalBot(room, game))
            room.NextBotGameTime = null;
        else
            room.NextBotGameTime ??= room.GameTime(utc) + options.BotDelay;
    }

    private static bool HasLegalBot(RoomCheckpoint room, GameState game) => room.Seats
        .Select((seat, index) => seat.IsBot && !ChangshaEngine.ViewSeat(game, index).LegalOptions.IsEmpty).Any(legal => legal);

    private void SetGame(RoomCheckpoint room, GameState game, DateTimeOffset gameTime, PublicView? previous)
    {
        var table = ChangshaEngine.ViewPublic(game);
        room.Game = game;
        room.AdvanceGameRevision();
        room.WindowAnchor = table.Window is null ? null
            : previous?.Window?.WindowId == table.Window.WindowId ? room.WindowAnchor : room.GameRevision;
        room.NextBotGameTime = table.Window is not null && HasLegalBot(room, game)
            ? gameTime + options.BotDelay : null;

        var nextStatus = table.Settlement is null ? "playing"
            : table.HandNumber >= room.MatchHands ? "complete" : "betweenHands";
        if (table.Settlement is { } settlement && (previous?.Settlement is null || previous.HandNumber != table.HandNumber))
        {
            var tableTotals = room.TableTotals;
            foreach (var result in settlement.SeatResults)
            {
                room.Totals[result.Seat] = checked(room.Totals[result.Seat] + result.Delta);
                tableTotals[result.Seat] = checked(tableTotals[result.Seat] + result.Delta);
            }
        }
        if (room.Status != nextStatus)
        {
            room.Status = nextStatus;
            room.TouchRevision();
        }
    }

    public static string RandomCode(int bytes) => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(bytes));
}
