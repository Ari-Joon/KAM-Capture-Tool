# Security

## What this application can reach

KAM Capture Tool reads the screen, reads audio devices while recording, writes
files, and asks GitHub whether there is a newer version. That is the whole list.

- **One network request, and it can be switched off.** A few seconds after it
  starts and every six hours after that, it asks
  `api.github.com/repos/Ari-Joon/KAM-Capture-Tool/releases/latest` which
  version is newest. The request carries the tool's version in its user agent
  and nothing else — no account, no identifier, nothing about your captures.
  GitHub sees your IP address, as it would for any web request. Settings,
  under Updates, turns it off.
- **Downloads only after a yes.** Update now fetches `KamCapture.exe` from that
  release, and runs it only if its SHA-256 matches the digest GitHub publishes
  and the file says it is the version the release says. Anything else is
  deleted and nothing changes.
- **No telemetry and no account.** Nothing it captures can leave the machine
  through it.
- **No elevation.** It installs per-user, runs as the invoking user, and has no
  service, driver or scheduled task.
- **No kernel component.** Capture is done with documented user-mode APIs.

## What it writes

| Path | Contents |
|---|---|
| `%APPDATA%\KAM Capture Tool\settings.json` | Settings |
| `%APPDATA%\KAM Capture Tool\kam-capture.log` | A rolling log, trimmed at 512 KB |
| `%TEMP%\KAM Capture Tool\Updates\` | A downloaded update, until it is installed |
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

**An update is trusted as far as GitHub is.** The checksum proves the download
is the file that was released — not corrupted, not swapped in transit — but not
who released it. If the GitHub account publishing releases were compromised, a
malicious release would pass. Code signing is what closes that, and this is not
signed. Switch the check off if that matters to you, and update by hand after
reading the release.

**`--uninstall` deletes the install folder.** If you installed to a folder you
were also using for something else, it takes that with it. The default location
is a folder of its own for this reason.

## Reporting a problem

Open an issue at
<https://github.com/Ari-Joon/KAM-Capture-Tool/issues>. For anything you would
rather not post publicly, say so in the issue without the detail and we will
find another channel.

This is a desktop utility with no server, no accounts and one outbound request,
so the realistic risk is a local one, or a bad release: a crafted image, a malformed settings file, or
a path handled carelessly. Those are worth reporting.
