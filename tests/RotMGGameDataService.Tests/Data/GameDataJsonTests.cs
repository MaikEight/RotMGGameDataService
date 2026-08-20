using RotMGGameDataService.Data;
using Xunit;

namespace RotMGGameDataService.Tests.Data;

public sealed class GameDataJsonTests
{
    [Fact]
    public void HashJson_ValueMatchesItsSerializedBytes()
    {
        var value = new { Name = "deterministic", Count = 42 };
        var bytes = GameDataJson.Serialize(value);

        Assert.Equal(GameDataJson.HashBytes(bytes), GameDataJson.HashJson(value));
    }

    [Fact]
    public void HashBytes_HashesRawBytesWithoutJsonSerialization()
    {
        byte[] bytes = [0, 1, 2, 3, 254, 255];

        Assert.Equal(
            "7ea646958715ed687aa9ac2f5d785feb1a93411f4f25fdd6c7fcc6ab07fdf0e3",
            GameDataJson.HashBytes(bytes));
    }
}
