// OcrEngine.cs
// 调用 Windows.Media.Ocr (WinRT) 做文字识别。
//
// 用 .NET Framework 4.5 自带的 System.Runtime.WindowsRuntime.dll 拿到 WinRT 投影。
// 数据流(对每张图片):
//   System.Drawing.Bitmap  → PNG bytes  →  InMemoryRandomAccessStream
//       → BitmapDecoder.CreateAsync      →  SoftwareBitmap
//       → OcrEngine.RecognizeAsync       →  OcrResult
//       → 逐行/逐词读取,转换为 OcrTextLine 列表。
//
// 坐标说明:
//   Windows OCR 的 OcrWord.BoundingRect 是「左上原点,像素单位」(Rect 字段为 float,WinRT 投影为 double)。
//   我们在输出时把它转换为「左下原点 + 归一化到 [0,1]」,与 macOS 版保持一致。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace WinOcrCli
{
    public sealed class WindowsOcrRecognizer
    {
        private readonly List<string> _languages;
        private readonly bool _languageCorrection;
        private readonly bool _fast;

        public WindowsOcrRecognizer(List<string> languages, OcrLevel level, bool languageCorrection)
        {
            if (languages == null || languages.Count == 0) languages = new List<string>(CliParser.DefaultLanguages);
            _languages = languages;
            _languageCorrection = languageCorrection;
            _fast = (level == OcrLevel.Fast);
        }

        /// <summary>对单张图片做识别,返回 OcrTextLine 列表。</summary>
        public List<OcrTextLine> Recognize(Bitmap bitmap)
        {
            if (bitmap == null) throw new ArgumentNullException("bitmap");

            OcrEngine engine = CreateEngine();
            if (engine == null)
                throw new OcrException("无法创建 Windows.Media.Ocr.OcrEngine(请检查系统是否安装了 OCR 语言包)");

            using (SoftwareBitmap softwareBitmap = BuildSoftwareBitmap(bitmap))
            {
                OcrResult result = engine.RecognizeAsync(softwareBitmap).AsTask().Result;
                return ExtractLines(result, bitmap.Width, bitmap.Height);
            }
        }

        // ----- 引擎创建 -----

        private OcrEngine CreateEngine()
        {
            // Windows OCR 引擎对单次识别只支持一种 RecognizerLanguage;
            // 选用 --lang 列表的第一个(CLI 默认 zh-Hans);失败则回落到系统首选语言。
            string primary = _languages[0];
            try
            {
                Language lang = new Language(primary);
                OcrEngine engine = OcrEngine.TryCreateFromLanguage(lang);
                if (engine != null) return engine;
            }
            catch
            {
                // 忽略,继续回落到用户配置文件
            }

            try
            {
                return OcrEngine.TryCreateFromUserProfileLanguages();
            }
            catch
            {
                return null;
            }
        }

        // ----- Bitmap → SoftwareBitmap -----

        private static SoftwareBitmap BuildSoftwareBitmap(Bitmap bitmap)
        {
            // 1. 把 Bitmap 编码为 PNG 字节
            byte[] png;
            using (MemoryStream ms = new MemoryStream())
            {
                bitmap.Save(ms, ImageFormat.Png);
                png = ms.ToArray();
            }

            // 2. 写进 InMemoryRandomAccessStream
            using (InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream())
            {
                DataWriter writer = new DataWriter(stream);
                try
                {
                    writer.WriteBytes(png);
                    // StoreAsync 把 writer 内部 buffer 提交到流,完成后才能 DetachStream
                    writer.StoreAsync().AsTask().Wait();
                }
                finally
                {
                    writer.DetachStream();
                    writer.Dispose();
                }

                // 3. 流 seek 到头(Read 之前必须)
                stream.Seek(0);

                // 4. BitmapDecoder.CreateAsync(stream) -> SoftwareBitmap
                //    让 decoder 自动判断格式;Windows OCR 引擎支持 Bgra8 / Rgba8 / Gray8 / Nv12 / Yuy2。
                //    注:BitmapDecoder 的 .NET 4.5 投影未实现 IDisposable(CS1674/CS0039),
                //    无法显式释放;它是 RCW,无引用后由 GC 终结时释放底层 COM 对象。
                BitmapDecoder decoder = BitmapDecoder.CreateAsync(stream).AsTask().Result;
                // SoftwareBitmap 自带像素缓冲,decoder/stream 之后释放仍有效
                return decoder.GetSoftwareBitmapAsync().AsTask().Result;
            }
        }

        // ----- OcrResult 提取 -----

        private static List<OcrTextLine> ExtractLines(OcrResult result, int imgW, int imgH)
        {
            var list = new List<OcrTextLine>();
            if (result == null) return list;

            IReadOnlyList<OcrLine> lines = result.Lines;
            if (lines == null) return list;

            for (int i = 0; i < lines.Count; i++)
            {
                OcrTextLine parsed = ParseLine(lines[i], i, imgW, imgH);
                if (parsed != null) list.Add(parsed);
            }
            return list;
        }

        private static OcrTextLine ParseLine(OcrLine line, int index, int imgW, int imgH)
        {
            if (line == null) return null;
            string text = line.Text ?? string.Empty;

            // Windows OCR 的 OcrLine 没有自己的 BoundingBox,
            // 用所有 OcrWord 的 BoundingRect 的并集。
            double xMin = double.MaxValue, yMin = double.MaxValue;
            double xMax = double.MinValue, yMax = double.MinValue;
            int wordCount = 0;

            IReadOnlyList<OcrWord> words = line.Words;
            if (words != null)
            {
                for (int i = 0; i < words.Count; i++)
                {
                    OcrWord w = words[i];
                    if (w == null) continue;
                    AccumulateRect(w.BoundingRect, imgW, imgH,
                        ref xMin, ref yMin, ref xMax, ref yMax);
                    wordCount++;
                }
            }

            OcrBoundingBox box;
            if (xMin == double.MaxValue)
            {
                box = new OcrBoundingBox(0, 0, 0, 0);
            }
            else
            {
                double w = Math.Max(0, xMax - xMin);
                double h = Math.Max(0, yMax - yMin);
                // 已是归一化(在 AccumulateRect 里转换过)
                box = new OcrBoundingBox(xMin, yMin, w, h);
            }

            // Windows OCR 不暴露单行置信度;用 1.0 占位
            return new OcrTextLine(index, text, 1.0, box);
        }

        /// <summary>
        /// 把 WinRT 的 Rect(左上原点,像素单位)累加到归一化、左下原点的范围。
        /// </summary>
        private static void AccumulateRect(Rect rect, int imgW, int imgH,
            ref double xMin, ref double yMin, ref double xMax, ref double yMax)
        {
            if (imgW <= 0 || imgH <= 0) return;

            // 归一化
            double nx1 = rect.X / imgW;
            double nx2 = (rect.X + rect.Width) / imgW;
            // 翻转 Y:左上 → 左下
            double nyTop = rect.Y / imgH;
            double nyBot = (rect.Y + rect.Height) / imgH;
            double ny1 = 1.0 - nyBot;
            double ny2 = 1.0 - nyTop;

            if (nx1 < xMin) xMin = nx1;
            if (ny1 < yMin) yMin = ny1;
            if (nx2 > xMax) xMax = nx2;
            if (ny2 > yMax) yMax = ny2;
        }
    }

    public sealed class OcrException : Exception
    {
        public OcrException(string message) : base(message) { }
        public OcrException(string message, Exception inner) : base(message, inner) { }
    }
}
