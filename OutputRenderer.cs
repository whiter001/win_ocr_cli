// OutputRenderer.cs
// 输出渲染:文本 / JSON 两种格式。
// .NET 4.0 没有 System.Text.Json,这里实现一个轻量的手写 JSON 序列化器,
// 足以覆盖我们的结构(OcrReport / OcrBatchReport / WindowInfo)。
//
// 文本格式与 macOS 版本保持一致(中文说明、对齐、归一化 box)。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WinOcrCli
{
    public static class OutputRenderer
    {
        // ===== 文本输出 =====

        public static string RenderText(OcrReport report, CliOptions opts)
        {
            var sb = new StringBuilder();
            sb.Append("图片: ").AppendLine(report.ImagePath);
            sb.Append("识别行数: ").Append(report.Lines.Count).AppendLine();

            if (!string.IsNullOrEmpty(opts.Keyword))
            {
                var scored = new List<ScoredLine>();
                for (int i = 0; i < report.Lines.Count; i++)
                {
                    double s = Score(report.Lines[i], opts.Keyword);
                    if (s > 0) scored.Add(new ScoredLine { Idx = i, Line = report.Lines[i], Score = s });
                }
                scored.Sort(CompareScoredLines);

                sb.Append("关键词: ").AppendLine(opts.Keyword);
                if (scored.Count == 0)
                {
                    sb.AppendLine("命中: 无");
                }
                else
                {
                    sb.AppendLine("命中(按相关度排序):");
                    foreach (var t in scored)
                    {
                        string preview = (t.Line.Text ?? "").Replace("\n", " ");
                        sb.AppendFormat(CultureInfo.InvariantCulture, "- [{0}] {1:0.00}  {2}",
                            t.Idx, t.Score, preview);
                        sb.AppendLine();
                    }
                }
                return sb.ToString();
            }

            sb.AppendLine("结果:");
            foreach (var line in report.Lines)
            {
                string preview = (line.Text ?? "").Replace("\n", " ");
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "- [{0}] conf={1:0.00} box=({2:0.00},{3:0.00},{4:0.00},{5:0.00})  {6}",
                    line.Index, line.Confidence,
                    line.BoundingBox.X, line.BoundingBox.Y,
                    line.BoundingBox.Width, line.BoundingBox.Height,
                    preview);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        public static string RenderBatchText(OcrBatchReport batch)
        {
            var sb = new StringBuilder();
            sb.AppendFormat("批量识别: 共 {0} 个 (成功 {1}, 失败 {2})",
                batch.Items.Count, batch.SuccessCount, batch.FailureCount);
            sb.AppendLine();

            for (int i = 0; i < batch.Items.Count; i++)
            {
                var item = batch.Items[i];
                sb.AppendLine();
                sb.AppendFormat("===== [{0}/{1}] {2} =====", i + 1, batch.Items.Count, item.ImagePath);
                sb.AppendLine();
                if (item.Status == "ok")
                {
                    var lines = item.Lines ?? new List<OcrTextLine>();
                    sb.Append("识别行数: ").Append(lines.Count).AppendLine();
                    foreach (var line in lines)
                    {
                        string preview = (line.Text ?? "").Replace("\n", " ");
                        sb.AppendFormat(CultureInfo.InvariantCulture,
                            "- [{0}] conf={1:0.00} box=({2:0.00},{3:0.00},{4:0.00},{5:0.00})  {6}",
                            line.Index, line.Confidence,
                            line.BoundingBox.X, line.BoundingBox.Y,
                            line.BoundingBox.Width, line.BoundingBox.Height,
                            preview);
                        sb.AppendLine();
                    }
                }
                else
                {
                    sb.Append("失败: ").AppendLine(item.ErrorMessage ?? "未知错误");
                }
            }
            return sb.ToString();
        }

        public static string RenderWindowListText(List<WindowInfo> wins)
        {
            var sb = new StringBuilder();
            if (wins.Count == 0)
            {
                sb.AppendLine("(无可用窗口)");
                return sb.ToString();
            }
            sb.Append("可见窗口: ").Append(wins.Count).AppendLine(" 个");
            foreach (var w in wins)
            {
                string name = string.IsNullOrEmpty(w.Title) ? "(无标题)" : w.Title;
                string owner = string.IsNullOrEmpty(w.Owner) ? "?" : w.Owner;
                sb.AppendFormat("  id={0} [{1}] {2}  ({3},{4},{5},{6})",
                    w.WindowId, owner, name, w.X, w.Y, w.W, w.H);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        public static string RenderClipboardText(int w, int h, string savePath)
        {
            if (!string.IsNullOrEmpty(savePath))
            {
                return string.Format("已复制到剪贴板: {0}x{1}  另存: {2}", w, h, savePath);
            }
            return string.Format("已复制到剪贴板: {0}x{1}", w, h);
        }

        // ===== 关键词打分 =====

        private sealed class ScoredLine
        {
            public int Idx;
            public OcrTextLine Line;
            public double Score;
        }

        private static int CompareScoredLines(ScoredLine a, ScoredLine b)
        {
            return b.Score.CompareTo(a.Score);
        }

        private static double Score(OcrTextLine line, string keyword)
        {
            if (string.IsNullOrEmpty(keyword)) return 0;
            string text = line.Text ?? string.Empty;
            if (text == keyword) return 1.0;
            if (text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return 0.9;
            return 0;
        }

        // ===== JSON 输出 =====

        public static string RenderJson(OcrReport report)
        {
            var w = new JsonWriter();
            w.BeginObject();
            w.Key("imagePath"); w.String(report.ImagePath);
            w.Key("lines"); w.BeginArray();
            foreach (var line in report.Lines)
            {
                w.BeginObject();
                w.Key("index"); w.Int(line.Index);
                w.Key("text"); w.String(line.Text ?? "");
                w.Key("confidence"); w.Double(line.Confidence);
                w.Key("boundingBox");
                WriteBoundingBox(w, line.BoundingBox);
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
            return w.ToString();
        }

        public static string RenderBatchJson(OcrBatchReport batch)
        {
            var w = new JsonWriter();
            w.BeginObject();
            w.Key("items"); w.BeginArray();
            foreach (var item in batch.Items)
            {
                w.BeginObject();
                w.Key("imagePath"); w.String(item.ImagePath ?? "");
                w.Key("status"); w.String(item.Status ?? "");
                if (item.Status == "ok" && item.Lines != null)
                {
                    w.Key("lines"); w.BeginArray();
                    foreach (var line in item.Lines)
                    {
                        w.BeginObject();
                        w.Key("index"); w.Int(line.Index);
                        w.Key("text"); w.String(line.Text ?? "");
                        w.Key("confidence"); w.Double(line.Confidence);
                        w.Key("boundingBox");
                        WriteBoundingBox(w, line.BoundingBox);
                        w.EndObject();
                    }
                    w.EndArray();
                }
                else
                {
                    w.Key("lines"); w.Null();
                }
                if (!string.IsNullOrEmpty(item.ErrorMessage))
                {
                    w.Key("errorMessage"); w.String(item.ErrorMessage);
                }
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
            return w.ToString();
        }

        public static string RenderWindowListJson(List<WindowInfo> wins)
        {
            var w = new JsonWriter();
            w.BeginObject();
            w.Key("windows"); w.BeginArray();
            foreach (var win in wins)
            {
                w.BeginObject();
                w.Key("windowId"); w.Int(win.WindowId);
                w.Key("ownerName"); w.String(win.Owner ?? "");
                w.Key("windowName"); w.String(win.Title ?? "");
                w.Key("layer"); w.Int(0);
                w.Key("bounds");
                w.BeginObject();
                w.Key("x"); w.Int(win.X);
                w.Key("y"); w.Int(win.Y);
                w.Key("width"); w.Int(win.W);
                w.Key("height"); w.Int(win.H);
                w.EndObject();
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
            return w.ToString();
        }

        public static string RenderClipboardJson(int w, int h, string savePath)
        {
            var jw = new JsonWriter();
            jw.BeginObject();
            jw.Key("clipboard"); jw.Bool(true);
            jw.Key("width"); jw.Int(w);
            jw.Key("height"); jw.Int(h);
            jw.Key("savePath");
            if (savePath != null) jw.String(savePath); else jw.Null();
            jw.EndObject();
            return jw.ToString();
        }

        private static void WriteBoundingBox(JsonWriter w, OcrBoundingBox b)
        {
            w.BeginObject();
            w.Key("x"); w.Double(b.X);
            w.Key("y"); w.Double(b.Y);
            w.Key("width"); w.Double(b.Width);
            w.Key("height"); w.Double(b.Height);
            w.EndObject();
        }
    }

    /// <summary>
    /// 轻量 pretty-printed JSON 写入器。
    /// 调用顺序必须形如 BeginObject → Key → value → ... → EndObject,数组同理。
    /// </summary>
    public sealed class JsonWriter
    {
        private enum ContainerKind { Object, Array }

        private readonly StringBuilder _sb = new StringBuilder();
        private readonly Stack<ContainerKind> _stack = new Stack<ContainerKind>();
        private readonly Stack<bool> _firstInContainer = new Stack<bool>();
        private int _indent = 0;
        // 标记刚刚调用了 Key();下一次 Value 调用应跳过 comma/indent 前缀。
        private bool _expectingValue = false;

        public void BeginObject()
        {
            PrefixElement();
            _sb.Append('{');
            _stack.Push(ContainerKind.Object);
            _firstInContainer.Push(true);
            _indent++;
            _expectingValue = false;
            NewLineIndent();
        }

        public void EndObject()
        {
            if (_stack.Count == 0 || _stack.Peek() != ContainerKind.Object)
                throw new InvalidOperationException("EndObject when not in object");
            _stack.Pop();
            _firstInContainer.Pop();
            _indent--;
            _expectingValue = false;
            NewLineIndent();
            _sb.Append('}');
        }

        public void BeginArray()
        {
            PrefixElement();
            _sb.Append('[');
            _stack.Push(ContainerKind.Array);
            _firstInContainer.Push(true);
            _indent++;
            _expectingValue = false;
            NewLineIndent();
        }

        public void EndArray()
        {
            if (_stack.Count == 0 || _stack.Peek() != ContainerKind.Array)
                throw new InvalidOperationException("EndArray when not in array");
            _stack.Pop();
            _firstInContainer.Pop();
            _indent--;
            _expectingValue = false;
            NewLineIndent();
            _sb.Append(']');
        }

        public void Key(string k)
        {
            if (_stack.Count == 0 || _stack.Peek() != ContainerKind.Object)
                throw new InvalidOperationException("Key() only valid inside object");
            bool first = _firstInContainer.Peek();
            if (first)
            {
                _firstInContainer.Pop();
                _firstInContainer.Push(false);
            }
            else
            {
                _sb.Append(',');
                NewLineIndent();
            }
            // 回到对象缩进
            for (int i = 0; i < _indent - 1; i++) _sb.Append("  ");
            WriteString(k);
            _sb.Append(": ");
            _expectingValue = true;
        }

        public void String(string s)
        {
            ValuePrefix();
            WriteString(s);
            _expectingValue = false;
        }

        public void Int(int n)
        {
            ValuePrefix();
            _sb.Append(n.ToString(CultureInfo.InvariantCulture));
            _expectingValue = false;
        }

        public void Double(double d)
        {
            ValuePrefix();
            _sb.Append(d.ToString("0.######", CultureInfo.InvariantCulture));
            _expectingValue = false;
        }

        public void Bool(bool b)
        {
            ValuePrefix();
            _sb.Append(b ? "true" : "false");
            _expectingValue = false;
        }

        public void Null()
        {
            ValuePrefix();
            _sb.Append("null");
            _expectingValue = false;
        }

        public override string ToString() { return _sb.ToString(); }

        /// <summary>在 BeginObject/BeginArray 之前处理逗号与缩进(在 array/object 内部)。</summary>
        private void PrefixElement()
        {
            if (_stack.Count == 0)
            {
                // 顶层,无需前缀
                return;
            }
            // 在 object 里,只有「刚调用了 Key」之后才能开始新对象/数组作为 value
            if (_stack.Peek() == ContainerKind.Object)
            {
                if (!_expectingValue)
                {
                    throw new InvalidOperationException("Value() in non-array container; use Key() first");
                }
                // Key 已经写了 "key: ",不再加前缀
                return;
            }
            // array 元素
            bool first = _firstInContainer.Peek();
            if (first)
            {
                _firstInContainer.Pop();
                _firstInContainer.Push(false);
            }
            else
            {
                _sb.Append(',');
                NewLineIndent();
                for (int i = 0; i < _indent - 1; i++) _sb.Append("  ");
            }
        }

        /// <summary>写入 Value 之前的前缀处理:对象里跟在 Key 后,数组里是元素前缀。</summary>
        private void ValuePrefix()
        {
            if (_expectingValue)
            {
                // Key() 已经写了 "key: ",不需要再写逗号或缩进
                _expectingValue = false;
                return;
            }
            if (_stack.Count == 0) return;  // 顶层
            if (_stack.Peek() == ContainerKind.Object)
            {
                throw new InvalidOperationException("Value() in object container; use Key() first");
            }
            // array 元素
            bool first = _firstInContainer.Peek();
            if (first)
            {
                _firstInContainer.Pop();
                _firstInContainer.Push(false);
            }
            else
            {
                _sb.Append(',');
                NewLineIndent();
                for (int i = 0; i < _indent - 1; i++) _sb.Append("  ");
            }
        }

        private void NewLineIndent()
        {
            _sb.Append('\n');
        }

        private void WriteString(string s)
        {
            _sb.Append('"');
            if (s != null)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    switch (c)
                    {
                        case '"': _sb.Append("\\\""); break;
                        case '\\': _sb.Append("\\\\"); break;
                        case '\b': _sb.Append("\\b"); break;
                        case '\f': _sb.Append("\\f"); break;
                        case '\n': _sb.Append("\\n"); break;
                        case '\r': _sb.Append("\\r"); break;
                        case '\t': _sb.Append("\\t"); break;
                        default:
                            if (c < 0x20)
                            {
                                _sb.Append("\\u");
                                _sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                _sb.Append(c);
                            }
                            break;
                    }
                }
            }
            _sb.Append('"');
        }
    }
}
