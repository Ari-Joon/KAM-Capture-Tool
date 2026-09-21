# Security

## What this application can reach

KAM Capture Tool reads the screen, reads audio devices while recording, and
writes files. That is the whole list.

- **No network code.** There is no HTTP client, no socket, no telemetry, no
  update check and no account. Nothing it captures can leave the machine through
  it.
- **No elevation.** It installs per-user, runs as the invoking user, and has no
  service, driver or scheduled task.
- **No kernel component.** Capture is done with documented user-mode APIs.

## What it writes

| Path | Contents |
|---|---|
| `%APPDATA%\KAM Capture Tool\settings.json` | Settings |
| `%APPDATA%\KAM Capture Tool\kam-capture.log` | A rolling log, trimmed at 512 KB |
| `%LOCALAPPDATA%\Programs\KAM Capture Tool\` | The executable, if installed |
| `HKCU\Software\KAM\Capture Tool` | Install path and version |
| `HKCU\...\CurrentVersion\Uninstall\KAMCaptureTool` | Add or Remove Programs entry |
| `HKCU\...\CurrentVersion\Run` | Only if you turn on "start with Windows" |
| Your captures and recordings folders | What you save |

The log records which capture modes ran and any errors. It does not record image
content, window titles or keystrokes.

## Things worth knowing

**Redaction is destructive, which is the point.** The mosaic is computed from the
underlying pixels and written into the exported image. The original pixels are not
carried in the export, so a redacted PNG cannot be un-redacted. The *unsaved*
document in the editor still holds the original underneath, so redact, then
export, then don't hand round the editor session.

**Captures contain whatever was on screen.** Including other people's windows,
notifications that arrived mid-capture, and anything in the background of a
recording. Check before sending.

**The executable is not code signed.** SmartScreen will warn on first run. Verify
the SHA-256 of a release against the checksum published with it, or build from
source.

**`--uninstall` deletes the install folder.** If you installed to a folder you
were also using for something else, it takes that with it. The default location
is a folder of its own for this reason.

## Reporting a problem

Open an issue at
<https://github.com/Ari-Joon/KAM-Capture-Tool/issues>. For anything you would
rather not post publicly, say so in the issue without the detail and we will
find another channel.

This is a desktop utility with no server, no accounts and no network surface, so
the realistic risk is a local one: a crafted image, a malformed settings file, or
a path handled carelessly. Those are worth reporting.
