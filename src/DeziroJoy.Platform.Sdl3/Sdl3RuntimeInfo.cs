namespace DeziroJoy.Platform.Sdl3;

/// <summary>
/// Version of the linked SDL3 runtime, as reported by <c>SDL_GetVersion</c> (packed
/// major/minor/micro) and <c>SDL_GetRevision</c> (source hash). The linked library, not the
/// version the bindings were generated from, is what actually enumerates and reads devices,
/// so the probe reports this pair verbatim: a support claim is only ever valid for the
/// runtime it was measured on.
/// </summary>
/// <param name="Version">
/// Packed version number: major * 1000000 + minor * 1000 + micro, e.g. 30420 for 3.4.20.
/// </param>
/// <param name="Revision">Arbitrary source revision string; empty when not built from git.</param>
public sealed record Sdl3RuntimeInfo(int Version, string Revision)
{
    /// <summary>The linked version as "major.minor.micro", e.g. "3.4.20".</summary>
    public string VersionText => $"{(uint)Version / 1000000}.{(uint)Version / 1000 % 1000}.{(uint)Version % 1000}";
}
