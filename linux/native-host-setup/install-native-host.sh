#!/usr/bin/env bash
set -euo pipefail

host_path="${1:?Usage: install-native-host.sh /absolute/path/to/ModernDownloadManager.NativeHost}"
host_path="$(readlink -f "$host_path")"
[[ -x "$host_path" ]] || { echo "Host is not executable: $host_path" >&2; exit 1; }

manifest_dir="${XDG_CONFIG_HOME:-$HOME/.config}/google-chrome/NativeMessagingHosts"
mkdir -p "$manifest_dir"
manifest="$manifest_dir/com.moderndownloadmanager.host.json"
sed "s#REPLACE_WITH_ABSOLUTE_HOST_PATH#$host_path#" "$(dirname "$0")/com.moderndownloadmanager.host.json" > "$manifest"
echo "Installed Chrome native host manifest: $manifest"

firefox_manifest_dir="${XDG_CONFIG_HOME:-$HOME/.config}/mozilla/native-messaging-hosts"
mkdir -p "$firefox_manifest_dir"
firefox_manifest="$firefox_manifest_dir/com.moderndownloadmanager.host.json"
sed "s#REPLACE_WITH_ABSOLUTE_HOST_PATH#$host_path#" "$(dirname "$0")/com.moderndownloadmanager.host.firefox.json" > "$firefox_manifest"
echo "Installed Firefox native host manifest: $firefox_manifest"
