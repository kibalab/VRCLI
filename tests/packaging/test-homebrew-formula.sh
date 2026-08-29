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

run_checked() {
  output_file="${temporary_root}/command-output.log"
  if "$@" >"$output_file" 2>&1; then
    cat "$output_file"
    return 0
  else
    status=$?
  fi

  cat "$output_file" >&2
  if [ -n "${GITHUB_ACTIONS:-}" ]; then
    annotation=$(tail -n 20 "$output_file" | sed 's/%/%25/g; s/\r/%0D/g' | awk 'BEGIN { ORS="%0A" } { print }')
    printf '::error title=Homebrew Formula validation failed::%s\n' "$annotation"
  fi
  return "$status"
}

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

run_checked env HOMEBREW_NO_AUTO_UPDATE=1 brew install --formula "$formula"
run_checked env HOMEBREW_NO_AUTO_UPDATE=1 brew test vrcli
run_checked "$(brew --prefix)/bin/vrcli" --help
run_checked env HOMEBREW_NO_AUTO_UPDATE=1 brew uninstall --formula vrcli

if [ -e "$(brew --prefix)/bin/vrcli" ]; then
  echo "Homebrew did not remove the vrcli command." >&2
  exit 1
fi
