using System.Text.Json;
using System.Text.Json.Serialization;

namespace JadeCourt.Solo;

internal static class SoloCheckpoint
{
    private const int MaxLength = 2048;
    private static readonly JsonSerializerOptions Options = new()
    {
        AllowDuplicateProperties = false,
        MaxDepth = 4,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Write<T>(string game, T state) =>
        $"{game}-v1:{JsonSerializer.Serialize(state, Options)}";

    public static T Read<T>(string game, string checkpoint) where T : class
    {
        if (string.IsNullOrEmpty(checkpoint) || checkpoint.Length > MaxLength)
            throw Invalid(game);

        var prefix = $"{game}-v1:";
        if (!checkpoint.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported {game} checkpoint version.");

        try
        {
            return JsonSerializer.Deserialize<T>(checkpoint.AsSpan(prefix.Length), Options)
                ?? throw Invalid(game);
        }
        catch (JsonException)
        {
            throw Invalid(game);
        }
    }

    public static InvalidDataException Invalid(string game) => new($"Invalid {game} checkpoint.");
}
