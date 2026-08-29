#!/bin/sh
set -eu

version=latest
prefix=${HOME}/.local
archive_path=
expected_sha256=
update_path=1

usage() {
  echo "Usage: install-vrcli.sh [--version x.y.z] [--prefix directory]" >&2
  echo "       install-vrcli.sh --archive file --sha256 hash [--prefix directory]" >&2
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --version) version=$2; shift 2 ;;
    --prefix) prefix=$2; shift 2 ;;
    --archive) archive_path=$2; shift 2 ;;
    --sha256) expected_sha256=$2; shift 2 ;;
    --no-path-update) update_path=0; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage; exit 2 ;;
  esac
done

case "$version" in
  latest|[0-9]*.[0-9]*.[0-9]*) ;;
  *) echo "Invalid version: $version" >&2; exit 2 ;;
esac
case "$prefix" in
  ''|/|*'"'*|*':'*) echo "Unsafe install prefix: $prefix" >&2; exit 2 ;;
esac

case "$(uname -m)" in
  arm64) runtime=osx-arm64 ;;
  x86_64) runtime=osx-x64 ;;
  *) echo "Unsupported macOS architecture: $(uname -m)" >&2; exit 2 ;;
esac

temporary_root=$(mktemp -d "${TMPDIR:-/tmp}/vrcli-install.XXXXXX")
cleanup() {
  rm -rf "$temporary_root"
}
trap cleanup EXIT HUP INT TERM

if [ -z "$archive_path" ]; then
  if [ "$version" = latest ]; then
    version=$(curl -fsSL \
      -H 'Accept: application/vnd.github+json' \
      -H 'User-Agent: kibalab.VRCLI-installer' \
      https://api.github.com/repos/kibalab/VRCLI/releases/latest |
      sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"v\([^"]*\)".*/\1/p' |
      head -n 1)
    [ -n "$version" ] || { echo 'Unable to resolve the latest version.' >&2; exit 1; }
  fi

  archive_name="VRCLI-${version}-${runtime}.tar.gz"
  base_url="https://github.com/kibalab/VRCLI/releases/download/v${version}"
  archive_path="${temporary_root}/${archive_name}"
  checksum_path="${temporary_root}/SHA256SUMS.txt"
  curl -fL --retry 3 -A 'kibalab.VRCLI-installer' -o "$archive_path" "${base_url}/${archive_name}"
  curl -fL --retry 3 -A 'kibalab.VRCLI-installer' -o "$checksum_path" "${base_url}/SHA256SUMS.txt"
  expected_sha256=$(awk -v name="$archive_name" '$2 == name || $2 == "*" name { print $1; exit }' "$checksum_path")
  [ -n "$expected_sha256" ] || { echo "$archive_name is missing from SHA256SUMS.txt." >&2; exit 1; }
else
  [ -n "$expected_sha256" ] || { echo '--sha256 is required with --archive.' >&2; exit 2; }
  archive_path=$(cd "$(dirname "$archive_path")" && pwd)/$(basename "$archive_path")
fi

actual_sha256=$(shasum -a 256 "$archive_path" | awk '{print $1}')
if [ "$(printf '%s' "$actual_sha256" | tr '[:lower:]' '[:upper:]')" != \
     "$(printf '%s' "$expected_sha256" | tr '[:lower:]' '[:upper:]')" ]; then
  echo "Archive checksum mismatch." >&2
  exit 1
fi

expanded_root="${temporary_root}/expanded"
mkdir -p "$expanded_root"
tar -xzf "$archive_path" -C "$expanded_root"
[ -f "${expanded_root}/VRCLI" ] || { echo 'The archive does not contain VRCLI.' >&2; exit 1; }
[ -f "${expanded_root}/UnityBridge/package.json" ] || { echo 'The archive does not contain the Unity bridge.' >&2; exit 1; }

lib_parent="${prefix}/lib"
install_root="${lib_parent}/vrcli"
bin_dir="${prefix}/bin"
mkdir -p "$lib_parent" "$bin_dir"
staging=$(mktemp -d "${lib_parent}/.vrcli-staging.XXXXXX")
cp -R "${expanded_root}/." "$staging/"
chmod +x "${staging}/VRCLI"

backup="${lib_parent}/.vrcli-backup.$$"
if [ -e "$backup" ]; then
  echo "Backup path already exists: $backup" >&2
  exit 1
fi
if [ -d "$install_root" ]; then
  mv "$install_root" "$backup"
fi
if ! mv "$staging" "$install_root"; then
  [ ! -d "$backup" ] || mv "$backup" "$install_root"
  exit 1
fi
rm -rf "$backup"
ln -sfn "${install_root}/VRCLI" "${bin_dir}/vrcli"

if [ "$update_path" -eq 1 ]; then
  case "${SHELL:-}" in
    */zsh) profile=${ZDOTDIR:-$HOME}/.zprofile ;;
    */bash) profile=$HOME/.bash_profile ;;
    *) profile=$HOME/.profile ;;
  esac
  marker='# >>> VRCLI PATH >>>'
  if [ ! -f "$profile" ] || ! grep -Fq "$marker" "$profile"; then
    {
      printf '\n%s\n' "$marker"
      printf 'export PATH="%s:$PATH"\n' "$bin_dir"
      printf '%s\n' '# <<< VRCLI PATH <<<'
    } >> "$profile"
  fi
fi

echo "VRCLI ${version} installed in ${install_root}"
echo 'Open a new terminal, then run: vrcli --help'
