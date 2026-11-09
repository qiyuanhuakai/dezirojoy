#!/usr/bin/env bash
# Builds the DeziroJoy .deb from a self-contained publish output.
#
# Usage:
#   tools/Packaging/linux/build-deb.sh <version> <rid> <publish-dir> <output-dir>
#
# Payload layout:
#   /opt/dezirojoy/                        the complete self-contained publish output
#   /opt/dezirojoy/dezirojoy.ico           application icon, referenced by the desktop entry
#   /usr/share/applications/dezirojoy.desktop
#
# The package deliberately ships no maintainer scripts, no systemd units, no cron jobs
# and no udev rules: login autostart and the elevated resident daemon stay opt-in
# features of the app itself (src/DeziroJoy.App/Program.cs), and the user's
# configuration under ~/.config/DeziroJoy is never touched.
#
# Depends lists the native libraries the shipped binaries actually load, plus the
# runtime's own: the X11 client stack, GLX and fontconfig for the Avalonia/Skia UI,
# libdbus for AT-SPI and the portal file picker, GTK for the file-picker fallback
# (Avalonia 12 defaults to the D-Bus portal, GTK when it is unavailable), and the
# C/C++ runtime pieces coreclr links (libstdc++6, libgcc1).
#
# ICU: DeziroJoy does not set InvariantGlobalization (only tools/ZipPack does), so
# the .NET runtime dlopens system libicuuc/libicui18n at startup and FailFasts when
# neither is present - a package whose other dependencies all resolve would still
# install cleanly and then refuse to start on a minimal system. The versioned
# alternatives cover the supported Debian/Ubuntu releases (jammy 70, bookworm 72,
# noble 74, trixie 76, sid 78); append a release's version when it appears. The
# runtime probes ICU from its build version + 30 downwards, so newer entries are
# safe, and each libicuXX package ships libicuuc, libicui18n and libicudata.
#
# Older names (libgtk-3-0, libgcc1) are kept where a newer distro renamed the real
# package with a Provides.

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
  linux-x64)   arch=amd64 ;;
  linux-arm64) arch=arm64 ;;
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

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
icon="$repo_root/src/DeziroJoy.App/Assets/dezirojoy.ico"
desktop="$repo_root/tools/Packaging/linux/dezirojoy.desktop"
for required in "$icon" "$desktop"; do
  if [ ! -f "$required" ]; then
    echo "required file missing: $required" >&2
    exit 2
  fi
done

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT

mkdir -p "$stage/DEBIAN" "$stage/opt/dezirojoy" "$stage/usr/share/applications"

# The complete publish output, PDBs included: crash reports need the symbols, and
# nothing is trimmed.
cp -a "$publish/." "$stage/opt/dezirojoy/"
chmod 0755 "$stage/opt/dezirojoy/DeziroJoy"
cp "$icon" "$stage/opt/dezirojoy/dezirojoy.ico"
install -m 0644 "$desktop" "$stage/usr/share/applications/dezirojoy.desktop"

installed_size=$(du -sk --exclude=DEBIAN "$stage" | cut -f1)

cat > "$stage/DEBIAN/control" <<CONTROL
Package: dezirojoy
Version: $version
Section: utils
Priority: optional
Architecture: $arch
Installed-Size: $installed_size
Maintainer: DeziroJoy <qiyuanhuikai@users.noreply.github.com>
Homepage: https://github.com/qiyuanhuikai/dezirojoy
Depends: libc6, libdbus-1-3, libfontconfig1, libgcc1, libgl1, libgtk-3-0, libice6, libsm6, libstdc++6, libx11-6, libxcursor1, libxext6, libxfixes3, libxi6, libxrandr2, libicu70 | libicu72 | libicu74 | libicu76 | libicu78
Description: gamepad to mouse/keyboard mapping tool
 DeziroJoy maps gamepad input to mouse and keyboard movement with per-device
 profiles, a radial deadzone, response curves and sub-pixel accumulation. The
 GUI is built with Avalonia; input is read through evdev and injected through
 uinput on Linux, XInput and SendInput on Windows.
CONTROL

artifact="DeziroJoy_${version}_${arch}.deb"
dpkg-deb --root-owner-group --build "$stage" "$outdir/$artifact"

if [ ! -s "$outdir/$artifact" ]; then
  echo "no deb produced at $outdir/$artifact" >&2
  exit 1
fi

echo "built $artifact"
dpkg-deb --info "$outdir/$artifact"
