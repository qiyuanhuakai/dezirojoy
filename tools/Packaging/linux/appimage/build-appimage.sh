#!/usr/bin/env bash
# Builds the DeziroJoy AppImage from a self-contained publish output.
#
# Usage:
#   tools/Packaging/linux/appimage/build-appimage.sh <version> <rid> <publish-dir> <output-dir>
#
# The AppDir that gets packed:
#   AppRun                     execs opt/dezirojoy/DeziroJoy with the caller's arguments
#   dezirojoy.desktop          the desktop entry (Exec=DeziroJoy, Icon=dezirojoy)
#   dezirojoy.png              the 256x256 icon frame; appimagetool links it as .DirIcon
#   opt/dezirojoy/             the complete self-contained publish output, plus the
#                              dezirojoy.ico and fix-device-access.sh the .deb also ships
#                              beside the binary
#
# appimagetool (continuous) is downloaded per run and invoked with
# --appimage-extract-and-run: the tool is itself a type-2 AppImage, and CI runners have
# no FUSE, so the runtime extracts it instead of mounting it. The flag is what keeps
# this script honest about where it can run. The tool only packs the AppDir - it never
# executes the payload - so an aarch64 image is built on an x64 host from a
# cross-published publish directory with ARCH=aarch64, the same cross-compilation
# convention the release workflow already uses for the linux-arm64 .deb. Running the
# packaged daemon, in contrast, is an x64-only check (an aarch64 ELF cannot execute
# here), which is why the arm64 leg is build-and-inspect only.
#
# The type-2 runtime sets $APPIMAGE (the image file) and $APPDIR (the mounted AppDir)
# before it execs AppRun; the app reads both to recognize an AppImage install, and an
# AppDir extracted by hand has neither, which reads as a portable install. $VERSION is
# exported so appimagetool stamps X-AppImage-Version into the desktop entry.
#
# No hicolor tree inside the AppDir: the icon travels as the root PNG that becomes
# .DirIcon, and nothing inside a squashfs consumes /usr/share/icons - desktop
# integration installs .DirIcon, not theme directories. The seven hicolor frames stay
# where their only consumer is, the .deb and .rpm icon-theme payload.
#
# The asset is named DeziroJoy-<version>-<arch>.AppImage with arch x86_64|aarch64 -
# the name the updater's asset selector looks for once it sees an AppImage install.

set -euo pipefail

if [ "$#" -ne 4 ]; then
  echo "usage: $0 <version> <rid> <publish-dir> <output-dir>" >&2
  exit 2
fi

version="$1"
rid="$2"
publish="$3"
outdir="$4"

case "$rid" in
  linux-x64)   arch=x86_64 ;;
  linux-arm64) arch=aarch64 ;;
  *)
    echo "unsupported rid: $rid (expected linux-x64 or linux-arm64)" >&2
    exit 2
    ;;
esac

if [ -z "$version" ]; then
  echo "version must not be empty" >&2
  exit 2
fi
if [ ! -d "$publish" ]; then
  echo "publish directory not found: $publish" >&2
  exit 2
fi

exe="$publish/DeziroJoy"
if [ ! -f "$exe" ]; then
  echo "DeziroJoy binary missing from publish output: $exe" >&2
  exit 2
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../../../.." && pwd)"
icon256="$repo_root/src/DeziroJoy.App/Assets/dezirojoy-256.png"
ico="$repo_root/src/DeziroJoy.App/Assets/dezirojoy.ico"
helper="$repo_root/tools/Packaging/linux/fix-device-access.sh"
apprun="$script_dir/AppRun"
desktop="$script_dir/dezirojoy.desktop"
for required in "$icon256" "$ico" "$helper" "$apprun" "$desktop"; do
  if [ ! -f "$required" ]; then
    echo "required file missing: $required" >&2
    exit 2
  fi
done

# appimagetool refuses to start without desktop-file-validate; fail here with the
# package to install rather than mid-build with the tool's one-line complaint.
if ! command -v desktop-file-validate >/dev/null 2>&1; then
  echo "desktop-file-validate not found (install desktop-file-utils); appimagetool requires it" >&2
  exit 2
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

appdir="$work/AppDir"
mkdir -p "$appdir/opt/dezirojoy"

# The complete publish output, PDBs included: crash reports need the symbols, and
# nothing is trimmed. The payload is byte-identical to the .deb's /opt/dezirojoy, so
# the three Linux formats differ only in their container.
cp -a "$publish/." "$appdir/opt/dezirojoy/"
chmod 0755 "$appdir/opt/dezirojoy/DeziroJoy"
cp "$ico" "$appdir/opt/dezirojoy/dezirojoy.ico"

# The device-access fix helper, shipped executable but inert: it writes the opt-in
# udev rules only when the app runs it through pkexec on the user's explicit request,
# and it registers nothing by itself. It rides along for payload parity with the
# .deb/rpm layout; the app currently resolves the helper at the absolute deb path,
# so on an AppImage the one-click fix degrades to the manual command until that
# lookup learns about this install kind.
install -m 0755 "$helper" "$appdir/opt/dezirojoy/fix-device-access.sh"

install -m 0755 "$apprun" "$appdir/AppRun"

# The desktop entry must sit at the AppDir root - appimagetool only looks there - and
# its Icon=dezirojoy resolves against the PNG below, which the tool links as .DirIcon.
# No hicolor payload: see the header.
install -m 0644 "$desktop" "$appdir/dezirojoy.desktop"
install -m 0644 "$icon256" "$appdir/dezirojoy.png"

# The publish output carries 0744 on many files, which appimagetool's permission check
# flags file by file: an AppImage is extracted and run by users other than the one who
# packaged it, so every payload file must be world-readable and everything carrying an
# execute bit must carry it for everyone. X adds execute only where an execute bit
# already exists, so plain data files stay non-executable.
chmod -R a+rX "$appdir"

# appimagetool continuous, fetched per run into the temp workdir the trap cleans: a
# pinned download would need a place to live in the repo, and CI runners have no
# appimagetool preinstalled. The tool pulls the type-2 runtime from GitHub while
# packing, so the build needs network access.
tool="$work/appimagetool"
curl -fsSL --retry 3 -o "$tool" \
  "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"
chmod 0755 "$tool"

mkdir -p "$outdir"
artifact="DeziroJoy-${version}-${arch}.AppImage"

# ARCH selects the image architecture (the tool otherwise guesses it from a bundled
# .so and refuses on ambiguity); VERSION is stamped into the desktop entry as
# X-AppImage-Version; TMPDIR pins the type-2 runtime's extraction directory - the
# continuous runtime unpacks itself into $TMPDIR/appimage_extracted_<md5> and does
# not remove it afterwards, so pointing it at the workdir routes that leftover into
# the EXIT trap instead of stranding it in /tmp on every build; -n skips the
# AppStream check, which needs appdata.xml the project does not ship. The explicit
# destination is what pins the asset name.
ARCH="$arch" VERSION="$version" TMPDIR="$work" \
  "$tool" --appimage-extract-and-run -n "$appdir" "$outdir/$artifact"

if [ ! -s "$outdir/$artifact" ]; then
  echo "no AppImage produced at $outdir/$artifact" >&2
  exit 1
fi

echo "built $artifact"
