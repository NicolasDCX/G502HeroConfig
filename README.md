# G502 HERO Config

A small, single-window Windows 11 tool for the **wired Logitech G502 HERO (USB 046D:C08B)**.
Open it, change a setting, save it to the mouse, close it. It has no account, telemetry, network access, tray icon
or background service, and closing the window ends the process. An optional **Start with Windows** checkbox (bottom
right) opens the window minimized at sign-in; it is a per-user `HKCU\...\Run` entry that unticking removes.

The Buttons section shows the mouse from the top and the left side with a numbered marker on every button;
point at a row to highlight its button. Buttons are matched by the factory function the mouse reports, not by slot order.

It talks to the mouse via HID++ 2.0 through the standard Windows HID driver, with normal user rights.
It refuses to write to the G502 LIGHTSPEED, G502 X or other models; they have different IDs and memory layouts.

> **Hardware status:** built and run on Windows 11 with a real G502 HERO (firmware U1 69.00.B0010): detection,
> reading profiles, DPI and the button layout work. Treat writing to the mouse as early-stage and see
> "What still needs testing on your mouse". The protocol logic passes 25 tests against a simulated G502,
> including late replies and inconsistent memory reads.

## Run

Build it (see **Build** below), then run:

- `dist\G502HeroConfig\G502HeroConfig.exe`: self-contained, needs no .NET install.
- `dist\G502HeroConfig-small\G502HeroConfig.exe`: about 1 MB, needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).

**Quit G HUB first** (tray icon → Quit). It can overwrite settings or leave the mouse in "host mode".

## What it does

