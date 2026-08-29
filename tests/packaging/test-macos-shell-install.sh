#!/bin/sh
set -eu

if [ "$#" -ne 1 ]; then
  echo "Usage: test-macos-shell-install.sh publish-directory" >&2
  exit 2
fi

repository=$(cd "$(dirname "$0")/../.." && pwd)
publish_root=$(cd "$1" && pwd)
temporary_root=$(mktemp -d "${TMPDIR:-/tmp}/vrcli-shell-test.XXXXXX")
cleanup() {
  rm -rf "$temporary_root"
}
trap cleanup EXIT HUP INT TERM

payload="${temporary_root}/payload"
archive="${temporary_root}/VRCLI-test.tar.gz"
prefix="${temporary_root}/prefix"
mkdir -p "$payload"
cp -R "${publish_root}/." "$payload/"
cp "${repository}/scripts/install-vrcli.sh" "$payload/"
cp "${repository}/scripts/uninstall-vrcli.sh" "$payload/"
tar -C "$payload" -czf "$archive" .
hash=$(shasum -a 256 "$archive" | awk '{print $1}')

sh "${repository}/scripts/install-vrcli.sh" \
  --archive "$archive" \
  --sha256 "$hash" \
  --prefix "$prefix" \
  --no-path-update
"${prefix}/bin/vrcli" --help >/dev/null

sh "${prefix}/lib/vrcli/uninstall-vrcli.sh" \
  --prefix "$prefix" \
  --no-path-update
[ ! -e "${prefix}/bin/vrcli" ]
[ ! -d "${prefix}/lib/vrcli" ]
