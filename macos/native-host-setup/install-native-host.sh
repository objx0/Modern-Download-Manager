#!/usr/bin/env bash
set -euo pipefail

host_path="${1:?Usage: install-native-host.sh /absolute/path/to/ModernDownloadManager.NativeHost}"
host_path="$(python3 -c 'import os,sys; print(os.path.realpath(sys.argv[1]))' "$host_path")"
[[ -x "$host_path" ]] || { echo "Host is not executable: $host_path" >&2; exit 1; }

script_dir="$(cd "$(dirname "$0")" && pwd)"
chrome_dir="$HOME/Library/Application Support/Google/Chrome/NativeMessagingHosts"
firefox_dir="$HOME/Library/Application Support/Mozilla/NativeMessagingHosts"
mkdir -p "$chrome_dir" "$firefox_dir"
sed "s#REPLACE_WITH_ABSOLUTE_HOST_PATH#$host_path#" "$script_dir/com.moderndownloadmanager.host.json" > "$chrome_dir/com.moderndownloadmanager.host.json"
sed "s#REPLACE_WITH_ABSOLUTE_HOST_PATH#$host_path#" "$script_dir/com.moderndownloadmanager.host.firefox.json" > "$firefox_dir/com.moderndownloadmanager.host.json"
echo "Installed Chrome and Firefox native host manifests."
