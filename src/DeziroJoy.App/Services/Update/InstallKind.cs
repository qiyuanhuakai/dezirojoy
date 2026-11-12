namespace DeziroJoy.App.Services;

/// <summary>
/// How this copy of DeziroJoy was installed. The form decides what an automatic update can
/// honestly do: a package or an installer can be replaced in place, a single-file AppImage
/// replaces itself, and a sandbox or an unpacked copy has nothing to replace - those get
/// guidance and the release page instead of a pretend install.
/// </summary>
public enum InstallKind
{
    /// <summary>
    /// A Flatpak sandbox. No package database and no way to reach the host's polkit, so the
    /// update surface only points at <c>flatpak update</c>.
    /// </summary>
    Flatpak,

    /// <summary>A type-2 AppImage: one executable file that can rename itself aside.</summary>
    AppImage,

    /// <summary>The per-user Inno Setup install on Windows, identified by its uninstall key.</summary>
    WindowsInstall,

    /// <summary>The .deb installed into <c>/opt/dezirojoy</c>.</summary>
    Deb,

    /// <summary>
    /// The .rpm installed into <c>/opt/dezirojoy</c> - the same directory layout as the .deb,
    /// so only the package database tells the two apart.
    /// </summary>
    Rpm,

    /// <summary>Unpacked, a zip, or "dotnet run": nothing to update in place.</summary>
    Portable,
}

/// <summary>
/// The detected install form, together with the runtime identifier the release assets are
/// named for (a self-contained publish carries its RID).
/// </summary>
public sealed record InstallKindInfo(InstallKind Kind, string Rid)
{
    /// <summary>
    /// Whether a downloadable package exists for this form. False for Flatpak and Portable,
    /// which are handled by pointing the user at the release page instead.
    /// </summary>
    public bool HasUpdatePackage
        => Kind is InstallKind.WindowsInstall or InstallKind.Deb or InstallKind.Rpm or InstallKind.AppImage;
}