| Feature | Device operation | How success is verified |
|---|---|---|
| Detect mouse, auto-reconnect after unplug | Windows HID enumeration, HID++ ping | Reply to the ping |
| Read DPI stages, current stage, live DPI | 0x8100 memory read, 0x8100 getCurrentDpiIndex, 0x2201 getSensorDpi | – |
| **Temporary DPI** (active stage only, RAM) | 0x2201 setSensorDpi | Reads the sensor DPI back |
| **Save to mouse** (onboard memory) | 0x8100 read → patch only the edited bytes → write sector, with CRC | Re-reads the whole sector and compares every byte. Then reloads the profile and compares the live DPI and polling rate to the saved values |
| Default DPI stage | Byte 1 of the profile | As above |
| Polling rate (125/250/500/1000 Hz) | Byte 0 of the profile (and 0x8060 to list and read rates) | Memory read-back, then the live rate from 0x8060 |
| Switch the active onboard profile | 0x8100 setCurrentProfile | Reads the active profile back |
| Button remap: Back, Forward, keyboard shortcut, clicks, DPI/profile functions, disable | Button binding bytes in the profile | Memory read-back. Press the button to confirm its behaviour (the mouse can't report that) |
| Lighting: Off or Solid colour per zone | LED effect bytes in the profile | Memory read-back. Check the light visually |
| Leave host mode | 0x8100 setOnboardMode | Reads the mode back |

**Temporary vs saved.** "Try active stage now (temporary)" only changes the sensor in RAM. The change is gone after a DPI-stage
or profile switch, or after unplugging. "Save to mouse" writes onboard memory, which survives unplugging. The result
panel lists each check as ✔ verified, ✖ failed, or – not checkable. Success is only reported after a read-back.

**What is never touched.** Saving rewrites one profile sector, but only the bytes you changed differ:
- Macros, G-shift bindings, timeouts, the profile name and unknown or reserved bytes are written back unchanged.
- Other profiles are not rewritten.
- The left button is locked.
- If the profile on the mouse changed after the app loaded it (for example, G HUB wrote to it), the save stops and nothing is written.
- The window refuses to close while a write is in progress.

**Factory-state mice.** A mouse that has never been configured runs a read-only ROM profile. The first save asks for
confirmation, then copies the factory profiles into onboard memory and applies your change. The ROM itself is never written.

## Build

**On Windows:** install the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`), then run:

```bash
powershell -ExecutionPolicy Bypass -File tools\build.ps1
```

This runs the tests and writes both builds to `dist\`. Or build manually:

```bash
dotnet publish src/G502Tool.App -c Release -r win-x64 --self-contained true -o dist/G502HeroConfig
```

**From Linux or macOS:** the same `dotnet publish` command works. The UI is WPF built in C# code (no XAML),
and the project sets `EnableWindowsTargeting`.

**Tests** (on any OS):

```bash
dotnet test tests/G502Tool.Tests
```

### Layout

- `src/G502Tool.Core`: protocol, profile byte layout and save/verify logic. Portable, no Windows code.
- `src/G502Tool.App`: WPF window and the Windows HID transport (`Hid/WinHid.cs`, P/Invoke to hid.dll and setupapi.dll).
- `tests/G502Tool.Tests`: xUnit tests against `FakeG502`, a simulated mouse.

## Startup time

The footer shows "Window ready N ms after launch", measured from process start to first render. Device discovery runs
on a background thread after the window appears. To measure a Release build several times:

```bash
powershell -ExecutionPolicy Bypass -File tools\measure-startup.ps1 -Exe dist\G502HeroConfig\G502HeroConfig.exe -Runs 5
```

Both builds are ReadyToRun-precompiled. The target is under 1000 ms. **This has not been measured on Windows yet.**

## Hardware test (about 10 minutes)

Before you start, quit G HUB from the tray. Do the DPI steps first; they matter most.

1. **Detect.** Start `G502HeroConfig.exe`. The header should say *G502 HERO … connected* with `USB 046D:C08B` and a firmware version.
   Write down the startup time from the footer.
   - If you see an error instead, it includes the Windows error and a next step. Click **Copy diagnostics** and keep the text.
   - If a yellow *host mode* banner appears, click **Use onboard memory**.
2. **Read.** One DPI stage is tagged **● current stage**, and the hint shows *Live sensor DPI*. Press the DPI buttons on
   the mouse, then click **Reload from mouse**. The current stage and live DPI should follow.
3. **Temporary change.** Change the current stage's value (for example 800 → 1600) and click **Try active stage now (temporary)**.
   You should get *✔ Live sensor DPI: Mouse reports 1600 DPI*, and the pointer should move faster.
   Unplug and replug the mouse: the speed goes back, and the app reconnects by itself without a restart.
4. **Save.** Change one stage, ideally the current one (for example to 1250), and click **Save to mouse**. Expected:
   *✔ Profile N: onboard memory (read back all … bytes)* and *✔ Live DPI … matching the saved value*.
   Any ✖ means it was not verified. Copy the diagnostics.
5. **Persistence. This is the main test.**
   1. Close the app. In Task Manager, confirm `G502HeroConfig.exe` is no longer running.
   2. Unplug the mouse, wait 5 seconds, then plug it back in.
   3. Without opening any software, check that the pointer speed matches 1250 DPI on that stage.
   4. Reopen the app. The stage should show **1250**, and if it is the current stage, *Live sensor DPI: 1250*.
   5. Optional: reboot and check again.
6. **Thumb buttons.** Expand *Buttons*.
   1. Find the row whose factory function is *Forward*. Set it to **Keyboard shortcut…**, click the box, press **Ctrl+C**, then click **Save to mouse**.
   2. Select text in Notepad and press that thumb button, then paste with Ctrl+V. The text should be copied.
   3. Set the row back to *Factory default (Forward)* and save again.
   4. Also try *Back* on the other thumb button in a web browser.
7. **Polling rate.** Pick 500 Hz and save. You should get *✔ Live polling rate: 500 Hz*. Then set it back to 1000 Hz.
8. **Profiles.** Pick another enabled profile, click **Make active**, and expect *✔ Active profile*. Unplug and replug,
   then click **Reload**, and note whether the mouse kept that profile.
9. **Lighting.** Set Zone 1 to Solid `#FF0000`, save, and check the light. Then do the same for Zone 2.

If anything fails, click **Copy diagnostics** and keep the text. It has the raw HID++ trace and the profile bytes,
and it stays on your PC unless you paste it somewhere.

## What still needs testing on your mouse

None of these could be checked without the hardware:

1. **Windows HID access.** That the HID++ collection (usage page FF00, usage 0002) opens without admin rights while
   Windows holds the mouse, and that replies arrive on it. The app also listens on the short collection (FF00/0001) as a fallback.
2. **Profile reload after a save.** The app calls setCurrentProfile on the active profile to make the mouse reload it.
   If *Live DPI* shows ✖ but the memory check shows ✔, the value is saved; unplug and replug to load it, and let me know.
3. **Byte layout on your firmware.** DPI at offset 3, buttons at offset 32, LEDs at offset 208, and the sector size.
   Taken from libratbag; the app refuses any other memory or profile format it reports.
4. **Button slot order and LED zone names.** Buttons are labelled by their factory binding, read from the mouse's ROM profile.
   "Zone 1 (logo) / Zone 2 (DPI indicator)" is my assumption.
5. **Factory-state first save.** The path that creates user profiles from ROM is the least tested.
   It only runs after you confirm the prompt.
6. **Whether profile selection survives a replug.** That is decided by the firmware (step 8 above).
7. **Startup time** on your PC.

## Licensing

The code is original. Protocol facts and the CRC routine come from libratbag (MIT) and are credited in
`THIRD-PARTY-NOTICES.md`. Solaar (GPL) was only consulted to cross-check the USB ID; none of its code is used.

The mouse illustrations in `src/G502Tool.App/Images` were made for this project. Logitech, G502 and the G logo are
trademarks of Logitech; this is an unofficial tool, not affiliated with or endorsed by Logitech.
