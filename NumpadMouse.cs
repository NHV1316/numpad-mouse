// NumpadMouse - turns a numpad into a mouse/trackpad, with a switch back to numbers.
// Single-file WinForms tray app for .NET Framework 4.x (ships with Windows 10/11).
// Written in C# 5 so it builds with the csc.exe that comes with Windows (see build.bat).

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("NumpadMouse")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace NumpadMouse
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, "NumpadMouse_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("NumpadMouse is already running (look for its icon in the system tray).",
                        "NumpadMouse", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Native.MakeDpiAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }

    // Everything a numpad key can do in mouse mode.
    enum Action
    {
        None, NumLock,
        UpLeft, Up, UpRight, Left, Right, DownLeft, Down, DownRight,
        LeftClick, DragLock, RightClick, DoubleClick,
        ScrollUp, ScrollDown, ScrollMode, Precision
    }

    class Settings
    {
        const string Key = @"Software\NumpadMouse";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public int SpeedLevel = 1;          // 0 slow, 1 normal, 2 fast
        public bool NumLockIsSwitch = true; // NumLock key toggles mouse mode

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    if (k != null)
                    {
                        s.SpeedLevel = Math.Max(0, Math.Min(2, (int)k.GetValue("SpeedLevel", 1)));
                        s.NumLockIsSwitch = (int)k.GetValue("NumLockIsSwitch", 1) != 0;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    k.SetValue("SpeedLevel", SpeedLevel, RegistryValueKind.DWord);
                    k.SetValue("NumLockIsSwitch", NumLockIsSwitch ? 1 : 0, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        public static bool StartsWithWindows
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                        return k != null && k.GetValue("NumpadMouse") != null;
                }
                catch { return false; }
            }
            set
            {
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                    {
                        if (value) k.SetValue("NumpadMouse", "\"" + Application.ExecutablePath + "\"");
                        else k.DeleteValue("NumpadMouse", false);
                    }
                }
                catch { }
            }
        }
    }

    class TrayApp : ApplicationContext
    {
        // Pointer speed presets in pixels/second: speed when a key is first pressed,
        // top speed, and seconds to ramp from one to the other.
        static readonly float[] StartSpeed = { 120f, 200f, 300f };
        static readonly float[] MaxSpeed = { 700f, 1400f, 2400f };
        static readonly string[] SpeedNames = { "Slow", "Normal", "Fast" };
        const float RampSeconds = 0.7f;
        const float PrecisionFactor = 0.25f;

        // Scrolling: one notch right away, then continuous after a short delay.
        const float ScrollDelay = 0.30f;
        const float ScrollStartRate = 8f;   // notches/second
        const float ScrollMaxRate = 30f;
        const float ScrollRamp = 1.2f;

        const int HotkeyId = 1;

        readonly Settings settings;
        readonly NotifyIcon tray;
        readonly Icon mouseIcon, numberIcon;
        readonly ToolStripMenuItem mouseModeItem, numLockSwitchItem, startupItem;
        readonly ToolStripMenuItem[] speedItems = new ToolStripMenuItem[3];
        readonly Osd osd = new Osd();
        readonly HotkeyWindow hotkeyWindow;
        readonly Native.LowLevelKeyboardProc hookProc;
        IntPtr hook = IntPtr.Zero;

        readonly object sync = new object();
        readonly bool[] held = new bool[Enum.GetValues(typeof(Action)).Length];
        volatile bool mouseMode;
        bool dragLocked, leftDown;
        volatile bool running = true;
        readonly Thread motionThread;

        public TrayApp()
        {
            settings = Settings.Load();
            mouseIcon = MakeIcon(true);
            numberIcon = MakeIcon(false);

            var menu = new ContextMenuStrip();
            mouseModeItem = new ToolStripMenuItem("Mouse mode", null, delegate { SetMouseMode(!mouseMode); });
            mouseModeItem.Font = new Font(mouseModeItem.Font, FontStyle.Bold);
            menu.Items.Add(mouseModeItem);
            menu.Items.Add(new ToolStripSeparator());

            var speedMenu = new ToolStripMenuItem("Pointer speed");
            for (int i = 0; i < 3; i++)
            {
                int level = i;
                speedItems[i] = new ToolStripMenuItem(SpeedNames[i], null, delegate
                {
                    settings.SpeedLevel = level;
                    settings.Save();
                    RefreshMenu();
                });
                speedMenu.DropDownItems.Add(speedItems[i]);
            }
            menu.Items.Add(speedMenu);

            numLockSwitchItem = new ToolStripMenuItem("Use NumLock key as the switch", null, delegate
            {
                settings.NumLockIsSwitch = !settings.NumLockIsSwitch;
                settings.Save();
                RefreshMenu();
            });
            menu.Items.Add(numLockSwitchItem);

            startupItem = new ToolStripMenuItem("Start with Windows", null, delegate
            {
                Settings.StartsWithWindows = !Settings.StartsWithWindows;
                RefreshMenu();
            });
            menu.Items.Add(startupItem);

            menu.Items.Add(new ToolStripMenuItem("Key layout...", null, delegate { ShowHelp(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, delegate { ExitThread(); }));

            tray = new NotifyIcon();
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) SetMouseMode(!mouseMode);
            };
            tray.Visible = true;

            var osdHandle = osd.Handle; // create it now so the hook can BeginInvoke on it
            if (settings.NumLockIsSwitch) EnsureNumLockOn();

            hookProc = HookCallback;
            using (var cur = Process.GetCurrentProcess())
            using (var mod = cur.MainModule)
                hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, hookProc, Native.GetModuleHandle(mod.ModuleName), 0);
            if (hook == IntPtr.Zero)
                MessageBox.Show("Could not install the keyboard hook (error " + Marshal.GetLastWin32Error() + ").",
                    "NumpadMouse", MessageBoxButtons.OK, MessageBoxIcon.Error);

            hotkeyWindow = new HotkeyWindow(delegate { SetMouseMode(!mouseMode); });
            Native.RegisterHotKey(hotkeyWindow.Handle, HotkeyId,
                Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.N);

            motionThread = new Thread(MotionLoop);
            motionThread.IsBackground = true;
            motionThread.Start();

            RefreshMenu();
            tray.ShowBalloonTip(3000, "NumpadMouse is running",
                "Press NumLock on the numpad (or Ctrl+Alt+N) to switch between mouse and number mode.",
                ToolTipIcon.Info);
        }

        protected override void ExitThreadCore()
        {
            running = false;
            SetMouseMode(false);
            if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
            Native.UnregisterHotKey(hotkeyWindow.Handle, HotkeyId);
            hotkeyWindow.DestroyHandle();
            tray.Visible = false;
            tray.Dispose();
            osd.Dispose();
            base.ExitThreadCore();
        }

        void RefreshMenu()
        {
            mouseModeItem.Checked = mouseMode;
            for (int i = 0; i < 3; i++) speedItems[i].Checked = settings.SpeedLevel == i;
            numLockSwitchItem.Checked = settings.NumLockIsSwitch;
            startupItem.Checked = Settings.StartsWithWindows;
            tray.Icon = mouseMode ? mouseIcon : numberIcon;
            tray.Text = mouseMode ? "NumpadMouse: mouse mode" : "NumpadMouse: number mode";
        }

        void SetMouseMode(bool on)
        {
            lock (sync)
            {
                if (on == mouseMode) return;
                mouseMode = on;
                Array.Clear(held, 0, held.Length);
                if (!on)
                {
                    // Never leave a button stuck down when switching back to numbers.
                    if (leftDown || dragLocked) Native.MouseButton(Native.MOUSEEVENTF_LEFTUP);
                    leftDown = dragLocked = false;
                }
            }
            if (on) Native.timeBeginPeriod(1); else Native.timeEndPeriod(1);
            if (!on) EnsureNumLockOn();
            if (running)
            {
                RefreshMenu();
                osd.Flash(on ? "Mouse mode" : "Number mode", on);
            }
        }

        static void EnsureNumLockOn()
        {
            if ((Native.GetKeyState(Native.VK_NUMLOCK) & 1) == 0)
            {
                // Injected, so our own hook lets it through.
                Native.keybd_event((byte)Native.VK_NUMLOCK, 0x45, Native.KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
                Native.keybd_event((byte)Native.VK_NUMLOCK, 0x45, Native.KEYEVENTF_EXTENDEDKEY | Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
        }

        // Identify numpad keys by hardware scan code so it works whether NumLock is on or off
        // (with NumLock off, numpad 8 reports as "Up arrow", but its scan code is unchanged and
        // it lacks the "extended" flag that the real arrow keys have).
        static Action Classify(uint vk, uint scan, bool extended)
        {
            if (vk == Native.VK_NUMLOCK) return Action.NumLock;
            if (scan != 0)
            {
                if (extended)
                {
                    if (scan == 0x1C) return Action.DoubleClick; // Enter
                    if (scan == 0x35) return Action.ScrollMode;  // /
                    return Action.None;
                }
                switch (scan)
                {
                    case 0x47: return Action.UpLeft;     // 7
                    case 0x48: return Action.Up;         // 8
                    case 0x49: return Action.UpRight;    // 9
                    case 0x4B: return Action.Left;       // 4
                    case 0x4C: return Action.LeftClick;  // 5
                    case 0x4D: return Action.Right;      // 6
                    case 0x4F: return Action.DownLeft;   // 1
                    case 0x50: return Action.Down;       // 2
                    case 0x51: return Action.DownRight;  // 3
                    case 0x52: return Action.DragLock;   // 0
                    case 0x53: return Action.RightClick; // .
                    case 0x37: return Action.Precision;  // *
                    case 0x4A: return Action.ScrollUp;   // -
                    case 0x4E: return Action.ScrollDown; // +
                }
                return Action.None;
            }
            // Some devices report no scan code; fall back to virtual-key codes.
            switch (vk)
            {
                case 0x67: return Action.UpLeft;
                case 0x68: return Action.Up;
                case 0x69: return Action.UpRight;
                case 0x64: return Action.Left;
                case 0x65: return Action.LeftClick;
                case 0x66: return Action.Right;
                case 0x61: return Action.DownLeft;
                case 0x62: return Action.Down;
                case 0x63: return Action.DownRight;
                case 0x60: return Action.DragLock;
                case 0x6E: return Action.RightClick;
                case 0x6A: return Action.Precision;
                case 0x6D: return Action.ScrollUp;
                case 0x6B: return Action.ScrollDown;
                case 0x6F: return Action.ScrollMode;
            }
            return Action.None;
        }

        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                bool injected = (k.flags & Native.LLKHF_INJECTED) != 0;
                if (!injected && HandleKey(k))
                    return (IntPtr)1; // swallow the key
            }
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        // Returns true when the key should be swallowed.
        bool HandleKey(Native.KBDLLHOOKSTRUCT k)
        {
            bool up = (k.flags & Native.LLKHF_UP) != 0;
            bool extended = (k.flags & Native.LLKHF_EXTENDED) != 0;
            Action a = Classify(k.vkCode, k.scanCode, extended);
            if (a == Action.None) return false;

            if (a == Action.NumLock)
            {
                if (!settings.NumLockIsSwitch) return false;
                if (!up && !held[(int)Action.NumLock])
                {
                    bool target = !mouseMode;
                    // Defer so the hook returns immediately.
                    osd.BeginInvoke((MethodInvoker)delegate { SetMouseMode(target); });
                }
                held[(int)Action.NumLock] = !up;
                return true;
            }

            if (!mouseMode) return false;

            lock (sync)
            {
                bool wasHeld = held[(int)a];
                held[(int)a] = !up;
                if (!up && wasHeld) return true; // auto-repeat

                switch (a)
                {
                    case Action.LeftClick:
                        if (!up)
                        {
                            // Pressing 5 while drag-locked drops whatever is being dragged.
                            if (dragLocked) { dragLocked = false; leftDown = false; Native.MouseButton(Native.MOUSEEVENTF_LEFTUP); }
                            else { leftDown = true; Native.MouseButton(Native.MOUSEEVENTF_LEFTDOWN); }
                        }
                        else if (leftDown && !dragLocked)
                        {
                            leftDown = false;
                            Native.MouseButton(Native.MOUSEEVENTF_LEFTUP);
                        }
                        break;
                    case Action.DragLock:
                        if (!up)
                        {
                            if (!dragLocked)
                            {
                                dragLocked = true;
                                if (!leftDown) { leftDown = true; Native.MouseButton(Native.MOUSEEVENTF_LEFTDOWN); }
                            }
                            else
                            {
                                dragLocked = leftDown = false;
                                Native.MouseButton(Native.MOUSEEVENTF_LEFTUP);
                            }
                        }
                        break;
                    case Action.RightClick:
                        Native.MouseButton(up ? Native.MOUSEEVENTF_RIGHTUP : Native.MOUSEEVENTF_RIGHTDOWN);
                        break;
                    case Action.DoubleClick:
                        if (!up)
                        {
                            Native.MouseButton(Native.MOUSEEVENTF_LEFTDOWN);
                            Native.MouseButton(Native.MOUSEEVENTF_LEFTUP);
                            Native.MouseButton(Native.MOUSEEVENTF_LEFTDOWN);
                            Native.MouseButton(Native.MOUSEEVENTF_LEFTUP);
                        }
                        break;
                    // Movement, scrolling and modifiers are read by the motion thread.
                }
            }
            return true;
        }

        void MotionLoop()
        {
            var clock = Stopwatch.StartNew();
            double last = clock.Elapsed.TotalSeconds;
            double moveStart = -1, scrollStart = -1;
            float remX = 0, remY = 0, accX = 0, accY = 0;

            while (running)
            {
                Thread.Sleep(mouseMode ? 4 : 50);
                double now = clock.Elapsed.TotalSeconds;
                float dt = (float)Math.Min(0.05, now - last);
                last = now;
                if (!mouseMode) { moveStart = scrollStart = -1; remX = remY = accX = accY = 0; continue; }

                float dx = 0, dy = 0, sx = 0, sy = 0;
                bool scrollMode, precision;
                lock (sync)
                {
                    if (held[(int)Action.Up] || held[(int)Action.UpLeft] || held[(int)Action.UpRight]) dy -= 1;
                    if (held[(int)Action.Down] || held[(int)Action.DownLeft] || held[(int)Action.DownRight]) dy += 1;
                    if (held[(int)Action.Left] || held[(int)Action.UpLeft] || held[(int)Action.DownLeft]) dx -= 1;
                    if (held[(int)Action.Right] || held[(int)Action.UpRight] || held[(int)Action.DownRight]) dx += 1;
                    if (held[(int)Action.ScrollUp]) sy += 1;
                    if (held[(int)Action.ScrollDown]) sy -= 1;
                    scrollMode = held[(int)Action.ScrollMode];
                    precision = held[(int)Action.Precision];
                }
                if (scrollMode)
                {
                    // Hold "/" and use the arrows to scroll like a two-finger swipe.
                    sx += dx; sy -= dy;
                    dx = dy = 0;
                }

                // Pointer movement
                if (dx != 0 || dy != 0)
                {
                    if (moveStart < 0) moveStart = now;
                    float len = (float)Math.Sqrt(dx * dx + dy * dy);
                    int lvl = settings.SpeedLevel;
                    float t = (float)Math.Min(1.0, (now - moveStart) / RampSeconds);
                    float speed = StartSpeed[lvl] + (MaxSpeed[lvl] - StartSpeed[lvl]) * t * t;
                    if (precision) speed *= PrecisionFactor;
                    remX += dx / len * speed * dt;
                    remY += dy / len * speed * dt;
                    int mx = (int)remX, my = (int)remY;
                    if (mx != 0 || my != 0)
                    {
                        remX -= mx; remY -= my;
                        Native.POINT p;
                        if (Native.GetCursorPos(out p)) Native.SetCursorPos(p.X + mx, p.Y + my);
                    }
                }
                else { moveStart = -1; remX = remY = 0; }

                // Scrolling
                if (sx != 0 || sy != 0)
                {
                    if (scrollStart < 0)
                    {
                        scrollStart = now;
                        accX = sx; accY = sy; // one notch immediately
                    }
                    float st = (float)(now - scrollStart);
                    if (st > ScrollDelay)
                    {
                        float r = Math.Min(ScrollMaxRate, ScrollStartRate + (ScrollMaxRate - ScrollStartRate) * (st - ScrollDelay) / ScrollRamp);
                        if (precision) r *= 0.4f;
                        accX += sx * r * dt;
                        accY += sy * r * dt;
                    }
                    int nx = (int)accX, ny = (int)accY;
                    if (ny != 0) { accY -= ny; Native.Wheel(Native.MOUSEEVENTF_WHEEL, ny * 120); }
                    if (nx != 0) { accX -= nx; Native.Wheel(Native.MOUSEEVENTF_HWHEEL, nx * 120); }
                }
                else { scrollStart = -1; accX = accY = 0; }
            }
        }

        void ShowHelp()
        {
            MessageBox.Show(
                "MOUSE MODE layout\n\n" +
                "  7 8 9      move diagonally / up\n" +
                "  4   6      move left / right\n" +
                "  1 2 3      move diagonally / down\n" +
                "     5       left click (hold to drag)\n" +
                "     0       drag lock: holds left button until 0 or 5\n" +
                "     .       right click\n" +
                "   Enter     double click\n" +
                "   -  +      scroll up / down (hold to keep scrolling)\n" +
                "     /       hold + arrows = scroll in any direction\n" +
                "     *       hold for slow, precise movement\n\n" +
                "SWITCH between mouse and numbers:\n" +
                "  NumLock key on the numpad, Ctrl+Alt+N,\n" +
                "  or left-click the tray icon.\n\n" +
                "Moving speeds up the longer you hold a key.\n" +
                "Change base speed from the tray menu.",
                "NumpadMouse - key layout", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        static Icon MakeIcon(bool mouse)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    using (var path = RoundRect(new RectangleF(1, 1, 30, 30), 7))
                    using (var b = new SolidBrush(mouse ? Color.FromArgb(0, 150, 90) : Color.FromArgb(90, 95, 105)))
                        g.FillPath(b, path);
                    if (mouse)
                    {
                        // A simple arrow pointer.
                        var pts = new[] {
                            new PointF(10, 5), new PointF(10, 25), new PointF(15, 20), new PointF(18.5f, 27.5f),
                            new PointF(22, 26), new PointF(18.5f, 18.5f), new PointF(25, 18.5f) };
                        g.FillPolygon(Brushes.White, pts);
                    }
                    else
                    {
                        using (var f = new Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel))
                        {
                            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                            g.DrawString("123", f, Brushes.White, new RectangleF(0, 1, 32, 30), sf);
                        }
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // Small on-screen label that shows the new mode for a moment without stealing focus.
    class Osd : Form
    {
        readonly System.Windows.Forms.Timer hideTimer = new System.Windows.Forms.Timer();
        string text = "";

        public Osd()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(260, 64);
            Font = new Font("Segoe UI", 18f, FontStyle.Bold);
            ForeColor = Color.White;
            Opacity = 0.92;
            DoubleBuffered = true;
            hideTimer.Interval = 900;
            hideTimer.Tick += delegate { hideTimer.Stop(); Hide(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020 | 0x00000008; // NOACTIVATE | TOOLWINDOW | TRANSPARENT | TOPMOST
                return cp;
            }
        }

        public void Flash(string message, bool mouse)
        {
            text = message;
            BackColor = mouse ? Color.FromArgb(0, 130, 80) : Color.FromArgb(60, 64, 72);
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - 80);
            Invalidate();
            if (!Visible) Show();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            hideTimer.Stop();
            hideTimer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            TextRenderer.DrawText(e.Graphics, text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    class HotkeyWindow : NativeWindow
    {
        readonly MethodInvoker onHotkey;

        public HotkeyWindow(MethodInvoker onHotkey)
        {
            this.onHotkey = onHotkey;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312) onHotkey(); // WM_HOTKEY
            base.WndProc(ref m);
        }
    }

    static class Native
    {
        public const int WH_KEYBOARD_LL = 13;
        public const uint LLKHF_EXTENDED = 0x01, LLKHF_INJECTED = 0x10, LLKHF_UP = 0x80;
        public const uint VK_NUMLOCK = 0x90;
        public const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x02, MOUSEEVENTF_LEFTUP = 0x04,
            MOUSEEVENTF_RIGHTDOWN = 0x08, MOUSEEVENTF_RIGHTUP = 0x10,
            MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000;
        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        // INPUT is a union whose largest member is MOUSEINPUT, so this has the right size on 32 and 64 bit.
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public MOUSEINPUT mi; }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] public static extern short GetKeyState(uint vk);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

        static void Send(uint flags, int data)
        {
            var input = new INPUT { type = 0 }; // INPUT_MOUSE
            input.mi.dwFlags = flags;
            input.mi.mouseData = unchecked((uint)data);
            SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }

        public static void MouseButton(uint flags) { Send(flags, 0); }
        public static void Wheel(uint flags, int amount) { Send(flags, amount); }

        // Work in real pixels on high-DPI and mixed-DPI multi-monitor setups.
        public static void MakeDpiAware()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } // PER_MONITOR_AWARE_V2
            catch (EntryPointNotFoundException) { }
            try { SetProcessDPIAware(); } catch (EntryPointNotFoundException) { }
        }
    }
}
