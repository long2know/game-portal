namespace Mahjong.Server;

internal sealed class ClientShells(string baseHref, bool portalEnabled)
{
    private static readonly string[] ShellMethods = [HttpMethods.Get, HttpMethods.Head];
    private readonly string? _mahjong = Read("index.html", baseHref, portalEnabled);
    private readonly string? _portal = portalEnabled ? Read("portal.html", baseHref) : null;

    public void Map(WebApplication app)
    {
        if (portalEnabled)
        {
            app.MapFallback(PortalRoute).WithMetadata(new HttpMethodMetadata(ShellMethods));
            return;
        }

        app.MapMethods("/index.html", ShellMethods, ClientShell);
        app.MapFallback(ClientShell).WithMetadata(new HttpMethodMetadata(ShellMethods));
    }

    private IResult ClientShell(HttpContext context) => Send(context, _mahjong);

    private IResult PortalRoute(HttpContext context)
    {
        var path = context.Request.Path.Value;
        return path switch
        {
            "/" or "" or null or "/chess/" or "/go/" or "/reversi/" or "/checkers/" or "/chinese-checkers/"
                or "/holdem/" or "/solitaire/" or "/sudoku/" or "/word-tiles/" or "/farkle/" or "/backgammon/" or "/lantern-run/"
                or "/xiangqi/" or "/gomoku/" or "/mahjong-solitaire/" or "/big-two/" or "/daily-court/"
                or "/koi-koi/" or "/nonograms/" or "/jade-harbors/" =>
                Send(context, _portal),
            "/mahjong" or "/chess" or "/go" or "/reversi" or "/checkers" or "/chinese-checkers"
                or "/holdem" or "/solitaire" or "/sudoku" or "/word-tiles" or "/farkle" or "/backgammon" or "/lantern-run"
                or "/xiangqi" or "/gomoku" or "/mahjong-solitaire" or "/big-two" or "/daily-court"
                or "/koi-koi" or "/nonograms" or "/jade-harbors" =>
                // A Location with no fragment lets the browser retain the current fragment.
                Results.Redirect(baseHref + path[1..] + "/" + context.Request.QueryString.ToUriComponent(),
                    permanent: true, preserveMethod: true),
            _ when path.StartsWith("/mahjong/", StringComparison.Ordinal) => ClientShell(context),
            _ => Results.NotFound()
        };
    }

    private static IResult Send(HttpContext context, string? shell)
    {
        if (shell is null) return Results.NotFound();
        context.Response.Headers.CacheControl = "no-cache";
        return Results.Content(shell, "text/html; charset=utf-8");
    }

    private static string? Read(string fileName, string baseHref, bool portalMahjong = false)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path)) return null;
        var html = File.ReadAllText(path);
        const string marker = "data-base=\"/\"";
        if (!html.Contains(marker, StringComparison.Ordinal))
            throw new InvalidOperationException($"The built client {fileName} is missing its data-base marker.");
        // Vite's entry URLs must resolve from the mount even on a nested SPA URL.
        var mount = $"data-base=\"{baseHref}\"" + (portalMahjong ? " data-portal-mahjong" : "");
        return html.Replace(marker, mount, StringComparison.Ordinal)
            .Replace("\"./assets/", $"\"{baseHref}assets/", StringComparison.Ordinal);
    }
}
