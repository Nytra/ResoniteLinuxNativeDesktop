# LinuxNativeDesktop

RML mod that makes Resonite's desktop tab work on Linux/Proton: native capture plus mouse, keyboard and scroll
input. User docs are in README.md; this file covers the design and the engine facts it depends on.

## Background

- `Engine.LoadConfig` sets `Config.DisableDesktop = true` under Wine/non-Windows, disabling the desktop tab.
- Capture is renderer-side (`uDesktopDuplication`, falling back to `uWindowCapture`), which fails under Wine.
- Desktop input goes through `InputInterface.InputInjector`, which `Renderite.Host` only registers on Windows.
- The engine runs natively inside the Steam Linux Runtime 4 container: no GStreamer, but libgio, libpipewire 1.4
  and libX11. The session bus and the PipeWire socket are forwarded. Everything here uses those via P/Invoke.

## Design

- **Enable**: `OnEngineInit` clears `DisableDesktop`. `DesktopScreenPatch` rebuilds desktop controllers that were
  saved empty while the desktop was disabled. The Enabled and Allow desktop input settings are read once there and
  used all session: what startup does can't be undone live, and mixing live and startup values once let the
  renderer capture again or left clicks unfiltered.
- **Capture** (`Capture/`, no engine dependencies): xdg-desktop-portal over GDBus (`PortalSession`), libpipewire
  with hand-built SPA pods (`PipeWireCapture`, `SpaPod`). The stream offers RGBx/RGBA/BGRx/BGRA without modifiers and
  asks for MemPtr/MemFd buffers, so frames arrive in CPU memory, not DMA-BUFs. `CaptureService` keeps one portal
  session per process (restore tokens are single use, so each new one is saved; a rejected token is cleared) and
  one stream per viewed display, closed when nobody views it. If a stream fails (the user stopped sharing), the
  next view asks the portal again. `DisplayMatcher` pairs Wine's displays with the shared screens (same rect, then
  same size unless scaling is in play, then left-to-right order); `PointerMapping` converts Wine coordinates into
  a stream's (logical) size.
- **Display**: each `DesktopTexture` gets a `DesktopMirror` with its own `Texture2D`. The prefix on
  `RenderableHelper.PackRenderableId(ITexture)` swaps in the Texture2D's id, redirecting every material and UI
  image. `DesktopTextureUpdatePatch` keeps the renderer from capturing; `DesktopMirror.ReportSize` fires the
  texture's `onUpdated` via `HandlePropertiesUpdate` so materials re-send the id.
- **Upload**: the capture thread fills a shared-memory `Bitmap2D`; a `World.RunInUpdates` pump hands it to
  `Texture2D.SetFromBitmap2D` (one upload in flight) and allocates a new one.
- **Input**: `LinuxInputInjector` (registered via `Engine.RunPostInit`) uses a RemoteDesktop portal session with
  the screen cast attached (persistence on `SelectDevices`; tokens stored per mode). Lasers drive one absolute
  pointer plus BTN_LEFT (a pressing laser keeps the pointer; otherwise the one that last moved). Characters go as
  keysyms so the layout picks the key, other keys as evdev codes (`Input/KeyMap.cs`); a modifier-only list is tapped
  (the Windows-logo button sends `Key.LeftWindows` → Meta; sequencing in `KeyMap.Tap`), and every release is sent
  again 50 ms later (engine fact 10). Notify calls are fire-and-forget
  `g_dbus_connection_call`s. Input only reaches the portal once a desktop view has started the session.
- **VR fixes**: `CursorLockPatch` drops the cursor lock in VR; `GameMouseFilterPatch` hides the game's mouse
  buttons in VR while the mod is injecting clicks (a button held, or within 0.5 s of one, since Wine reports them late).
- **Scrolling** (`DesktopScroll`): while `DesktopInteractionRelay.ProcessEvent` sees a laser (0.1 s timeout), its
  handler's `Axis` value is turned by `ScrollAccumulator` into notch-sized steps, sent as smooth `NotifyPointerAxis`
  scrolling (15 units per notch). Vertical is negated, because through KDE's portal a positive dy scrolls up
  (confirmed in-game). After pointer motion each scroll starts with a 1-unit primer (`ScrollAccumulator.Events`,
  engine fact 9). The stick is deliberately not claimed (`Axis.RegisterBlocks`), so it still moves and turns the
  user: by the author's choice, blocking that is left to mods like NoTankControls (which also clears
  `RegisterBlocks` on every handler, so a claim here would be undone anyway). Its inspector scroll compatibility
  mode blocks on any controller here: the userspace handler holds an invisible tool that uses secondary.

## Engine facts (each one broke an in-game test)

1. Never let the renderer capture: its `uWindowCapture` fallback crashes it. Unloading a desktop texture the
   renderer never saw is harmless.
2. Upload only `RGBA32` + sRGB. `BGRA32` has no D3D11 mapping and kills the renderer; BGR frames are swapped in
   `FrameCopy`.
3. `Texture2D` owns every bitmap passed to `SetFromBitmap2D` and disposes the previous one, so never reuse one.
   The bitmaps must use shared memory.
4. Rows must be flipped (`flipVertical: true`), whatever the decompiled upload code suggests.
5. The engine locks the cursor whenever its window is focused, even in VR. Under Wine/Wayland that traps the real
   pointer, injected motion included, in the game window.
