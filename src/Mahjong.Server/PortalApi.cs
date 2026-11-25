using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Server;

internal sealed class PortalFault(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

internal static class PortalApi
{
    public const string Read = "board-read";
    public const string Write = "board-write";
    private static readonly object CookieGuestKey = new();

    public sealed record RevisionRequest(long Revision);

    public static Task<IResult> ForOptionalGuestReadAsync(HttpContext context, IResult anonymous,
        Func<GuestRow, CancellationToken, Task<IResult>> action) =>
        CookieGuest(context).HasValue ? ForGuestAsync(context, false, action) : Task.FromResult(anonymous);

    public static async Task<IResult> ForGuestAsync(HttpContext context, bool write,
        Func<GuestRow, CancellationToken, Task<IResult>> action)
    {
        if (CookieGuest(context) is not { } id) return HttpApi.Error(401, "not_authenticated");
        if (write && !context.RequestServices.GetRequiredService<ServerOptions>().IsSameOrigin(context))
            return HttpApi.Error(403, "forbidden");
        try
        {
            var stop = context.RequestAborted;
            var factory = context.RequestServices.GetRequiredService<IDbContextFactory<MahjongDb>>();
            GuestRow? guest;
            await using (var db = await factory.CreateDbContextAsync(stop))
                guest = await db.Guests.AsNoTracking().SingleOrDefaultAsync(g => g.Id == id, stop);
            return guest is not null
                ? await action(guest, stop) : HttpApi.Error(401, "not_authenticated");
        }
        catch (PortalFault fault)
        {
            return HttpApi.Error(fault.Status, fault.Code);
        }
        catch (InvalidDataException)
        {
            var log = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(PortalApi));
            log.LogError("A portal game engine could not complete its operation.");
            return HttpApi.Error(500, "engine_unavailable");
        }
        catch (Exception ex) when (Rooms.Unavailable(ex))
        {
            return HttpApi.Error(429, "busy");
        }
    }

    public static RateLimitPartition<string> PerGuest(HttpContext context, int guestLimit, int addressLimit)
    {
        var guest = CookieGuest(context);
        var key = guest is { } id ? $"guest:{id:N}" : $"address:{context.Connection.RemoteIpAddress}";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = guest.HasValue ? guestLimit : addressLimit,
            Window = TimeSpan.FromMinutes(1), AutoReplenishment = true, QueueLimit = 0
        });
    }

    private static Guid? CookieGuest(HttpContext context)
    {
        if (context.Items.TryGetValue(CookieGuestKey, out var stored)) return (Guid?)stored;
        var guest = context.RequestServices.GetRequiredService<ProtectedIdentity>().ReadGuest(context);
        context.Items[CookieGuestKey] = guest;
        return guest;
    }

    public static string? ReadInvite(JsonElement body)
    {
        var invite = body.Get("invite").AsString();
        return Wire.Fields(body, "invite") && invite is { Length: 32 } && invite.All(char.IsAsciiHexDigit)
            ? invite.ToLowerInvariant() : null;
    }

    public static RevisionRequest? ReadRevision(JsonElement body) =>
        Wire.Fields(body, "revision") && Wire.Revision(body.Get("revision"), out var revision) ? new(revision) : null;

    public static bool IsKey(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigitLower);
    public static IResult Missing() => HttpApi.Error(404, "room_not_found");
    public static IResult Invalid() => HttpApi.Error(400, "invalid_request");
    public static IResult Json(object value) => Results.Json(value, Wire.Json);
}
