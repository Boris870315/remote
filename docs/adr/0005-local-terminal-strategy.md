# ADR 0005: Cross-platform local terminal strategy

- Status: Accepted
- Date: 2026-09-02

## Decision

Remote runs local Terminal sessions through `Porta.Pty` 2.2.2, isolated behind `LocalTerminalSession`. The package targets .NET 10, uses Windows ConPTY and a bundled native `forkpty` shim on macOS/Linux, and is MIT licensed.

The Infrastructure wrapper owns shell selection, working-directory validation, environment setup, resize, cancellation, process termination, and PTY handle disposal. View Only is checked before the writer stream receives input.

The current basic Avalonia text renderer incrementally decodes UTF-8 and removes ANSI CSI/OSC/DCS control sequences, including sequences split across network or PTY reads. This produces readable command output without rendering escape bytes as glyphs. A future advanced terminal renderer may implement the full VT cursor, color, alternate-screen, mouse, and selection model without changing the PTY/session boundary.

Configured shell paths must resolve to an existing file. The default is `%ComSpec%`/`cmd.exe` on Windows and the user's `$SHELL`, `/bin/zsh`, or `/bin/sh` on Unix platforms. Remote does not invoke a shell through a constructed command string, which avoids command-injection at process startup.
