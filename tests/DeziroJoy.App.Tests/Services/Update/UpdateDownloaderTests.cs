using System.Net;
using System.Security.Cryptography;
using System.Text;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services.Update;

/// <summary>
/// The package download and its SHA256 verification, over a stubbed transport. The checksum
/// is the only integrity story this product has, so the interesting cases are all about
/// refusing: a wrong hash, a missing sidecar, a sidecar that is not a checksum, and a
/// transport that never delivers.
/// </summary>
public sealed class UpdateDownloaderTests : IDisposable
{
    private readonly string _cache;

    private readonly StubHandler _handler;

    private readonly UpdateDownloader _downloader;

    public UpdateDownloaderTests()
    {
        _cache = Path.Combine(Path.GetTempPath(), $"dezirojoy-update-test-{Guid.NewGuid():N}");
        _handler = new StubHandler();
        _downloader = new UpdateDownloader(
            _handler,
            TimeSpan.FromSeconds(10),
            () => _cache);
    }

    public void Dispose()
    {
        _downloader.Dispose();
        if (Directory.Exists(_cache))
        {
            Directory.Delete(_cache, recursive: true);
        }
    }

    private static ReleaseAsset Package(string name = "DeziroJoy_0.0.1_amd64.deb", long size = 0)
        => new() { Name = name, DownloadUrl = $"https://example.invalid/{name}", Size = size };

    private static ReleaseAsset Sidecar(string name, string content)
        => new() { Name = name, DownloadUrl = $"https://example.invalid/{name}", Size = content.Length };

