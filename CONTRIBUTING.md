# Contributing

## Building

You need the .NET 9 SDK and Windows 10 2004 or later. Everything else the build
needs, it generates.

```powershell
./scripts/build.ps1
```

That regenerates the icon, compiles, runs the self-test, and publishes a
single-file executable to `dist/app`. Add `-Install` to install it for the
current user, `-DocShots` to regenerate the screenshots in `docs/images`.

For a normal edit-run loop, skip the script:

```powershell
dotnet run --project src/KamCapture
```

## Testing

There is no unit test project. What there is instead:

```powershell
dotnet run --project src/KamCapture -- --selftest=out.png
```

This builds a board containing every annotation type, groups three objects,
moves and scales the group, checks the scale factor came out right, exports at
2x and checks the output dimensions. It touches no screen and no window, so it
runs in CI. Exit code 0 means the drawing, grouping, transform and export paths
are intact.

Recording has its own headless check, which needs ffmpeg and a sound card and so
does not run in CI:

```powershell
dotnet run --project src/KamCapture -- --rectest=out.mp4,5
```

It records a 640 x 360 region for five seconds with system audio, no interface at
all, and fails if the frame count is short of the wall clock. A correct run
writes exactly `fps x seconds` frames. Check the result with ffprobe: the video
duration should match the seconds you asked for, not be shorter.

The rest run on your machine rather than in CI:

| Check | What it proves |
|---|---|
| `--ghosttest` | The tool's own windows leave nothing in a desktop grab. Scores each hiding strategy, and fails if the one in use leaks more than 2% |
| `--lifecycletest` | After a capture, the home window and any open annotator come back where they should |
| `--foldertest` | A OneDrive folder Windows chose is moved back to local disk; one you confirmed is left alone |
| `--updatetest` | Versions compare number by number, GitHub's reply is read correctly, a download that fails its checksum is refused and removed, a failed program swap puts the old one back, and a download opened over a running copy is offered as an update. No network |
| `--installtest` | "Start with Windows" starts the installed copy, not the download, and an unattended update keeps the folder, shortcuts and startup choice. Runs against a scratch registry key and folders, then checks the real install was left alone |

Each was run once against the bug it guards, to watch it fail, before it was
trusted to pass. Keep doing that for new checks: the first ghost test scored
every strategy 0%, including the broken one, because its window had no title
bar and Windows does not animate those. It passed and proved nothing.

The update itself — check, download, checksum, hand-over, restart — takes two
builds and two processes, so it is a script rather than a flag:

```powershell
./scripts/test-update.ps1
```

It builds this version and a newer one, installs the older into a sandbox,
runs it hidden against a local copy of a release feed, and asks it to update
twice: once with a checksum that does not match, which must be refused with
nothing changed, and once for real, which must end with the newer version
running where the old one was. `KAM_CAPTURE_SANDBOX` gives every copy it starts
its own folder, registry key and single-instance lock, so it runs beside a real
install without touching it, and it checks at the end that it did not.
`KAM_CAPTURE_UPDATE_FEED` stands in for GitHub, and is ignored outside a
sandbox.

The selection overlay itself is still not covered, because it needs a pointer.
If you change it, say in the pull request what you did by hand to check it.

## Layout

```
src/KamCapture/
  Interop/      P/Invoke, DPI, window styling
  Capture/      desktop snapshot, monitors, window enumeration, overlay
  Editor/       annotation items, symbol catalogue, board document, canvas
  Controls/     colour wheel, symbol palette, hotkey box, border preview
  Recording/    audio mixing, frame grabbing, ffmpeg
  Services/     capture and recording controllers, hotkeys, log, self-test
  Setup/        the self-installer
  UI/           windows and the theme
```

## House rules

These are the ones worth knowing before a first change.

1. **A capture is never resampled.** If you find yourself scaling a bitmap on the
   way to the user, something upstream is wrong. Crop out of the device-resolution
   buffer instead.
2. **Geometry comes from WPF's per-visual DPI**, not from `GetDpiForMonitor`,
   which has been observed reporting 96 on a display the compositor renders at
   150%. `VisualTreeHelper.GetDpi(element).DpiScaleX` is the number that matches
   what is actually drawn.
3. **The tool must not appear in its own output.** Stills are taken before any
   overlay exists; the recording bar excludes itself at the compositor level.
4. **No network code.** There is none today. Adding any is a change of product,
   not a feature, and needs to be argued for first.
5. **Depth behind one control, not clutter across many.** New capability belongs
   inside an existing surface — the way thirty symbols live behind one button —
   rather than as another toolbar item.
6. **Comments explain why.** What the code does is readable from the code.
7. **Animate opacity and transforms, nothing else.** Both compose on the render
   thread and cost nothing measurable. Animating `Width`, `Height` or `Margin`
   forces a layout pass every frame, which is where a smooth interface turns
   into a janky one. Durations live in the comment at the top of `Theme.xaml`;
   stay inside them.
8. **A state that only lives in `Trigger.EnterActions` does not exist.** A
   control created already in that state never transitions, so the storyboard
   never runs and the control renders wrong. Set the resting value with a
   `Setter` and let the storyboard cover only the transition, with an explicit
   `From` and `FillBehavior="Stop"`.

## Style

Nullable is on and warnings are not ignored. Match the surrounding code rather
than reformatting it. British spelling in user-facing text, since the rest of
the application uses it.
