#!/bin/sh
set -eu

if [ "$#" -ne 2 ]; then
  echo "Usage: test-homebrew-formula.sh publish-directory runtime" >&2
  exit 64
fi

repository=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
publish_directory=$(CDPATH= cd -- "$1" && pwd)
runtime=$2
temporary_root=$(mktemp -d "${TMPDIR:-/tmp}/vrcli-homebrew-test.XXXXXX")
archive="${temporary_root}/VRCLI-1.2.3-${runtime}.tar.gz"
formula="${temporary_root}/vrcli.rb"

cleanup() {
  HOMEBREW_NO_AUTO_UPDATE=1 brew uninstall --formula vrcli >/dev/null 2>&1 || true
  rm -rf "$temporary_root"
}
trap cleanup EXIT HUP INT TERM

tar -C "$publish_directory" -czf "$archive" .
checksum=$(shasum -a 256 "$archive" | awk '{print $1}')
archive_url="file://${archive}"

pwsh -NoLogo -NoProfile -File "${repository}/scripts/new-homebrew-formula.ps1" \
  -Version 1.2.3 \
  -Arm64Sha256 "$checksum" \
  -X64Sha256 "$checksum" \
  -Arm64Url "$archive_url" \
  -X64Url "$archive_url" \
  -OutputPath "$formula" >/dev/null

HOMEBREW_NO_AUTO_UPDATE=1 brew install --formula "$formula"
HOMEBREW_NO_AUTO_UPDATE=1 brew test vrcli
"$(brew --prefix)/bin/vrcli" --help >/dev/null
HOMEBREW_NO_AUTO_UPDATE=1 brew uninstall --formula vrcli

if [ -e "$(brew --prefix)/bin/vrcli" ]; then
  echo "Homebrew did not remove the vrcli command." >&2
  exit 1
fi
