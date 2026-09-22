# Setup and reference

## Installing

Download `KamCapture.exe` from the
[Releases](https://github.com/Ari-Joon/KAM-Capture-Tool/releases) page and run it.

The file is the whole application: a self-contained build with the .NET runtime
inside it, so there is nothing to install first. On first run it asks where it
should live.

| Choice | What happens |
|---|---|
| **Install** | Copies itself to the folder you pick — `%LOCALAPPDATA%\Programs\KAM Capture Tool` by default — adds the shortcuts you asked for, and registers an entry in Add or Remove Programs. Per-user, so it never asks for an administrator. |
| **Just run it, don't install** | Runs from wherever it is and stops asking. Settings still live in `%APPDATA%\KAM Capture Tool`. |

Windows SmartScreen will warn the first time, because the executable is not code
signed. *More info* then *Run anyway*.

### Command line

| Argument | Effect |
|---|---|
| `--tray` | Start hidden in the notification area |
| `--portable` | Skip the install prompt for this run |
| `--capture=Region` | Take a capture immediately (`Region`, `Window`, `Monitor`, `FullScreen`) |
| `--update` | Check GitHub and, if there is a newer version, install it and restart |
| `--apply-update` | Used by the updater: install this copy over the running one, then start it |
| `--install-silent` | Install with no interface. Over an existing install it updates it in place, keeping its folder, the shortcuts still there, and start-with-Windows; otherwise the default folder with both shortcuts |
| `--uninstall` | Remove shortcuts, registry entries and the install folder |
| `--uninstall --quiet` | The same without the confirmation |
| `--no-tray` | Run without the notification-area icon |
| `--selftest=<path>` | Render every annotation type to a PNG and exit (used by CI) |
| `--foldertest` | Check the OneDrive rules in a scratch folder; touches nothing real |
| `--lifecycletest` | Check windows come back after a capture; opens windows briefly |
| `--ghosttest` | Measure how much of a hidden window leaks into a grab; flashes a window |
| `--savetest` | Walk the Save path end to end and report where it breaks |
| `--installtest` | Check install and update against a scratch registry key and folders; touches nothing real |
| `--updatetest` | Check version comparison, reading a release, checksum refusal and the program swap; no network |
| `--rectest=<file>,<seconds>` | Record a small region headlessly and check the frame count |
| `--docshots=<dir>` | Render the windows to PNGs offscreen, for documentation |

The checks run alongside a copy that is already in the tray. Setup commands
(`--install-silent`, `--uninstall`, `--apply-update`) ask a running copy to close first, since an
installed executable cannot be replaced or removed while it runs.

### Uninstalling

Add or Remove Programs, or:

```powershell
& "$env:LOCALAPPDATA\Programs\KAM Capture Tool\KamCapture.exe" --uninstall
```

Captures and recordings are never touched. Settings stay in
`%APPDATA%\KAM Capture Tool` in case you reinstall; delete that folder to remove
them too.

## Updates

The circular arrow at the top right of the home window checks GitHub for a newer
release. The tool also checks by itself, a few seconds after starting and every
six hours. When there is one:

| Where | What you see |
|---|---|
| Home window | The arrow becomes a gold **Update to x.y.z** button, and a bar offers **What's new**, **Not now** and **Update now** |
| Tray | A notice, once per version, and **Update to x.y.z…** at the top of the menu |

**Update now** downloads `KamCapture.exe` from the release into
`%TEMP%\KAM Capture Tool\Updates`, checks its SHA-256 against the digest GitHub
publishes, checks the file says it is the version the release says, and then
starts it with `--apply-update`. That copy asks the running one to close,
installs itself over it — same folder, same shortcuts, same startup choice — and
starts. Updating closes any open annotator, so it asks first; during a recording
it asks you to stop the recording first.

**Not now** hides that version until a newer one is published. The button stays
gold.

A copy that was never installed shows **Get the update**, which opens the release
page instead. Opening a newer download by hand while the tool is running offers
the update as well, and closes the running copy only once you agree.

The automatic check can be switched off in Settings, under Updates. Clicking the
arrow still checks.

## Recording prerequisites

Video recording shells out to ffmpeg for H.264 encoding. Everything else works
without it.

```powershell
winget install Gyan.FFmpeg
```

KAM finds it on `PATH`, in the WinGet package folders, next to its own
executable, or wherever you point it in Settings.

## Files it writes

| Path | Contents |
|---|---|
| `%APPDATA%\KAM Capture Tool\settings.json` | Every setting |
| `%APPDATA%\KAM Capture Tool\kam-capture.log` | A short rolling log, trimmed at 512 KB |
| `%TEMP%\KAM Capture Tool\Updates` | A downloaded update, deleted once it is installed |
| `Pictures\KAM Capture Tool\Screenshots` | Saved captures, by default |
| `Videos\KAM Capture Tool\Recordings` | Saved recordings, by default |

Nothing is written anywhere else. The only thing sent anywhere is the update
check, described above.

Closing the last annotator brings the home window back, so the next capture is
always one click away.

Captures are kept on local disk on purpose. Windows' known-folder move often
repoints Pictures into OneDrive, which would upload every screenshot you take;
KAM steps around a redirected folder and uses the real one in your profile. If
you would rather they synced, point it at a OneDrive folder in Settings and it
will ask you to confirm.

## Global shortcuts

Work anywhere in Windows while KAM Capture Tool is running. Change them in
Settings; if another program already owns a combination, KAM says so instead of
silently failing.

| | |
|---|---|
| `Ctrl+Shift+S` | Capture a region |
| `Ctrl+Shift+W` | Capture a window |
| `Ctrl+Shift+F` | Capture everything |
| `Ctrl+Shift+R` | Start or stop recording |

## While selecting

| | |
|---|---|
| Drag | Draw a selection |
| Drag an edge or corner | Adjust it — the selection stays live until you commit |
| Drag inside it | Move it |
| Arrow keys | Nudge by one pixel, `Shift` for ten |
| `Ctrl` + arrows | Resize by one pixel |
| `Ctrl+A` | Select the whole screen |
| `W` | Switch to picking a window |
| `M` | Toggle the pixel magnifier |
| `Enter` or double-click | Annotate |
| `Ctrl+C` / `Ctrl+S` | Copy / save straight away |
| Right-click | Clear the selection |
| `Esc` | Cancel |

After releasing the mouse a small bar appears: **Annotate**, **Copy**, **Save**,
**Record** (records that exact region), **Cancel**.

## In the annotator

### Tools

| | | | |
|---|---|---|---|
| `V` | Select | `S` | Numbered marker |
| `H` | Pan | `D` | Symbols |
| `P` | Pencil | `X` | Redact |
| `K` | Highlighter | `C` | Crop |
| `L` | Line | `T` | Text |
| `A` | Arrow | `R` | Rectangle |
| `O` | Ellipse | | |

### Selecting and grouping

| | |
|---|---|
| Click an object | Select it |
| `Ctrl` or `Shift` click | Add to or remove from the selection |
| Drag on empty board | Marquee-select |
| Click the screenshot | Pick up the image layer, then drag to move it |
| Corner handles | Scale proportionally — `Shift` to scale freely |
| Edge handles | Scale one axis |
| `Ctrl+G` / `Ctrl+Shift+G` | Group / ungroup |
| `Ctrl+D` | Duplicate |
| `Delete` | Remove |
| Arrow keys | Nudge, `Shift` for ten |
| Right-click the board | Duplicate, delete, bring to front, send to back, select all, save as |

### View

| | |
|---|---|
| Wheel | Zoom around the pointer |
| Middle-drag, or hold `Space` and drag | Pan |
| `Ctrl+0` | Fit the board |
| `Ctrl+1` | 100% |

The board is always kept overlapping the window, and the image is always kept on
the board, so neither can be lost off the edge.

### Output

| | |
|---|---|
| `Ctrl+S` | Save a PNG to the captures folder |
| `Ctrl+Shift+C` | Copy the whole board to the clipboard |
| `Ctrl+N` or **New capture** | Take another capture — this annotator stays open, work and all |
| Export 1x–4x | Re-renders the annotations at that resolution |

Export scale is worth understanding: the screenshot is a bitmap and gets no
sharper, but text, arrows, markers and symbols are vectors and are re-rendered at
the chosen scale. On a small crop with a lot of writing around it, 2x or 3x is
the difference between readable and not.

## Numbered markers

Five independent sequences: **1 2 3**, **A B C**, **a b c**, **i ii iii**,
**I II III**. Each counts on separately, so a numbered list and a lettered list
can run side by side. *Restart* sets them all back to the beginning.

The intended use is to drop markers on the screenshot and write the matching
list in the margin:

> **1.** make this button bigger
> **A.** this row wraps badly
> **i.** redact this before sending

## Recording

Set the target and the audio before you start, or change the audio while it runs
— the control bar carries the same switches. Switching microphone mid-recording
does not interrupt the file: the mixer's output stream keeps running, so the
audio track stays continuous and in sync.

The control bar never appears in the recording. Neither does the selection
overlay while a recording is in progress.

| Setting | Notes |
|---|---|
| Frame rate | 15–60. 30 is a good default for software demos. |
| Quality | x264 CRF, 14 (near-lossless) to 30 (small). 20 is the default. |
| Encoder | Automatic uses libx264. Pick a hardware encoder if you have one. |
| Cursor | Optional, drawn into each frame. |

## Troubleshooting

**Captures look soft.** Check the scaling figure next to the title on the home
screen. If it says 100% on a high-DPI laptop, Windows is reporting something
unusual; the log will say what was detected.

**Recording will not start.** ffmpeg was not found. Settings shows where it
looked; *Find* locates it or lets you point at it.

**A shortcut does nothing.** Another program owns that combination. KAM shows a
notification listing the ones it could not register.

**Something went wrong.** `%APPDATA%\KAM Capture Tool\kam-capture.log` has the
detail, and it is plain text.
