using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
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

        // Tangkap unhandled exception agar tidak silent crash dan langsung memunculkan pesan error
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) =>
        {
            MessageBox.Show($"UI Thread Exception:\n{e.Exception.Message}\n\nStack:\n{e.Exception.StackTrace}", "MiniLiveHost Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                MessageBox.Show($"AppDomain Exception:\n{ex.Message}\n\nStack:\n{ex.StackTrace}", "MiniLiveHost Crash", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        Application.Run(new TrayApplicationContext());
    }
}

public enum WallpaperType
{
    Executable,
    Video
}

public class AppConfig
{
    public string? SelectedWallpaperPath { get; set; }
    public WallpaperType WallpaperType { get; set; } = WallpaperType.Executable;
    public bool PauseOnFloatingWindow { get; set; } = false; // Pause when focused on any non-desktop window
    public bool PauseOnBatterySaver { get; set; } = true;    // Pause when on battery or battery saver
    public bool VideoMuted { get; set; } = true;             // Mute video wallpaper audio
}

public class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _trayMenu;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ToolStripMenuItem _currentWallpaperItem;
    private readonly ToolStripMenuItem _floatingWindowPauseItem;
    private readonly ToolStripMenuItem _batterySaverPauseItem;
    private readonly ToolStripMenuItem _videoMuteItem;

    private readonly string _configFilePath;
    private AppConfig _config;

    // Layanan terpisah untuk masing-masing tipe wallpaper
    private readonly ExeWallpaperService _exeService = new();
    private readonly VideoWallpaperService _videoService = new();

    // Mouse hook untuk interaksi patting/klik pada Executable wallpaper
    private IntPtr _mouseHook = IntPtr.Zero;
    private Win32.LowLevelMouseProc? _mouseProc;

    // Monitor timer untuk Pause saat fullscreen / hemat baterai
    private System.Windows.Forms.Timer? _monitorTimer;

    private int _pauseConditionDurationSeconds = 0;
    private bool _isSuspended = false;
    private const int PauseThresholdSeconds = 3;

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

        var selectExeItem = new ToolStripMenuItem("Select Interactive Wallpaper (.exe)...", null, OnSelectExeWallpaper);
        _trayMenu.Items.Add(selectExeItem);

        var selectVideoItem = new ToolStripMenuItem("Select Video Wallpaper (.mp4, .mkv, .webm, etc.)...", null, OnSelectVideoWallpaper);
        _trayMenu.Items.Add(selectVideoItem);

        var restartItem = new ToolStripMenuItem("Restart Wallpaper", null, (s, e) => RestartWallpaper());
        _trayMenu.Items.Add(restartItem);

        var stopItem = new ToolStripMenuItem("Stop Wallpaper (Debug)", null, (s, e) =>
        {
            StopWallpaper(refreshDesktop: true);
            MessageBox.Show("All wallpaper processes stopped and cleaned up.", "Debug Stop", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        _trayMenu.Items.Add(stopItem);

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

        _videoMuteItem = new ToolStripMenuItem("Mute Video Wallpaper", null, OnToggleVideoMute)
        {
            Checked = _config.VideoMuted
        };
        pauseMenu.DropDownItems.Add(_videoMuteItem);

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

        if (!string.IsNullOrEmpty(_config.SelectedWallpaperPath))
        {
            if (File.Exists(_config.SelectedWallpaperPath))
            {
                StartWallpaper(_config.SelectedWallpaperPath, _config.WallpaperType);
            }
            else
            {
                string missingFile = Path.GetFileName(_config.SelectedWallpaperPath);
                _currentWallpaperItem.Text = $"Status: File not found ({missingFile})";
                _trayIcon.ShowBalloonTip(3000, "Wallpaper Not Found",
                    $"The previous wallpaper file was deleted or moved:\n{missingFile}\n\nPlease select a new wallpaper from the tray menu.",
                    ToolTipIcon.Warning);
            }
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

    private Process? GetActiveProcess()
    {
        return _config.WallpaperType == WallpaperType.Video
            ? _videoService.CurrentProcess
            : _exeService.CurrentProcess;
    }

    private IntPtr GetActiveWindowHandle()
    {
        return _config.WallpaperType == WallpaperType.Video
            ? _videoService.WindowHandle
            : _exeService.WindowHandle;
    }

    private void OnMonitorTick(object? sender, EventArgs e)
    {
        var proc = GetActiveProcess();
        if (proc == null || proc.HasExited)
            return;

        bool shouldPause = ShouldSuspendWallpaper();

        if (shouldPause)
        {
            _pauseConditionDurationSeconds++;
            int threshold = _config.PauseOnFloatingWindow ? 1 : PauseThresholdSeconds;
            if (_pauseConditionDurationSeconds >= threshold && !_isSuspended)
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
        if (_config.PauseOnBatterySaver)
        {
            var powerStatus = SystemInformation.PowerStatus;
            if (powerStatus.PowerLineStatus == PowerLineStatus.Offline)
            {
                if (powerStatus.BatteryLifePercent < 0.35f)
                {
                    return true;
                }
            }
        }

        IntPtr fgHwnd = Win32.GetForegroundWindow();
        if (fgHwnd == IntPtr.Zero) return false;

        // Kalau window yang sedang foreground itu ADALAH wallpaper kita sendiri,
        // jangan diperlakukan sebagai "app lain yang menutupi layar" - kalau tidak
        // kita akan hide lalu show berulang (flicker) tanpa alasan.
        if (fgHwnd == GetActiveWindowHandle()) return false;

        if (IsDesktopSurface(fgHwnd)) return false;

        if (_config.PauseOnFloatingWindow)
        {
            return true;
        }

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
        var proc = GetActiveProcess();
        if (_isSuspended || proc == null || proc.HasExited) return;

        // Jangan bekukan proses sebelum window wallpaper benar-benar jadi.
        // Kalau proses di-suspend duluan (mis. saat Windows boot, foreground sudah
        // aplikasi lain sehingga PauseOnFloatingWindow langsung aktif), Godot
        // tidak pernah sempat membuat window-nya -> wallpaper tidak pernah muncul.
        IntPtr hwnd = GetActiveWindowHandle();
        if (hwnd == IntPtr.Zero) return;

        _isSuspended = true;

        try
        {
            Win32.ShowWindow(hwnd, 0); // SW_HIDE

            Win32.NtSuspendProcess(proc.Handle);
            Win32.EmptyWorkingSet(proc.Handle);

            using var current = Process.GetCurrentProcess();
            Win32.EmptyWorkingSet(current.Handle);
        }
        catch
        {
            _isSuspended = false;
        }
    }

    private void ResumeWallpaper()
    {
        var proc = GetActiveProcess();
        if (!_isSuspended || proc == null || proc.HasExited) return;
        _isSuspended = false;

        try
        {
            Win32.NtResumeProcess(proc.Handle);

            IntPtr hwnd = GetActiveWindowHandle();
            if (hwnd != IntPtr.Zero)
            {
                // SW_SHOWNOACTIVATE (4): tampilkan tanpa mengaktifkan window.
                // SW_SHOWNA (8) mirip SW_SHOW dan bisa meninggikan z-order.
                Win32.ShowWindow(hwnd, 4); // SW_SHOWNOACTIVATE
            }
        }
        catch { }
    }

    private void InstallMouseHook()
    {
        _mouseProc = HookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc, Win32.GetModuleHandle(curModule?.ModuleName), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Hanya forward mouse event ke EXE wallpaper (bukan video) dan saat tidak di-pause
        if (nCode >= 0 && !_isSuspended && _config.WallpaperType == WallpaperType.Executable && _exeService.IsRunning)
        {
            uint msg = (uint)wParam.ToInt64();
            if (msg == Win32.WM_LBUTTONDOWN || msg == Win32.WM_LBUTTONUP || msg == Win32.WM_MOUSEMOVE)
            {
                var hookStruct = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
                IntPtr targetWnd = Win32.WindowFromPoint(hookStruct.pt);

                if (IsDesktopSurface(targetWnd))
                {
                    IntPtr hwnd = _exeService.WindowHandle;
                    if (hwnd != IntPtr.Zero)
                    {
                        Win32.POINT pt = hookStruct.pt;
                        Win32.ScreenToClient(hwnd, ref pt);

                        IntPtr lparamCoord = new IntPtr((pt.y << 16) | (pt.x & 0xFFFF));
                        IntPtr wparamFlags = (msg == Win32.WM_LBUTTONDOWN || (msg == Win32.WM_MOUSEMOVE && (Control.MouseButtons & MouseButtons.Left) != 0))
                            ? (IntPtr)Win32.MK_LBUTTON
                            : IntPtr.Zero;

                        Win32.PostMessage(hwnd, msg, wparamFlags, lparamCoord);
                    }
                }
            }
        }

        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static readonly System.Collections.Generic.HashSet<string> DesktopWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "SHELLDLL_DefView",
        "SysListView32",
        "#32768",                       // Win32 Context Menu
        "Windows.UI.Core.CoreWindow",   // Win10/11 Start, Action Center, XAML Context Menu
        "XamlExplorerHostIslandWindow", // Win11 Desktop Context Menu & TaskView
        "Shell_TrayWnd",                // Taskbar
        "Shell_SecondaryTrayWnd",       // Multi-monitor Taskbar
        "NotifyIconOverflowWindow",     // Tray overflow popup
        "TopLevelWindowForOverflowXamlIsland"
    };

    private bool IsDesktopSurface(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        if (hWnd == Win32.GetDesktopWindow() || hWnd == Win32.GetShellWindow()) return true;

        var sb = new System.Text.StringBuilder(256);
        Win32.GetClassName(hWnd, sb, sb.Capacity);
        string className = sb.ToString();

        if (DesktopWindowClasses.Contains(className)) return true;

        Win32.GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid != 0)
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                if (proc.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                {
                    if (!className.Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch { }
        }

        return false;
    }

    private async void OnSelectExeWallpaper(object? sender, EventArgs e)
    {
        using var ofd = new OpenFileDialog
        {
            Filter = "Executable Files (*.exe)|*.exe",
            Title = "Select Interactive Wallpaper (.exe)"
        };

        if (ofd.ShowDialog() == DialogResult.OK)
        {
            _config.SelectedWallpaperPath = ofd.FileName;
            _config.WallpaperType = WallpaperType.Executable;
            SaveConfig();
            await StartWallpaperAsync(ofd.FileName, WallpaperType.Executable);
        }
    }

    private async void OnSelectVideoWallpaper(object? sender, EventArgs e)
    {
        try
        {
            bool installed = await _videoService.EnsureMpvInstalledAsync();
            if (!installed) return;

            using var ofd = new OpenFileDialog
            {
                Filter = "Video Files (*.mp4;*.mkv;*.webm;*.avi;*.mov;*.wmv)|*.mp4;*.mkv;*.webm;*.avi;*.mov;*.wmv|All Files (*.*)|*.*",
                Title = "Select Video Wallpaper"
            };

            if (ofd.ShowDialog() == DialogResult.OK)
            {
                string chosenFile = ofd.FileName;
                _config.SelectedWallpaperPath = chosenFile;
                _config.WallpaperType = WallpaperType.Video;
                SaveConfig();

                await StartWallpaperAsync(chosenFile, WallpaperType.Video);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error in OnSelectVideoWallpaper:\n{ex.Message}\n\nStack:\n{ex.StackTrace}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void RestartWallpaper()
    {
        if (!string.IsNullOrEmpty(_config.SelectedWallpaperPath) && File.Exists(_config.SelectedWallpaperPath))
        {
            await StartWallpaperAsync(_config.SelectedWallpaperPath, _config.WallpaperType);
        }
    }

    private async Task StartWallpaperAsync(string path, WallpaperType type)
    {
        // Stop monitor timer agar tidak ada race condition saat proses ganti wallpaper
        _monitorTimer?.Stop();
        try
        {
            _currentWallpaperItem.Text = "Status: Stopping previous wallpaper...";
            await StopWallpaperAsync(refreshDesktop: true);

            if (type == WallpaperType.Video)
            {
                _videoService.Start(path, _config.VideoMuted, status => _currentWallpaperItem.Text = status);
            }
            else
            {
                _exeService.Start(path, status => _currentWallpaperItem.Text = status);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error in StartWallpaperAsync:\n{ex.Message}\n\nStack:\n{ex.StackTrace}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            // Restart monitor timer setelah switching selesai
            _monitorTimer?.Start();
        }
    }

    private void StartWallpaper(string path, WallpaperType type)
    {
        _ = StartWallpaperAsync(path, type);
    }

    private async Task StopWallpaperAsync(bool refreshDesktop = true)
    {
        if (_isSuspended)
        {
            ResumeWallpaper();
        }

        // Tunggu proses EXE benar-benar selesai keluar
        await _exeService.StopAsync();
        _videoService.Stop();

        if (refreshDesktop)
        {
            RefreshDesktopWallpaper();
        }
        _currentWallpaperItem.Text = "Status: No active wallpaper";
    }

    private void StopWallpaper(bool refreshDesktop = true)
    {
        _ = StopWallpaperAsync(refreshDesktop);
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

    private void OnToggleVideoMute(object? sender, EventArgs e)
    {
        _config.VideoMuted = !_config.VideoMuted;
        _videoMuteItem.Checked = _config.VideoMuted;
        SaveConfig();

        if (_config.WallpaperType == WallpaperType.Video && _videoService.IsRunning)
        {
            RestartWallpaper();
        }
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
                var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                if (cfg != null) return cfg;
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
