using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RotMGGameDataService.Configuration;
using RotMGGameDataService.Realm;
using Xunit;

namespace RotMGGameDataService.Tests.Realm;

public sealed class RealmBuildClientTests
{
    [Fact]
    public async Task EnsureResourceFile_ValidatesDecompressedChecksumAndReusesFile()
    {
        var sourceBytes = Encoding.UTF8.GetBytes(string.Concat(
            Enumerable.Repeat("repeatable-resource-content", 10_000)));
        var compressedBytes = Compress(sourceBytes);
        var checksum = Convert.ToHexString(MD5.HashData(sourceBytes)).ToLowerInvariant();
        var workDirectory = Path.Combine(
            Path.GetTempPath(),
            "rotmg-game-data-tests",
            Guid.NewGuid().ToString("N"));
        var handler = new RealmFixtureHandler(compressedBytes, checksum);
        var options = Options.Create(new RealmOptions
        {
            AppInitUrl = new Uri("https://realm.test/app/init"),
            AllowedCdnHosts = ["cdn.test"],
            ResourcePath = "RotMG Exalt_Data/resources.assets",
            WorkDirectory = workDirectory,
            MaxCompressedBytes = compressedBytes.Length + 1_024,
            MaxUncompressedBytes = sourceBytes.Length + 1_024,
        });
        var client = new RealmBuildClient(
            new HttpClient(handler),
            options,
            NullLogger<RealmBuildClient>.Instance);

        try
        {
            var build = await client.DiscoverAsync(CancellationToken.None);
            var firstPath = await client.EnsureResourceFileAsync(build, CancellationToken.None);
            var secondPath = await client.EnsureResourceFileAsync(build, CancellationToken.None);

            Assert.Equal(firstPath, secondPath);
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(firstPath));
            Assert.Equal(1, handler.ResourceDownloadCount);
            Assert.True(sourceBytes.Length > build.Resource.CompressedSize);
        }
        finally
        {
            if (Directory.Exists(workDirectory))
                Directory.Delete(workDirectory, recursive: true);
        }
    }

    private static byte[] Compress(byte[] source)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(source);
        return output.ToArray();
    }

    private sealed class RealmFixtureHandler(
        byte[] compressedBytes,
        string checksum) : HttpMessageHandler
    {
        public int ResourceDownloadCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = request.RequestUri?.AbsolutePath switch
            {
                "/app/init" => TextResponse("""
                    <AppSettings>
                      <BuildId>fixture-build</BuildId>
                      <BuildCDN>https://cdn.test/builds/</BuildCDN>
                      <BuildHash>aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</BuildHash>
                    </AppSettings>
                    """, "application/xml"),
                "/builds/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/fixture-build/checksum.json" =>
                    TextResponse($$"""
                        {
                          "files": [
                            {
                              "file": "RotMG Exalt_Data/resources.assets",
                              "checksum": "{{checksum}}",
                              "size": {{compressedBytes.Length}}
                            }
                          ]
                        }
                        """, "application/json"),
                "/builds/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/fixture-build/RotMG%20Exalt_Data/resources.assets.gz" =>
                    ResourceResponse(),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            return Task.FromResult(response);
        }

        private HttpResponseMessage ResourceResponse()
        {
            ResourceDownloadCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(compressedBytes),
            };
        }

        private static HttpResponseMessage TextResponse(string content, string contentType) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, contentType),
            };
    }
}
