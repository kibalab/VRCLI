#!/bin/sh
set -eu

prefix=${HOME}/.local
update_path=1

while [ "$#" -gt 0 ]; do
  case "$1" in
    --prefix) prefix=$2; shift 2 ;;
    --no-path-update) update_path=0; shift ;;
    -h|--help) echo "Usage: uninstall-vrcli.sh [--prefix directory]"; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

case "$prefix" in
  ''|/|*'"'*|*':'*) echo "Unsafe install prefix: $prefix" >&2; exit 2 ;;
esac

install_root="${prefix}/lib/vrcli"
command_path="${prefix}/bin/vrcli"
if [ -L "$command_path" ]; then
  link_target=$(readlink "$command_path")
  if [ "$link_target" = "${install_root}/VRCLI" ]; then
    rm "$command_path"
  fi
fi
if [ -d "$install_root" ]; then
  rm -rf "$install_root"
fi

if [ "$update_path" -eq 1 ]; then
  case "${SHELL:-}" in
    */zsh) profile=${ZDOTDIR:-$HOME}/.zprofile ;;
    */bash) profile=$HOME/.bash_profile ;;
    *) profile=$HOME/.profile ;;
  esac
  if [ -f "$profile" ]; then
    temporary_profile=$(mktemp "${TMPDIR:-/tmp}/vrcli-profile.XXXXXX")
    awk '
      $0 == "# >>> VRCLI PATH >>>" { removing = 1; next }
      $0 == "# <<< VRCLI PATH <<<" { removing = 0; next }
      !removing { print }
    ' "$profile" > "$temporary_profile"
    mv "$temporary_profile" "$profile"
  fi
fi

echo "VRCLI was removed from ${install_root}"
