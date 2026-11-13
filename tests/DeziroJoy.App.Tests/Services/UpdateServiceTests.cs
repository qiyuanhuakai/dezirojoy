using System.Net;
using System.Text;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The release check over a stubbed transport: which release wins, how versions compare, and
/// that every failure mode comes back as a Chinese, presentable message instead of an
/// exception. Only a cancelled caller token is allowed to throw.
/// </summary>
public sealed class UpdateServiceTests
{
    private const string Current = "0.0.1-beta.3-preview";

    private static UpdateService Service(HttpMessageHandler handler, string current = Current)
        => new(handler, TimeSpan.FromSeconds(10), current);

    private static StubHandler Responds(HttpStatusCode status, string body) => new((_, _) =>
        Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));

    private static StubHandler RespondsWithReleases(string json) => Responds(HttpStatusCode.OK, json);

    [Fact]
    public void The_Releases_List_Is_Queried_For_The_Repository()
    {
        // The newest release of this repo is a prerelease, so /releases/latest would 404: the
        // LIST endpoint is the only source that carries it.
        var handler = RespondsWithReleases("[]");
        var service = Service(handler);

        _ = service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        var request = Assert.IsType<HttpRequestMessage>(handler.LastRequest);
        Assert.Equal(
            "https://api.github.com/repos/qiyuanhuakai/dezirojoy/releases?per_page=10",
            request.RequestUri?.ToString());
        Assert.Contains("DeziroJoy", request.Headers.UserAgent.ToString());
        Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString());
    }

    [Fact]
    public async Task The_Newest_Release_By_Published_At_Is_The_One_Compared()
    {
        // The newest entry is last in the array and a draft published even later is ignored:
        // position in the list is not the ordering criterion, published_at is.
        var handler = RespondsWithReleases(
            """
            [
              {"tag_name":"v0.0.2-alpha.1","published_at":"2026-02-01T12:00:00Z","html_url":"https://github.com/qiyuanhuakai/dezirojoy/releases/tag/v0.0.2-alpha.1","draft":false,"prerelease":true},
              {"tag_name":"v0.0.9-draft","published_at":"2026-03-01T12:00:00Z","html_url":"https://example.invalid/draft","draft":true,"prerelease":true},
              {"tag_name":"v0.0.1-beta.3-preview","published_at":"2026-01-01T12:00:00Z","html_url":"https://example.invalid/older","draft":false,"prerelease":true}
            ]
            """);
        var service = Service(handler);

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.NotNull(result.Latest);
        Assert.Equal("v0.0.2-alpha.1", result.Latest!.TagName);
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero), result.Latest!.PublishedAt);
        Assert.Equal(
            "https://github.com/qiyuanhuakai/dezirojoy/releases/tag/v0.0.2-alpha.1",
            result.Latest!.PageUrl);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task A_Release_Matching_The_Running_Version_Reports_Up_To_Date()
    {
        var handler = RespondsWithReleases(
            """
            [
              {"tag_name":"v0.0.1-beta.3-preview","published_at":"2026-01-01T12:00:00Z","html_url":"https://example.invalid/same","draft":false,"prerelease":true}
            ]
            """);
        var service = Service(handler);

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Latest);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task A_Newer_Prerelease_Of_An_Older_Core_Still_Counts_As_An_Update()
    {
        // 0.0.2-alpha.1 outranks 0.0.1-beta.3-preview: the core wins before any prerelease.
        var handler = RespondsWithReleases(
            """
            [
              {"tag_name":"v0.0.2-alpha.1","published_at":"2026-02-01T12:00:00Z","html_url":"https://example.invalid/newer","draft":false,"prerelease":true}
            ]
            """);
        var service = Service(handler);

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
    }

    [Fact]
    public async Task A_Release_Newer_Than_Any_Prerelease_Of_The_Same_Core_Is_An_Update()
    {
        var handler = RespondsWithReleases(
            """
            [
              {"tag_name":"v0.0.2","published_at":"2026-02-01T12:00:00Z","html_url":"https://example.invalid/stable","draft":false,"prerelease":false}
            ]
            """);
        var service = Service(handler, current: "0.0.2-beta.3");

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("v0.0.2", result.Latest!.TagName);
    }

    [Fact]
    public async Task A_Prerelease_Older_Than_The_Running_One_Reports_Up_To_Date()
    {
        var handler = RespondsWithReleases(
            """
            [
              {"tag_name":"v0.0.2-beta.1","published_at":"2026-02-01T12:00:00Z","html_url":"https://example.invalid/older-preview","draft":false,"prerelease":true}
            ]
            """);
        var service = Service(handler, current: "0.0.2-beta.2");

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task A_Release_Whose_Tag_Is_Not_Semver_Is_Skipped_Not_Fatal()
    {
        var handler = RespondsWithReleases(
            """
            [
              {"tag_name":"nightly","published_at":"2026-03-01T12:00:00Z","html_url":"https://example.invalid/nightly","draft":false,"prerelease":true},
              {"tag_name":"v0.0.2-alpha.1","published_at":"2026-02-01T12:00:00Z","html_url":"https://example.invalid/semver","draft":false,"prerelease":true}
            ]
            """);
        var service = Service(handler);

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("v0.0.2-alpha.1", result.Latest!.TagName);
    }

    [Fact]
    public async Task A_Missing_HtmlUrl_Falls_Back_To_The_Tag_Page()
    {
        var handler = RespondsWithReleases(
            """
            [
              {"tag_name":"v0.0.2-alpha.1","published_at":"2026-02-01T12:00:00Z","html_url":null,"draft":false,"prerelease":true}
            ]
            """);
        var service = Service(handler);

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            "https://github.com/qiyuanhuakai/dezirojoy/releases/tag/v0.0.2-alpha.1",
            result.Latest!.PageUrl);
    }

    [Fact]
    public async Task A_404_Is_Reported_In_Chinese()
    {
        var service = Service(Responds(HttpStatusCode.NotFound, "{}"));

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("404", result.ErrorMessage);
        Assert.Null(result.Latest);
    }

    [Fact]
    public async Task A_Timeout_Is_Reported_In_Chinese()
    {
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            throw new InvalidOperationException("the service timeout should cancel this delay first");
        });
        var service = new UpdateService(handler, TimeSpan.FromMilliseconds(100), Current);

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("超时", result.ErrorMessage);
    }

    [Fact]
    public async Task A_Network_Failure_Is_Reported_In_Chinese()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("no route to host"));
        var service = Service(handler);

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("网络", result.ErrorMessage);
    }

    [Fact]
    public async Task Unparsable_Json_Is_Reported_In_Chinese()
    {
        var service = Service(Responds(HttpStatusCode.OK, "<html>rate limited</html>"));

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("无法解析", result.ErrorMessage);
    }

    [Fact]
    public async Task An_Empty_Release_List_Is_Reported()
    {
        var service = Service(RespondsWithReleases("[]"));

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("未找到任何发布记录", result.ErrorMessage);
    }

    [Fact]
    public async Task A_Null_Entry_In_The_List_Is_Skipped_Not_Fatal()
    {
        var service = Service(RespondsWithReleases(
            """
            [
              null,
              {"tag_name":"v0.0.2-alpha.1","published_at":"2026-02-01T12:00:00Z","html_url":"https://example.invalid/semver","draft":false,"prerelease":true}
            ]
            """));

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("v0.0.2-alpha.1", result.Latest!.TagName);
    }

    [Fact]
    public async Task A_List_Without_One_Usable_Release_Is_Reported()
    {
        var service = Service(RespondsWithReleases(
            """
            [
              {"tag_name":"nightly","published_at":"2026-03-01T12:00:00Z","html_url":null,"draft":false,"prerelease":true}
            ]
            """));

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("未找到可识别的发布版本", result.ErrorMessage);
    }

    [Fact]
    public async Task A_Cancelled_Caller_Token_Propagates_As_Cancellation()
    {
        var handler = new StubHandler((_, cancellationToken) => Task.Delay(TimeSpan.FromSeconds(30), cancellationToken)
            .ContinueWith(_ => new HttpResponseMessage(HttpStatusCode.OK), TaskScheduler.Default));
        var service = Service(handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckForUpdateAsync(cts.Token));
    }

    [Fact]
    public void The_Current_Version_Comes_From_The_Assembly_By_Default()
    {
        // Read the same source UpdateService reads, so the assertion tracks the build version
        // instead of a hardcoded prefix that breaks on every release bump.
        var informational = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                typeof(UpdateService).Assembly)?.InformationalVersion;
        var expected = informational?.Split('+', 2)[0];

        Assert.False(string.IsNullOrWhiteSpace(expected));
        Assert.Equal(expected, new UpdateService().CurrentVersionText);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
            => _respond = respond;

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return _respond(request, cancellationToken);
        }
    }
}

