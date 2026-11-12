using System.Net;
using System.Security.Cryptography;

namespace DeziroJoy.App.Services;

/// <summary>
/// Outcome of one package download. A failure carries a Chinese, user-presentable reason and
/// never a half-written file: the partial download is removed before the result is built, so
/// nothing on disk can be mistaken for a verified package.
/// </summary>
public sealed record UpdateDownloadResult
{
    public required bool Succeeded { get; init; }

    /// <summary>The verified package on disk; set only when <see cref="Succeeded"/> is true.</summary>
    public string? FilePath { get; init; }

    /// <summary>A Chinese, user-presentable failure reason; set only on failure.</summary>
    public string? ErrorMessage { get; init; }

    public static UpdateDownloadResult Ok(string filePath)
        => new() { Succeeded = true, FilePath = filePath };

    public static UpdateDownloadResult Failed(string errorMessage)
        => new() { Succeeded = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Downloads one release package and proves it against the release's own SHA256 sidecar
/// before anything is allowed to install it. The checksum is the only integrity story this
/// product has - nothing is code-signed - so a missing sidecar is a refusal, not a warning.
/// </summary>
/// <remarks>
/// The transport is a seam exactly as in <see cref="UpdateService"/>: production passes
/// nothing, tests pass a stub <see cref="HttpMessageHandler"/>, a short timeout and a
/// temporary cache directory. The package is streamed straight to disk while it is hashed,
/// so a large setup.exe is never held in memory and never read twice.
/// </remarks>
public sealed class UpdateDownloader : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;

    private readonly TimeSpan _timeout;

    private readonly Func<string> _cacheDirectory;

    private bool _disposed;

    public UpdateDownloader(
        HttpMessageHandler? handler = null,
        TimeSpan? timeout = null,
        Func<string>? cacheDirectory = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        _cacheDirectory = cacheDirectory ?? DefaultCacheDirectory;

        _http = new HttpClient(handler ?? new HttpClientHandler())
        {
            // The linked cancellation token below is the single timeout authority.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// Downloads the package, verifies its SHA256 against the sidecar and returns the path
    /// of the verified file. <paramref name="progress"/> reports 0..1 while the bytes arrive;
    /// only a cancelled <paramref name="cancellationToken"/> throws.
    /// </summary>
    public async Task<UpdateDownloadResult> DownloadAsync(
        ReleaseAsset asset,
        ReleaseAsset? checksumAsset,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(asset);

        if (checksumAsset is null)
        {
            // The release does not publish a checksum for this package. Trusting the
            // transport instead would make the download step a silent integrity hole.
            return UpdateDownloadResult.Failed(
                $"更新包校验失败：{asset.Name} 未提供 SHA256 校验值，无法验证更新包完整性。");
        }

        // The service timeout and the caller's cancellation are linked: the caller's token
        // still aborts immediately, and a timeout surfaces as "超时" rather than as a cancel.
        using var timeout = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);

        try
        {
            return await DownloadCoreAsync(asset, checksumAsset, progress, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UpdateDownloadResult.Failed("下载失败：下载超时，请检查网络连接后重试。");
        }
    }

    private async Task<UpdateDownloadResult> DownloadCoreAsync(
        ReleaseAsset asset,
        ReleaseAsset checksumAsset,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        string expected;
        try
        {
            expected = await ReadChecksumAsync(checksumAsset, cancellationToken).ConfigureAwait(false);
        }
        catch (UpdateDownloadException exception)
        {
            return UpdateDownloadResult.Failed(exception.Message);
        }

        var directory = _cacheDirectory();
        var finalPath = Path.Combine(directory, asset.Name);
        var partialPath = finalPath + ".part";

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return UpdateDownloadResult.Failed($"下载失败：无法创建下载目录（{exception.Message}）。");
        }

        string actual;
        try
        {
            actual = await DownloadAndHashAsync(asset, partialPath, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UpdateDownloadException exception)
        {
            DeleteQuietly(partialPath);
            return UpdateDownloadResult.Failed(exception.Message);
        }
        catch (IOException exception)
        {
            DeleteQuietly(partialPath);
            return UpdateDownloadResult.Failed($"下载失败：更新包传输中断（{exception.Message}）。");
        }
        catch (UnauthorizedAccessException exception)
        {
            // A read-only or permission-denied cache directory: the package is not the problem,
            // the place it was going is.
            DeleteQuietly(partialPath);
            return UpdateDownloadResult.Failed($"下载失败：无法写入更新包（{exception.Message}）。");
        }
        catch (OperationCanceledException)
        {
            // A cancelled download is the caller's business; the half-written file is not.
            DeleteQuietly(partialPath);
            throw;
        }

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            DeleteQuietly(partialPath);
            return UpdateDownloadResult.Failed(
                $"更新包校验失败：{asset.Name} 的 SHA256 与发布页不一致，下载可能损坏或被篡改。");
        }

        try
        {
            // The hash matched, so the partial file is the package: promote it in place of
            // any earlier download of the same release.
            File.Move(partialPath, finalPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(partialPath);
            return UpdateDownloadResult.Failed($"下载失败：无法保存更新包（{exception.Message}）。");
        }

        progress?.Report(1d);
        return UpdateDownloadResult.Ok(finalPath);
    }

    /// <summary>
    /// Streams the package to <paramref name="partialPath"/> while hashing it, and returns
    /// the lowercase hex SHA256 of what actually arrived.
    /// </summary>
    private async Task<string> DownloadAndHashAsync(
        ReleaseAsset asset,
        string partialPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(asset.DownloadUrl, "更新包", cancellationToken)
            .ConfigureAwait(false);

        var total = response.Content.Headers.ContentLength is { } length && length > 0
            ? length
            : asset.Size > 0 ? asset.Size : (long?)null;

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = new FileStream(
            partialPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        var received = 0L;

        while (true)
        {
            var read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer.AsSpan(0, read));
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

            received += read;
            if (total is { } expectedBytes)
            {
                progress?.Report(Math.Clamp((double)received / expectedBytes, 0d, 1d));
            }
        }

        await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// Reads the sidecar and returns the checksum it carries. The sidecar is sha256sum
    /// format - <c>&lt;hex&gt;  &lt;name&gt;</c> - so only the first field is the checksum,
    /// and anything that is not 64 hex digits is a refusal rather than a mismatch to report.
    /// </summary>
    private async Task<string> ReadChecksumAsync(
        ReleaseAsset checksumAsset,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(checksumAsset.DownloadUrl, "SHA256 校验文件", cancellationToken)
            .ConfigureAwait(false);

        string text;
        try
        {
            text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new UpdateDownloadException(
                "更新包校验失败：无法读取发布页提供的 SHA256 校验值，请检查网络连接后重试。");
        }

        var checksum = FirstField(text);
        if (checksum is null)
        {
            throw new UpdateDownloadException(
                $"更新包校验失败：{checksumAsset.Name} 的内容不是有效的 SHA256 校验值。");
        }

        return checksum;
    }

    /// <summary>
    /// One GET, with the transport failures classified into the Chinese message the user
    /// sees. A cancellation is deliberately not classified here: the caller decides whether
    /// it was its own token or this service's timeout.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        string url,
        string what,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // GitHub's download hosts reject requests without a User-Agent.
        request.Headers.TryAddWithoutValidation("User-Agent", "DeziroJoy");

        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new UpdateDownloadException("下载失败：无法连接到 GitHub，请检查网络连接。");
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new UpdateDownloadException(DescribeStatus(status, what));
        }

        return response;
    }

    private static string DescribeStatus(HttpStatusCode status, string what) => status switch
    {
        HttpStatusCode.NotFound => what == "更新包"
            ? "下载失败：发布页上找不到该更新包（HTTP 404）。"
            : "更新包校验失败：发布页上找不到 SHA256 校验文件（HTTP 404）。",
        HttpStatusCode.Forbidden => "下载失败：GitHub 访问受限（HTTP 403），请稍后再试。",
        HttpStatusCode.Unauthorized => "下载失败：GitHub 拒绝访问（HTTP 401）。",
        _ => $"下载失败：GitHub 返回 HTTP {(int)status}。",
    };

    /// <summary>The first whitespace-delimited field of a checksum sidecar, if it is a SHA256.</summary>
    private static string? FirstField(string text)
    {
        var trimmed = text.Trim();
        var end = 0;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]))
        {
            end++;
        }

        if (end != 64)
        {
            return null;
        }

        foreach (var c in trimmed.AsSpan(0, end))
        {
            var hex = c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
            if (!hex)
            {
                return null;
            }
        }

        return trimmed[..end].ToLowerInvariant();
    }

    private static string DefaultCacheDirectory()
        => Path.Combine(Path.GetTempPath(), "DeziroJoy", "updates");

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover partial file is not worth failing the reported error over: the
            // result already says the download did not succeed.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
    }

    /// <summary>
    /// A download failure that already carries its user-presentable Chinese reason. Internal
    /// so the public surface stays a result record rather than an exception contract.
    /// </summary>
    internal sealed class UpdateDownloadException(string message) : Exception(message);
}
