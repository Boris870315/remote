# Remote FreeRDP bridge

This bridge is the stable C ABI between the Avalonia client and FreeRDP 3 on
macOS. It deliberately hides FreeRDP structures from managed code so a native
library update cannot silently change .NET structure offsets.

Development build:

```sh
clang -dynamiclib -O2 -fvisibility=hidden \
  -I/opt/homebrew/include/freerdp3 -I/opt/homebrew/include/winpr3 \
  remote_freerdp_bridge.c -L/opt/homebrew/lib -lfreerdp3 -lwinpr3 \
  -Wl,-rpath,/opt/homebrew/lib -o libremote-freerdp.dylib
```

Release builds must compile both arm64 and x86_64 slices and package FreeRDP,
WinPR, their transitive dylibs, license notices, hardened-runtime signing, and
notarization metadata inside the app bundle.
## macOS runtime packaging

`Remote.Desktop` builds `libremote-freerdp.dylib` automatically on macOS when
Homebrew FreeRDP is installed. During `dotnet publish`,
`bundle-freerdp-runtime.sh` copies the complete non-system dylib dependency
closure into the publish directory, rewrites install names to `@loader_path`,
and applies ad-hoc signatures. The published output therefore does not depend
on `/opt/homebrew` being present on the target Mac.
