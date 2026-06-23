// ScreenCapture.cs
// 屏幕截图(主屏 / 区域 / 窗口) + 窗口枚举。
// 用 System.Windows.Forms + System.Drawing 实现主屏和区域;
// 用 P/Invoke user32 实现按窗口标题 / ID 截屏。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace WinOcrCli
{
    public sealed class WindowInfo
    {
        public int WindowId;
        public string Title;
        public string Owner;     // 进程名(尽力)
        public int X, Y, W, H;
        public bool IsVisible;
    }

    public static class ScreenCapture
    {
        // ----- 主屏 / 区域 -----

        public static Bitmap CaptureMainDisplay()
        {
            var screen = Screen.PrimaryScreen;
            if (screen == null) throw new ScreenCaptureException("找不到主屏幕");
            Rectangle bounds = screen.Bounds;
            return CaptureRegion(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }

        public static Bitmap CaptureRegion(int x, int y, int w, int h)
        {
            if (w <= 0 || h <= 0) throw new ScreenCaptureException("截图区域无效:宽高必须 > 0");

            int vX = SystemInformation.VirtualScreen.X;
            int vY = SystemInformation.VirtualScreen.Y;
            int vW = SystemInformation.VirtualScreen.Width;
            int vH = SystemInformation.VirtualScreen.Height;

            int x1 = Math.Max(x, vX);
            int y1 = Math.Max(y, vY);
            int x2 = Math.Min(x + w, vX + vW);
            int y2 = Math.Min(y + h, vY + vH);
            int cw = x2 - x1;
            int ch = y2 - y1;
            if (cw <= 0 || ch <= 0) throw new ScreenCaptureException("截图区域完全落在屏幕外");

            var bmp = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(x1, y1, 0, 0, new Size(cw, ch), CopyPixelOperation.SourceCopy);
            }
            return bmp;
        }

        // ----- 窗口截图 -----

        public static Bitmap CaptureWindow(int windowId)
        {
            IntPtr hwnd = new IntPtr(windowId);
            if (!IsWindow(hwnd)) throw new ScreenCaptureException("无效的窗口句柄: " + windowId);
            RECT rc;
            GetWindowRect(hwnd, out rc);
            int w = rc.Right - rc.Left;
            int hh = rc.Bottom - rc.Top;
            if (w <= 0 || hh <= 0) throw new ScreenCaptureException("窗口尺寸为 0(可能已最小化)");

            var img = new Bitmap(w, hh, PixelFormat.Format32bppArgb);
            try
            {
                using (var g = Graphics.FromImage(img))
                {
                    IntPtr hdc = g.GetHdc();
                    try
                    {
                        // PW_RENDERFULLCONTENT = 0x00000002 (Win 8.1+,DWM 合成)
                        bool ok = PrintWindowNative(hwnd, hdc, 0x00000002);
                        if (!ok)
                        {
                            // 回落 PW_CLIENTONLY = 0x00000001
                            ok = PrintWindowNative(hwnd, hdc, 0x00000001);
                            if (!ok) throw new ScreenCaptureException("PrintWindow 失败(窗口可能被遮挡或无权限)");
                        }
                    }
                    finally
                    {
                        g.ReleaseHdc(hdc);
                    }
                }
            }
            catch
            {
                img.Dispose();
                throw;
            }
            return img;
        }

        public static Bitmap CaptureWindowByQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ScreenCaptureException("窗口查询字符串为空");
            string q = query.Trim();
            string qLower = q.ToLowerInvariant();

            var wins = ListVisibleWindows();
            foreach (var w in wins)
            {
                if (!w.IsVisible) continue;
                if (w.W <= 0 || w.H <= 0) continue;
                string title = (w.Title ?? "").ToLowerInvariant();
                string owner = (w.Owner ?? "").ToLowerInvariant();
                if (title == qLower || owner == qLower
                    || title.Contains(qLower) || owner.Contains(qLower))
                {
                    return CaptureWindow(w.WindowId);
                }
            }
            throw new ScreenCaptureException("无法找到匹配的窗口: " + query);
        }

        // ----- 窗口列表 -----

        public static List<WindowInfo> ListVisibleWindows()
        {
            var result = new List<WindowInfo>();
            EnumWindowsProc proc = (hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                int len = GetWindowTextLength(hWnd);
                var sb = new StringBuilder(len + 1);
                GetWindowTextRaw(hWnd, sb, sb.Capacity);
                string title = sb.ToString();

                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                string owner = TryGetProcessName(pid);

                if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(owner)) return true;

                RECT rc;
                GetWindowRect(hWnd, out rc);
                int w = rc.Right - rc.Left;
                int hh = rc.Bottom - rc.Top;
                if (w <= 0 || hh <= 0) return true;

                result.Add(new WindowInfo
                {
                    WindowId = (int)hWnd,
                    Title = title,
                    Owner = owner,
                    X = rc.Left, Y = rc.Top,
                    W = w, H = hh,
                    IsVisible = true
                });
                return true;
            };
            EnumWindows(proc, IntPtr.Zero);
            return result;
        }

        private static string TryGetProcessName(uint pid)
        {
            if (pid == 0) return string.Empty;
            try
            {
                return Process.GetProcessById((int)pid).ProcessName;
            }
            catch { return string.Empty; }
        }

        // ----- 临时文件路径 -----

        public static string ResolveTempPngPath(string savePath)
        {
            if (!string.IsNullOrEmpty(savePath))
            {
                string dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                return savePath;
            }
            return Path.Combine(Path.GetTempPath(),
                "win_ocr_cli-shot-" + Guid.NewGuid().ToString("N") + ".png");
        }

        // ----- P/Invoke -----

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
        private static extern int GetWindowTextRaw(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextLengthW")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool PrintWindowNative(IntPtr hwnd, IntPtr hdcBlt, int nFlags);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }
    }

    public sealed class ScreenCaptureException : Exception
    {
        public ScreenCaptureException(string message) : base(message) { }
    }
}
