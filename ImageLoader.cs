// ImageLoader.cs
// 用 System.Drawing (GDI+) 加载图片为 Bitmap。
// .NET Framework 自带 Image.FromFile,支持 PNG/JPEG/BMP/GIF/TIFF。
// HEIC/WebP 在 GDI+ 中不受支持,会抛 NotSupportedException,被外层转为错误信息。

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace WinOcrCli
{
    public static class ImageLoader
    {
        /// <summary>从文件路径加载为 Bitmap。加载后调用方负责 Dispose。</summary>
        public static Bitmap Load(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ImageLoaderException("图片路径为空");
            if (!File.Exists(path)) throw new ImageLoaderException("图片文件不存在或无法访问: " + path);

            try
            {
                // 用 FileStream 包装,避免 Bitmap 持有文件锁
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var img = Image.FromStream(fs);
                    var bmp = new Bitmap(img);
                    img.Dispose();
                    return bmp;
                }
            }
            catch (OutOfMemoryException)
            {
                // GDI+ 用 OutOfMemoryException 表示格式不受支持或文件已损坏
                string ext = Path.GetExtension(path);
                if (string.IsNullOrEmpty(ext) || !CliParser.SupportedExtensions.Contains(ext))
                {
                    throw new ImageLoaderException("文件不是受支持的图片格式: " + path
                        + " (Windows 移植版支持: PNG / JPEG / BMP / GIF / TIFF)");
                }
                throw new ImageLoaderException("图片解码失败(文件可能已损坏): " + path);
            }
            catch (ArgumentException)
            {
                throw new ImageLoaderException("图片解码失败(文件可能已损坏): " + path);
            }
            catch (ExternalException ex)
            {
                throw new ImageLoaderException("GDI+ 错误: " + ex.Message);
            }
        }

        /// <summary>把 Bitmap 保存为 PNG 到指定文件路径。</summary>
        public static void SavePng(Bitmap bmp, string path)
        {
            if (bmp == null) throw new ArgumentNullException("bmp");
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            bmp.Save(path, ImageFormat.Png);
        }
    }

    public sealed class ImageLoaderException : Exception
    {
        public ImageLoaderException(string message) : base(message) { }
    }
}
