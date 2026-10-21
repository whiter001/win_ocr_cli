// SkillInstaller.cs
// 把 win_ocr_cli 的 Agent Skill(SKILL.md)安装到全局 skills 目录。
// 默认目标: %USERPROFILE%\.agents\skills\win-ocr-cli\SKILL.md

using System;
using System.IO;
using System.Text;

namespace WinOcrCli
{
    public static class SkillInstaller
    {
        public const string SkillDirName = "win-ocr-cli";

        public static string DefaultSkillsRoot
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".agents", "skills");
            }
        }

        /// <summary>安装 SKILL.md 到全局 skills 目录,返回写入的文件路径。已存在则覆盖。</summary>
        public static string Install()
        {
            string dir = Path.Combine(DefaultSkillsRoot, SkillDirName);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "SKILL.md");
            File.WriteAllText(path, SkillContent, new UTF8Encoding(false));
            return path;
        }

        public const string SkillContent = @"---
name: win-ocr-cli
description: Windows 命令行 OCR 工具(基于 Windows.Media.Ocr)。当需要识别图片中的文字、截屏并 OCR、读取剪贴板图片中的文字、枚举或截取窗口时使用。支持简体中文/繁体/英文,输出纯文本或 JSON(含归一化坐标框)。
---

# win_ocr_cli

基于 Windows.Media.Ocr 的 Windows 命令行 OCR 工具,零运行时依赖的单文件 exe。

## 前提

- Windows 10+,且已安装对应语言的 OCR 功能包(见 `win_ocr_cli --help`)
- `win_ocr_cli` 在 PATH 中,或用完整路径调用

## 常用命令

```bash
win_ocr_cli photo.png                          # 识别图片(默认纯文本)
win_ocr_cli shot.jpg --json                    # JSON 输出(含归一化 boundingBox)
win_ocr_cli --screen                           # 截主屏并 OCR
win_ocr_cli --region x y w h                   # 截屏幕区域并 OCR(左上角原点)
win_ocr_cli --window ""记事本""                 # 按标题/进程名截窗口并 OCR
win_ocr_cli --window-list                      # 列出可见窗口(用于查 window id)
win_ocr_cli --window-id <id>                   # 按 id 截窗口并 OCR
win_ocr_cli --from-clipboard                   # 识别剪贴板图片(Win+Shift+S 截图后用)
win_ocr_cli --clipboard [--region x y w h]     # 截图到剪贴板(不做 OCR)
win_ocr_cli --dir .\shots --json -o all.json   # 批量:递归扫描目录
win_ocr_cli a.png b.png --json -o all.json     # 批量:多文件
```

## 注意

- 退出码:0 成功 / 1 参数或运行错误 / 2 批量模式有失败项。
- JSON 中 `boundingBox` 为 [0,1] 归一化值,原点在图像**左下角**;`confidence` 恒为 1.0(Windows OCR 不暴露单行置信度)。
- 完整参数说明运行 `win_ocr_cli --help`。
";
    }
}
