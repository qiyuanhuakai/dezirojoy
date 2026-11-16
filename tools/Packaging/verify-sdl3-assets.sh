#!/usr/bin/env bash
# Verifies that the SDL3 runtime assets the release requires are present.
#
# The SDL3 input backend (ROADMAP S1/S2) rides on two publish outputs of the
# binding's NuGet packages: a managed binding assembly and the native SDL3
# library for the target RID. `dotnet publish -r <rid>` flattens both into the
# publish root, and every release artefact is that publish output copied
# wholesale - the Inno [Files] section (tools/Packaging/win/DeziroJoy.iss),
# build-deb.sh, build-rpm.sh and build-appimage.sh all stage it as-is. So the
# one failure this script guards against is a publish output that does not
# carry SDL3 at all: a dropped PackageReference, or a binding whose native
# package does not cover the RID. That would otherwise ship silently and only
# surface as a missing input backend on a user machine.
#
# Usage:
#   verify-sdl3-assets.sh <rid> <dir>...
#       Checks a publish output, or an extracted package payload directory.
#       Each directory is searched flat, the way dotnet publish lays the
#       assets out; pass opt/dezirojoy for an extracted Linux payload.
#   verify-sdl3-assets.sh --listing <rid> < listing
#       Checks a package listing on stdin instead - `dpkg-deb --contents`,
#       `rpm2cpio ... | cpio -t`, or find output: one path per line, with or
#       without dpkg-deb's leading metadata fields.
#
# The expected names come from the binding packages the app references (today
# SDL3-CS plus the per-platform SDL3-CS.Linux / SDL3-CS.Windows native packs):
# a managed assembly whose name starts with SDL3 (SDL3-CS.dll) and, per RID,
# SDL3.dll on Windows or libSDL3.so[.version] on Linux. The matching is
# pattern-based on purpose so a different binding package keeps working, and
# this script is the single place that knows the names: the release workflow
# calls it for the publish output and inside the package validations, the build
# scripts call it on their input, and build-and-verify.ps1 keeps the same
# contract in PowerShell for the Windows installer legs.

set -euo pipefail

usage() {
  echo "usage: $0 [--listing] <rid> [dir ...]" >&2
  echo "  rid: win-x64 | win-arm64 | linux-x64 | linux-arm64" >&2
  echo "  without --listing: check each dir for the required asset files" >&2
  echo "  with --listing: read a package listing (paths, one per line) from stdin" >&2
}

mode=tree
rid=''
dirs=()

while [ "$#" -gt 0 ]; do
  case "$1" in
    --listing)
      mode=listing
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    --*)
      echo "unknown option: $1" >&2
      usage
      exit 2
      ;;
    *)
      if [ -z "$rid" ]; then
        rid="$1"
      else
        dirs+=("$1")
      fi
      shift
      ;;
  esac
done

case "$rid" in
  win-x64|win-arm64)
    native_kind=windows
    native_desc='SDL3.dll'
    ;;
  linux-x64|linux-arm64)
    native_kind=linux
    native_desc='libSDL3.so[.version]'
    ;;
  *)
    echo "unsupported rid: '$rid' (expected win-x64, win-arm64, linux-x64 or linux-arm64)" >&2
    usage
    exit 2
    ;;
esac

if [ "$mode" = listing ] && [ "${#dirs[@]}" -ne 0 ]; then
  echo "--listing takes no directories; the listing comes from stdin" >&2
  exit 2
fi
if [ "$mode" = tree ] && [ "${#dirs[@]}" -eq 0 ]; then
  echo "no directories to check" >&2
  usage
  exit 2
fi

fail=0

# --- tree mode: the asset files themselves -------------------------------------------
# find -iname keeps the match case-insensitive, the way Windows would spell
# SDL3.dll; on Linux the names are already exact. -L follows symlinks, so a
# symlinked directory argument (a symlinked publish output, or a native package
# that ships libSDL3.so as a link to its versioned name) is searched like the
# files it points at instead of reading as empty.
if [ "$mode" = tree ]; then
  for dir in "${dirs[@]}"; do
    if [ ! -d "$dir" ]; then
      echo "not a directory: $dir" >&2
      exit 2
    fi
  done

  native=()
  managed=()
  for dir in "${dirs[@]}"; do
    if [ "$native_kind" = windows ]; then
      # A managed SDL3 assembly is any SDL3*.dll that is not the Windows native
      # library of the same prefix; on Linux the native is a .so, so nothing is
      # excluded there and a binding whose managed assembly is literally
      # SDL3.dll still passes.
      while IFS= read -r found; do native+=("$found"); done \
        < <(find -L "$dir" -maxdepth 1 -type f -iname 'SDL3.dll' | sort)
      while IFS= read -r found; do managed+=("$found"); done \
        < <(find -L "$dir" -maxdepth 1 -type f -iname 'SDL3*.dll' ! -iname 'SDL3.dll' | sort)
    else
      while IFS= read -r found; do native+=("$found"); done \
        < <(find -L "$dir" -maxdepth 1 -type f -iname 'libSDL3.so*' | sort)
      while IFS= read -r found; do managed+=("$found"); done \
        < <(find -L "$dir" -maxdepth 1 -type f -iname 'SDL3*.dll' | sort)
    fi
  done

  if [ "${#native[@]}" -eq 0 ]; then
    echo "missing SDL3 native library ($native_desc) in: ${dirs[*]}" >&2
    fail=1
  else
    echo "SDL3 native library: ${native[*]}"
  fi
  if [ "${#managed[@]}" -eq 0 ]; then
    echo "missing SDL3 managed binding assembly (SDL3*.dll) in: ${dirs[*]}" >&2
    fail=1
  else
    echo "SDL3 managed binding assembly: ${managed[*]}"
  fi

# --- listing mode: a package content listing ------------------------------------------
# The patterns anchor on a path separator or the start of the line, so a
# directory component that merely contains "SDL3" cannot satisfy the check,
# and [^/]* keeps the managed pattern from matching across separators.
else
  lines="$(cat)"
  if [ -z "$lines" ]; then
    echo "empty listing on stdin" >&2
    exit 2
  fi

  if [ "$native_kind" = windows ]; then
    native_hits="$(printf '%s\n' "$lines" | grep -Ei '(^|/)SDL3\.dll$' || true)"
    managed_hits="$(printf '%s\n' "$lines" | grep -Ei '(^|/)SDL3[^/]*\.dll$' | grep -Eiv '(^|/)SDL3\.dll$' || true)"
  else
    native_hits="$(printf '%s\n' "$lines" | grep -Ei '(^|/)libSDL3\.so' || true)"
    managed_hits="$(printf '%s\n' "$lines" | grep -Ei '(^|/)SDL3[^/]*\.dll$' || true)"
  fi

  if [ -z "$native_hits" ]; then
    echo "missing SDL3 native library ($native_desc) in the listing" >&2
    fail=1
  else
    echo "SDL3 native library in listing:"
    printf '%s\n' "$native_hits" | sed 's/^/  /'
  fi
  if [ -z "$managed_hits" ]; then
    echo "missing SDL3 managed binding assembly (SDL3*.dll) in the listing" >&2
    fail=1
  else
    echo "SDL3 managed binding assembly in listing:"
    printf '%s\n' "$managed_hits" | sed 's/^/  /'
  fi
fi

if [ "$fail" -ne 0 ]; then
  echo "SDL3 asset verification FAILED for $rid" >&2
  exit 1
fi
echo "SDL3 assets verified for $rid"
