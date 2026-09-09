#!/bin/bash
set -eo pipefail

output_dir="${1:?publish output directory is required}"
bridge="$output_dir/libremote-freerdp.dylib"
if [[ ! -f "$bridge" ]]; then
  echo "FreeRDP bridge not found: $bridge" >&2
  exit 1
fi

queue=("$bridge")
visited=()
index=0
while (( index < ${#queue[@]} )); do
  binary="${queue[$index]}"
  ((index += 1))
  already_visited=0
  for item in "${visited[@]}"; do
    [[ "$item" == "$binary" ]] && already_visited=1 && break
  done
  (( already_visited == 1 )) && continue
  visited+=("$binary")

  while IFS= read -r dependency; do
    [[ "$dependency" == /opt/homebrew/* ]] || continue
    name="$(basename "$dependency")"
    bundled="$output_dir/$name"
    if [[ ! -f "$bundled" ]]; then
      cp -L "$dependency" "$bundled"
      chmod u+w "$bundled"
      install_name_tool -id "@loader_path/$name" "$bundled"
      queue+=("$bundled")
    fi
    install_name_tool -change "$dependency" "@loader_path/$name" "$binary"
  done < <(otool -L "$binary" | tail -n +2 | awk '{ print $1 }')
done

for binary in "${visited[@]}"; do
  codesign --force --sign - "$binary" >/dev/null
done

echo "Bundled ${#visited[@]} native binaries into $output_dir"
