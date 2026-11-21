namespace JadeCourt.Games;

public static class BoardGames
{
    public static IReadOnlyList<IBoardGame> All { get; } = Array.AsReadOnly<IBoardGame>(
        [new ReversiGame(), new CheckersGame(), new ChineseCheckersGame(), new GoGame()]);

    public static IBoardGame? Find(string id) => All.FirstOrDefault(game => game.Definition.Id == id);
}
