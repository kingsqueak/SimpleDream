#!/usr/bin/env bash
# Check a Mac app bundle will run on a machine that isn't this one: nothing may
# load a library from outside the bundle and macOS (Homebrew, /usr/local, a home
# folder), or need a newer macOS than the bundle claims to support.
# Usage: scripts/check-mac-bundle.sh path/to/SimpleDream.app
#
# The usual cause: Homebrew's dotnet supplies its own runtime for a build for
# this machine's architecture, and that links Homebrew's brotli. Build with
# Microsoft's dotnet (as CI does).
set -euo pipefail
app="$1"
min=$(/usr/libexec/PlistBuddy -c 'Print :LSMinimumSystemVersion' "$app/Contents/Info.plist")
checked=0 bad=0
while IFS= read -r f; do
  file -b "$f" | grep -q 'Mach-O' || continue
  checked=$((checked + 1))
  id=$(otool -D "$f" | tail -n +2 | head -1)
  while IFS= read -r dep; do
    case "$dep" in
      /usr/lib/*|/System/*|@rpath/*|@loader_path/*|@executable_path/*) ;;
      "$id") ;;   # the library's own name, not a load
      *) echo "BAD load: ${f#"$app"/} -> $dep"; bad=$((bad + 1)) ;;
    esac
  done < <(otool -L "$f" | grep '^[[:space:]]' | awk '{print $1}' | sort -u)
  while IFS= read -r need; do
    [ "$(printf '%s\n%s\n' "$need" "$min" | sort -V | tail -1)" = "$min" ] && continue
    echo "BAD macOS: ${f#"$app"/} needs $need, bundle says $min"; bad=$((bad + 1))
  done < <(otool -l "$f" | awk '/LC_BUILD_VERSION/{b=1} b&&/minos/{print $2; b=0}' | sort -u)
done < <(find "$app" -type f)
echo "$checked binaries checked, $bad problems"
[ "$bad" -eq 0 ]
