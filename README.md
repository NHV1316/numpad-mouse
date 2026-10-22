# NumpadMouse

A tiny Windows tray app that turns your numpad into a mouse / trackpad, with a one-key switch back to numbers. Built for a Bluetooth numpad, but it works with the numpad on any keyboard.

## Download and run

1. Grab `NumpadMouse.zip` from the [latest release](../../releases/latest) and unzip it anywhere (e.g. `Documents\NumpadMouse`).
2. Double-click `NumpadMouse.exe`. A small icon appears in the system tray (bottom-right; you may need to click the `^` arrow).
   - If Windows SmartScreen says "Windows protected your PC", click **More info → Run anyway**. It shows this for any unsigned app.
3. Press **NumLock** to switch to mouse mode, press it again to go back to numbers. A short "Mouse mode" / "Number mode" label appears.

## Build it yourself

No install needed. Windows already ships the C# compiler (.NET Framework 4):

- Double-click `build.bat`. It compiles `NumpadMouse.cs` and produces `NumpadMouse.exe`.
- If it can't find `csc.exe`, turn on ".NET Framework 4.8" in *Turn Windows features on or off*.

Every push to `main` is also built by GitHub Actions (download the zip from the run's artifacts), and pushing a tag like `v1.0.0` publishes a release with the exe attached.

## Switching modes

Any of these:

- **NumLock** on the numpad
- **Ctrl + Alt + N**
- Left-click the tray icon (green arrow = mouse mode, grey 123 = numbers)

## Mouse mode layout

```
NumLock    /          *          -
switch     scroll     slow       scroll up
           (hold)     (hold)
7  8  9                          +
  moves                          scroll down
4  5  6
1  2  3                          Enter = double click
0 = drag lock      . = right click
```

- **1–9** (except 5) move the pointer; movement speeds up the longer you hold a key.
- **5** = left click (hold 5 and move to drag).
- **0** = drag lock: holds the left button; press 0 or 5 to let go.
- **.** = right click, **Enter** = double click.
- **- / +** = scroll up / down.
- Hold **/** and press 8 2 4 6 to scroll up/down/left/right, like a two-finger swipe.
- Hold **\*** for slow, precise moves.
- Other keys (Backspace, Esc, Tab, Calculator, 00 …) keep working normally.

## Tray menu (right-click the icon)

- **Pointer speed**: Slow / Normal / Fast
- **Use NumLock key as the switch**: untick to get NumLock back as a normal key (then switch with Ctrl+Alt+N or the tray icon)
- **Start with Windows**
- **Key layout…**: shows the layout
- **Exit**

Settings are stored in `HKCU\Software\NumpadMouse`.

## Good to know

- Numpad keys are recognised by scan code, so it reacts to numpad keys from any keyboard regardless of NumLock state.
- Windows blocks normal apps from controlling windows that run as administrator (Task Manager, some installers). To control those too, right-click `NumpadMouse.exe` → **Run as administrator**.
- Going back to number mode always switches NumLock on, so the keys type digits.
