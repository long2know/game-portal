using System.Security.Cryptography;

namespace Mahjong.Server.Social;

internal sealed class SocialPresence(TimeProvider time)
{
    private const int MaxGuests = 512;
    private const int MaxTabs = 8;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(45);
    private readonly byte[] _handleKey = RandomNumberGenerator.GetBytes(32);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Guest> _guests = [];

    private sealed record Tab(bool Active, bool Busy, long ExpiresAt);

    private sealed class Guest
    {
        public Dictionary<string, Tab> Tabs { get; } = new(StringComparer.Ordinal);
        public Guid Epoch;
        public long HandleExpiry;
        public long BusyUntil;
        public Handoff? Claim;

        public bool Visible => Tabs.Values.Any(tab => tab.Active);
        public bool Busy(long now) => Claim is not null || BusyUntil > now || Tabs.Values.Any(tab => tab.Busy);
    }

    public void Apply(Guid id, SocialPulse pulse, bool available)
    {
        lock (_gate)
        {
            if (!available)
            {
                _guests.Remove(id);
                return;
            }
            var utc = time.GetUtcNow();
            PruneExpired(utc.UtcTicks);
            var guest = _guests.GetValueOrDefault(id);
            if (pulse.Active)
            {
                if (guest is null)
                {
                    if (_guests.Count >= MaxGuests) throw new BusyException();
                    _guests.Add(id, guest = new Guest());
                }
                if (guest.Tabs.Count >= MaxTabs && !guest.Tabs.ContainsKey(pulse.TabId))
                    throw new BusyException();
                guest.Tabs[pulse.TabId] = new(true, pulse.Busy, (utc + Lifetime).UtcTicks);
            }
            else if (guest?.Tabs.GetValueOrDefault(pulse.TabId) is { } tab)
            {
                if (tab.Busy || pulse.Busy)
                    guest.Tabs[pulse.TabId] = tab with { Active = false, Busy = true };
                else
                    guest.Tabs.Remove(pulse.TabId);
            }
            if (guest is not null) DiscardUndiscoverable(id, guest, utc.UtcTicks);
        }
    }

    public Dictionary<Guid, string> Players(Guid viewer)
    {
        lock (_gate)
        {
            var utc = time.GetUtcNow();
            PruneExpired(utc.UtcTicks);
            Dictionary<Guid, string> result = [];
            if (_guests.GetValueOrDefault(viewer)?.Visible != true) return result;
            foreach (var (id, guest) in _guests)
            {
                if (id == viewer || !guest.Visible || guest.Busy(utc.UtcTicks)) continue;
                if (guest.HandleExpiry <= utc.UtcTicks)
                {
                    guest.Epoch = Guid.NewGuid();
                    guest.HandleExpiry = (utc + Lifetime).UtcTicks;
                }
                result.Add(id, Handle(viewer, guest.Epoch));
            }
            return result;
        }
    }

    public Guid? FindPlayer(Guid viewer, string handle)
    {
        if (!PortalApi.IsKey(handle)) return null;
        lock (_gate)
        {
            var now = time.GetUtcNow().UtcTicks;
            PruneExpired(now);
            if (_guests.GetValueOrDefault(viewer)?.Visible != true) return null;
            foreach (var (id, guest) in _guests)
                if (id != viewer && guest.HandleExpiry > now && Handle(viewer, guest.Epoch) == handle)
                    return id;
            return null;
        }
    }

    public Handoff Claim(Guid recipient, DateTimeOffset utc)
    {
        lock (_gate)
        {
            PruneExpired(utc.UtcTicks);
            if (!_guests.TryGetValue(recipient, out var guest)) throw SocialLedger.Missing();
            if (guest.Busy(utc.UtcTicks)) throw SocialLedger.AlreadySeated();
            if (!guest.Visible) throw SocialLedger.Missing();
            guest.HandleExpiry = 0;
            return guest.Claim = new Handoff(this, recipient);
        }
    }

    public void Prune(DateTimeOffset utc)
    {
        lock (_gate) PruneExpired(utc.UtcTicks);
    }

    private void PruneExpired(long now)
    {
        foreach (var (id, guest) in _guests)
        {
            foreach (var (tabId, tab) in guest.Tabs)
                if (tab.ExpiresAt <= now) guest.Tabs.Remove(tabId);
            DiscardUndiscoverable(id, guest, now);
        }
    }

    private void DiscardUndiscoverable(Guid id, Guest guest, long now)
    {
        var busy = guest.Busy(now);
        if (busy || !guest.Visible) guest.HandleExpiry = 0;
        if (!busy && guest.Tabs.Count == 0) _guests.Remove(id);
    }

    private string Handle(Guid viewer, Guid epoch)
    {
        Span<byte> input = stackalloc byte[32];
        Span<byte> digest = stackalloc byte[32];
        viewer.TryWriteBytes(input);
        epoch.TryWriteBytes(input[16..]);
        HMACSHA256.HashData(_handleKey, input, digest);
        return Convert.ToHexStringLower(digest[..16]);
    }

    private void Release(Guid id, Handoff claim, bool committed)
    {
        lock (_gate)
        {
            if (!_guests.TryGetValue(id, out var guest) || guest.Claim != claim) return;
            guest.Claim = null;
            if (committed) guest.BusyUntil = (time.GetUtcNow() + SocialLedger.HandoffLifetime).UtcTicks;
        }
    }

    internal sealed class Handoff(SocialPresence presence, Guid recipient) : IDisposable
    {
        public void Commit() => presence.Release(recipient, this, committed: true);
        public void Dispose() => presence.Release(recipient, this, committed: false);
    }
}
