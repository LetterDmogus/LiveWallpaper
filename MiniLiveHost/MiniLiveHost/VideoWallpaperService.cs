using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MiniLiveHost;

/// <summary>
/// Handler khusus untuk Video Wallpaper via hardware-accelerated MPV player.
/// Terisolasi dari logika EXE sehingga modifikasi video tidak akan mempengaruhi game/interactive wallpaper.
/// </summary>
public class VideoWallpaperService
{
    private Process? _wallpaperProcess;
    private IntPtr _wallpaperHwnd = IntPtr.Zero;

    private readonly string _mpvDirectory;
    private readonly string _mpvExePath;

    public Process? CurrentProcess => _wallpaperProcess;
    public IntPtr WindowHandle => _wallpaperHwnd;
    public bool IsRunning => _wallpaperProcess != null && !_wallpaperProcess.HasExited;

    public VideoWallpaperService()
    {
        _mpvDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mpv");
        _mpvExePath = Path.Combine(_mpvDirectory, "mpv.exe");
    }

    public async Task<bool> EnsureMpvInstalledAsync()
    {
        if (File.Exists(_mpvExePath)) return true;

        var dr = MessageBox.Show(
            "MPV video engine is required to play video wallpapers with 0% CPU (hardware-accelerated).\n\nWould you like to download and install MPV automatically now (~33 MB)?",
            "Video Engine Setup",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (dr != DialogResult.Yes) return false;

        return await DownloadAndInstallMpvAsync();
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

    public bool Start(string videoPath, bool isMuted, Action<string> onStatusChanged)
    {
        Stop();

        if (!File.Exists(_mpvExePath))
        {
            MessageBox.Show("MPV player not found. Please select video again to download it.", "Engine Missing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        try
        {
            IntPtr workerw = Win32.GetDesktopWorkerW();
            if (workerw == IntPtr.Zero) return false;

            var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            string muteArg = isMuted ? "--mute=yes" : "--mute=no";

            // MPV flags dioptimasi khusus untuk wallpaper (ultra-rendah CPU & GPU):
            // --wid: pasang langsung ke kanvas WorkerW
            // --vo=gpu, --profile=fast, --hwdec=auto-safe: hemat GPU/CPU
            // --media-controls=no: tidak memunculkan overlay media Windows
            // --input-cursor-passthrough=yes: mouse pass-through agar klik desktop selalu tembus
            string args = $"--wid={workerw.ToInt64()} --loop-file=inf --vo=gpu --profile=fast --hwdec=auto-safe --scale=bilinear --dscale=bilinear --video-sync=audio --keep-open {muteArg} --no-osc --no-osd-bar --media-controls=no --load-context-menu=no --input-cursor-passthrough=yes --no-input-default-bindings --no-input-cursor --geometry={screen.Width}x{screen.Height}+0+0 \"{videoPath}\"";

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
                return false;
            }

            onStatusChanged($"Active (Video): {Path.GetFileName(videoPath)}");

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

            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to start video wallpaper: {ex.Message}\n\nStack:\n{ex.StackTrace}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    public void Stop()
    {
        _wallpaperHwnd = IntPtr.Zero;

        if (_wallpaperProcess != null)
        {
            var proc = _wallpaperProcess;
            _wallpaperProcess = null;

            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill();
                }
            }
            catch { }
            finally
            {
                Task.Run(() =>
                {
                    try
                    {
                        proc.WaitForExit(1000);
                        proc.Dispose();
                    }
                    catch { }
                });
            }
        }
    }
}
