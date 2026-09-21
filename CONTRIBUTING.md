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

Screen capture and the selection overlay are not covered. They need a
desktop, a pointer and in one case a sound card. If you change them, say in the
pull request what you did by hand to check them.

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

## Style

Nullable is on and warnings are not ignored. Match the surrounding code rather
than reformatting it. British spelling in user-facing text, since the rest of
the application uses it.
