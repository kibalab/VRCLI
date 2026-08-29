#!/bin/sh
set -eu

if [ "$#" -lt 4 ] || [ "$#" -gt 5 ]; then
  echo "Usage: build-macos-package.sh version runtime publish-directory output-directory [install-prefix]" >&2
  exit 2
fi

version=$1
runtime=$2
publish_directory=$3
output_directory=$4
install_prefix=${5:-/usr/local}

case "$version" in
  [0-9]*.[0-9]*.[0-9]*) ;;
  *) echo "Invalid version: $version" >&2; exit 2 ;;
esac
case "$runtime" in
  osx-arm64|osx-x64) ;;
  *) echo "Unsupported runtime: $runtime" >&2; exit 2 ;;
esac
case "$install_prefix" in
  /*) ;;
  *) echo 'The install prefix must be an absolute path.' >&2; exit 2 ;;
esac
if [ "$install_prefix" = / ]; then
  echo 'The install prefix cannot be the filesystem root.' >&2
  exit 2
fi

repository=$(cd "$(dirname "$0")/.." && pwd)
publish_root=$(cd "$publish_directory" && pwd)
mkdir -p "$output_directory"
output_root=$(cd "$output_directory" && pwd)
temporary_root=$(mktemp -d "${TMPDIR:-/tmp}/vrcli-pkg.XXXXXX")
cleanup() {
  rm -rf "$temporary_root"
}
trap cleanup EXIT HUP INT TERM

payload="${temporary_root}/payload"
relative_prefix=${install_prefix#/}
install_root="${payload}/${relative_prefix}/lib/vrcli"
command_root="${payload}/${relative_prefix}/bin"
mkdir -p "$install_root" "$command_root"
cp -R "${publish_root}/." "$install_root/"
cp "${repository}/scripts/uninstall-vrcli.sh" "$install_root/"
chmod +x "$install_root/VRCLI" "$install_root/uninstall-vrcli.sh"
ln -s ../lib/vrcli/VRCLI "$command_root/vrcli"

package="${output_root}/VRCLI-${version}-${runtime}.pkg"
pkgbuild \
  --root "$payload" \
  --identifier com.kibalab.vrcli \
  --version "$version" \
  --install-location / \
  --ownership recommended \
  "$package"

pkgutil --check-signature "$package" >/dev/null 2>&1 || true
echo "$package"
