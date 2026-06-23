// Program.cs
// win_ocr_cli 主入口。
// 行为对应 mac_ocr_cli 的 main.swift:
//   * 单文件/批量文件 OCR(并发上限 = min(4, 核数))
//   * 屏幕截图 + OCR
//   * 截图到剪贴板(--clipboard)
//   * 识别剪贴板图片(--from-clipboard)
//   * 窗口列表(--window-list)
//
// C# 5 / .NET 4.0 限制:
//   * 无字符串内插 → 用 string.Format
//   * Clipboard / Screen 在 WinForms 命名空间 → 需 [STAThread]
//   * 批量并发用 ThreadPool + ManualResetEvent 自己拼

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WinOcrCli
{
    public static class Program
    {
        // 批量模式并发上限:macOS 版本用 min(4, 核数);这里一致。
        private static readonly int MaxConcurrent = Math.Max(1, Math.Min(4, Environment.ProcessorCount));

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                var opts = CliParser.Parse(args, ReadStdin);
                return Run(opts);
            }
            catch (HelpRequestedException)
            {
                Console.Write(CliPrinter.Usage);
                return 0;
            }
            catch (VersionRequestedException)
            {
                Console.WriteLine(CliPrinter.Version);
                return 0;
            }
            catch (CliException ex)
            {
                Console.Error.WriteLine("错误: " + ex.Message);
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("错误: " + ex.Message);
                return 1;
            }
        }

        // ---------- 调度 ----------

        private static int Run(CliOptions opts)
        {
            // 优先级:window-list > clipboard > pasteboard > capture > file
            if (opts.WindowList) return RunWindowList(opts);
            if (opts.ClipboardCapture != null) return RunClipboardCapture(opts, opts.ClipboardCapture);
            if (opts.PasteboardSource) return RunPasteboardOcr(opts);
            if (opts.Capture != null) return RunCaptureThenOcr(opts, opts.Capture);
            if (opts.ImagePaths.Count == 1) return RunSingleFile(opts, opts.ImagePaths[0]);
            return RunBatchFiles(opts);
        }

        // ---------- 窗口列表 ----------

        private static int RunWindowList(CliOptions opts)
        {
            var wins = ScreenCapture.ListVisibleWindows();
            string payload;
            if (opts.OutputMode == CliOutputMode.Json)
            {
                payload = OutputRenderer.RenderWindowListJson(wins);
            }
            else
            {
                payload = OutputRenderer.RenderWindowListText(wins);
            }
            Emit(payload, opts.OutputPath, opts.Quiet);
            return 0;
        }

        // ---------- 截图到剪贴板 ----------

        private static int RunClipboardCapture(CliOptions opts, CaptureSpec cap)
        {
            using (Bitmap bmp = DoCapture(cap))
            {
                if (bmp == null) return 1;
                ClipboardHelper.WriteImage(bmp);
                int w = bmp.Width, h = bmp.Height;
                string payload;
                if (opts.OutputMode == CliOutputMode.Json)
                {
                    payload = OutputRenderer.RenderClipboardJson(w, h, cap.SavePath);
                }
                else
                {
                    payload = OutputRenderer.RenderClipboardText(w, h, cap.SavePath);
                }
                Emit(payload, opts.OutputPath, opts.Quiet);
            }
            return 0;
        }

        // ---------- 识别剪贴板图片 ----------

        private static int RunPasteboardOcr(CliOptions opts)
        {
            string tmp = ClipboardHelper.SavePngToTemp();
            if (tmp == null)
            {
                Console.Error.WriteLine("错误: 剪贴板中没有图片(先复制一张图片再试)");
                return 1;
            }
            try
            {
                var report = RecognizeOne(tmp, opts);
                report.ImagePath = "<clipboard> (" + report.Lines.Count + " lines, " + tmp + ")";
                string payload = (opts.OutputMode == CliOutputMode.Json)
                    ? OutputRenderer.RenderJson(report)
                    : OutputRenderer.RenderText(report, opts);
                Emit(payload, opts.OutputPath, opts.Quiet);
                return 0;
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        // ---------- 截图 + OCR ----------

        private static int RunCaptureThenOcr(CliOptions opts, CaptureSpec cap)
        {
            string tempPath = ScreenCapture.ResolveTempPngPath(cap.SavePath);
            bool isTemp = string.IsNullOrEmpty(cap.SavePath);
            Bitmap bmp = null;
            try
            {
                bmp = DoCapture(cap);
                if (bmp == null) return 1;
                bmp.Save(tempPath, System.Drawing.Imaging.ImageFormat.Png);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("错误: " + ex.Message);
                return 1;
            }
            finally
            {
                if (bmp != null) bmp.Dispose();
            }

            try
            {
                var report = RecognizeOne(tempPath, opts);
                string payload = (opts.OutputMode == CliOutputMode.Json)
                    ? OutputRenderer.RenderJson(report)
                    : OutputRenderer.RenderText(report, opts);
                Emit(payload, opts.OutputPath, opts.Quiet);
                return 0;
            }
            finally
            {
                if (isTemp)
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
        }

        private static Bitmap DoCapture(CaptureSpec cap)
        {
            try
            {
                switch (cap.Source)
                {
                    case CaptureSpec.Kind.MainDisplay:
                        return ScreenCapture.CaptureMainDisplay();
                    case CaptureSpec.Kind.Region:
                        return ScreenCapture.CaptureRegion(cap.RegionX, cap.RegionY, cap.RegionW, cap.RegionH);
                    case CaptureSpec.Kind.WindowId:
                        return ScreenCapture.CaptureWindow(cap.WindowId);
                    case CaptureSpec.Kind.WindowQuery:
                        return ScreenCapture.CaptureWindowByQuery(cap.WindowQuery);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("错误: " + ex.Message);
                return null;
            }
            return null;
        }

        // ---------- 单文件 OCR ----------

        private static int RunSingleFile(CliOptions opts, string path)
        {
            try
            {
                var report = RecognizeOne(path, opts);
                string payload = (opts.OutputMode == CliOutputMode.Json)
                    ? OutputRenderer.RenderJson(report)
                    : OutputRenderer.RenderText(report, opts);
                Emit(payload, opts.OutputPath, opts.Quiet);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("错误: " + ex.Message);
                return 1;
            }
        }

        // ---------- 批量 OCR(并发) ----------

        private static int RunBatchFiles(CliOptions opts)
        {
            var started = DateTime.UtcNow;
            int total = opts.ImagePaths.Count;
            int doneCount = 0;
            var items = new OcrBatchItem[total];

            // 简易信号量实现并发上限
            using (var sem = new SemaphoreSlim(MaxConcurrent, MaxConcurrent))
            {
                var tasks = new List<Task>(total);
                for (int i = 0; i < total; i++)
                {
                    int idx = i;
                    string path = opts.ImagePaths[idx];
                    tasks.Add(Task.Run(() =>
                    {
                        sem.Wait();
                        try
                        {
                            OcrBatchItem item;
                            try
                            {
                                var report = RecognizeOne(path, opts);
                                item = OcrBatchItem.Success(path, report.Lines);
                            }
                            catch (Exception ex)
                            {
                                item = OcrBatchItem.Failure(path, ex.Message);
                            }
                            items[idx] = item;
                            int done = Interlocked.Increment(ref doneCount);
                            if (!opts.Quiet) ProgressLine(done, total, item);
                        }
                        finally
                        {
                            sem.Release();
                        }
                    }));
                }
                Task.WaitAll(tasks.ToArray());
            }

            var batch = new OcrBatchReport(new List<OcrBatchItem>(items));
            string payload = (opts.OutputMode == CliOutputMode.Json)
                ? OutputRenderer.RenderBatchJson(batch)
                : OutputRenderer.RenderBatchText(batch);
            Emit(payload, opts.OutputPath, opts.Quiet);

            if (!opts.Quiet)
            {
                double elapsed = (DateTime.UtcNow - started).TotalSeconds;
                Console.Error.WriteLine(string.Format(
                    "完成: {0}/{1} ok, {2} failed, 用时 {3:0.00}s",
                    batch.SuccessCount, total, batch.FailureCount, elapsed));
            }
            return batch.FailureCount > 0 ? 2 : 0;
        }

        private static void ProgressLine(int done, int total, OcrBatchItem item)
        {
            string name = Path.GetFileName(item.ImagePath);
            string status = item.Status == "ok"
                ? string.Format("ok ({0} lines)", item.Lines != null ? item.Lines.Count : 0)
                : "FAILED: " + (item.ErrorMessage ?? "?");
            string prefix = total >= 10 && done < 10 ? " " : "";
            Console.Error.Write("\r[" + done + "/" + total + "] " + prefix + name + "  " + status);
            if (done == total) Console.Error.WriteLine();
        }

        // ---------- 识别单图核心 ----------

        private static OcrReport RecognizeOne(string path, CliOptions opts)
        {
            using (Bitmap bmp = ImageLoader.Load(path))
            {
                var recognizer = new WindowsOcrRecognizer(opts.Languages, opts.Level, opts.LanguageCorrection);
                var lines = recognizer.Recognize(bmp);
                return new OcrReport(path, lines);
            }
        }

        // ---------- 输出落地 ----------

        private static void Emit(string payload, string outputPath, bool quiet)
        {
            if (!string.IsNullOrEmpty(outputPath))
            {
                string dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(outputPath, payload, new UTF8Encoding(false));
            }
            if (!quiet)
            {
                Console.Write(payload);
            }
        }

        // ---------- stdin ----------

        private static string ReadStdin()
        {
            try
            {
                if (Console.IsInputRedirected)
                {
                    var sb = new StringBuilder();
                    string line;
                    while ((line = Console.In.ReadLine()) != null)
                    {
                        sb.AppendLine(line);
                    }
                    return sb.ToString();
                }
            }
            catch { }
            return null;
        }
    }
}
