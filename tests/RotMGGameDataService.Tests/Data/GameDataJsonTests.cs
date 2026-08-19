using RotMGGameDataService.Data;
using Xunit;

namespace RotMGGameDataService.Tests.Data;

public sealed class GameDataJsonTests
{
    [Fact]
    public void Hash_ValueMatchesItsSerializedBytes()
    {
        var value = new { Name = "deterministic", Count = 42 };
        var bytes = GameDataJson.Serialize(value);

        Assert.Equal(GameDataJson.Hash(bytes.AsSpan()), GameDataJson.Hash(value));
    }
}
