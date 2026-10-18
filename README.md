# OpenClaw Workbench

A small Windows desktop shell that gives [OpenClaw](https://openclaw.ai) a real window, an explicit
gateway lifecycle, and a single source of truth for "should the gateway be running?".

It wraps the existing OpenClaw Control UI in WebView2 instead of re-implementing chat. The shell owns
start/stop/restart; a tiny supervisor persists user intent and recovers the gateway with a bounded budget.

## Why

Running an OpenClaw gateway on a desktop tends to grow multiple starters: a logon task, a resume hook,
a watchdog, IDE plugins, and a man page full of `openclaw gateway start`. They fight. When one of them
restarts a gateway the user just stopped, "stop" stops being true.

This project reduces that to one authority:

- **User intent is persisted** (`desired: running | stopped`) before any lifecycle action.
- **Stop is verified** by three independent facts: the target process exited, the port is free, and the
  stopped intent was written. Failure keeps the UI open and says so.
- **Recovery is bounded**: only when intent is `running`, only when no gateway process exists, at most
  three attempts (1 / 5 / 15 min), never by killing processes.
- **Opening the app is the start gesture.** Logon does not secretly start anything.

## Layout

```text
src/                 WinForms + WebView2 shell (C#, .NET 10)
  Core/              gateway controller, models, P1 test-harness helpers
  Ui/                custom chrome (rail, header, icon drawing)
scripts/
  jfctl.cjs          the supervisor / single control authority (Node, no dependencies)
  openclaw.cmd       shim: intercepts only gateway start|stop|restart issued by the shell
  jf.cmd             terminal entry point: jf status | start | stop | restart
  jf-ensure.vbs      logon task: writes stopped, starts nothing
  jf-guard.vbs       2-minute task: budgeted recovery
```

## Build

Requires the .NET 10 SDK and the WebView2 runtime.

```powershell
dotnet publish .\src\JFWorkbench.csproj -c Release -o .\out
```

## Install (Windows, per-user)

1. Publish to `%USERPROFILE%\.openclaw\jf-shell\app`.
2. Put `scripts\jfctl.cjs` in `%USERPROFILE%\.openclaw\jf-shell\bin\`, and `openclaw.cmd` in
   `%USERPROFILE%\.openclaw\jf-shell\` (the shell's working directory — that is what makes the shim local
   to the shell instead of hijacking your global PATH).
3. Register two per-user scheduled tasks, both passive (no failure-restart, ignore-new):
   - `OpenClaw Workbench Gateway` — at logon, runs `jf-ensure.vbs`
   - `JFlash Shell Guard` — every 2 minutes, runs `jf-guard.vbs`
4. Point a desktop shortcut at the published `JFWorkbench.exe`.

Nothing here writes to your OpenClaw config. The supervisor talks to the official CLI
(`openclaw gateway ...`) and to local HTTP health endpoints only.

## Terminal

```powershell
jf status
jf start
jf stop      # persists desired=stopped; the guard will not resurrect it
jf restart
```

> Prefer `jf stop` over `openclaw gateway stop`. The raw CLI stops the process but does not change the
> persisted intent, so the guard may legitimately bring it back.

## Status

Early but working on the author's machine. Sleep/wake behavior is not yet fully verified. Interfaces may
change. No code signing.

## License

MIT — see `LICENSE`.
