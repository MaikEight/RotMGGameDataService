using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RotMGGameDataService.Data;

public static class GameDataJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static byte[] Serialize<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T Deserialize<T>(byte[] bytes) =>
        JsonSerializer.Deserialize<T>(bytes, Options)
        ?? throw new InvalidDataException($"Stored {typeof(T).Name} JSON was empty.");

    public static string HashJson<T>(T value)
    {
        var bytes = Serialize(value);
        return HashBytes(bytes);
    }

    public static string HashBytes(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
