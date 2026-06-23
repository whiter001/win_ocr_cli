// Cli.cs
// 命令行参数解析 + 输出渲染 + 使用说明。移植自 macOS 版本。
// C# 5 兼容（无字符串内插、无 nameof、无 null 条件、无 using static）。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WinOcrCli
{
    // ---------- 模式与错误 ----------

    public enum CliOutputMode
    {
        Text,
        Json
    }

    public enum OcrLevel
    {
        Accurate,
        Fast
    }

    public sealed class CliException : Exception
    {
        public CliException(string message) : base(message) { }
    }

    public sealed class CliOptions
    {
        public List<string> ImagePaths = new List<string>();  // 位置参数 + --dir 扫描 + stdin
        public List<string> Languages;                          // BCP-47 标签
        public OcrLevel Level = OcrLevel.Accurate;
        public bool LanguageCorrection = true;
        public CliOutputMode OutputMode = CliOutputMode.Text;
        public string OutputPath;                               // -o/--output,非 null 强制 JSON
        public string Keyword;                                  // -k/--keyword
        public bool Quiet = false;                              // -q/--quiet

        // 模式互斥字段
        public CaptureSpec Capture;                             // 截图模式 (--screen/--window/--region)
        public bool WindowList = false;                         // --window-list
        public CaptureSpec ClipboardCapture;                    // --clipboard(把截图写到剪贴板)
        public bool PasteboardSource = false;                   // --from-clipboard

        public bool IsSingleImage
        {
            get { return ImagePaths.Count == 1; }
        }
    }

    /// <summary>截图规格(主屏/按窗口/按区域)。</summary>
    public sealed class CaptureSpec
    {
        public enum Kind { MainDisplay, WindowQuery, WindowId, Region }

        public Kind Source;
        public string WindowQuery;
        public int WindowId;
        public int RegionX, RegionY, RegionW, RegionH;
        public string SavePath;  // --save-screenshot,可空
    }

    public static class CliParser
    {
        public static readonly string[] DefaultLanguages = new[] { "zh-Hans", "zh-Hant", "en-US" };
        public static readonly string[] CjkLanguages = new[] { "zh-Hans", "zh-Hant", "zh-HK", "en-US" };

        // GDI+ 原生支持的扩展名(小写、含点)
        public static readonly HashSet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"
        };

        /// <summary>
        /// 解析命令行参数。reader 为 null 时若用户传 `-`(stdin 路径列表)会抛异常。
        /// </summary>
        public static CliOptions Parse(string[] args, Func<string> stdinReader)
        {
            if (args == null) args = new string[0];

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--help" || args[i] == "-h") throw new HelpRequestedException();
                if (args[i] == "--version" || args[i] == "-v") throw new VersionRequestedException();
            }

            var opts = new CliOptions();
            opts.Languages = new List<string>(DefaultLanguages);

            var imagePaths = new List<string>();
            var inputDirs = new List<string>();
            bool cjkPreset = false;
            bool explicitOutputMode = false;
            string saveScreenshotPath = null;
            bool windowListRequested = false;
            bool clipboardRequested = false;
            bool pasteboardRequested = false;
            CaptureSpec captureSource = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "--lang":
                    case "-l":
                        i++;
                        if (i >= args.Length) throw new CliException("--lang 需要一个值(逗号分隔的 BCP-47 标签)");
                        var parts = SplitLanguages(args[i]);
                        if (parts.Length == 0) throw new CliException("--lang 至少需要一种语言");
                        if (cjkPreset) throw new CliException("--cjk 与 --lang/-l 不能同时使用");
                        opts.Languages = new List<string>(parts);
                        break;

                    case "--cjk":
                        if (cjkPreset)
                        {
                            // 重复 --cjk 忽略
                        }
                        else if (!LanguagesEqual(opts.Languages, DefaultLanguages))
                        {
                            throw new CliException("--cjk 与 --lang/-l 不能同时使用");
                        }
                        else
                        {
                            opts.Languages = new List<string>(CjkLanguages);
                            cjkPreset = true;
                        }
                        break;

                    case "--level":
                        i++;
                        if (i >= args.Length) throw new CliException("--level 需要一个值: accurate 或 fast");
                        string lv = args[i].ToLowerInvariant();
                        if (lv == "accurate") opts.Level = OcrLevel.Accurate;
                        else if (lv == "fast") opts.Level = OcrLevel.Fast;
                        else throw new CliException("--level 必须是 accurate 或 fast,收到: " + args[i]);
                        break;

                    case "--no-correction":
                        opts.LanguageCorrection = false;
                        break;

                    case "--quiet":
                    case "-q":
                        opts.Quiet = true;
                        break;

                    case "--json":
                        if (explicitOutputMode && opts.OutputMode != CliOutputMode.Json)
                            throw new CliException("--json 和 --text 不能同时使用");
                        opts.OutputMode = CliOutputMode.Json;
                        explicitOutputMode = true;
                        break;

                    case "--text":
                        if (explicitOutputMode && opts.OutputMode != CliOutputMode.Text)
                            throw new CliException("--json 和 --text 不能同时使用");
                        opts.OutputMode = CliOutputMode.Text;
                        explicitOutputMode = true;
                        break;

                    case "--output":
                    case "-o":
                        i++;
                        if (i >= args.Length) throw new CliException("--output 需要一个文件路径");
                        opts.OutputPath = args[i];
                        if (explicitOutputMode && opts.OutputMode != CliOutputMode.Json)
                            throw new CliException("--json 和 --text 不能同时使用");
                        opts.OutputMode = CliOutputMode.Json;
                        explicitOutputMode = true;
                        break;

                    case "--keyword":
                    case "-k":
                        i++;
                        if (i >= args.Length) throw new CliException("--keyword 需要一个值");
                        opts.Keyword = args[i];
                        break;

                    case "--dir":
                        i++;
                        if (i >= args.Length) throw new CliException("--dir 需要一个目录路径");
                        string dir = args[i];
                        if (!Directory.Exists(dir)) throw new CliException("目录不存在: " + dir);
                        inputDirs.Add(dir);
                        break;

                    case "--screen":
                        if (captureSource != null) throw new CliException("--screen / --window / --window-id / --region 只能选一个");
                        captureSource = new CaptureSpec { Source = CaptureSpec.Kind.MainDisplay };
                        break;

                    case "--window":
                        i++;
                        if (i >= args.Length) throw new CliException("--window 需要一个窗口标题或应用名称");
                        if (captureSource != null) throw new CliException("--screen / --window / --window-id / --region 只能选一个");
                        captureSource = new CaptureSpec { Source = CaptureSpec.Kind.WindowQuery, WindowQuery = args[i] };
                        break;

                    case "--window-id":
                        i++;
                        if (i >= args.Length) throw new CliException("--window-id 需要一个窗口编号");
                        int wid;
                        if (!int.TryParse(args[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out wid) || wid <= 0)
                            throw new CliException("--window-id 必须是大于 0 的数字");
                        if (captureSource != null) throw new CliException("--screen / --window / --window-id / --region 只能选一个");
                        captureSource = new CaptureSpec { Source = CaptureSpec.Kind.WindowId, WindowId = wid };
                        break;

                    case "--region":
                        if (i + 4 >= args.Length)
                            throw new CliException("--region 需要 4 个正数: x y width height");
                        int rx, ry, rw, rh;
                        if (!int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out rx) ||
                            !int.TryParse(args[i + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out ry) ||
                            !int.TryParse(args[i + 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out rw) ||
                            !int.TryParse(args[i + 4], NumberStyles.Integer, CultureInfo.InvariantCulture, out rh) ||
                            rw <= 0 || rh <= 0)
                            throw new CliException("--region 需要 4 个正数: x y width height");
                        if (captureSource != null) throw new CliException("--screen / --window / --window-id / --region 只能选一个");
                        captureSource = new CaptureSpec
                        {
                            Source = CaptureSpec.Kind.Region,
                            RegionX = rx, RegionY = ry, RegionW = rw, RegionH = rh
                        };
                        i += 4;
                        break;

                    case "--save-screenshot":
                        i++;
                        if (i >= args.Length) throw new CliException("--save-screenshot 需要一个值");
                        saveScreenshotPath = args[i];
                        break;

                    case "--window-list":
                        windowListRequested = true;
                        break;

                    case "--clipboard":
                        clipboardRequested = true;
                        break;

                    case "--from-clipboard":
                        pasteboardRequested = true;
                        break;

                    default:
                        if (a == "-")
                        {
                            if (stdinReader == null) throw new CliException("位置参数 `-` 要求从 stdin 读取路径列表");
                            string raw = stdinReader();
                            if (raw == null) throw new CliException("stdin 中没有可读取的路径(每行一个,空行与 # 注释会被跳过)");
                            var fromStdin = ParseStdinPaths(raw);
                            if (fromStdin.Count == 0) throw new CliException("stdin 中没有可读取的路径(每行一个,空行与 # 注释会被跳过)");
                            imagePaths.AddRange(fromStdin);
                        }
                        else if (a.StartsWith("-"))
                        {
                            throw new CliException("不支持的参数: " + a);
                        }
                        else
                        {
                            imagePaths.Add(a);
                        }
                        break;
                }
            }

            // ----- 模式互斥与组装 -----

            if (windowListRequested)
            {
                if (captureSource != null || saveScreenshotPath != null
                    || clipboardRequested || pasteboardRequested
                    || imagePaths.Count > 0 || inputDirs.Count > 0)
                {
                    throw new CliException("--window-list 不能与任何截图或输入参数同时使用");
                }
                if (opts.OutputPath != null) opts.OutputMode = CliOutputMode.Json;
                opts.WindowList = true;
                if (saveScreenshotPath != null)
                {
                    // 忽略,--window-list 不支持 --save-screenshot
                }
                return opts;
            }

            if (clipboardRequested)
            {
                if (pasteboardRequested) throw new CliException("--clipboard 与 --from-clipboard 互斥");
                if (imagePaths.Count > 0 || inputDirs.Count > 0)
                    throw new CliException("--clipboard 是截图模式,不能与位置参数 / --dir / stdin `-` / --from-clipboard 同时使用");
                if (captureSource == null) captureSource = new CaptureSpec { Source = CaptureSpec.Kind.MainDisplay };
                if (saveScreenshotPath != null) captureSource.SavePath = saveScreenshotPath;
                if (opts.OutputPath != null) opts.OutputMode = CliOutputMode.Json;
                opts.ClipboardCapture = captureSource;
                return opts;
            }

            if (pasteboardRequested)
            {
                if (captureSource != null || saveScreenshotPath != null)
                    throw new CliException("--from-clipboard 必须单独使用,不能与 --screen/--window/--region/--clipboard/--window-list 组合");
                if (imagePaths.Count > 0 || inputDirs.Count > 0)
                    throw new CliException("--from-clipboard 不能与位置参数 / --dir / stdin `-` 同时使用");
                if (opts.Keyword == null && opts.OutputPath != null) opts.OutputMode = CliOutputMode.Json;
                opts.PasteboardSource = true;
                return opts;
            }

            if (captureSource != null)
            {
                if (imagePaths.Count > 0 || inputDirs.Count > 0)
                    throw new CliException("截图模式(--screen/--window/--region)不能与位置参数 / --dir / stdin `-` 同时使用");
                if (opts.Keyword != null) throw new CliException("--keyword 不能与截图模式同时使用");
                if (saveScreenshotPath != null) captureSource.SavePath = saveScreenshotPath;
                if (opts.OutputPath != null) opts.OutputMode = CliOutputMode.Json;
                opts.Capture = captureSource;
                return opts;
            }

            // 文件输入分支
            if (saveScreenshotPath != null) throw new CliException("--save-screenshot 必须与 --screen/--window/--window-id/--region 同时使用");
            if (imagePaths.Count == 0 && inputDirs.Count == 0)
                throw new CliException("缺少图片路径(位置参数、--dir、stdin `-`、--screen/--window/--region、--clipboard、--from-clipboard)");

            if (opts.Keyword != null && opts.OutputPath != null)
                throw new CliException("--keyword 与 --output/-o 不能同时使用");

            // 合并并去重
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var urls = new List<string>();
            foreach (var raw in imagePaths)
            {
                string key = Path.GetFullPath(raw);
                if (!seen.Add(key)) throw new CliException("重复的输入: " + key);
                urls.Add(key);
            }
            foreach (var dir in inputDirs)
            {
                var scanned = ScanDirectory(dir);
                foreach (var p in scanned)
                {
                    string key = Path.GetFullPath(p);
                    if (seen.Add(key)) urls.Add(key);
                }
            }
            opts.ImagePaths = urls;
            return opts;
        }

        private static string[] SplitLanguages(string raw)
        {
            var list = new List<string>();
            foreach (var p in raw.Split(','))
            {
                var t = p.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list.ToArray();
        }

        private static bool LanguagesEqual(List<string> a, string[] b)
        {
            if (a == null || a.Count != b.Length) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        /// <summary>递归扫描目录,返回按相对路径排序的图片绝对路径。</summary>
        public static List<string> ScanDirectory(string dir)
        {
            var found = new List<ScannedFile>();
            ScanDirRecursive(dir, dir, found);
            found.Sort(CompareScanned);
            var result = new List<string>(found.Count);
            foreach (var f in found) result.Add(f.Full);
            return result;
        }

        private static int CompareScanned(ScannedFile a, ScannedFile b)
        {
            return string.CompareOrdinal(a.Rel, b.Rel);
        }

        private static void ScanDirRecursive(string baseDir, string current, List<ScannedFile> found)
        {
            try
            {
                foreach (var file in Directory.GetFiles(current))
                {
                    string ext = Path.GetExtension(file);
                    if (string.IsNullOrEmpty(ext)) continue;
                    if (!SupportedExtensions.Contains(ext)) continue;
                    string rel = Relativize(file, baseDir);
                    found.Add(new ScannedFile { Rel = rel, Full = file });
                }
                foreach (var sub in Directory.GetDirectories(current))
                {
                    // 跳过隐藏目录
                    var attrs = File.GetAttributes(sub);
                    if ((attrs & FileAttributes.Hidden) == FileAttributes.Hidden) continue;
                    if ((attrs & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint) continue;
                    ScanDirRecursive(baseDir, sub, found);
                }
            }
            catch (UnauthorizedAccessException) { /* 跳过无权限目录 */ }
            catch (IOException) { /* 跳过锁定的目录 */ }
        }

        private sealed class ScannedFile
        {
            public string Rel;
            public string Full;
        }

        private static string Relativize(string full, string baseDir)
        {
            string b = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar);
            string f = Path.GetFullPath(full);
            if (f.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return f.Substring(b.Length + 1);
            return f;
        }

        /// <summary>把 stdin 文本拆成路径列表。空行与 # 注释跳过。</summary>
        public static List<string> ParseStdinPaths(string raw)
        {
            var list = new List<string>();
            using (var sr = new StringReader(raw))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    var t = line.Trim();
                    if (t.Length == 0) continue;
                    if (t.StartsWith("#")) continue;
                    list.Add(t);
                }
            }
            return list;
        }
    }

    public sealed class HelpRequestedException : Exception { }
    public sealed class VersionRequestedException : Exception { }

    // ---------- 使用说明与版本 ----------

    public static class CliPrinter
    {
        public const string Version = "win_ocr_cli 1.0.0";

        public static string Usage
        {
            get
            {
                var sb = new StringBuilder();
                sb.AppendLine("win_ocr_cli - 基于 Windows.Media.Ocr 的命令行 OCR 工具 (Windows 10/11)");
                sb.AppendLine();
                sb.AppendLine("用法:");
                sb.AppendLine("  win_ocr_cli <图片路径>... [选项]                 # 文件输入");
                sb.AppendLine("  win_ocr_cli --dir <目录> [选项]");
                sb.AppendLine("  find . -name \"*.png\" | win_ocr_cli - [选项]     # stdin");
                sb.AppendLine("  win_ocr_cli --screen [选项]                      # 截主屏 + OCR");
                sb.AppendLine("  win_ocr_cli --window <查询> [选项]               # 截匹配窗口");
                sb.AppendLine("  win_ocr_cli --window-id <id> [选项]");
                sb.AppendLine("  win_ocr_cli --region x y w h [选项]              # 截屏幕区域");
                sb.AppendLine("  win_ocr_cli --clipboard [--screen|--window|--region]  # 截图到剪贴板");
                sb.AppendLine("  win_ocr_cli --from-clipboard [选项]              # 识别剪贴板里的图片");
                sb.AppendLine("  win_ocr_cli --window-list                        # 列出可见窗口");
                sb.AppendLine();
                sb.AppendLine("输入源(仅可选其一,不可叠加):");
                sb.AppendLine("  <位置参数>...          一张或多张图片路径");
                sb.AppendLine("  --dir <目录>           递归扫描目录中的图片");
                sb.AppendLine("  -                      从 stdin 读路径列表(每行一个,# 开头是注释)");
                sb.AppendLine("  --screen/--window/--window-id/--region  截图后直接 OCR");
                sb.AppendLine("  --clipboard            截图后写入剪贴板(不跑 OCR)");
                sb.AppendLine("  --from-clipboard       从剪贴板读取图片并 OCR");
                sb.AppendLine();
                sb.AppendLine("选项:");
                sb.AppendLine("  -l, --lang <list>        识别语言,逗号分隔的 BCP-47 标签");
                sb.AppendLine("                           (默认: zh-Hans,zh-Hant,en-US)");
                sb.AppendLine("  --cjk                    切换到 CJK 预设 (zh-Hans,zh-Hant,zh-HK,en-US)");
                sb.AppendLine("                           与 --lang 互斥");
                sb.AppendLine("  --level <accurate|fast>  识别等级 (默认: accurate)");
                sb.AppendLine("  --no-correction          关闭语言自动纠错");
                sb.AppendLine("  -k, --keyword <kw>       在识别结果里搜索关键词(单文件模式)");
                sb.AppendLine("  --save-screenshot <p>    把截图另存到 p(--clipboard 时也支持)");
                sb.AppendLine("  -q, --quiet              抑制 stdout 报告和 stderr 进度;错误仍到 stderr");
                sb.AppendLine("  --text                   以纯文本输出 (默认)");
                sb.AppendLine("  --json                   以 JSON 输出");
                sb.AppendLine("  -o, --output <path>      把结果写入文件(强制 JSON)");
                sb.AppendLine("  -v, --version            打印版本");
                sb.AppendLine("  -h, --help               显示本帮助");
                sb.AppendLine();
                sb.AppendLine("支持的图片格式(由 GDI+ 解码): PNG / JPEG / BMP / GIF / TIFF");
                sb.AppendLine();
                sb.AppendLine("注意:");
                sb.AppendLine("  需要 Windows 10 或更高版本,且已安装对应语言的 OCR 功能包。");
                sb.AppendLine("  PowerShell 安装命令(管理员):");
                sb.AppendLine("    Get-WindowsCapability -Online | Where-Object Name -like 'Language.OCR*'");
                sb.AppendLine("    Add-WindowsCapability -Online -Name \"Language.OCR~~~und-LANG~0.0.1.0\"");
                sb.AppendLine("  默认语言跟随用户配置文件;--lang 仅在 OcrEngine 支持时生效。");
                sb.AppendLine();
                sb.AppendLine("示例:");
                sb.AppendLine("  win_ocr_cli photo.png");
                sb.AppendLine("  win_ocr_cli --dir .\\shots --json -o result.json");
                sb.AppendLine("  dir /b /s *.png | win_ocr_cli - --json -o all.json");
                sb.AppendLine("  win_ocr_cli --screen --cjk");
                sb.AppendLine("  win_ocr_cli --window \"记事本\" --save-screenshot shot.png");
                sb.AppendLine("  win_ocr_cli --region 0 0 800 600");
                sb.AppendLine("  win_ocr_cli --window-list");
                sb.AppendLine("  win_ocr_cli --clipboard");
                sb.AppendLine("  win_ocr_cli --from-clipboard");
                return sb.ToString();
            }
        }
    }
}
