// ClipboardHelper.cs
// 剪贴板支持 —— 读取 / 写入图片。
// 用 System.Windows.Forms.Clipboard。
//
// 注意:Clipboard.GetImage() 要求 STA 线程。

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace WinOcrCli
{
    public static class ClipboardHelper
    {
        /// <summary>把 Bitmap 写到剪贴板(PNG 格式)。</summary>
        public static void WriteImage(Bitmap bmp)
        {
            if (bmp == null) throw new ClipboardException("无图片可写");
            EnsureSta();

            try
            {
                Clipboard.SetImage(bmp);
            }
            catch (Exception ex)
            {
                throw new ClipboardException("写入剪贴板失败: " + ex.Message);
            }
        }

        /// <summary>从剪贴板读取图片(Bitmap)。剪贴板无图时返回 null。</summary>
        public static Bitmap ReadImage()
        {
            EnsureSta();
            try
            {
                if (!Clipboard.ContainsImage())
                {
                    // 剪贴板里可能不是 .NET Image 格式;尝试读 DataObject
                    IDataObject data = Clipboard.GetDataObject();
                    if (data == null) return null;
                    if (data.GetDataPresent(DataFormats.Bitmap))
                    {
                        var img = data.GetData(DataFormats.Bitmap) as Image;
                        if (img == null) return null;
                        return new Bitmap(img);
                    }
                    return null;
                }
                return new Bitmap(Clipboard.GetImage());
            }
            catch (Exception ex)
            {
                throw new ClipboardException("读取剪贴板失败: " + ex.Message);
            }
        }

        /// <summary>把剪贴板图片(若存在)保存为临时 PNG,返回路径;无图返回 null。</summary>
        public static string SavePngToTemp()
        {
            Bitmap bmp = ReadImage();
            if (bmp == null) return null;
            try
            {
                string path = Path.Combine(Path.GetTempPath(),
                    "win_ocr_cli-pasteboard-" + Guid.NewGuid().ToString("N") + ".png");
                bmp.Save(path, ImageFormat.Png);
                return path;
            }
            finally
            {
                bmp.Dispose();
            }
        }

        // 剪贴板 API 要求 STA;若主线程不是 STA,抛出提示。
        private static void EnsureSta()
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return;
            // 这里实际上 Program 入口已经是 STA,但保险起见提示。
            throw new ClipboardException("剪贴板操作要求 STA 线程(请用 [STAThread] 标记入口)");
        }
    }

    public sealed class ClipboardException : Exception
    {
        public ClipboardException(string message) : base(message) { }
    }
}
