#!/usr/bin/env bash
# Builds the macOS app (Mac Catalyst, universal arm64 + x86_64) and packs it into a .dmg
# with the usual drag-to-Applications layout.
#
#   src/BuildMacDmg.sh [--version 1.2.3] [--arch universal|arm64|x64] [--out DIR]
#                      [--sign "Developer ID Application: Name (TEAMID)"] [--notarize PROFILE]
#                      [--protoc-dir DIR]
#
# Without --sign the app is ad-hoc signed: it runs on the machine that built it, and on other
# Macs only after the user clears the quarantine flag (right-click > Open, or
#   xattr -dr com.apple.quarantine "/Applications/Azure Design Studio.app").
# --sign uses a Developer ID certificate from the login keychain and the hardened runtime;
# --notarize then submits the .dmg with `xcrun notarytool` (PROFILE = a keychain profile made
# with `xcrun notarytool store-credentials`) and staples the ticket.
#
# Needs: macOS, Xcode, the .NET SDK from global.json and `dotnet workload install maui-maccatalyst`.
# The repository's Grpc.Tools ships an Intel-only protoc: on Apple Silicon it runs under Rosetta
# (`softwareupdate --install-rosetta`), or pass --protoc-dir with a native protoc and
# grpc_csharp_plugin.
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
project="$repo/src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj"
entitlements="$repo/src/AzureDesignStudio.Desktop/Platforms/MacCatalyst/Entitlements.plist"
app_name="Azure Design Studio"

version=""
arch="universal"
out="$repo/artifacts"
sign=""
notarize=""
protoc_dir=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) version="$2"; shift 2 ;;
    --arch) arch="$2"; shift 2 ;;
    --out) out="$2"; shift 2 ;;
    --sign) sign="$2"; shift 2 ;;
    --notarize) notarize="$2"; shift 2 ;;
    --protoc-dir) protoc_dir="$2"; shift 2 ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

[[ "$(uname)" == "Darwin" ]] || { echo "Builds on macOS only." >&2; exit 1; }
[[ -n "$notarize" && -z "$sign" ]] && { echo "--notarize needs --sign (a Developer ID identity)." >&2; exit 2; }

# Release Mac Catalyst builds both architectures by default. A RID given on the command line is a
# global property that reaches every referenced project too, so only a single-arch build sets one.
rid_args=()
case "$arch" in
  universal) suffix="universal" ;;
  arm64) rid_args=("-p:RuntimeIdentifier=maccatalyst-arm64"); suffix="arm64" ;;
  x64) rid_args=("-p:RuntimeIdentifier=maccatalyst-x64"); suffix="x64" ;;
  *) echo "--arch must be universal, arm64 or x64." >&2; exit 2 ;;
esac

# Version: --version, else the major.minor in src/version.json plus the commit height.
if [[ -z "$version" ]]; then
  base="$(sed -n 's/.*"version": *"\([0-9.]*\)".*/\1/p' "$repo/src/version.json" | head -1)"
  height="$(git -C "$repo" rev-list --count HEAD 2>/dev/null || echo 0)"
  version="${base:-1.0}.$height"
fi
[[ "$version" =~ ^[0-9]+(\.[0-9]+){0,2}$ ]] || { echo "Version must be numeric (1, 1.2 or 1.2.3): $version" >&2; exit 2; }


protoc_args=()
if [[ -n "$protoc_dir" ]]; then
  protoc_args=("-p:Protobuf_ProtocFullPath=$protoc_dir/protoc" "-p:gRPC_PluginFullPath=$protoc_dir/grpc_csharp_plugin")
elif [[ "$(uname -m)" == "arm64" ]] && ! arch -x86_64 /usr/bin/true 2>/dev/null; then
  echo "Rosetta is not installed, and the repository's protoc is an Intel binary." >&2
  echo "Install it with:  softwareupdate --install-rosetta --agree-to-license" >&2
  echo "or pass --protoc-dir with native protoc and grpc_csharp_plugin binaries." >&2
  exit 1
fi

sign_args=()
if [[ -n "$sign" ]]; then
  sign_args=("-p:EnableCodeSigning=true" "-p:CodesignKey=$sign" "-p:UseHardenedRuntime=true")
fi

bin_dir="$repo/src/AzureDesignStudio.Desktop/bin/Release/net10.0-maccatalyst"
rm -rf "$bin_dir"
mkdir -p "$out"
echo "==> Building $app_name $version ($suffix)"
# build, not publish: for Mac Catalyst the build output is the finished .app (publish only wraps
# it in a .pkg), and publish would also run the referenced web client's publish pipeline.
# RunAOTCompilation=false: Release would WASM-AOT that web client, which the desktop app does not
# run (it hosts the same components in-process).
dotnet build "$project" -c Release -f net10.0-maccatalyst \
  -p:RunAOTCompilation=false \
  -p:ApplicationDisplayVersion="$version" \
  -p:ApplicationVersion="$version" \
  "${rid_args[@]+"${rid_args[@]}"}" "${protoc_args[@]+"${protoc_args[@]}"}" "${sign_args[@]+"${sign_args[@]}"}" --nologo

# Universal: bin/.../net10.0-maccatalyst/<app>.app; single-arch: one folder deeper, under the RID.
app="$(find "$bin_dir" -maxdepth 2 -name "$app_name.app" -type d | sort | head -1)"
[[ -d "$app" ]] || { echo "No $app_name.app was produced under $bin_dir." >&2; exit 1; }

if [[ -z "$sign" ]]; then
  echo "==> Ad-hoc signing (no Developer ID given)"
  codesign --force --deep --sign - --entitlements "$entitlements" "$app"
fi
codesign --verify --deep --strict "$app"

echo "==> Building the disk image"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
ditto "$app" "$stage/$app_name.app"
ln -s /Applications "$stage/Applications"

dmg="$out/AzureDesignStudio-$version-macos-$suffix.dmg"
rm -f "$dmg"
hdiutil create -volname "$app_name" -srcfolder "$stage" -fs HFS+ -format UDZO -imagekey zlib-level=9 -ov "$dmg" >/dev/null

if [[ -n "$sign" ]]; then
  codesign --force --sign "$sign" --timestamp "$dmg"
fi
if [[ -n "$notarize" ]]; then
  echo "==> Notarizing"
  xcrun notarytool submit "$dmg" --keychain-profile "$notarize" --wait
  xcrun stapler staple "$dmg"
fi

echo "==> $dmg ($(du -h "$dmg" | cut -f1))"
