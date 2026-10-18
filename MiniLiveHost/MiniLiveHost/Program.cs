using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
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

    private readonly string _mpvDirectory;
    private readonly string _mpvExePath;

    public TrayApplicationContext()
    {
        _configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        _config = LoadConfig();

        _mpvDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mpv");
        _mpvExePath = Path.Combine(_mpvDirectory, "mpv.exe");

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

        if (!string.IsNullOrEmpty(_config.SelectedWallpaperPath) && File.Exists(_config.SelectedWallpaperPath))
        {
            StartWallpaper(_config.SelectedWallpaperPath, _config.WallpaperType);
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
        if (_wallpaperProcess == null || _wallpaperProcess.HasExited)
            return;

        bool shouldPause = ShouldSuspendWallpaper();

        if (shouldPause)
        {
            _pauseConditionDurationSeconds++;
            // When PauseOnFloatingWindow is enabled, pause immediately after 1 second instead of waiting 3 seconds
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
        // 1. Check Battery Saver / Unplugged condition if enabled
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

        // 2. Check Foreground Window
        IntPtr fgHwnd = Win32.GetForegroundWindow();
        if (fgHwnd == IntPtr.Zero) return false;

        // If user is focused on the desktop itself, wallpaper MUST run
        if (IsDesktopSurface(fgHwnd)) return false;

        // 3. If option "Pause when other app is focused" is turned ON:
        if (_config.PauseOnFloatingWindow)
        {
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
                Win32.ShowWindow(_wallpaperHwnd, 0); // SW_HIDE
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
                Win32.ShowWindow(_wallpaperHwnd, 8); // SW_SHOWNA
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
        if (nCode >= 0 && _wallpaperHwnd != IntPtr.Zero && !_isSuspended && _config.WallpaperType == WallpaperType.Executable)
        {
            uint msg = (uint)wParam.ToInt64();
            if (msg == Win32.WM_LBUTTONDOWN || msg == Win32.WM_LBUTTONUP || msg == Win32.WM_MOUSEMOVE)
            {
                var hookStruct = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
                IntPtr targetWnd = Win32.WindowFromPoint(hookStruct.pt);

                if (IsDesktopSurface(targetWnd))
                {
                    Win32.POINT pt = hookStruct.pt;
                    Win32.ScreenToClient(_wallpaperHwnd, ref pt);

                    IntPtr lparamCoord = new IntPtr((pt.y << 16) | (pt.x & 0xFFFF));
                    IntPtr wparamFlags = (msg == Win32.WM_LBUTTONDOWN || (msg == Win32.WM_MOUSEMOVE && (Control.MouseButtons & MouseButtons.Left) != 0))
                        ? (IntPtr)Win32.MK_LBUTTON
                        : IntPtr.Zero;

                    Win32.PostMessage(_wallpaperHwnd, msg, wparamFlags, lparamCoord);
                }
            }
        }

        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool IsDesktopSurface(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;

        var sb = new System.Text.StringBuilder(256);
        Win32.GetClassName(hWnd, sb, sb.Capacity);
        string className = sb.ToString();

        return className == "Progman" ||
               className == "WorkerW" ||
               className == "SHELLDLL_DefView" ||
               className == "SysListView32";
    }

    private void OnSelectExeWallpaper(object? sender, EventArgs e)
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
            StartWallpaper(ofd.FileName, WallpaperType.Executable);
        }
    }

    private async void OnSelectVideoWallpaper(object? sender, EventArgs e)
    {
        // 1. Ensure mpv engine is installed; if not, ask and download automatically
        if (!File.Exists(_mpvExePath))
        {
            var dr = MessageBox.Show(
                "MPV video engine is required to play video wallpapers with 0% CPU (hardware-accelerated).\n\nWould you like to download and install MPV automatically now (~33 MB)?",
                "Video Engine Setup",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (dr != DialogResult.Yes) return;

            bool downloaded = await DownloadAndInstallMpvAsync();
            if (!downloaded)
            {
                MessageBox.Show("Failed to download or extract MPV. Please check your internet connection.", "Download Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }

        // 2. Open file dialog to choose video
        using var ofd = new OpenFileDialog
        {
            Filter = "Video Files (*.mp4;*.mkv;*.webm;*.avi;*.mov;*.wmv)|*.mp4;*.mkv;*.webm;*.avi;*.mov;*.wmv|All Files (*.*)|*.*",
            Title = "Select Video Wallpaper"
        };

        if (ofd.ShowDialog() == DialogResult.OK)
        {
            _config.SelectedWallpaperPath = ofd.FileName;
            _config.WallpaperType = WallpaperType.Video;
            SaveConfig();
            StartWallpaper(ofd.FileName, WallpaperType.Video);
        }
    }

    private async Task<bool> DownloadAndInstallMpvAsync()
    {
        using var progressForm = new Form
        {
            Text = "Downloading MPV Video Engine...",
            Size = new Size(420, 150),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false,
            ControlBox = false
        };

        var label = new Label
        {
            Text = "Downloading hardware-accelerated MPV player (~33 MB)...",
            Location = new Point(20, 20),
            AutoSize = true
        };
        progressForm.Controls.Add(label);

        var pbar = new ProgressBar
        {
            Location = new Point(20, 55),
            Size = new Size(360, 25),
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30
        };
        progressForm.Controls.Add(pbar);

        progressForm.Show();

        return await Task.Run(() =>
        {
            string tempArchive = Path.Combine(Path.GetTempPath(), "mpv_engine.7z");
            try
            {
                // High-performance direct release build from shinchiro
                string downloadUrl = "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261018/mpv-x86_64-20261018-git-01e2533f31.7z";

                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                    using var response = httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                    response.EnsureSuccessStatusCode();

                    using var fs = new FileStream(tempArchive, FileMode.Create, FileAccess.Write, FileShare.None);
                    response.Content.CopyToAsync(fs).GetAwaiter().GetResult();
                }

                Directory.CreateDirectory(_mpvDirectory);

                // Use Windows built-in tar.exe which natively extracts .7z
                var tarPsi = new ProcessStartInfo
                {
                    FileName = "tar.exe",
                    Arguments = $"-xf \"{tempArchive}\" -C \"{_mpvDirectory}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };

                using var tarProc = Process.Start(tarPsi);
                tarProc?.WaitForExit(30000);

                return File.Exists(_mpvExePath);
            }
            catch
            {
                return false;
            }
            finally
            {
                try { if (File.Exists(tempArchive)) File.Delete(tempArchive); } catch { }
                progressForm.Invoke(() => progressForm.Close());
            }
        });
    }

    private void RestartWallpaper()
    {
        if (!string.IsNullOrEmpty(_config.SelectedWallpaperPath) && File.Exists(_config.SelectedWallpaperPath))
        {
            StartWallpaper(_config.SelectedWallpaperPath, _config.WallpaperType);
        }
    }

    private void StartWallpaper(string path, WallpaperType type)
    {
        StopWallpaper();

        if (type == WallpaperType.Video)
        {
            StartVideoWallpaper(path);
        }
        else
        {
            StartExeWallpaper(path);
        }
    }

    private void StartVideoWallpaper(string videoPath)
    {
        if (!File.Exists(_mpvExePath))
        {
            MessageBox.Show("MPV player not found. Please select video again to download it.", "Engine Missing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            IntPtr workerw = GetDesktopWorkerW();
            if (workerw == IntPtr.Zero) return;

            var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);

            // MPV flags dioptimasi khusus untuk wallpaper (ultra-rendah CPU & GPU):
            // --vo=gpu: Gunakan direct shader GPU renderer standar yang sangat ringan (bukan gpu-next yang berat)
            // --profile=fast: Matikan post-processing shader berat yang tidak perlu
            // --hwdec=auto-safe: Gunakan hardware video decoding chip (DXVA2 / D3D11VA)
            // --scale=bilinear / dscale=bilinear: Interpolasi video ringan, hemat GPU hingga 70%
            // --video-sync=audio: Mencegah render loop overhead tinggi
            // --no-osc --no-osd-bar: Matikan UI controller
            string muteArg = _config.VideoMuted ? "--mute=yes" : "--mute=no";
            string args = $"--wid={workerw.ToInt64()} --loop-file=inf --vo=gpu --profile=fast --hwdec=auto-safe --scale=bilinear --dscale=bilinear --video-sync=audio --keep-open {muteArg} --no-osc --no-osd-bar --no-input-default-bindings --no-input-cursor --geometry={screen.Width}x{screen.Height}+0+0 \"{videoPath}\"";

            var psi = new ProcessStartInfo
            {
                FileName = _mpvExePath,
                Arguments = args,
                WorkingDirectory = _mpvDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _wallpaperProcess = Process.Start(psi);
            if (_wallpaperProcess == null)
            {
                MessageBox.Show("Failed to launch MPV video player.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _currentWallpaperItem.Text = $"Active (Video): {Path.GetFileName(videoPath)}";

            Task.Run(() =>
            {
                for (int i = 0; i < 50; i++)
                {
                    if (_wallpaperProcess == null || _wallpaperProcess.HasExited) return;
                    _wallpaperProcess.Refresh();
                    IntPtr hwnd = _wallpaperProcess.MainWindowHandle;
                    if (hwnd != IntPtr.Zero)
                    {
                        _wallpaperHwnd = hwnd;
                        break;
                    }
                    Thread.Sleep(100);
                }
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to start video wallpaper: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StartExeWallpaper(string exePath)
    {
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

            Task.Run(() => AttachExeToDesktop(_wallpaperProcess));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AttachExeToDesktop(Process proc)
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

    private void OnToggleVideoMute(object? sender, EventArgs e)
    {
        _config.VideoMuted = !_config.VideoMuted;
        _videoMuteItem.Checked = _config.VideoMuted;
        SaveConfig();

        // If currently playing video, restart to apply mute setting
        if (_config.WallpaperType == WallpaperType.Video && _wallpaperProcess != null && !_wallpaperProcess.HasExited)
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

public static class Win32
{
    public const int GWL_STYLE = -16;
    public const int WS_CAPTION = 0x00C00000;
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_MINIMIZEBOX = 0x00020000;
    public const int WS_MAXIMIZEBOX = 0x00010000;
    public const int WS_SYSMENU = 0x00080000;

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [Flags]
    public enum SendMessageTimeoutFlags : uint
    {
        SMTO_NORMAL = 0x0,
        SMTO_BLOCK = 0x1,
        SMTO_ABORTIFHUNG = 0x2,
        SMTO_NOTIMEOUTIFNOTHUNG = 0x8
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        SendMessageTimeoutFlags fuFlags, uint uTimeout, out IntPtr lpdwResult);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    // Desktop wallpaper restore & redraw flags
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