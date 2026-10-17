using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MiniLiveHost;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}

public class AppConfig
{
    public string? SelectedExePath { get; set; }
    public bool PauseOnFloatingWindow { get; set; } = false; // Pause when focused on any non-desktop window
    public bool PauseOnBatterySaver { get; set; } = true;    // Pause when on battery or battery saver
}

public class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _trayMenu;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ToolStripMenuItem _currentWallpaperItem;
    private readonly ToolStripMenuItem _floatingWindowPauseItem;
    private readonly ToolStripMenuItem _batterySaverPauseItem;

    private Process? _wallpaperProcess;
    private IntPtr _wallpaperHwnd = IntPtr.Zero;
    private readonly string _configFilePath;
    private AppConfig _config;

    // Mouse hook
    private IntPtr _mouseHook = IntPtr.Zero;
    private Win32.LowLevelMouseProc? _mouseProc;

    // Monitor timer
    private System.Windows.Forms.Timer? _monitorTimer;
    private int _pauseConditionDurationSeconds = 0;
    private bool _isSuspended = false;
    private const int PauseThresholdSeconds = 3; // Trigger suspend after 3 seconds

    private const string AppName = "MiniLiveHost";
    private const string RegistryRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public TrayApplicationContext()
    {
        _configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        _config = LoadConfig();

        _trayMenu = new ContextMenuStrip();

        _currentWallpaperItem = new ToolStripMenuItem("Status: No active wallpaper") { Enabled = false };
        _trayMenu.Items.Add(_currentWallpaperItem);
        _trayMenu.Items.Add(new ToolStripSeparator());

        var selectItem = new ToolStripMenuItem("Select Wallpaper (.exe)...", null, OnSelectWallpaper);
        _trayMenu.Items.Add(selectItem);

        var restartItem = new ToolStripMenuItem("Restart Wallpaper", null, (s, e) => RestartWallpaper());
        _trayMenu.Items.Add(restartItem);

        // Performance / Pause Options Submenu
        var pauseMenu = new ToolStripMenuItem("Power & Performance");

        _floatingWindowPauseItem = new ToolStripMenuItem("Pause when other app is focused", null, OnToggleFloatingWindowPause)
        {
            Checked = _config.PauseOnFloatingWindow
        };
        pauseMenu.DropDownItems.Add(_floatingWindowPauseItem);

        _batterySaverPauseItem = new ToolStripMenuItem("Pause on Battery / Saver mode", null, OnToggleBatterySaverPause)
        {
            Checked = _config.PauseOnBatterySaver
        };
        pauseMenu.DropDownItems.Add(_batterySaverPauseItem);

        _trayMenu.Items.Add(pauseMenu);

        _autoStartItem = new ToolStripMenuItem("Start with Windows", null, OnToggleAutoStart)
        {
            Checked = IsAutoStartEnabled()
        };
        _trayMenu.Items.Add(_autoStartItem);

        _trayMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit", null, OnExit);
        _trayMenu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            ContextMenuStrip = _trayMenu,
            Text = "Mini Live Wallpaper Host",
            Visible = true
        };

        InstallMouseHook();
        StartMonitor();

        if (!string.IsNullOrEmpty(_config.SelectedExePath) && File.Exists(_config.SelectedExePath))
        {
            StartWallpaper(_config.SelectedExePath);
        }

        // Trim startup unpacking memory
        Task.Delay(2000).ContinueWith(_ =>
        {
            try
            {
                using var current = Process.GetCurrentProcess();
                Win32.EmptyWorkingSet(current.Handle);
            }
            catch { }
        });
    }

    private void StartMonitor()
    {
        _monitorTimer = new System.Windows.Forms.Timer
        {
            Interval = 1000 // Check every 1 second
        };
        _monitorTimer.Tick += OnMonitorTick;
        _monitorTimer.Start();
    }

    private void OnMonitorTick(object? sender, EventArgs e)
    {
        if (_wallpaperProcess == null || _wallpaperProcess.HasExited || _wallpaperHwnd == IntPtr.Zero)
            return;

        bool shouldPause = ShouldSuspendWallpaper();

        if (shouldPause)
        {
            _pauseConditionDurationSeconds++;
            if (_pauseConditionDurationSeconds >= PauseThresholdSeconds && !_isSuspended)
            {
                SuspendWallpaper();
            }
        }
        else
        {
            _pauseConditionDurationSeconds = 0;
            if (_isSuspended)
            {
                ResumeWallpaper();
            }
        }
    }

    private bool ShouldSuspendWallpaper()
    {
        // 1. Check Battery Saver / Unplugged condition if enabled
        if (_config.PauseOnBatterySaver)
        {
            var powerStatus = SystemInformation.PowerStatus;
            // If on battery and below 30%, or BatteryChargeStatus flag has BatterySaver active
            if (powerStatus.PowerLineStatus == PowerLineStatus.Offline)
            {
                // When on battery, save juice if battery saver is engaged or user enabled battery pause
                if (powerStatus.BatteryLifePercent < 0.35f)
                {
                    return true;
                }
            }
        }

        // 2. Check Foreground Window
        IntPtr fgHwnd = Win32.GetForegroundWindow();
        if (fgHwnd == IntPtr.Zero) return false;

        // If user is focused on the desktop itself, wallpaper MUST run
        if (IsDesktopSurface(fgHwnd)) return false;

        // 3. If option "Pause when other app is focused" is turned ON:
        if (_config.PauseOnFloatingWindow)
        {
            // Any active floating window (browser, discord, notepad) pauses wallpaper
            return true;
        }

        // 4. Otherwise, default behavior: only pause when app is FULLSCREEN (F11 / Game / Maximized)
        if (Win32.GetWindowRect(fgHwnd, out Win32.RECT rect))
        {
            var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            bool coversScreen = (rect.Left <= screen.Left && rect.Top <= screen.Top &&
                                 rect.Right >= screen.Right && rect.Bottom >= screen.Bottom);
            return coversScreen;
        }

        return false;
    }

    private void SuspendWallpaper()
    {
        if (_isSuspended || _wallpaperProcess == null || _wallpaperProcess.HasExited) return;
        _isSuspended = true;

        try
        {
            if (_wallpaperHwnd != IntPtr.Zero)
            {
                Win32.ShowWindow(_wallpaperHwnd, 0); // SW_HIDE (GPU render 0%)
            }

            Win32.NtSuspendProcess(_wallpaperProcess.Handle);

            Win32.EmptyWorkingSet(_wallpaperProcess.Handle);
            using var current = Process.GetCurrentProcess();
            Win32.EmptyWorkingSet(current.Handle);
        }
        catch { }
    }

    private void ResumeWallpaper()
    {
        if (!_isSuspended || _wallpaperProcess == null || _wallpaperProcess.HasExited) return;
        _isSuspended = false;

        try
        {
            Win32.NtResumeProcess(_wallpaperProcess.Handle);

            if (_wallpaperHwnd != IntPtr.Zero)
            {
                Win32.ShowWindow(_wallpaperHwnd, 8); // SW_SHOWNA (show without stealing focus)
            }
        }
        catch { }
    }

    private void InstallMouseHook()
    {
        _mouseProc = HookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc,
            Win32.GetModuleHandle(curModule?.ModuleName), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_isSuspended && _wallpaperHwnd != IntPtr.Zero && _wallpaperProcess != null && !_wallpaperProcess.HasExited)
        {
            var hookStruct = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
            uint msg = (uint)wParam;

            IntPtr pointHwnd = Win32.WindowFromPoint(hookStruct.pt);
            if (IsDesktopSurface(pointHwnd))
            {
                var pt = hookStruct.pt;
                Win32.ScreenToClient(_wallpaperHwnd, ref pt);

                IntPtr mousePosLParam = (IntPtr)((pt.y << 16) | (pt.x & 0xFFFF));
                IntPtr mouseWParam = IntPtr.Zero;

                if (msg == Win32.WM_LBUTTONDOWN || msg == Win32.WM_MOUSEMOVE)
                {
                    if (Control.MouseButtons.HasFlag(MouseButtons.Left))
                    {
                        mouseWParam = (IntPtr)Win32.MK_LBUTTON;
                    }
                }

                if (msg == Win32.WM_LBUTTONDOWN || msg == Win32.WM_LBUTTONUP || msg == Win32.WM_MOUSEMOVE)
                {
                    Win32.PostMessage(_wallpaperHwnd, msg, mouseWParam, mousePosLParam);
                }
            }
        }

        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool IsDesktopSurface(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (hwnd == _wallpaperHwnd) return true;

        var sb = new System.Text.StringBuilder(256);
        Win32.GetClassName(hwnd, sb, sb.Capacity);
        string cls = sb.ToString();

        return cls == "SysListView32" || cls == "SHELLDLL_DefView" || cls == "WorkerW" || cls == "Progman";
    }

    private void OnSelectWallpaper(object? sender, EventArgs e)
    {
        using var ofd = new OpenFileDialog
        {
            Filter = "Executable Files (*.exe)|*.exe",
            Title = "Select Wallpaper Executable (Godot / Unity / etc.)"
        };

        if (ofd.ShowDialog() == DialogResult.OK)
        {
            _config.SelectedExePath = ofd.FileName;
            SaveConfig();
            StartWallpaper(ofd.FileName);
        }
    }

    private void RestartWallpaper()
    {
        if (!string.IsNullOrEmpty(_config.SelectedExePath) && File.Exists(_config.SelectedExePath))
        {
            StartWallpaper(_config.SelectedExePath);
        }
    }

    private void StartWallpaper(string exePath)
    {
        StopWallpaper();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                UseShellExecute = true
            };

            _wallpaperProcess = Process.Start(psi);
            if (_wallpaperProcess == null)
            {
                MessageBox.Show("Failed to start the executable file.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _currentWallpaperItem.Text = $"Active: {Path.GetFileName(exePath)}";

            Task.Run(() => AttachToDesktop(_wallpaperProcess));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AttachToDesktop(Process proc)
    {
        IntPtr hWndChild = IntPtr.Zero;
        for (int i = 0; i < 50; i++)
        {
            if (proc.HasExited) return;
            proc.Refresh();
            hWndChild = proc.MainWindowHandle;
            if (hWndChild != IntPtr.Zero) break;
            Thread.Sleep(100);
        }

        if (hWndChild == IntPtr.Zero) return;
        _wallpaperHwnd = hWndChild;

        IntPtr workerw = GetDesktopWorkerW();
        if (workerw == IntPtr.Zero) return;

        Win32.SetParent(hWndChild, workerw);

        int style = Win32.GetWindowLong(hWndChild, Win32.GWL_STYLE);
        style &= ~Win32.WS_CAPTION;
        style &= ~Win32.WS_THICKFRAME;
        style &= ~Win32.WS_MINIMIZEBOX;
        style &= ~Win32.WS_MAXIMIZEBOX;
        style &= ~Win32.WS_SYSMENU;
        Win32.SetWindowLong(hWndChild, Win32.GWL_STYLE, style);

        var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        Win32.SetWindowPos(hWndChild, IntPtr.Zero, 0, 0, bounds.Width, bounds.Height,
            Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW);
    }

    private IntPtr GetDesktopWorkerW()
    {
        IntPtr progman = Win32.FindWindow("Progman", null);
        IntPtr workerw = IntPtr.Zero;

        Win32.SendMessageTimeout(progman, 0x052C, new IntPtr(0), IntPtr.Zero,
            Win32.SendMessageTimeoutFlags.SMTO_NORMAL, 1000, out _);

        Win32.EnumWindows((toplevelHwnd, lParam) =>
        {
            IntPtr defView = Win32.FindWindowEx(toplevelHwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                workerw = Win32.FindWindowEx(IntPtr.Zero, toplevelHwnd, "WorkerW", null);
            }
            return true;
        }, IntPtr.Zero);

        return workerw != IntPtr.Zero ? workerw : progman;
    }

    private void StopWallpaper()
    {
        if (_isSuspended)
        {
            ResumeWallpaper();
        }

        _wallpaperHwnd = IntPtr.Zero;

        if (_wallpaperProcess != null && !_wallpaperProcess.HasExited)
        {
            try
            {
                IntPtr hWnd = _wallpaperProcess.MainWindowHandle;
                if (hWnd != IntPtr.Zero)
                {
                    Win32.SetParent(hWnd, IntPtr.Zero);
                    Win32.ShowWindow(hWnd, 0);
                }
                _wallpaperProcess.Kill();
                _wallpaperProcess.WaitForExit(1000);
            }
            catch { }
            _wallpaperProcess = null;
        }

        RefreshDesktopWallpaper();
        _currentWallpaperItem.Text = "Status: No active wallpaper";
    }

    private void RefreshDesktopWallpaper()
    {
        try
        {
            Win32.SystemParametersInfo(Win32.SPI_SETDESKWALLPAPER, 0, null, Win32.SPIF_UPDATEINIFILE | Win32.SPIF_SENDCHANGE);

            IntPtr progman = Win32.FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
            {
                Win32.RedrawWindow(progman, IntPtr.Zero, IntPtr.Zero,
                    Win32.RDW_INVALIDATE | Win32.RDW_ERASE | Win32.RDW_ALLCHILDREN | Win32.RDW_UPDATENOW);
            }

            Win32.EnumWindows((hwnd, lParam) =>
            {
                Win32.RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
                    Win32.RDW_INVALIDATE | Win32.RDW_ERASE | Win32.RDW_ALLCHILDREN | Win32.RDW_UPDATENOW);
                return true;
            }, IntPtr.Zero);
        }
        catch { }
    }

    private void OnToggleFloatingWindowPause(object? sender, EventArgs e)
    {
        _config.PauseOnFloatingWindow = !_config.PauseOnFloatingWindow;
        _floatingWindowPauseItem.Checked = _config.PauseOnFloatingWindow;
        SaveConfig();
    }

    private void OnToggleBatterySaverPause(object? sender, EventArgs e)
    {
        _config.PauseOnBatterySaver = !_config.PauseOnBatterySaver;
        _batterySaverPauseItem.Checked = _config.PauseOnBatterySaver;
        SaveConfig();
    }

    private void OnToggleAutoStart(object? sender, EventArgs e)
    {
        bool isCurrentlyEnabled = IsAutoStartEnabled();
        SetAutoStart(!isCurrentlyEnabled);
        _autoStartItem.Checked = !isCurrentlyEnabled;
    }

    private bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, false);
        return key?.GetValue(AppName) != null;
    }

    private void SetAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, true);
        if (key == null) return;

        if (enable)
        {
            string exePath = Application.ExecutablePath;
            key.SetValue(AppName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AppName, false);
        }
    }

    private AppConfig LoadConfig()
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                string json = File.ReadAllText(_configFilePath);
                return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
        }
        catch { }
        return new AppConfig();
    }

    private void SaveConfig()
    {
        try
        {
            string json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configFilePath, json);
        }
        catch { }
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _monitorTimer?.Stop();
        StopWallpaper();
        if (_mouseHook != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        _trayIcon.Visible = false;
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _monitorTimer?.Stop();
            _monitorTimer?.Dispose();
            StopWallpaper();
            if (_mouseHook != IntPtr.Zero)
            {
                Win32.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
            _trayIcon.Dispose();
            _trayMenu.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal static class Win32
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        SendMessageTimeoutFlags fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [Flags]
    public enum SendMessageTimeoutFlags : uint
    {
        SMTO_NORMAL = 0x0,
        SMTO_BLOCK = 0x0001,
        SMTO_ABORTIFHUNG = 0x0002,
        SMTO_NOTIMEOUTIFNOTHUNG = 0x0008
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    public const int GWL_STYLE = -16;
    public const int WS_CAPTION = 0x00C00000;
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_MINIMIZEBOX = 0x00020000;
    public const int WS_MAXIMIZEBOX = 0x00010000;
    public const int WS_SYSMENU = 0x00080000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public const uint RDW_INVALIDATE = 0x0001;
    public const uint RDW_ERASE = 0x0004;
    public const uint RDW_ALLCHILDREN = 0x0080;
    public const uint RDW_UPDATENOW = 0x0100;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    public const uint SPI_SETDESKWALLPAPER = 0x0014;
    public const uint SPIF_UPDATEINIFILE = 0x01;
    public const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(uint uAction, uint uParam, string? lpvParam, uint fuWinIni);

    // Mouse Hook & Message Forwarding
    public const int WH_MOUSE_LL = 14;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint MK_LBUTTON = 0x0001;

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT Point);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    // Monitor & Process Suspend / Memory Trim
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("ntdll.dll", SetLastError = true)]
    public static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll", SetLastError = true)]
    public static extern int NtResumeProcess(IntPtr processHandle);

    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyWorkingSet(IntPtr hProcess);
}