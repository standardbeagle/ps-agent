# Demo recordings

`ps-agent-acp.gif` / `.mp4` — `Invoke-Acp` driving a live `opencode acp` session: the prompt typed
into the viewer's prompt line, thought / tool / answer rows streaming in, a row expanded, then quit.

## Recording

VHS needs a **real PTY**. On Windows that means recording from WSL and letting the tape launch
`pwsh.exe` through interop — the console that process gets is genuine (`IsInputRedirected` and
`IsOutputRedirected` are both false), so the interactive viewer runs rather than falling back to
the headless pipeline.

```bash
wsl -d Ubuntu-24.04
sudo apt install ttyd ffmpeg
curl -fsSL https://github.com/charmbracelet/vhs/releases/latest/download/vhs_0.11.0_Linux_x86_64.tar.gz \
  | tar xz -C /tmp && install /tmp/vhs_*/vhs ~/.local/bin/

cd /mnt/c/.../ps-agent
docs/demo/record.sh                 # every tape
docs/demo/record.sh acp-runthrough  # one
```

`frames.sh` pulls stills out of a recording, which is how you check a demo without watching 70
seconds of it:

```bash
docs/demo/frames.sh docs/demo/ps-agent-acp.mp4 24 58
```

## Things that fail silently

Each of these produced a plausible-looking result rather than an error, so they are worth knowing
before editing a tape.

- **Quote every path in an `Output` line.** VHS's parser splits an unquoted one and reports
  `Invalid command: <basename>`.
- **Never put `$` in a `Type` line.** The text is typed into **bash** first, so `$env:TEMP` is
  expanded away before pwsh ever sees it, leaving `:TEMP`. Use literal Windows paths.
- **Set the working directory explicitly.** Launched from WSL, `pwsh.exe` starts on a
  `\\wsl.localhost\...` UNC path, which is not a usable workspace root.
- **Don't assume the output file is named after the tape.** `record.sh` reads the `Output` lines
  out of the tape, because guessing wrong copies nothing while still reporting success — which is
  how the first recording appeared to vanish.
- **Recordings are the only place the glyph set gets stress-tested.** The first take rendered `●`,
  `⚙`, `↑↓` and the prompt caret as `?` while `›`, `·` and `—` survived — the signature of a
  console sitting on **CP1252**, not a missing font. The viewer now forces UTF-8 for the duration
  of the session (`ConsoleTerminal.BeginSession`). If markers come back as `?`, suspect the output
  encoding, not the font.

## Machine-specific paths

`acp-runthrough.tape` hard-codes two absolute paths — the built module and `workspace/` in this
repo. Adjust both for your checkout. `workspace/greet.ps1` is committed so the demo has something
real (and wrong) to read: its loop uses `-le` where it should use `-lt`.
