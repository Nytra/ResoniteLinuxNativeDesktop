# LinuxNativeDesktop

A [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader) mod that makes Resonite's
desktop tab work on Linux. Under Proton the tab is disabled, and its Windows screen capture and input don't work
under Wine. This mod captures your screen natively through the desktop portal and PipeWire, and sends clicks,
typing and scrolling back through the portal, so you can see and use your Linux desktop from inside Resonite.

## Requirements

- Resonite on Linux through Steam's Proton (the default setup).
- [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader).
- A desktop session with PipeWire and an xdg-desktop-portal that supports screen casting. The distro doesn't
  matter; your desktop's portal does:

| Desktop | Portal package | Screen | Input |
|---|---|---|---|
| KDE Plasma (Wayland) | `xdg-desktop-portal-kde` | ✅ tested | ✅ tested |
| GNOME (Wayland) | `xdg-desktop-portal-gnome` | should work, untested | should work, untested |
| Sway, river and other wlroots desktops | `xdg-desktop-portal-wlr` | should work, untested | ❌ view only |
| Hyprland | `xdg-desktop-portal-hyprland` | should work, untested | only if your portal version has RemoteDesktop |
| X11 sessions | | only if your portal can cast on X11 (KDE's can't) | |

Input needs the portal's RemoteDesktop interface; without it the tab still shows your screen. Most desktops install
the right portal already. Tested on Arch Linux with KDE Plasma 6 (Wayland), one monitor, in VR.

## Install

1. Install ResoniteModLoader.
2. Download `LinuxNativeDesktop.dll` from the [releases](https://github.com/Nytra/ResoniteLinuxNativeDesktop/releases)
   and put it in `Resonite/rml_mods`.
3. Start Resonite and open the desktop tab in your dash.

The first time, your desktop asks which screens to share and whether to allow remote control.
**The dialog appears on your monitor, not in VR**, so take the headset off or use a desktop overlay to answer it.
The choice is remembered; to choose again, revoke it in your desktop's settings (KDE: System Settings → Application
Permissions). If you stop sharing from the system tray, sharing restarts the next time the tab is opened.

## Settings

Change these with a mod settings mod such as ResoniteModSettings, or in `rml_config/LinuxNativeDesktop.json`.

| Setting | Default | |
|---|---|---|
| Enabled | true | Enable the desktop tab. Needs a restart. |
| Max framerate | 30 | Capture frame rate limit. |
| Show cursor | true | Draw the mouse cursor into the image. Needs a restart. |
| Allow desktop input | true | Let the desktop tab control your mouse and keyboard. Off = view only. Needs a restart. |
| Scroll speed | 100 | Scroll speed with the thumbstick fully pushed, in wheel notches per second. |
| Debug logging | false | Log desktop views, frame statistics and input events (button presses, shortcuts and special keys, never typed text or click positions). |

## Using it in VR

- **Laser**: moves your mouse pointer; trigger clicks, secondary action right-clicks.
- **Thumbstick**: scrolls what's under the laser. It still moves and turns you as usual; to stop that while pointing
  at UI, use a mod such as [NoTankControls](https://github.com/Nytra/NoTankControls).
- **Windows-logo button**: opens your application launcher (Meta key).
- **Desktop keyboard**: types into the focused window, on any keyboard layout.

The mod also keeps Resonite from locking the cursor in VR and from reacting to its own clicks on the game window,
which would otherwise trap the pointer or stop the in-game UI from responding.

In desktop mode the tab only shows your screen, as on Windows; use your real mouse and keyboard there.

## Limitations

- "Follow cursor" follows Wine's cursor, not your real one.
- Multiple monitors are untested. Displays are matched by position and size, then by order; sharing all screens
  works best.
- Resonite's "stream desktop" button doesn't work: the game captures the stream for other users with a
  Windows-only screen capture that can't run on Linux. Only the desktop tab itself is supported.

## Troubleshooting

Mod messages appear in `Resonite/Logs`, tagged `[ResoniteModLoader/LinuxNativeDesktop]`. Turn on **Debug logging**
for more. The mod never logs what you type, where you click, or its saved share permission.

- **The desktop tab is still missing**: check that the log says `Re-enabled the desktop feature` and that **Enabled**
  is on.
- **No share dialog / "Screen capture unavailable" / "xdg-desktop-portal is not running"**: make sure a portal is
  running (`systemctl --user status xdg-desktop-portal`) and that your desktop's portal package is installed. Look on
  your monitor: the dialog may be waiting behind the game.
- **"No RemoteDesktop portal; desktop input is unavailable"**: your portal can't inject input (see the table above).
  The tab works view only.
- **"No shared screen matches display"**: that screen wasn't shared in the dialog. Revoke the permission (see
  Install) and share it, or share all screens.
- **The game closes**: check the renderer log at
  `<Steam library>/steamapps/compatdata/2519830/pfx/drive_c/users/steamuser/AppData/LocalLow/Yellow Dog Man Studios/Renderite.Renderer/Player.log`
  and open an issue with it and the Resonite log.

## Building

Needs the .NET 10 SDK and Resonite installed.

```bash
dotnet build LinuxNativeDesktop -c Release
```

The build looks for Resonite in Steam's default library (native or Flatpak Steam) and copies the DLL into
`rml_mods`. If your game is in another library, pass `-p:ResonitePath=/path/to/Resonite/` (or set a `ResonitePath`
environment variable), and `-p:CopyToMods=false` to skip the copy.

```bash
dotnet test LinuxNativeDesktop.Tests
```

Some tests load `Renderite.Shared.dll` from the game, and the PipeWire stream test needs GStreamer's
`pipewiresink` (skipped otherwise). Design notes and the engine behaviour the mod depends on are in
[CLAUDE.md](CLAUDE.md).

### ContainerProbe

`tools/ContainerProbe` tests capture and input outside the game, inside the Steam runtime container the engine
uses (`$RESONITE` is your Resonite folder):

```bash
dotnet build tools/ContainerProbe -c Release -o /tmp/lnd-probe
```

```bash
"<Steam library>/steamapps/common/SteamLinuxRuntime_4/run" -- "$RESONITE/dotnet-runtime/dotnet" /tmp/lnd-probe/ContainerProbe.dll --portal
```

`--portal` shows the share dialog and reports a frame per screen; `--input` moves the pointer and taps Meta;
`--key <evdev code> [taps]` taps a key the way the mod does; `--scroll` tries each way of scrolling on the window in
the middle of the screen; `--scroll-game` scrolls it the way the mod does and measures from the capture how far it
moved. All but `--portal` control your real desktop.

## License

[MIT](LICENSE)
