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
| `--record` | Record video as the home window is set up, or stop the recording that is running |
| `--record-audio` | Record sound only, as the home window is set up, or stop the recording that is running |
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
| `--savetest` | Walk the Save path end to end and report where it breaks; the sample it writes is removed again |
| `--installtest` | Check install and update against a scratch registry key and folders; touches nothing real |
| `--updatetest` | Check version comparison, reading a release, checksum refusal and the program swap; no network |
| `--rectest=<file>,<seconds>[,<pause>]` | Record a small region headlessly and check the frame count; with a pause, check the paused stretch is left out |
| `--audiotest=<file>,<seconds>[,<pause>]` | Record sound only to `.mp3`, `.m4a` or `.wav`, switching system audio off and on mid-take, and check the length |
| `--docshots=<dir>` | Render the windows to PNGs offscreen, for documentation |
| `--retaketest[=<dir>]` | Retake then Stop an audio take through the real controller, and check both takes went to the Recycle Bin; shows the recording bar briefly, and removes its takes from the bin afterwards |
| `--mixtest[=<dir>]` | Play a quiet tone through each output from another process and record it while every microphone is switched; checks system audio is heard and unaffected. Audible |
| `--audiodiag` | List the default devices and which output each program is playing through; changes nothing |
| `--layoutcheck[=<dir>]` | Lay out every window offscreen, in the states that stretch it, and fail if anything is cut off; writes a PNG of each state |

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

## The home window

Three things across the top, and what each one needs underneath it.

| | What | Sound | The button |
|---|---|---|---|
| **Screenshot** | Region, Window or Full screen, and a delay | — | Take screenshot |
| **Video** | Region, Window or Full screen | System audio and the microphone, each with its device | Start recording |
| **Audio** | — | System audio and the microphone, each with its device | Start recording audio |

**System audio** is taken from **Every output** unless you pick one: whatever the
computer plays is recorded, whichever output it plays through, and an output that
appears during a take — headphones plugged in — is picked up within two seconds.
The microphone list says which device *Default* means right now, because it is
not always the one you would guess: a headset jack can be Windows' default
microphone while you talk into a USB one.

**Region** is dragged on the overlay, **Window** is a click on the window, and
**Full screen** is the display under the pointer, taken without a click.
Screenshot and Video each remember their own choice, so a habit of recording the
full screen does not change what a screenshot does. The sound choices are shared
by Video and Audio. While a recording runs, the button becomes **Save recording**.

Underneath the button is one line: what just happened — *Audio saved as
KAM-2026-09-24-10-15-22.mp3, 86.4 MB* — and otherwise the shortcut for what is
selected. Along the bottom, **Open Screenshots**, **Open Video** and **Open
Audio** each open their own folder, whatever you did last. Hover one to see
where it points.

The tray menu has the same things in the same words — *Screenshot a region*,
*Screenshot a window*, *Screenshot the full screen*, *Record video*, *Record
audio* — and the recording item reads *Save the … recording* while one is running.

## Updates

The circular arrow at the top right of the home window checks GitHub for a newer
release. The tool also checks by itself, once, a few seconds after starting, and
not again until it is started again; if Windows started it before the network
was up, that one check waits for the connection. When there is one:

| Where | What you see |
|---|---|
| Home window | The arrow becomes a gold **Update to x.y.z** button, and a bar offers **What's new**, **Not now** and **Update now** |
| Tray | A notice, once per version, and **Update to x.y.z…** at the top of the menu |

While it downloads, the gold button counts it up — *Downloading 42%* — and that
is the only progress shown; clicking it cancels the download.

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

Recording shells out to ffmpeg: H.264 for video, and MP3, AAC or plain PCM for
sound on its own. Everything else works without it.

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
| `Videos\KAM Capture Tool\Recordings` | Saved videos, by default |
| `Music\KAM Capture Tool\Audio` | Saved audio-only recordings, by default |

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
| `Ctrl+Shift+F` | Screenshot the full screen — the display the pointer is on |
| `Ctrl+Shift+R` | Record video, or save whatever is recording |

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
| `F12` | Save as — choose the name and the folder |
| Right-click | Clear the selection |
| `Esc` | Cancel |

After releasing the mouse a small bar appears: **Annotate**, **Copy**, **Save**,
**Save as…**, **Record** (records that exact region), and **STOP**, outlined in
red, which ends the selection and keeps nothing — as `Esc` does.

When the overlay was opened to record a video, the bar is **Start recording** and
**STOP**, `Enter` or a double-click starts it, and in window mode the click on
the window is the decision — the recording starts there and then.

## In the annotator

### Tools

| | | | |
|---|---|---|---|
| `V` | Select | `S` | Numbered marker |
| `M` | Move the screenshot | `D` | Symbols |
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
| Middle-drag, or hold `Space` and drag | Pan the view |
| `Ctrl+0` | Fit the board |
| `Ctrl+1` | 100% |

The board is always kept overlapping the window, and the image is always kept on
the board, so neither can be lost off the edge.

### Output

