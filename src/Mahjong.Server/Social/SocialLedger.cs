using Microsoft.EntityFrameworkCore;

namespace Mahjong.Server.Social;

internal static class SocialLedger
{
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ReceiptLifetime = TimeSpan.FromMinutes(5);

    public static IQueryable<SocialInviteRow> Pending(MahjongDb db, long now) =>
        db.SocialInvites.Where(i => !i.Accepted && i.ExpiresAtTicks > now);

    public static IQueryable<SocialInviteRow> Between(IQueryable<SocialInviteRow> invites, Guid first, Guid second) =>
        invites.Where(i => (i.SenderId == first && i.RecipientId == second)
            || (i.SenderId == second && i.RecipientId == first));

    public static async Task<bool> AllowedAsync(MahjongDb db, Guid first, Guid second, CancellationToken stop) =>
        await db.Guests.CountAsync(g => g.SocialAvailable && (g.Id == first || g.Id == second), stop) == 2
        && !await db.SocialBlocks.AnyAsync(b => (b.BlockerId == first && b.BlockedId == second)
            || (b.BlockerId == second && b.BlockedId == first), stop);

    public static PortalFault Missing() => new(404, "invite_unavailable");
    public static PortalFault AlreadySeated() => new(409, "already_seated");
}

// This capability is made only from an authenticated recipient's durable row. The hint is
// not authority: every adapter reads the row again in the transaction that inserts the seat.
internal sealed class SocialAcceptance(SocialInviteRow hint, SocialPresence presence, TimeProvider time)
{
    public SocialTarget Target { get; } = new(hint.Game, hint.TableId);
    public Guid SenderId => hint.SenderId;
    public Guid RecipientId => hint.RecipientId;
    public bool ReceiptHint => hint.Accepted;

    public async Task<SocialInviteRow> AuthorizeAsync(MahjongDb db, DateTimeOffset utc, CancellationToken stop)
    {
        var row = await db.SocialInvites.FindAsync([hint.Id], stop);
        if (row is null || row.ExpiresAtTicks <= utc.UtcTicks || row.RecipientId != RecipientId
            || row.SenderId != SenderId || row.Game != Target.Game || row.TableId != Target.TableId
            || !await SocialLedger.AllowedAsync(db, SenderId, RecipientId, stop))
            throw SocialLedger.Missing();
        return row;
    }

    public SocialPresence.Handoff Stage(SocialInviteRow row)
    {
        var utc = time.GetUtcNow();
        if (row.ExpiresAtTicks <= utc.UtcTicks) throw SocialLedger.Missing();
        var handoff = presence.Claim(RecipientId, utc);
        row.Accepted = true;
        row.ExpiresAtTicks = (utc + SocialLedger.ReceiptLifetime).UtcTicks;
        return handoff;
    }
}
