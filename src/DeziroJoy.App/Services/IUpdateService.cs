namespace DeziroJoy.App.Services;

/// <summary>Outcome of a release check.</summary>
public enum UpdateCheckStatus
{
    /// <summary>
    /// The check could not be completed. <see cref="UpdateCheckResult.ErrorMessage"/> carries a
    /// Chinese, user-presentable reason (offline, timeout, API refusal, unparsable payload).
    /// </summary>
    Failed,

    /// <summary>The running version is the newest published release.</summary>
    UpToDate,

    /// <summary>A release newer than the running version was published.</summary>
    UpdateAvailable,
}

/// <summary>One GitHub release, reduced to what the update surface shows.</summary>
public sealed record ReleaseInfo
{
    public required string TagName { get; init; }

    public required SemanticVersion Version { get; init; }

    /// <summary>When GitHub published the release, or null when the API omitted it.</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>The release page to send the user to for the download.</summary>
    public required string PageUrl { get; init; }

    /// <summary>
    /// The files this release publishes, or an empty list when the API carried none. The
    /// update flow picks its package from here by runtime identifier and install form.
    /// </summary>
    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];
}

/// <summary>
/// Result of <see cref="IUpdateService.CheckForUpdateAsync"/>. Exactly one of
/// <see cref="Latest"/> / <see cref="ErrorMessage"/> is meaningful, depending on
/// <see cref="Status"/>.
/// </summary>
public sealed record UpdateCheckResult
{
    public required UpdateCheckStatus Status { get; init; }

    /// <summary>The newest release; set only when <see cref="Status"/> is <see cref="UpdateCheckStatus.UpdateAvailable"/>.</summary>
    public ReleaseInfo? Latest { get; init; }

    /// <summary>A Chinese, user-presentable failure reason; set only when <see cref="Status"/> is <see cref="UpdateCheckStatus.Failed"/>.</summary>
    public string? ErrorMessage { get; init; }

    public static UpdateCheckResult Available(ReleaseInfo latest)
        => new() { Status = UpdateCheckStatus.UpdateAvailable, Latest = latest };

    public static UpdateCheckResult UpToDate()
        => new() { Status = UpdateCheckStatus.UpToDate };

    public static UpdateCheckResult Failed(string errorMessage)
        => new() { Status = UpdateCheckStatus.Failed, ErrorMessage = errorMessage };
}

/// <summary>
/// Checks the GitHub releases of this repository for a version newer than the running one.
/// Interface-based so the settings surface and its tests can run without the network.
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// The running version as it should be displayed, e.g. "0.0.1-beta.3-preview". Read from
    /// the assembly's informational version, so a dev build and a packaged build differ.
    /// </summary>
    string CurrentVersionText { get; }

    /// <summary>
    /// Queries the releases list and compares the newest published release with the running
    /// version. Network, timeout and payload failures come back as
    /// <see cref="UpdateCheckStatus.Failed"/> with a Chinese message rather than as exceptions;
    /// only a cancelled <paramref name="cancellationToken"/> throws.
    /// </summary>
    Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default);
}