| | |
|---|---|
| `Ctrl+S` | Save a PNG to the captures folder |
| `F12` or **Save as…** | Choose the name, the folder and the format — PNG, JPEG or BMP |
| `Ctrl+Shift+C` | Copy the whole board to the clipboard |
| `Ctrl+N` or **New capture** | Take another capture — this annotator stays open, work and all |
| `Ctrl+R` or **Retake** | Throw this capture away and take it again the same way — region, window or full screen. Asks first only if something is drawn on it; any file already saved from it goes to the Recycle Bin |
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

Choose **Video** or **Audio** on the home window, set the sound, and press the
button. A small control bar appears with the same switches, so a source can be
added, dropped or turned up while it runs. Switching mid-recording does not
interrupt the file: the mixer's output stream keeps running, so the audio track
stays continuous and in sync.

**Pause** leaves the paused stretch out of the file, picture and sound alike.

The bar reads **Pause · Retake · Save · Save as… · STOP**.

| | |
|---|---|
| **Save** | Finish and save under the automatic name |
| **Save as…** | Finish, then choose the name and folder; the finished file is moved there. Cancel that dialog and it is kept under its automatic name |
| **Retake** | Throw this take away and start again straight away — same sources, same region or window, the bar where you left it, and *take 2* beside the time |
| **STOP** | End everything without saving, and bring the home window back to the front. Capitals and a red outline, because it is the one that keeps nothing |

Retake and STOP never ask "are you sure", because that would slow down the thing
they exist to make quick; instead the take they throw away goes to the Recycle
Bin, not away for good, so a mistaken click costs nothing. The home window's
**Save recording**, the tray and `Ctrl+Shift+R` all save, like **Save**.

The control bar never appears in the recording. Neither does the selection
overlay while a recording is in progress.

### Video

| Setting | Notes |
|---|---|
| Frame rate | 15–60. 30 is a good default for software demos. |
| Quality | x264 CRF, 14 (near-lossless) to 30 (small). 20 is the default. |
| Encoder | Automatic uses libx264. Pick a hardware encoder if you have one. |
| Cursor | Optional, drawn into each frame. |

### Audio only

| Setting | Notes |
|---|---|
| Format | Settings → Recording → Audio only. MP3 at 192 kbps (the default), M4A at AAC 128 kbps, or WAV. |
| System audio device | The output to record from, on the home window. Pick the one the call plays through if it is not the default — a headset, say. |

MP3 is the default because a take that is cut off — a crash, a flat battery — is
still playable up to the cut. An M4A that was never stopped properly cannot be
opened at all. If the ffmpeg in use has no MP3 encoder, the file is saved as M4A
and a notice says so. Audio has a folder of its own, `Music\KAM Capture
Tool\Audio`, changed in Settings → Recording → Audio to.

### A lecture over slides

The case audio-only was built for: every slide as a sharp screenshot, the call's
sound underneath, put together afterwards in an editor.

1. Settings → Capture → Afterwards: tick **Save a PNG straight away**, untick
   **Open the annotator**. Each screenshot is then one keypress and no windows.
2. On the home window, **Audio**, with **System audio** set to the output the call
   plays through. Add the microphone if your own voice belongs in it.
3. Start recording audio, then press `Ctrl+Shift+F` on each new slide. It takes
   the display the pointer is on, at once, and the home window stays where it
   was rather than jumping in front of the call.
4. Glance at the recording bar before settling in: it has a meter for system
   audio and one for the microphone. With the call talking, the system audio
   meter should move.

Screenshots are named to the second, and one taken in the same second as the
last is saved as `…-2` rather than replacing it.

To name the slides as you go instead, leave **Save a PNG straight away** off,
drag each one with `Ctrl+Shift+S` and press **Save as…** (or `F12`). Name the
first *Lecture 5 slide 1* in a folder of its own; every Save as after it opens in
that folder with the next number already filled in, so the rest are Enter.

## Save as

Screenshots, videos and audio all have it: on the selection bar and in the
annotator for a screenshot (`F12` in both), and beside **Save** on the recording
bar. Each kind remembers where its last Save as went, and opens there next time.

If the last name ended in a number, the suggestion is the next one —
*slide 9* is followed by *slide 10*, *slide 09* by *slide 10* — stepping past any
name already taken, so accepting it never overwrites anything. After a name the
tool made up itself, whose last number is only the seconds of a timestamp, the
suggestion is a fresh automatic name instead.

`Ctrl+Shift+S` would be the usual shortcut, but it is the region shortcut, and
Windows hands it to that before any window sees it; `F12` is what Office uses.

## Troubleshooting

**Captures look soft.** Check the scaling figure next to the title on the home
screen. If it says 100% on a high-DPI laptop, Windows is reporting something
unusual; the log will say what was detected.

**Recording will not start.** ffmpeg was not found. Settings shows where it
looked; *Find* locates it or lets you point at it.

**A recording has no system audio.** Watch the system audio meter on the
recording bar while something plays. If it stays flat, the log's *Take ended*
line says which devices were recorded and how loud each got, and
`KamCapture.exe --audiodiag` lists which output every program is playing
through. A program using an output in exclusive mode cannot be recorded by
anything.

**A shortcut does nothing.** Another program owns that combination. KAM shows a
notification listing the ones it could not register.

**Something went wrong.** `%APPDATA%\KAM Capture Tool\kam-capture.log` has the
detail, and it is plain text.
