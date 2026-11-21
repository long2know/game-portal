using JadeCourt.Chess;
using JadeCourt.Games;

namespace Mahjong.Server.Board;

internal sealed class BoardRegistry(string? stockfishPath, ILogger<BoardRegistry> log)
{
    private readonly Dictionary<string, IBoardGame> _games = BoardGames.All
        .Prepend(new ChessGame(stockfishPath, reason => log.LogWarning("Chess bot fallback: {Reason}", reason)))
        .ToDictionary(game => game.Definition.Id, StringComparer.Ordinal);

    public IEnumerable<GameDefinition> Definitions => _games.Values.Select(game => game.Definition);

    public IBoardGame? Find(string game) => _games.GetValueOrDefault(game);
}
