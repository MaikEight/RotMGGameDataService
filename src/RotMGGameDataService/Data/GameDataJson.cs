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

    /// <summary>
    /// Hashes an object map as <c>id:metadataHash</c> lines joined by newlines,
    /// ordered by ordinal key.
    /// </summary>
    /// <remarks>
    /// Deliberately independent of JSON serialization, so a consumer written in
    /// another language can reproduce it byte for byte. That is what lets a
    /// client verify a manifest it assembled from a diff, which it cannot check
    /// against the manifest hash because it never sees the canonical bytes.
    /// </remarks>
    public static string HashObjectCatalog(IReadOnlyDictionary<string, GameObjectRecord> objects)
    {
        var lines = objects
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"{entry.Key}:{entry.Value.MetadataHash}");
        return HashBytes(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines)));
    }
}
