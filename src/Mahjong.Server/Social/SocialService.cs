using System.Security.Cryptography;
using Mahjong.Server.Board;
using Mahjong.Server.Seated;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Server.Social;

internal sealed record SocialPlayer(string Id, string DisplayName);
internal sealed record SocialInvite(string Id, string Game, string DisplayName, DateTimeOffset ExpiresAtUtc);
internal sealed record SocialView(bool Available, SocialPlayer[] Players, SocialInvite[] Incoming, SocialInvite[] Outgoing);
internal sealed record SocialJoined(string Game, string TableId);

internal sealed class SocialService(IDbContextFactory<MahjongDb> factory, SocialPresence presence, Rooms rooms,
    BoardTables board, SeatedTables seated, TimeProvider time)
{
    private const int MaxRoster = 32;
    private const int MaxPerGuest = 8;
    private const int MaxPerPair = 3;
    private const int MaxPending = 512;
    private const int MaxLedger = 2048;

    // Serialize preference publication with heartbeats for the same guest. No room work runs in these lanes.
    private readonly WorkLane[] _pulses = Enumerable.Range(0, 32).Select(_ => new WorkLane(32)).ToArray();

    public async Task<SocialView> PulseAsync(Guid guest, SocialPulse pulse, CancellationToken stop)
    {
        await using var db = await factory.CreateDbContextAsync(stop);
        var lane = _pulses[(int)((uint)guest.GetHashCode() % _pulses.Length)];
        var available = await lane.Run(async () =>
        {
            stop.ThrowIfCancellationRequested();
            var optedIn = pulse.Available is { } wanted ? await SetAvailabilityAsync(db, guest, wanted, stop)
                : await db.Guests.AnyAsync(g => g.Id == guest && g.SocialAvailable, stop);
            presence.Apply(guest, pulse, optedIn);
            return optedIn;
        }, bounded: true);
        if (!available || !pulse.Active) return new(available, [], [], []);

        var candidates = presence.Players(guest);
        var ids = candidates.Keys.ToArray();
        var players = await db.Guests.Where(g => g.SocialAvailable && ids.Contains(g.Id)
                && !db.SocialBlocks.Any(b => (b.BlockerId == guest && b.BlockedId == g.Id)
                    || (b.BlockerId == g.Id && b.BlockedId == guest)))
            .OrderBy(g => g.Id).Take(MaxRoster).Select(g => new { g.Id, g.DisplayName }).ToArrayAsync(stop);

        var pending = await (from invite in SocialLedger.Pending(db, time.GetUtcNow().UtcTicks)
            where invite.SenderId == guest || invite.RecipientId == guest
            join other in db.Guests on (invite.SenderId == guest ? invite.RecipientId : invite.SenderId) equals other.Id
            orderby invite.ExpiresAtTicks, invite.Id
            select new { invite.Id, invite.Game, other.DisplayName, invite.ExpiresAtTicks, Incoming = invite.RecipientId == guest })
            .ToArrayAsync(stop);
        var inbox = pending.ToLookup(i => i.Incoming, i => new SocialInvite(i.Id, i.Game,
            i.DisplayName, new DateTimeOffset(i.ExpiresAtTicks, TimeSpan.Zero)));
        return new(true, players.Select(g => new SocialPlayer(candidates[g.Id], g.DisplayName)).ToArray(),
            inbox[true].ToArray(), inbox[false].ToArray());
    }

    private static async Task<bool> SetAvailabilityAsync(MahjongDb db, Guid guest, bool wanted, CancellationToken stop)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(stop);
        var profile = await db.Guests.FindAsync([guest], stop) ?? throw new PortalFault(401, "not_authenticated");
        profile.SocialAvailable = wanted;
        if (!wanted)
            await db.SocialInvites.Where(i => !i.Accepted && (i.SenderId == guest || i.RecipientId == guest))
                .ExecuteDeleteAsync(stop);
        await db.SaveChangesAsync(stop);
        await transaction.CommitAsync(stop);
        return wanted;
    }

    public async Task<string> SendAsync(Guid sender, string playerId, SocialTarget target, CancellationToken stop)
    {
        if (!PortalApi.IsKey(playerId)) throw SocialLedger.Missing();
        await using var db = await factory.CreateDbContextAsync(stop);
        await using var transaction = await db.Database.BeginTransactionAsync(stop);
        if (presence.FindPlayer(sender, playerId) is not { } recipient
            || !await SocialLedger.AllowedAsync(db, sender, recipient, stop))
            throw SocialLedger.Missing();
        var now = time.GetUtcNow();
        var version = await WaitingVersionAsync(db, target, sender, now, stop);
        if (version is null) throw SocialLedger.Missing();

        var pending = SocialLedger.Pending(db, now.UtcTicks);
        var sent = pending.Where(i => i.SenderId == sender);
        var same = await sent.Where(i => i.RecipientId == recipient && i.Game == target.Game
                && i.TableId == target.TableId && i.WaitingVersion == version)
            .Select(i => i.Id).FirstOrDefaultAsync(stop);
        if (same is not null) return same;
        if (await sent.CountAsync(stop) >= MaxPerGuest
            || await pending.CountAsync(i => i.RecipientId == recipient, stop) >= MaxPerGuest
            || await SocialLedger.Between(pending, sender, recipient).CountAsync(stop) >= MaxPerPair
            || await pending.CountAsync(stop) >= MaxPending)
            throw new BusyException();
        if (await db.SocialInvites.CountAsync(stop) >= MaxLedger)
        {
            await db.SocialInvites.Where(i => i.ExpiresAtTicks <= now.UtcTicks).ExecuteDeleteAsync(stop);
            if (await db.SocialInvites.CountAsync(stop) >= MaxLedger) throw new BusyException();
        }

        var invite = new SocialInviteRow
        {
            Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            SenderId = sender, RecipientId = recipient, Game = target.Game, TableId = target.TableId,
            WaitingVersion = version.Value, ExpiresAtTicks = (now + SocialLedger.PendingLifetime).UtcTicks
        };
        db.SocialInvites.Add(invite);
        await db.SaveChangesAsync(stop);
        await transaction.CommitAsync(stop);
        return invite.Id;
    }

    private static async Task<long?> WaitingVersionAsync(MahjongDb db, SocialTarget target, Guid sender,
        DateTimeOffset now, CancellationToken stop)
    {
        if (target.Mahjong) return await Rooms.SocialWaitingVersionAsync(db, target.TableId, sender, now, stop);
        var open = target.Board ? await BoardTables.CanInviteAsync(db, target, sender, now, stop)
            : target.Seated is { } rules && await SeatedTables.CanInviteAsync(db, rules, target.TableId, sender, now, stop);
        return open ? 0L : null;
    }

    public async Task<SocialJoined> AcceptAsync(GuestRow guest, string id, CancellationToken stop)
    {
        if (!PortalApi.IsKey(id)) throw SocialLedger.Missing();
        SocialInviteRow hint;
        var now = time.GetUtcNow().UtcTicks;
        await using (var db = await factory.CreateDbContextAsync(stop))
            hint = await db.SocialInvites.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id
                && i.RecipientId == guest.Id && i.ExpiresAtTicks > now, stop) ?? throw SocialLedger.Missing();

        var admission = new SocialAcceptance(hint, presence, time);
        var target = admission.Target;
        if (target.Mahjong) await rooms.JoinSocialAsync(admission, stop);
        else if (target.Board) await board.JoinSocialAsync(guest, admission, stop);
        else if (target.Seated is { } rules) await seated.JoinSocialAsync(rules, guest, admission, stop);
        else throw SocialLedger.Missing();
        return new(target.Game, target.TableId);
    }

    public async Task ResolveAsync(Guid actor, string id, string action, CancellationToken stop)
    {
        if (!PortalApi.IsKey(id)) throw SocialLedger.Missing();
        await using var db = await factory.CreateDbContextAsync(stop);
        await using var transaction = await db.Database.BeginTransactionAsync(stop);
        var pending = SocialLedger.Pending(db, time.GetUtcNow().UtcTicks);
        var invite = await pending.SingleOrDefaultAsync(i => i.Id == id
            && (action == "revoke" ? i.SenderId == actor : i.RecipientId == actor), stop);
        if (invite is null || !await SocialLedger.AllowedAsync(db, invite.SenderId, invite.RecipientId, stop))
            throw SocialLedger.Missing();
        if (action == "block")
        {
            db.SocialBlocks.Add(new SocialBlockRow { BlockerId = actor, BlockedId = invite.SenderId });
            await SocialLedger.Between(db.SocialInvites.Where(i => !i.Accepted), actor, invite.SenderId)
                .ExecuteDeleteAsync(stop);
        }
        else
        {
            db.SocialInvites.Remove(invite);
        }
        await db.SaveChangesAsync(stop);
        await transaction.CommitAsync(stop);
    }
}
