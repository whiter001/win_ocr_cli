// OcrTypes.cs
// 数据类型定义 — 对应 macOS 版本的 OCRBoundingBox / OCRTextLine / OCRReport / OCRBatchItem。
// Windows 移植版,使用 .NET Framework 4.5 + C# 5。

using System;
using System.Collections.Generic;
using System.Globalization;

namespace WinOcrCli
{
    /// <summary>
    /// 归一化边界框（坐标原点在图像**左下角**,Y 轴向上）,
    /// 字段取值范围 [0, 1]。与 macOS 版输出一致(Windows OCR 原始坐标为左上原点 + 像素,已翻转归一化)。
    /// </summary>
    public struct OcrBoundingBox
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;

        public OcrBoundingBox(double x, double y, double w, double h)
        {
            X = Sanitize(x);
            Y = Sanitize(y);
            Width = Sanitize(Math.Abs(w));
            Height = Sanitize(Math.Abs(h));
        }

        private static double Sanitize(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }
    }

    /// <summary>单行识别结果。</summary>
    public sealed class OcrTextLine
    {
        public int Index;
        public string Text;
        public double Confidence;
        public OcrBoundingBox BoundingBox;

        public OcrTextLine(int index, string text, double confidence, OcrBoundingBox box)
        {
            Index = Math.Max(0, index);
            Text = text ?? string.Empty;
            Confidence = SanitizeConf(confidence);
            BoundingBox = box;
        }

        private static double SanitizeConf(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }
    }

    /// <summary>单文件识别报告。</summary>
    public sealed class OcrReport
    {
        public string ImagePath;
        public List<OcrTextLine> Lines;

        public OcrReport(string imagePath, List<OcrTextLine> lines)
        {
            ImagePath = imagePath;
            Lines = lines ?? new List<OcrTextLine>();
        }
    }

    /// <summary>批量模式下的单条结果。ok 与 failed 二选一。</summary>
    public sealed class OcrBatchItem
    {
        public string ImagePath;
        public string Status;            // "ok" | "failed"
        public List<OcrTextLine> Lines;  // null when failed
        public string ErrorMessage;      // null when ok

        public OcrBatchItem(string imagePath, string status, List<OcrTextLine> lines, string errorMessage)
        {
            ImagePath = imagePath;
            Status = status;
            Lines = lines;
            ErrorMessage = errorMessage;
        }

        public static OcrBatchItem Success(string imagePath, List<OcrTextLine> lines)
        {
            return new OcrBatchItem(imagePath, "ok", lines, null);
        }

        public static OcrBatchItem Failure(string imagePath, string errorMessage)
        {
            return new OcrBatchItem(imagePath, "failed", null, errorMessage);
        }
    }

    /// <summary>批量报告。</summary>
    public sealed class OcrBatchReport
    {
        public List<OcrBatchItem> Items;

        public OcrBatchReport(List<OcrBatchItem> items)
        {
            Items = items ?? new List<OcrBatchItem>();
        }

        public int SuccessCount
        {
            get
            {
                int c = 0;
                for (int i = 0; i < Items.Count; i++) if (Items[i].Status == "ok") c++;
                return c;
            }
        }

        public int FailureCount
        {
            get
            {
                int c = 0;
                for (int i = 0; i < Items.Count; i++) if (Items[i].Status == "failed") c++;
                return c;
            }
        }
    }
}
