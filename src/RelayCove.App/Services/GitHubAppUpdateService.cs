using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RelayCove.App.Services;

public sealed class GitHubAppUpdateService : IAppUpdateService, IDisposable
{
    private const string Repository = "Dailin521/relaycove";
    private const string ManifestName = "update-win-x64.json";
    private const long MaximumInstallerSize = 2L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient client;
    private readonly TimeSpan checkTimeout;
    private readonly TimeSpan downloadTimeout;

    public GitHubAppUpdateService() : this(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false,
        Credentials = null
    })
    {
    }

    internal GitHubAppUpdateService(HttpMessageHandler handler, TimeSpan? checkTimeout = null,
        TimeSpan? downloadTimeout = null)
    {
        client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RichChat-Updater/1.0");
        this.checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(30);
        this.downloadTimeout = downloadTimeout ?? TimeSpan.FromMinutes(15);
    }

    public async Task<AppUpdateInfo?> CheckForUpdateAsync(int currentBuildNumber,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(checkTimeout);
        try
        {
            var uri = new Uri($"https://api.github.com/repos/{Repository}/releases?per_page=30");
            var bytes = await ReadBoundedAsync(uri, 1024 * 1024, timeout.Token).ConfigureAwait(false);
            var releases = JsonSerializer.Deserialize<Release[]>(bytes, JsonOptions)
                ?? throw new InvalidDataException();
            if (releases.Length > 30) throw new InvalidDataException();
            foreach (var release in releases.Where(release => release is not null && !release.Draft && !release.Prerelease)
                         .OrderByDescending(release => release.PublishedAt))
            {
                var update = await ReadReleaseAsync(release, timeout.Token).ConfigureAwait(false);
                if (update is not null)
                    return update.BuildNumber > currentBuildNumber ? update : null;
            }
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            throw new InvalidOperationException("检查更新失败，请稍后重试。");
        }
    }

    public async Task<string> DownloadAsync(AppUpdateInfo update, string destinationDirectory,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(downloadTimeout);
        string? partialPath = null;
        try
        {
            // Each attempt owns a fresh child directory; an existing installer is never overwritten.
            var attemptDirectory = Path.Combine(Path.GetFullPath(destinationDirectory), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(attemptDirectory);
            var finalPath = Path.Combine(attemptDirectory, update.InstallerName);
            partialPath = finalPath + ".partial";
            using var response = await GetAsync(update.DownloadUri, timeout.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is long contentLength && contentLength != update.InstallerSize)
                throw new InvalidDataException();
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            var buffer = new byte[81920];
            progress?.Report(0);
            await using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    received += count;
                    if (received > update.InstallerSize || received > MaximumInstallerSize)
                        throw new InvalidDataException();
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    progress?.Report((double)received / update.InstallerSize);
                }
                if (received != update.InstallerSize ||
                    !Convert.ToHexString(hash.GetHashAndReset()).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException();
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            timeout.Token.ThrowIfCancellationRequested();
            File.Move(partialPath, finalPath);
            partialPath = null;
            return finalPath;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            throw new InvalidOperationException("更新下载或校验失败，请重试。");
        }
        finally
        {
            if (partialPath is not null)
            {
                try { File.Delete(partialPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private async Task<AppUpdateInfo?> ReadReleaseAsync(Release release, CancellationToken cancellationToken)
    {
        if (release.TagName is null || release.PublishedAt is null || release.Assets is null ||
            release.Assets.Any(asset => asset is null)) return null;
        var tagVersion = release.TagName.StartsWith('v') ? release.TagName[1..] : release.TagName;
        if (tagVersion.Length > 64 || !Regex.IsMatch(tagVersion, @"\A[0-9]+\.[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant)) return null;
        var manifests = release.Assets.Where(asset => asset.Name == ManifestName).ToArray();
        if (manifests.Length != 1 || manifests[0].Size is <= 0 or > 32768 ||
            !TryAssetUri(release.TagName, manifests[0], out var manifestUri)) return null;
        try
        {
            var bytes = await ReadBoundedAsync(manifestUri!, 32768, cancellationToken).ConfigureAwait(false);
            if (bytes.LongLength != manifests[0].Size || !MatchesDigest(manifests[0].Digest, SHA256.HashData(bytes)))
                return null;
            var manifest = JsonSerializer.Deserialize<AppUpdateManifest>(bytes, JsonOptions);
            if (manifest is null || manifest.SchemaVersion != 1 || manifest.ProductId != "com.relaycove.client" ||
                manifest.Platform != "win-x64" || manifest.Version != tagVersion || manifest.BuildNumber <= 0 ||
                manifest.InstallerName != $"RichChat-{tagVersion}-win-x64-Setup.exe" ||
                manifest.Size is <= 0 or > MaximumInstallerSize || manifest.Sha256 is null ||
                !Regex.IsMatch(manifest.Sha256, @"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant)) return null;
            var installers = release.Assets.Where(asset => asset.Name == manifest.InstallerName).ToArray();
            if (installers.Length != 1 || installers[0].Size != manifest.Size ||
                !TryAssetUri(release.TagName, installers[0], out var installerUri) ||
                !MatchesDigest(installers[0].Digest, Convert.FromHexString(manifest.Sha256))) return null;
            var notes = release.Body ?? "";
            if (notes.Length > 8000) notes = notes[..8000];
            return new AppUpdateInfo(manifest.Version, manifest.BuildNumber, notes, release.PublishedAt.Value,
                manifest.InstallerName, manifest.Size, manifest.Sha256, installerUri!);
        }
        catch (JsonException) { return null; }
        catch (InvalidDataException) { return null; }
    }

    private static bool MatchesDigest(string? digest, byte[] hash) => string.IsNullOrEmpty(digest) ||
        digest.Equals("sha256:" + Convert.ToHexString(hash), StringComparison.OrdinalIgnoreCase);

    private static bool TryAssetUri(string tag, Asset asset, out Uri? uri)
    {
        uri = null;
        var expected = $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{asset.Name}";
        return asset.BrowserDownloadUrl == expected && Uri.TryCreate(expected, UriKind.Absolute, out uri);
    }

    private async Task<byte[]> ReadBoundedAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(uri, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > maximumBytes) throw new InvalidDataException();
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!IsAllowedUri(uri)) throw new InvalidDataException();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || redirects == 5) throw new InvalidDataException();
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new HttpRequestException();
            }
            return response;
        }
        throw new InvalidDataException();
    }

    private static bool IsAllowedUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) &&
        uri.IdnHost is "github.com" or "api.github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";

    private static bool IsExpectedFailure(Exception exception) => exception is HttpRequestException or IOException or InvalidDataException or
        UnauthorizedAccessException or JsonException or OperationCanceledException or ArgumentException;

    public void Dispose() => client.Dispose();

    private sealed record Release(
        [property: System.Text.Json.Serialization.JsonPropertyName("tag_name")] string? TagName,
        bool Draft, bool Prerelease,
        [property: System.Text.Json.Serialization.JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
        string? Body, Asset[]? Assets);

    private sealed record Asset(string? Name, long Size,
        [property: System.Text.Json.Serialization.JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl,
        string? Digest);
}
