using System.Buffers;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using RotMGGameDataService.Configuration;

namespace RotMGGameDataService.Realm;

public sealed class RealmBuildClient(
    HttpClient httpClient,
    IOptions<RealmOptions> options,
    ILogger<RealmBuildClient> logger)
{
    private const int MaximumMetadataBytes = 4 * 1024 * 1024;
    private readonly RealmOptions _options = options.Value;

    public async Task<RealmBuildInfo> DiscoverAsync(CancellationToken cancellationToken)
    {
        ValidateOptions();

        using var appInitRequest = new HttpRequestMessage(HttpMethod.Post, _options.AppInitUrl)
        {
            Content = new ByteArrayContent([]),
        };
        appInitRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/x-www-form-urlencoded");

        using var appInitResponse = await httpClient.SendAsync(
            appInitRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        appInitResponse.EnsureSuccessStatusCode();
        var appInitXml = await ReadBoundedStringAsync(
            appInitResponse,
            MaximumMetadataBytes,
            cancellationToken);
        var appInit = RealmBuildParser.ParseAppInit(appInitXml, _options.AllowedCdnHosts);

        using var checksumResponse = await httpClient.GetAsync(
            appInit.ChecksumUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        checksumResponse.EnsureSuccessStatusCode();
        var checksumJson = await ReadBoundedStringAsync(
            checksumResponse,
            MaximumMetadataBytes,
            cancellationToken);
        var resource = RealmBuildParser.ParseResourceFile(
            checksumJson,
            appInit,
            _options.ResourcePath,
            _options.MaxCompressedBytes,
            _options.AllowedCdnHosts);

        logger.LogInformation(
            "Discovered Realm build {BuildHash} with resources checksum {ResourceChecksum}",
            appInit.BuildHash,
            resource.Checksum);

        return new RealmBuildInfo(
            appInit.BuildId,
            appInit.BuildHash,
            appInit.BuildCdn,
            appInit.ChecksumUri,
            resource);
    }

    public async Task<string> EnsureResourceFileAsync(
        RealmBuildInfo build,
        CancellationToken cancellationToken)
    {
        ValidateOptions();

        var sourceDirectory = Path.GetFullPath(Path.Combine(
            _options.WorkDirectory,
            "sources",
            build.Resource.Checksum));
        Directory.CreateDirectory(sourceDirectory);
        var targetPath = Path.Combine(sourceDirectory, "resources.assets");

        if (await IsCurrentFileAsync(targetPath, build.Resource, cancellationToken))
        {
            logger.LogInformation("Reusing verified Realm resource file at {ResourcePath}", targetPath);
            return targetPath;
        }

        var partialPath = targetPath + $".partial-{Guid.NewGuid():N}";
        try
        {
            using var response = await httpClient.GetAsync(
                build.Resource.DownloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value > _options.MaxCompressedBytes)
            {
                throw new InvalidDataException(
                    $"Realm compressed resource was larger than the configured {_options.MaxCompressedBytes} byte limit.");
            }
            if (contentLength.HasValue && contentLength.Value != build.Resource.CompressedSize)
            {
                throw new InvalidDataException(
                    $"Realm compressed resource size mismatch. Expected {build.Resource.CompressedSize}, received {contentLength.Value} bytes.");
            }

            await using var compressedStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var gzipStream = new GZipStream(
                compressedStream,
                CompressionMode.Decompress,
                leaveOpen: false);
            await using var outputStream = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var checksum = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            long totalBytes = 0;
            try
            {
                while (true)
                {
                    var bytesRead = await gzipStream.ReadAsync(buffer, cancellationToken);
                    if (bytesRead == 0)
                        break;

                    totalBytes += bytesRead;
                    if (totalBytes > _options.MaxUncompressedBytes)
                        throw new InvalidDataException("Realm resource exceeded the configured uncompressed size limit.");

                    checksum.AppendData(buffer.AsSpan(0, bytesRead));
                    await outputStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await outputStream.FlushAsync(cancellationToken);
            var actualChecksum = Convert.ToHexString(checksum.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actualChecksum, build.Resource.Checksum, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Realm resource checksum mismatch. Expected {build.Resource.Checksum}, received {actualChecksum}.");
            }

            await outputStream.DisposeAsync();
            File.Move(partialPath, targetPath, overwrite: true);
            logger.LogInformation(
                "Downloaded and verified {ResourceBytes} bytes to {ResourcePath}",
                totalBytes,
                targetPath);
            return targetPath;
        }
        catch
        {
            if (File.Exists(partialPath))
                File.Delete(partialPath);
            throw;
        }
    }

    private async Task<bool> IsCurrentFileAsync(
        string path,
        RealmResourceFile expected,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0 || file.Length > _options.MaxUncompressedBytes)
            return false;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        var checksum = await MD5.HashDataAsync(stream, cancellationToken);
        return string.Equals(
            Convert.ToHexString(checksum).ToLowerInvariant(),
            expected.Checksum,
            StringComparison.Ordinal);
    }

    private void ValidateOptions()
    {
        if (!_options.AppInitUrl.IsAbsoluteUri || _options.AppInitUrl.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Realm AppInitUrl must be an absolute HTTPS URL.");
        if (_options.AllowedCdnHosts.Length == 0)
            throw new InvalidOperationException("At least one Realm CDN host must be configured.");
        if (string.IsNullOrWhiteSpace(_options.ResourcePath))
            throw new InvalidOperationException("Realm ResourcePath must be configured.");
        if (string.IsNullOrWhiteSpace(_options.WorkDirectory))
            throw new InvalidOperationException("Realm WorkDirectory must be configured.");
        if (_options.MaxCompressedBytes <= 0 || _options.MaxUncompressedBytes <= 0)
            throw new InvalidOperationException("Realm download size limits must be positive.");
    }

    private static async Task<string> ReadBoundedStringAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maximumBytes)
            throw new InvalidDataException("Realm metadata response exceeded the configured size limit.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var bytesRead = await input.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                    break;
                if (output.Length + bytesRead > maximumBytes)
                    throw new InvalidDataException("Realm metadata response exceeded the configured size limit.");
                output.Write(buffer, 0, bytesRead);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return System.Text.Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }
}