6. Resonite's fullscreen window covers the desktop, so laser clicks land on it and Wine reports them as the game's
   own mouse buttons, which stalls the VR UI. In that state KDE also ignored the Windows button's Meta taps (they did
   reach the portal). The click filter fixed both; if the Meta problem returns, call
   `org.kde.PlasmaShell.activateLauncherMenu` for a lone Windows key instead.
7. In VR the thumbstick is bound to `InteractionHandlerInputs.Axis`; `Canvas.ProcessAxis`/`TouchAxis` only
   carries the mouse wheel.
8. KWin marks all remote scroll as source "unknown". XWayland apps (Steam) then ignore discrete steps
   (`NotifyPointerAxisDiscrete`) but act on smooth `NotifyPointerAxis`; sending both scrolls them twice. So
   scrolling is smooth only, 15 units per step (KWin's size for one wheel notch). `ContainerProbe --scroll` compares
   the variants on whatever window is under the screen centre.
9. XWayland reports absolute pointer motion from one slave device (`xwayland-pointer`) and scrolling from another
   (`xwayland-relative-pointer`), so motion between scrolls causes a `DeviceChanged`/SlaveSwitch, and Chromium apps
   (Steam) drop the first scroll after one. A VR laser moves the pointer nearly every frame, so Steam scrolled only
   when the hand was still. The primer is the event they drop. `ContainerProbe --scroll-game` measures it (moving
   pointer: no primer 0 px, primer ~300 px for 4 steps); `xinput test-xi2 --root` shows the events.
10. KDE's portal or KWin (Plasma 6.8.2, libei 1.6) sometimes loses a key release that reached the bus: about 1 F3
   tap in 8 from the in-game keyboard. `dbus-monitor` showed every press and release on the bus, 20 µs apart;
   `xinput test-xi2 --root` showed the press without its release, then XWayland auto-repeating F3 to the focused X
   window until the next tap's release (its press was dropped as a duplicate). So every key release is sent again
   50 ms later (`LinuxInputInjector.ReleaseAgainLater`, under `_gate` so it can't split a Ctrl+C); a release of a
   key that isn't down is ignored. Confirmed fixed in-game; not reproduced from outside the game
   (`ContainerProbe --key 61 2`).

The desktop relay ignores all input in desktop mode (`ScreenActive`), so input features are VR only. Vanilla
quirk, not a bug: the relay locks a laser press to itself (`TouchEnterLock`/`TouchExitLock`), so a press that just
misses the dash's ≡ button clicks the desktop instead. The button already has priority (it's the later sibling).

## Working on it

- Keep `Capture/` free of engine dependencies; the tests and `tools/ContainerProbe` compile it directly.
- Engine-facing code (patches, injector, mirror) can only be tested in-game. Use the Debug logging setting, and
  `dbus-monitor --session "type='method_call',interface='org.freedesktop.portal.RemoteDesktop'"` to see what the
  mod sends. The engine log is buffered (the LogFlush mod flushes it).
- `pgrep -f`/`pkill -f` match their own shell (a `pkill -f` kills the command running it); check for the game
  with `ps -eo comm | grep -E '^(dotnet|Renderite)'`.
- Changing a pod: build it with the real `spa_pod_builder` in `tools/spa-reference.c` and paste the bytes into
  `SpaPodTests.cs`.
- `ContainerProbe --input`, `--key`, `--scroll` and `--scroll-game` act on the user's desktop; only run them with
  their consent.
- `Directory.Build.props` holds the list of Resonite paths both projects use (the usual Steam folders, Flatpak
  Steam and the author's library); add new ones there. The mod build errors clearly when `FrooxEngine.dll` is
  missing.
- Portal Response signals are only accepted from the portal's unique name (`GetNameOwner`) and with the
  `(ua{sv})` signature; any process on the session bus can emit signals. GVariant parse errors don't include the
  text, which can hold a restore token.
- Privacy relies on two vanilla gates the mod must keep: `DesktopTextureProvider.UpdateAsset` needs a privileged
  token, so mirrors only exist where the engine made a desktop texture (and their `Texture2D` is local, never
  synced); `DesktopInteractionRelay` only works in userspace, and scrolling keys off its `ProcessEvent`.
- Treat logs as public: users post them in bug reports. Never log typed text or anything that reveals it (lengths,
  one line per character: legacy text input sends every character as a key press), so `InjectString` isn't logged
  and `KeyMap.ForLog` drops typing and shows shortcuts' character keys as "?". Clicks are logged without
  positions. Restore tokens never reach the log.
- Decompile the game with `ilspycmd`. Style: tabs, file-scoped namespaces, csproj layout as in the author's other
  RML mods.
- Tests (`dotnet test`, 115): SPA pods byte-for-byte against `tools/spa-reference.c`, frame copy, display matching,
  pointer mapping, key mapping and tap sequences, keysyms, scroll accumulation and primers, portal GVariant
  messages and Response checks (real GLib), bus name lookup (skipped without a session bus), log redaction, and a
  real PipeWire stream from a GStreamer source.
- Tested on KDE Plasma 6.8 Wayland, single monitor, VR. Untested: multiple monitors, Resonite's "stream desktop"
  (left as on Windows by the user's choice). Ideas offered but not built: a window-switcher (KDE Overview) button,
  a bigger hit area for the ≡ button.
- The mod was renamed from LinuxDesktopTab; only the local repo folder still uses the old name. GitHub:
  https://github.com/Nytra/ResoniteLinuxNativeDesktop