    private static string ChecksumFor(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public async Task A_Package_Whose_Sha256_Matches_The_Sidecar_Is_Accepted()
    {
        var bytes = Encoding.UTF8.GetBytes("the deb payload");
        _handler.Serve(
            "https://example.invalid/DeziroJoy_0.0.1_amd64.deb",
            new StringContent(Encoding.UTF8.GetString(bytes)));
        _handler.Serve(
            "https://example.invalid/DeziroJoy_0.0.1_amd64.deb.sha256",
            new StringContent($"{ChecksumFor(bytes)}  DeziroJoy_0.0.1_amd64.deb\n"));

        var progress = new List<double>();
        var result = await _downloader.DownloadAsync(
            Package(),
            Sidecar("DeziroJoy_0.0.1_amd64.deb.sha256", string.Empty),
            new SyncProgress(progress.Add),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.NotNull(result.FilePath);
        Assert.True(File.Exists(result.FilePath));
        Assert.Equal(Path.Combine(_cache, "DeziroJoy_0.0.1_amd64.deb"), result.FilePath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(result.FilePath, TestContext.Current.CancellationToken));
        Assert.Equal(1d, progress[^1]);
    }

    [Fact]
    public async Task A_Package_Whose_Sha256_Does_Not_Match_Is_Refused_And_Nothing_Is_Left_Behind()
    {
        var bytes = Encoding.UTF8.GetBytes("a tampered payload");
        _handler.Serve(
            "https://example.invalid/DeziroJoy_0.0.1_amd64.deb",
            new StringContent(Encoding.UTF8.GetString(bytes)));
        _handler.Serve(
            "https://example.invalid/DeziroJoy_0.0.1_amd64.deb.sha256",
            new StringContent($"{new string('0', 64)}  DeziroJoy_0.0.1_amd64.deb\n"));

        var result = await _downloader.DownloadAsync(
            Package(),
            Sidecar("DeziroJoy_0.0.1_amd64.deb.sha256", string.Empty),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("SHA256", result.ErrorMessage);
        Assert.Contains("不一致", result.ErrorMessage);
        Assert.Null(result.FilePath);
        Assert.Empty(Directory.GetFiles(_cache));
    }

    [Fact]
    public async Task A_Release_Without_A_Sidecar_Is_Refused_Before_Anything_Is_Downloaded()
    {
        var result = await _downloader.DownloadAsync(
            Package(),
            checksumAsset: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("未提供 SHA256 校验值", result.ErrorMessage);
        Assert.Empty(_handler.Requested);
    }

    [Fact]
    public async Task A_Sidecar_That_Is_Not_A_Checksum_Is_Refused()
    {
        _handler.Serve(
            "https://example.invalid/DeziroJoy_0.0.1_amd64.deb.sha256",
            new StringContent("SHA256 (DeziroJoy_0.0.1_amd64.deb) = deadbeef\n"));

        var result = await _downloader.DownloadAsync(
            Package(),
            Sidecar("DeziroJoy_0.0.1_amd64.deb.sha256", string.Empty),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("不是有效的 SHA256 校验值", result.ErrorMessage);
        Assert.DoesNotContain("https://example.invalid/DeziroJoy_0.0.1_amd64.deb", _handler.Requested);
    }

    [Fact]
    public async Task A_Sidecar_Carrying_Only_The_Hash_Is_Accepted()
    {
        var bytes = Encoding.UTF8.GetBytes("payload without a file name on the line");
        _handler.Serve(
            "https://example.invalid/DeziroJoy_0.0.1_amd64.deb",
            new StringContent(Encoding.UTF8.GetString(bytes)));
        _handler.Serve(
            "https://example.invalid/DeziroJoy_0.0.1_amd64.deb.sha256",
            new StringContent(ChecksumFor(bytes)));

        var result = await _downloader.DownloadAsync(
            Package(),
            Sidecar("DeziroJoy_0.0.1_amd64.deb.sha256", string.Empty),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.ErrorMessage);
    }

    [Fact]
    public async Task A_Missing_Package_Is_Reported_In_Chinese()
    {
        _handler.Serve(
            "https://example.invalid/DeziroJoy_0.0.1_amd64.deb.sha256",
            new StringContent($"{new string('a', 64)}\n"));

        var result = await _downloader.DownloadAsync(
            Package(),
            Sidecar("DeziroJoy_0.0.1_amd64.deb.sha256", string.Empty),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("404", result.ErrorMessage);
    }

    [Fact]
    public async Task A_Download_Timeout_Is_Reported_In_Chinese()
    {
        var downloader = new UpdateDownloader(
            new StubHandler(async (_, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                throw new InvalidOperationException("the downloader timeout should cancel this first");
            }),
            TimeSpan.FromMilliseconds(100),
            () => _cache);

        var result = await downloader.DownloadAsync(
            Package(),
            Sidecar("DeziroJoy_0.0.1_amd64.deb.sha256", string.Empty),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("超时", result.ErrorMessage);
    }

    [Fact]
    public async Task A_Network_Failure_Is_Reported_In_Chinese()
    {
        var downloader = new UpdateDownloader(
            new StubHandler((_, _) => throw new HttpRequestException("no route to host")),
            TimeSpan.FromSeconds(10),
            () => _cache);

        var result = await downloader.DownloadAsync(
            Package(),
            Sidecar("DeziroJoy_0.0.1_amd64.deb.sha256", string.Empty),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("无法连接", result.ErrorMessage);
    }

    [Fact]
    public async Task A_Cancelled_Caller_Token_Propagates_As_Cancellation()
    {
        // The caller's token, not this service's timeout, decides: a cancelled download is an
        // exception, while an expired budget is a presentable failure.
        _handler.Serve(
            "https://example.invalid/DeziroJoy_0.0.1_amd64.deb",
            new StringContent("first chunk"));
        _handler.Serve(
            "https://example.invalid/DeziroJoy_0.0.1_amd64.deb.sha256",
            new StringContent($"{new string('a', 64)}\n"));

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _downloader.DownloadAsync(
            Package(),
            Sidecar("DeziroJoy_0.0.1_amd64.deb.sha256", string.Empty),
            cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Progress_Reports_The_Bytes_As_They_Arrive()
    {
        var first = Encoding.UTF8.GetBytes(new string('a', 64));
        var second = Encoding.UTF8.GetBytes(new string('b', 64));

        // The second read is entered only after the first chunk has been hashed, written and
        // reported, so reaching it is the deterministic point at which half the bytes are
        // already on disk - no sleep, and no dependence on how the transport chunks.
        var secondReadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _handler.Serve(
            "https://example.invalid/DeziroJoy_0.0.1_amd64.deb",
            new StreamContent(new GatedStream(first, second, secondReadEntered, releaseSecond)));
        _handler.Serve(
            "https://example.invalid/DeziroJoy_0.0.1_amd64.deb.sha256",
            new StringContent($"{ChecksumFor(first.Concat(second).ToArray())}\n"));

        var progress = new List<double>();
        var download = _downloader.DownloadAsync(
            Package(size: first.Length + second.Length),
            Sidecar("DeziroJoy_0.0.1_amd64.deb.sha256", string.Empty),
            new SyncProgress(progress.Add),
            TestContext.Current.CancellationToken);

        await secondReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(0.5, progress[^1], 6);
        Assert.False(download.IsCompleted, "the second chunk has not been released yet");

        releaseSecond.SetResult();

        var result = await download;

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(1d, progress[^1]);
        Assert.Equal(progress.OrderBy(value => value), progress);
    }

    [Fact]
    public async Task A_Cache_That_Cannot_Be_Written_To_Is_Reported_As_A_Download_Failure()
    {
        // The package's own path is taken by a directory, so the partial file cannot be
        // created: that is a failure of the place the package was going, not of the package,
        // and it must not escape as an exception.
        var bytes = Encoding.UTF8.GetBytes("the deb payload");
        _handler.Serve(
            "https://example.invalid/DeziroJoy_0.0.1_amd64.deb",
            new StringContent(Encoding.UTF8.GetString(bytes)));
        _handler.Serve(
            "https://example.invalid/DeziroJoy_0.0.1_amd64.deb.sha256",
            new StringContent($"{ChecksumFor(bytes)}\n"));
        Directory.CreateDirectory(Path.Combine(_cache, "DeziroJoy_0.0.1_amd64.deb.part"));

        var result = await _downloader.DownloadAsync(
            Package(),
            Sidecar("DeziroJoy_0.0.1_amd64.deb.sha256", string.Empty),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("无法写入", result.ErrorMessage);
    }

    [Fact]
    public async Task A_Package_Is_Streamed_To_The_Cache_Under_Its_Own_Name()
    {
        var bytes = Encoding.UTF8.GetBytes("appimage bytes");
        _handler.Serve(
            "https://example.invalid/DeziroJoy-0.0.1-x86_64.AppImage",
            new StringContent(Encoding.UTF8.GetString(bytes)));
        _handler.Serve(
            "https://example.invalid/DeziroJoy-0.0.1-x86_64.AppImage.sha256",
            new StringContent($"{ChecksumFor(bytes)}\n"));

        var result = await _downloader.DownloadAsync(
            Package("DeziroJoy-0.0.1-x86_64.AppImage"),
            Sidecar("DeziroJoy-0.0.1-x86_64.AppImage.sha256", string.Empty),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.EndsWith("DeziroJoy-0.0.1-x86_64.AppImage", result.FilePath!.Replace('\\', '/'));
    }

    /// <summary>
    /// Reports synchronously, so a test observes the same values the downloader produced
    /// instead of a value that is still queued on a captured context.
    /// </summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpContent>> _bodies = new(StringComparer.Ordinal);

        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _responder;

        public StubHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? responder = null)
            => _responder = responder;

        public List<string> Requested { get; } = [];

        public void Serve(string url, HttpContent content)
            => _bodies[url] = () => content;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);

            if (_responder is { } responder)
            {
                return responder(request, cancellationToken);
            }

            if (_bodies.TryGetValue(url, out var body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body() });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>
    /// A body that arrives in two chunks, where the second read only completes once the test
    /// releases it. That makes "half the bytes were reported" observable without a sleep.
    /// </summary>
    private sealed class GatedStream : Stream
    {
        private readonly byte[] _first;
        private readonly byte[] _second;
        private readonly TaskCompletionSource _secondReadEntered;
        private readonly TaskCompletionSource _releaseSecond;
        private int _offset;

        public GatedStream(
            byte[] first,
            byte[] second,
            TaskCompletionSource secondReadEntered,
            TaskCompletionSource releaseSecond)
        {
            _first = first;
            _second = second;
            _secondReadEntered = secondReadEntered;
            _releaseSecond = releaseSecond;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get; set; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_offset == 0)
            {
                _offset = _first.Length;
                _first.CopyTo(buffer);
                return _first.Length;
            }

            if (_offset == _first.Length)
            {
                _secondReadEntered.TrySetResult();
                await _releaseSecond.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

                _offset += _second.Length;
                _second.CopyTo(buffer);
                return _second.Length;
            }

            return 0;
        }
    }
}
