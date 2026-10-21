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
            // Ingat window yang sedang aktif sebelum wallpaper start.
            // Godot pada show_window() memanggil SetForegroundWindow() sehingga
            // wallpaper sempat merebut fokus; nanti kita pulihkan ke window ini.
            IntPtr previousForeground = Win32.GetForegroundWindow();

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
            Task.Run(() => AttachToDesktop(proc, previousForeground));
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"An error occurred starting EXE wallpaper: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void AttachToDesktop(Process proc, IntPtr previousForeground)
    {
        IntPtr hWndChild = IntPtr.Zero;

        // Tunggu window game muncul.
        //
        // PENTING: jangan_ANDalkan proc.Id. Dengan UseShellExecute=true, process
        // di-spawn lewat shell/explorer sehingga PID yang-reported .NET bisa
        // berbeda dari PID proses yang benar-benar punya window (window Godot
        // punya title kosong sehingga Process.MainWindowHandle juga selalu 0).
        // Jadi cocokkan window berdasarkan NAMA PROSES + WAKTU START.
        string procName = SafeProcessName(proc);
        DateTime startTime = DateTime.Now;

        for (int i = 0; i < 100; i++)
        {
            proc.Refresh();

            hWndChild = FindWallpaperWindow(procName, startTime);
            if (hWndChild != IntPtr.Zero) break;
            Thread.Sleep(100);
        }

        if (hWndChild == IntPtr.Zero) return;
        _wallpaperHwnd = hWndChild;

        IntPtr workerw = Win32.GetDesktopWorkerW();
        if (workerw == IntPtr.Zero) return;

        // 1. Ubah gaya window menjadi CHILD (WAJIB sebelum SetParent).
        //    MSDN: "if hWndNewParent is not NULL and the window was previously a child
        //    of the desktop, you should clear the WS_POPUP style and set the WS_CHILD
        //    style before calling SetParent." Tanpa ini window tetap diperlakukan
        //    sebagai top-level dan bisa naik ke depan jendela lain.
        try
        {
            ApplyDesktopStyle(hWndChild);
        }
        catch
        {
            return;
        }

        // 2. Set parent ke WorkerW
        Win32.SetParent(hWndChild, workerw);

        // 3. Posisikan window 1 KALI saja (Tanpa loop yang mengganggu desktop!)
        //
        //    PENTING: jangan posisikan PERSIS di 0,0 dengan ukuran layar penuh.
        //    Godot 4.3 (display_server_windows.cpp:4851) mendeteksi kondisi:
        //        window_rect.position == screen_position && window_rect.size == screen_size
        //    lalu menganggap window itu FULLSCREEN, memanggil _update_window_style()
        //    yang menulis ulang GWL_STYLE -> mengembalikan WS_POPUP -> window jadi
        //    top-level lagi dan menutupi layar. OFFSET 1px (yang tidak terlihat)
        //    mencegah deteksi itu, sehingga style child kita tetap berlaku.
        var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        Win32.SetWindowPos(hWndChild, Win32.HWND_BOTTOM, -1, -1, bounds.Width + 2, bounds.Height + 2,
            Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW);

        // 4. Kembalikan fokus ke window yang sebelumnya aktif.
        //    Godot calling SetForegroundWindow() saat show_window(), sehingga
        //    wallpaper sempat menjadi foreground window (fokus keyboard hilang dari
        //    aplikasi yang sedang Anda pakai). Pulihkan di sini.
        try
        {
            if (previousForeground != IntPtr.Zero && previousForeground != hWndChild)
            {
                Win32.SetForegroundWindow(previousForeground);
            }
        }
        catch { }
    }

    /// <summary>
    /// Baca GWL_STYLE / GWL_EXSTYLE sebagai int 32-bit.
    /// GetWindowLongPtr mengembalikan pointer-sized value, jadi pada proses 64-bit
    /// nilainya bisa &gt; int.MaxValue dan .ToInt32() akan melempar OverflowException.
    /// Window style memang hanya 32-bit, jadi masking ke 32 bit sudah cukup.
    /// </summary>
    private static int ReadWindowLong(IntPtr hwnd, int index)
    {
        long raw = Win32.GetWindowLongPtr(hwnd, index).ToInt64();
        return unchecked((int)(raw & 0xFFFFFFFFL));
    }

    /// <summary>
    /// Ambil nama proses tanpa exception (proses bisa sudah mati / tidak punya nama).
    /// </summary>
    private static string SafeProcessName(Process proc)
    {
        try
        {
            proc.Refresh();
            return proc.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Cari window milik proses wallpaper berdasarkan NAMA PROSES.
    /// Menghindari masalah PID yang tidak cocok saat process di-spawn via ShellExecute.
    /// </summary>
    private static IntPtr FindWallpaperWindow(string processName, DateTime startTime)
    {
        if (string.IsNullOrEmpty(processName)) return IntPtr.Zero;

        IntPtr found = IntPtr.Zero;
        IntPtr candidate = IntPtr.Zero;

        // Cache nama proses + waktu start per PID supaya tidak memanggil
        // GetProcessById berulang kali untuk setiap window pada setiap poll.
        var nameCache = new Dictionary<uint, string>();
        var startCache = new Dictionary<uint, DateTime>();

        Win32.EnumWindows((h, l) =>
        {
            Win32.GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0) return true;

            if (!nameCache.TryGetValue(pid, out string? name))
            {
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    name = p.ProcessName;
                    startCache[pid] = p.StartTime;
                }
                catch
                {
                    name = string.Empty;
                }
                nameCache[pid] = name;
            }

            if (!name.Equals(processName, StringComparison.OrdinalIgnoreCase)) return true;

            // Abaikan proses lama (mis. instance sebelumnya yang belum mati),
            // supaya tidak salah attach ke window milik instance lain.
            if (startCache.TryGetValue(pid, out DateTime started) && started < startTime.AddSeconds(-2))
                return true;

            // Prioritaskan window yang terlihat dan punya area client (window utama),
            // bukan window bantu/IME.
            if (Win32.IsWindowVisible(h))
            {
                if (candidate == IntPtr.Zero) candidate = h;
                var sb = new System.Text.StringBuilder(256);
                Win32.GetWindowText(h, sb, sb.Capacity);
                if (sb.Length > 0 || candidate == h)
                {
                    found = h;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);

        return found != IntPtr.Zero ? found : candidate;
    }

    /// <summary>
    /// Menerapkan style "child window yang tidak pernah merebut fokus".
    /// </summary>
    private static void ApplyDesktopStyle(IntPtr hwnd)
    {
        // --- Style standar ---
        int currentStyle = ReadWindowLong(hwnd, Win32.GWL_STYLE);

        // Buang WS_POPUP (wajib, kalau tidak window tetap top-level) dan set WS_CHILD.
        int style = currentStyle & ~Win32.WS_POPUP;
        style |= Win32.WS_CHILD;

        // Buang border/caption standar window.
        style &= ~Win32.WS_CAPTION;
        style &= ~Win32.WS_THICKFRAME;
        style &= ~Win32.WS_MINIMIZEBOX;
        style &= ~Win32.WS_MAXIMIZEBOX;
        style &= ~Win32.WS_SYSMENU;

        if (style != currentStyle)
        {
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_STYLE, new IntPtr(style));
        }

        // --- Extended style ---
        int currentExStyle = ReadWindowLong(hwnd, Win32.GWL_EXSTYLE);

        // WS_EX_NOACTIVATE: window tidak pernah merebut fokus keyboard.
        int exStyle = currentExStyle | Win32.WS_EX_NOACTIVATE;

        // WS_EX_TOOLWINDOW + buang WS_EX_APPWINDOW: jangan muncul di taskbar/Alt-Tab.
        exStyle |= Win32.WS_EX_TOOLWINDOW;
        exStyle &= ~Win32.WS_EX_APPWINDOW;

        // Buang WS_EX_TOPMOST: Godot akan memasang ini saat no_focus=true.
        // Untuk child window ini tidak diperlukan dan bisa membuat wallpaper
        // tampak di atas jendela aplikasi lain.
        exStyle &= ~Win32.WS_EX_TOPMOST;

        if (exStyle != currentExStyle)
        {
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(exStyle));
        }
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

                    // Kembalikan gaya top-level sebelum melepas parent, supaya tidak
                    // tertinggal sebagai window anak dari desktop window.
                    int style = ReadWindowLong(hwndToHide, Win32.GWL_STYLE);
                    style &= ~Win32.WS_CHILD;
                    style |= Win32.WS_POPUP;
                    Win32.SetWindowLongPtr(hwndToHide, Win32.GWL_STYLE, new IntPtr(style));

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
