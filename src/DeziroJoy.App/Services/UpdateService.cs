using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeziroJoy.App.Services;

/// <summary>
/// Semantic version (semver 2.0) with full prerelease precedence. This repository ships
/// every build as a prerelease ("0.0.1-beta.3-preview"), so the comparison must rank
/// prerelease identifiers - alpha &lt; beta &lt; preview &lt; release, numeric identifiers by
/// value - rather than falling back to a string compare.
/// </summary>
public sealed class SemanticVersion : IEquatable<SemanticVersion>, IComparable, IComparable<SemanticVersion>
{
    private readonly string[] _prerelease;

    private SemanticVersion(int major, int minor, int patch, string[] prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _prerelease = prerelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>Whether this version carries a prerelease suffix ("1.0.0-beta.1").</summary>
    public bool IsPrerelease => _prerelease.Length > 0;

    /// <summary>The dot-joined prerelease identifiers, or an empty string for a release.</summary>
    public string Prerelease => string.Join('.', _prerelease);

    public static SemanticVersion Parse(string version)
        => TryParse(version, out var parsed)
            ? parsed
            : throw new FormatException($"\"{version}\" 不是有效的语义化版本号。");

    /// <summary>
    /// Parses "1.2.3[-prerelease][+build]". A leading "v" is accepted (GitHub tags carry it)
    /// and build metadata is dropped, since it takes no part in precedence. Returns false for
    /// anything that is not three dot-separated non-negative integers with valid identifiers.
    /// </summary>
    public static bool TryParse(string? version, [NotNullWhen(true)] out SemanticVersion? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var text = version.AsSpan().Trim();
        if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V'))
        {
            text = text[1..];
        }

        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            text = text[..plus];
        }

        string[] prerelease;
        string coreText;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            coreText = text[..dash].ToString();
            prerelease = text[(dash + 1)..].ToString().Split('.');
        }
        else
        {
            coreText = text.ToString();
            prerelease = [];
        }

        var parts = coreText.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0
                || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        foreach (var identifier in prerelease)
        {
            if (identifier.Length == 0)
            {
                return false;
            }
        }

