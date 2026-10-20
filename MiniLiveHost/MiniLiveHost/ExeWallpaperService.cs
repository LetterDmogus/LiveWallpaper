using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MiniLiveHost;

/// <summary>
/// Handler khusus untuk Interactive Executable Wallpaper (.exe seperti Godot, Unity).
/// Menggunakan implementasi "Set & Forget" murni dari versi awal yang terbukti mulus tanpa loop yang mengganggu desktop.
/// </summary>
public class ExeWallpaperService
{
    private Process? _wallpaperProcess;
    private IntPtr _wallpaperHwnd = IntPtr.Zero;

    public Process? CurrentProcess => _wallpaperProcess;
    public IntPtr WindowHandle => _wallpaperHwnd;
    public bool IsRunning => _wallpaperProcess != null && !_wallpaperProcess.HasExited;

    public bool Start(string exePath, Action<string> onStatusChanged)
    {
        Stop();

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
                return false;
            }

            onStatusChanged($"Active: {Path.GetFileName(exePath)}");

            var proc = _wallpaperProcess;
            Task.Run(() => AttachToDesktop(proc));
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"An error occurred starting EXE wallpaper: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void AttachToDesktop(Process proc)
    {
        IntPtr hWndChild = IntPtr.Zero;

        // Tunggu window game muncul (cek MainWindowHandle dan EnumWindows untuk kestabilan)
        for (int i = 0; i < 60; i++)
        {
            if (proc.HasExited) return;
            proc.Refresh();

            hWndChild = proc.MainWindowHandle;
            if (hWndChild == IntPtr.Zero)
            {
                Win32.EnumWindows((h, l) =>
                {
                    Win32.GetWindowThreadProcessId(h, out uint pid);
                    if (pid == proc.Id && Win32.IsWindowVisible(h))
                    {
                        hWndChild = h;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
            }

            if (hWndChild != IntPtr.Zero) break;
            Thread.Sleep(100);
        }

        if (hWndChild == IntPtr.Zero) return;
        _wallpaperHwnd = hWndChild;

        IntPtr workerw = Win32.GetDesktopWorkerW();
        if (workerw == IntPtr.Zero) return;

        // 1. Buang border/caption standar window
        int style = Win32.GetWindowLong(hWndChild, Win32.GWL_STYLE);
        style &= ~Win32.WS_CAPTION;
        style &= ~Win32.WS_THICKFRAME;
        style &= ~Win32.WS_MINIMIZEBOX;
        style &= ~Win32.WS_MAXIMIZEBOX;
        style &= ~Win32.WS_SYSMENU;
        Win32.SetWindowLong(hWndChild, Win32.GWL_STYLE, style);

        // 2. Set parent ke WorkerW (Metode versi awal)
        Win32.SetParent(hWndChild, workerw);

        // 3. Posisikan window 1 KALI saja dengan SWP_NOZORDER (Tanpa loop yang mengganggu desktop!)
        var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        Win32.SetWindowPos(hWndChild, IntPtr.Zero, 0, 0, bounds.Width, bounds.Height,
            Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW);
    }

    public async Task StopAsync()
    {
        IntPtr hwndToHide = _wallpaperHwnd;
        _wallpaperHwnd = IntPtr.Zero;

        if (_wallpaperProcess != null)
        {
            var proc = _wallpaperProcess;
            _wallpaperProcess = null;

            try
            {
                if (hwndToHide != IntPtr.Zero)
                {
                    Win32.ShowWindow(hwndToHide, 0); // SW_HIDE
                    Win32.SetParent(hwndToHide, IntPtr.Zero);
                }

                if (!proc.HasExited)
                {
                    proc.Kill();
                }
            }
            catch { }

            // Pastikan proses Godot benar-benar sudah terminate sebelum lanjut ke MPV
            await Task.Run(() =>
            {
                try
                {
                    proc.WaitForExit(3000);
                    proc.Dispose();
                }
                catch { }
            });
        }
    }

    public void Stop()
    {
        _ = StopAsync();
    }
}