/// <summary>
/// Semver 2.0 precedence with prerelease identifiers, which is what makes a repo that ships
/// only prereleases comparable: alpha &lt; beta &lt; preview &lt; release, numeric identifiers
/// by value, longer identifier sets above shorter ones.
/// </summary>
public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.4")]
    [InlineData("1.2.9", "1.2.10")]
    [InlineData("0.0.1-beta.3-preview", "0.0.2-alpha.1")]
    [InlineData("0.0.2-alpha.1", "0.0.2-alpha.2")]
    [InlineData("0.0.2-alpha.2", "0.0.2-alpha.10")]
    [InlineData("0.0.2-alpha", "0.0.2-beta")]
    [InlineData("0.0.2-beta", "0.0.2-preview")]
    [InlineData("0.0.2-preview", "0.0.2")]
    [InlineData("0.0.2-beta.1", "0.0.2-beta.1.1")]
    public void Precedence_Ranks_Versions_As_Semver_Does(string lower, string higher)
    {
        Assert.True(SemanticVersion.Parse(lower) < SemanticVersion.Parse(higher));
        Assert.True(SemanticVersion.Parse(higher) > SemanticVersion.Parse(lower));
        Assert.NotEqual(SemanticVersion.Parse(lower), SemanticVersion.Parse(higher));
    }

    [Theory]
    [InlineData("0.0.2-beta.1", "v0.0.2-beta.1")]
    [InlineData("0.0.2", "0.0.2+build.7")]
    [InlineData("0.0.2+abc", "0.0.2+xyz")]
    public void Build_Metadata_And_The_V_Prefix_Do_Not_Change_Identity(string left, string right)
    {
        Assert.Equal(SemanticVersion.Parse(left), SemanticVersion.Parse(right));
        Assert.Equal(0, SemanticVersion.Parse(left).CompareTo(SemanticVersion.Parse(right)));
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("1..3")]
    [InlineData("1.2.-1")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-a..b")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("release-1.2.3")]
    public void Anything_That_Is_Not_Three_Numbers_With_Valid_Identifiers_Is_Rejected(string text)
    {
        Assert.False(SemanticVersion.TryParse(text, out _));
        Assert.Throws<FormatException>(() => SemanticVersion.Parse(text));
    }

    [Fact]
    public void Parse_Accepts_The_Leading_V_And_Keeps_The_Prerelease()
    {
        var version = SemanticVersion.Parse("v0.0.1-beta.3-preview");

        Assert.Equal(0, version.Major);
        Assert.Equal(0, version.Minor);
        Assert.Equal(1, version.Patch);
        Assert.True(version.IsPrerelease);
        Assert.Equal("beta.3-preview", version.Prerelease);
        Assert.Equal("0.0.1-beta.3-preview", version.ToString());
    }
}
