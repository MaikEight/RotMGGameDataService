using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RotMGGameDataService.Configuration;

namespace RotMGGameDataService.Data;

public sealed class BuildIdentity(IOptions<ServiceOptions> options)
{
    private readonly ServiceOptions _options = options.Value;

    public int SchemaVersion => _options.SchemaVersion;

    public string Calculate(string sourceChecksum)
    {
        var identity = string.Join('\n',
            _options.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            sourceChecksum,
            _options.ExtractorVersion,
            _options.RendererVersion);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
    }
}
