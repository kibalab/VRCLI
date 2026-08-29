#!/bin/sh
set -eu

if [ "$#" -ne 2 ]; then
  echo "Usage: test-macos-pkg-install.sh publish-directory runtime" >&2
  exit 2
fi

repository=$(cd "$(dirname "$0")/../.." && pwd)
publish_root=$(cd "$1" && pwd)
runtime=$2
temporary_root=$(mktemp -d "${TMPDIR:-/tmp}/vrcli-pkg-test.XXXXXX")
cleanup() {
  rm -rf "$temporary_root"
}
trap cleanup EXIT HUP INT TERM

install_prefix="${temporary_root}/prefix"
output_directory="${temporary_root}/packages"
package=$(sh "${repository}/scripts/build-macos-package.sh" \
  0.0.0 \
  "$runtime" \
  "$publish_root" \
  "$output_directory" \
  "$install_prefix" |
  tail -n 1)

sudo installer -pkg "$package" -target / >/dev/null
"${install_prefix}/bin/vrcli" --help >/dev/null
sudo sh "${install_prefix}/lib/vrcli/uninstall-vrcli.sh" \
  --prefix "$install_prefix" \
  --no-path-update
[ ! -L "${install_prefix}/bin/vrcli" ]
[ ! -d "${install_prefix}/lib/vrcli" ]
