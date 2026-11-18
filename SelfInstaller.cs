// SelfInstaller.cs
// 把当前运行的 win_ocr_cli.exe 复制到全局可执行目录(默认 ~/bin,即 %USERPROFILE%\bin),
// 并确保该目录位于用户 PATH 中,以便在任意目录直接调用 `win_ocr_cli`。
//
// 前提:产物是零运行时依赖的单文件 exe(.NET Framework 4.5+ 系统自带),
// 复制到 PATH 目录即可全局使用,不需要额外安装器。
//
// C# 5 兼容:无字符串内插、无 null 条件、无表达式体成员。

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinOcrCli
{
    /// <summary>一次全局安装的结果。</summary>
    public sealed class InstallResult
    {
        public string SourcePath;         // 复制来源(当前正在运行的 exe)
        public string TargetDir;          // 安装目录
        public string ExecutablePath;     // 安装后的 exe 完整路径
        public bool Copied;               // 是否真的执行了复制(false = 源与目标相同)
        public bool PathAlreadyPresent;   // 安装前该目录是否已在用户 PATH
        public bool PathUpdated;          // 是否写入了用户 PATH
        public long SizeBytes;            // 安装后文件大小
        public string LeftoverPath;       // 目标正在运行时残留的旧文件(通常为 null)
    }

    public static class SelfInstaller
    {
        public const string ExeName = "win_ocr_cli.exe";

        /// <summary>默认全局目录:~ / bin(Windows 上即 %USERPROFILE%\bin)。</summary>
        public static string DefaultBinDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "bin");
            }
        }

        /// <summary>
        /// 安装到全局目录。targetDir 为空时使用 DefaultBinDir。
        /// </summary>
        public static InstallResult Install(string targetDir)
        {
            if (string.IsNullOrEmpty(targetDir)) targetDir = DefaultBinDir;
            targetDir = Path.GetFullPath(targetDir);

            string source = CurrentExecutablePath();
            if (!File.Exists(source))
                throw new IOException("找不到当前运行的 exe: " + source);

            if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

            string target = Path.Combine(targetDir, ExeName);
            var result = new InstallResult
            {
                SourcePath = source,
                TargetDir = targetDir,
                ExecutablePath = target
            };

            bool same = string.Equals(Path.GetFullPath(source), target, StringComparison.OrdinalIgnoreCase);
            if (same)
            {
                // 已经就是目标文件本身(从全局目录里再次执行 --install),无需复制
                result.Copied = false;
            }
            else
            {
                CopyOver(source, target, result);
                result.Copied = true;
            }

            try
            {
                var fi = new FileInfo(target);
                result.SizeBytes = fi.Exists ? fi.Length : 0;
            }
            catch { result.SizeBytes = 0; }

            result.PathAlreadyPresent = UserPathContains(targetDir);
            if (!result.PathAlreadyPresent)
            {
                AppendUserPath(targetDir);
                result.PathUpdated = true;
            }
            return result;
        }

        /// <summary>当前进程正在运行的 exe 路径。</summary>
        public static string CurrentExecutablePath()
        {
            try
            {
                string p = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(p)) return p;
            }
            catch { }
            try
            {
                using (var proc = System.Diagnostics.Process.GetCurrentProcess())
                {
                    if (proc.MainModule != null) return proc.MainModule.FileName;
                }
            }
            catch { }
            throw new IOException("无法确定当前 exe 的路径");
        }

        // ---------- 复制 ----------

        /// <summary>
        /// 覆盖复制。目标正在运行时(Windows 不允许覆盖运行中的 exe),
        /// 先把旧文件改名再复制;旧文件删不掉时保留并回报路径。
        /// </summary>
        private static void CopyOver(string source, string target, InstallResult result)
        {
            try
            {
                File.Copy(source, target, true);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            // 目标被占用(可能正在运行)→ 改名腾位
            string backup = target + ".old";
            try { if (File.Exists(backup)) File.Delete(backup); } catch { }
            try
            {
                if (File.Exists(target)) File.Move(target, backup);
            }
            catch (Exception ex)
            {
                throw new IOException("无法替换目标文件(可能被占用): " + target + " —— " + ex.Message);
            }

            try
            {
                File.Copy(source, target, true);
            }
            catch (Exception ex)
            {
                try { if (!File.Exists(target) && File.Exists(backup)) File.Move(backup, target); } catch { }
                throw new IOException("复制失败: " + ex.Message);
            }

            // 旧文件仍被运行中的进程占用,删不掉就留着(下次安装会覆盖它)
            try { File.Delete(backup); }
            catch { result.LeftoverPath = backup; }
        }

        // ---------- 用户 PATH ----------

        /// <summary>读取用户 PATH 原始值(不展开环境变量)。</summary>
        private static string ReadUserPathRaw()
        {
            try
            {
                using (RegistryKey env = Registry.CurrentUser.OpenSubKey("Environment", false))
                {
                    if (env == null) return null;
                    object v = env.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    return v as string;
                }
            }
            catch { return null; }
        }

        private static bool UserPathContains(string dir)
        {
            string raw = ReadUserPathRaw();
            if (string.IsNullOrEmpty(raw)) return false;
            string target = NormalizeDir(dir);
            foreach (string part in raw.Split(';'))
            {
                string t = NormalizeDir(part);
                if (t.Length == 0) continue;
                if (string.Equals(t, target, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>把目录追加到用户 PATH,并广播 WM_SETTINGCHANGE 让新终端立即可见。</summary>
        private static void AppendUserPath(string dir)
        {
            using (RegistryKey env = Registry.CurrentUser.OpenSubKey("Environment", true))
            {
                if (env == null)
                    throw new IOException("无法打开 HKCU\\Environment(写用户 PATH 失败)");

                object existing = env.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                RegistryValueKind kind = RegistryValueKind.String;
                try { kind = env.GetValueKind("Path"); } catch { }

                string raw = existing as string;
                string value = string.IsNullOrEmpty(raw)
                    ? dir
                    : raw.TrimEnd(';') + ";" + dir;

                env.SetValue("Path", value, kind);
            }
            BroadcastEnvironmentChange();
        }

        private static string NormalizeDir(string dir)
        {
            if (dir == null) return string.Empty;
            return dir.Trim().Trim('"').TrimEnd('\\', '/');
        }

        // ---------- WM_SETTINGCHANGE ----------

        private const uint WM_SETTINGCHANGE = 0x001A;
        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xffff);
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam,
            uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

        /// <summary>通知 Explorer 用户环境变量已变化(新开的终端才能拿到新的 PATH)。</summary>
        private static void BroadcastEnvironmentChange()
        {
            try
            {
                UIntPtr result;
                SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero, "Environment",
                    SMTO_ABORTIFHUNG, 2000, out result);
            }
            catch { }
        }
    }
}