        result = new SemanticVersion(numbers[0], numbers[1], numbers[2], prerelease);
        return true;
    }

    /// <summary>
    /// Semver 2.0 precedence: core numbers first, then prerelease - a release outranks every
    /// prerelease of the same core, identifiers compare pairwise (numeric numerically,
    /// alphanumeric by ASCII order, numeric below alphanumeric) and a longer identifier set
    /// wins when all shared identifiers are equal.
    /// </summary>
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = Major.CompareTo(other.Major);
        if (core != 0)
        {
            return core;
        }

        core = Minor.CompareTo(other.Minor);
        if (core != 0)
        {
            return core;
        }

        core = Patch.CompareTo(other.Patch);
        if (core != 0)
        {
            return core;
        }

        if (_prerelease.Length == 0 && other._prerelease.Length == 0)
        {
            return 0;
        }

        if (_prerelease.Length == 0)
        {
            return 1;
        }

        if (other._prerelease.Length == 0)
        {
            return -1;
        }

        var shared = Math.Min(_prerelease.Length, other._prerelease.Length);
        for (var i = 0; i < shared; i++)
        {
            var comparison = CompareIdentifiers(_prerelease[i], other._prerelease[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    /// <summary>Null-aware comparison so the operators below never dereference a null.</summary>
    private static int Compare(SemanticVersion? left, SemanticVersion? right)
        => left is null ? right is null ? 0 : -1 : left.CompareTo(right);

    public static bool operator ==(SemanticVersion? left, SemanticVersion? right) => Equals(left, right);

    public static bool operator !=(SemanticVersion? left, SemanticVersion? right) => !Equals(left, right);

    public static bool operator <(SemanticVersion? left, SemanticVersion? right) => Compare(left, right) < 0;

    public static bool operator >(SemanticVersion? left, SemanticVersion? right) => Compare(left, right) > 0;

    public static bool operator <=(SemanticVersion? left, SemanticVersion? right) => Compare(left, right) <= 0;

    public static bool operator >=(SemanticVersion? left, SemanticVersion? right) => Compare(left, right) >= 0;

    private static int CompareIdentifiers(string left, string right)
    {
        var leftNumeric = IsNumericIdentifier(left);
        var rightNumeric = IsNumericIdentifier(right);

        if (leftNumeric && rightNumeric)
        {
            return CompareNumericIdentifiers(left, right);
        }

        if (leftNumeric)
        {
            return -1;
        }

        if (rightNumeric)
        {
            return 1;
        }

        return string.CompareOrdinal(left, right);
    }

    private static bool IsNumericIdentifier(string identifier)
    {
        foreach (var c in identifier)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return identifier.Length > 0;
    }

    /// <summary>Compares all-digit identifiers by value, not lexically: "10" outranks "9".</summary>
    private static int CompareNumericIdentifiers(string left, string right)
    {
        var significantLeft = left.TrimStart('0');
        var significantRight = right.TrimStart('0');

        return significantLeft.Length != significantRight.Length
            ? significantLeft.Length.CompareTo(significantRight.Length)
            : string.CompareOrdinal(significantLeft, significantRight);
    }

    public bool Equals(SemanticVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => Equals(obj as SemanticVersion);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Major);
        hash.Add(Minor);
        hash.Add(Patch);
        foreach (var identifier in _prerelease)
        {
            hash.Add(identifier);
        }

        return hash.ToHashCode();
    }

    int IComparable.CompareTo(object? obj) => CompareTo(obj as SemanticVersion);

    public override string ToString()
        => IsPrerelease ? $"{Major}.{Minor}.{Patch}-{Prerelease}" : $"{Major}.{Minor}.{Patch}";
}

/// <summary>
/// Checks GitHub releases for a version newer than the running one. The releases LIST is
/// queried, not <c>/releases/latest</c>: every release of this repository is a prerelease,
/// and GitHub answers <c>/releases/latest</c> with 404 when the newest release is one. The
/// newest entry is picked by <c>published_at</c>, so a tag renamed or re-published later
/// cannot hide behind an older-but-first list position.
/// </summary>
/// <remarks>
/// No authentication is used, so the unauthenticated 60-requests-per-hour GitHub limit
/// applies - one check per launch stays far below it.
/// </remarks>
public sealed class UpdateService : IUpdateService, IDisposable
{
    private const string Owner = "qiyuanhuakai";

    private const string Repository = "dezirojoy";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;

    private readonly TimeSpan _timeout;

    private bool _disposed;

    /// <summary>
    /// Builds the service around an optional transport seam. Production passes nothing; tests
    /// pass a stub <see cref="HttpMessageHandler"/>, a short <paramref name="timeout"/> and a
    /// fixed <paramref name="currentVersion"/> so the comparison never depends on the build.
    /// </summary>
    public UpdateService(
        HttpMessageHandler? handler = null,
        TimeSpan? timeout = null,
        string? currentVersion = null)
    {
        _timeout = timeout ?? DefaultTimeout;

        // The linked cancellation token below is the single timeout authority, so the client
        // itself must not time out underneath it.
        _http = new HttpClient(handler ?? new HttpClientHandler())
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };

        CurrentVersionText = !string.IsNullOrWhiteSpace(currentVersion)
            ? currentVersion
            : ReadCurrentVersion();
    }

    /// <summary>The releases endpoint, newest first is NOT guaranteed by GitHub - see the class remark.</summary>
    public static string ReleasesUrl => $"https://api.github.com/repos/{Owner}/{Repository}/releases?per_page=10";

    public string CurrentVersionText { get; }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The service timeout and the caller's cancellation are linked: the caller's token
        // still aborts immediately, and a timeout surfaces as "超时" rather than as a cancel.
        using var timeout = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);

            // GitHub rejects API calls without a User-Agent. TryAddWithoutValidation keeps an
            // odd informational version (a local build stamp, say) from failing the check.
            request.Headers.TryAddWithoutValidation("User-Agent", $"DeziroJoy/{CurrentVersionText}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Failed("检查更新超时，请检查网络连接后重试。");
        }
        catch (HttpRequestException)
        {
            return UpdateCheckResult.Failed("检查更新失败：无法连接到 GitHub，请检查网络连接。");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failed(DescribeStatus(response.StatusCode));
            }

            string json;
            try
            {
                json = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return UpdateCheckResult.Failed("检查更新超时，请检查网络连接后重试。");
            }
            catch (HttpRequestException)
            {
                return UpdateCheckResult.Failed("检查更新失败：无法连接到 GitHub，请检查网络连接。");
            }
            catch (IOException)
            {
                return UpdateCheckResult.Failed("检查更新失败：无法连接到 GitHub，请检查网络连接。");
            }

            // The element type is nullable: a JSON array of nulls is malformed payload, and the
            // draft/semver filter below must never dereference one.
            List<GitHubRelease?>? releases;
            try
            {
                releases = JsonSerializer.Deserialize<List<GitHubRelease?>>(json);
            }
            catch (JsonException)
            {
                return UpdateCheckResult.Failed("检查更新失败：发布信息无法解析。");
            }

            if (releases is null || releases.Count == 0)
            {
                return UpdateCheckResult.Failed("检查更新失败：未找到任何发布记录。");
            }

            if (!SemanticVersion.TryParse(CurrentVersionText, out var current))
            {
                return UpdateCheckResult.Failed("检查更新失败：无法解析当前版本。");
            }

            // Drafts are unpublished by definition, and a tag that is not a semantic version
            // cannot be compared at all - both are skipped so the newest USABLE release wins.
            var latest = releases
                .OfType<GitHubRelease>()
                .Where(release => !release.Draft
                    && release.PublishedAt is not null
                    && release.TagName is { Length: > 0 } tag
                    && SemanticVersion.TryParse(tag, out _))
                .OrderByDescending(release => release.PublishedAt)
                .FirstOrDefault();

            if (latest?.TagName is not { } latestTag
                || !SemanticVersion.TryParse(latestTag, out var latestVersion))
            {
                return UpdateCheckResult.Failed("检查更新失败：未找到可识别的发布版本。");
            }

            var info = new ReleaseInfo
            {
                TagName = latestTag,
                Version = latestVersion,
                PublishedAt = latest.PublishedAt,
                PageUrl = !string.IsNullOrWhiteSpace(latest.HtmlUrl)
                    ? latest.HtmlUrl
                    : $"https://github.com/{Owner}/{Repository}/releases/tag/{latestTag}",
            };

            return latestVersion.CompareTo(current) > 0
                ? UpdateCheckResult.Available(info)
                : UpdateCheckResult.UpToDate();
        }
    }

    private static string DescribeStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound => "检查更新失败：未找到发布信息（HTTP 404）。",
        HttpStatusCode.Forbidden => "检查更新失败：GitHub 接口访问受限（HTTP 403），请稍后再试。",
        _ => $"检查更新失败：GitHub 返回 HTTP {(int)status}。",
    };

    private static string ReadCurrentVersion()
    {
        var assembly = typeof(UpdateService).Assembly;

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // SourceLink-style builds append "+<commit>" to the informational version; the
            // precedence-relevant part ends at the plus.
            var cut = informational.Split('+', 2)[0];
            if (!string.IsNullOrWhiteSpace(cut))
            {
                return cut;
            }
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
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

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }
    }
}
