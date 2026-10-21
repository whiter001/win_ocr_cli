# win_ocr_cli

基于 [Windows.Media.Ocr](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr) (WinRT) 的 Windows 命令行 OCR 工具。
[mac_ocr_cli](https://github.com/whiter001/mac_ocr_cli) 的 Windows 移植版,使用
`C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe` (C# 5 / .NET Framework 4.5+) 编译。

零运行时依赖 —— 链接到系统内置的 Windows Runtime,产物为单文件 45 KB exe。

## 功能

- 读取 **PNG / JPEG / BMP / GIF / TIFF**(由 GDI+ 解码)
- 调用 `Windows.Media.Ocr.OcrEngine` 做文字识别,默认简/繁/英三种语言
- `--cjk` 预设额外增加粤语(香港书面语),覆盖港版 UI
- 输出每行文字与**归一化**边界框(原点在图像**左下角**,与 macOS 版一致 —— Windows OCR 内部用左上原点,这里做了翻转)
- 可选 JSON / 纯文本输出,可写入文件
- 可选关键词搜索并按相关度排序
- **批量**:多张图片、目录递归扫描、stdin 路径列表,并发处理,单张失败不影响其他
- **屏幕截图**:截主屏幕、按窗口标题/ID 截、截屏幕区域;另含 `--window-list` 列出可见窗口
- **剪贴板**:截图到剪贴板(`--clipboard`)、识别剪贴板里的图片(`--from-clipboard`)
- **Agent Skill**:`--install-skill` 把 SKILL.md 安装到全局 skills 目录(`%USERPROFILE%\.agents\skills\win-ocr-cli\`)
- **进度** + **`--quiet`**:批量模式默认在 stderr 打印每张完成进度,`--quiet` 抑制所有非错误输出

## 编译

需要 **Windows 10 或更高版本** + **.NET Framework 4.5+**(已自带在 Win 10/11)。
还需本机已安装 **Windows SDK 10.0.22621.0** 的合并 WinMD(`Windows.winmd`)。

直接运行 `build.cmd`:

```cmd
build.cmd
```

输出 `bin\win_ocr_cli.exe`(单文件 45 KB)。

如果 SDK 路径不同,修改 `build.cmd` 里的 `WINRT` 变量;找不到 SDK 时,可以从
`C:\Windows\System32\WinMetadata\` 下分别引用 `Windows.Foundation.winmd` /
`Windows.Media.winmd` / `Windows.Graphics.winmd` / `Windows.Globalization.winmd` /
`Windows.Storage.winmd` 代替合并的 `Windows.winmd`,但合并 WinMD 更简单。

## 安装(从 release)

```powershell
# 下载预编译的 win_ocr_cli.exe
Invoke-WebRequest -Uri https://github.com/whiter001/win_ocr_cli/releases/latest/download/win_ocr_cli.exe -OutFile win_ocr_cli.exe

# 放到 PATH(任选)
Move-Item win_ocr_cli.exe $env:LOCALAPPDATA\Microsoft\WindowsApps\

# 验证
win_ocr_cli --version
```

## OCR 语言包

Windows 的 OCR 引擎按单语言启动;`--lang` 选第一个,失败时回落到系统默认。
`--lang` 选的语言需要本机有对应的 OCR 功能包:

```powershell
# 查看已安装的 OCR 语言
Get-WindowsCapability -Online | Where-Object Name -like 'Language.OCR*'

# 安装一个(例如简体中文)
Add-WindowsCapability -Online -Name "Language.OCR~~~zh-Hans~0.0.1.0"
Add-WindowsCapability -Online -Name "Language.OCR~~~zh-Hant~0.0.1.0"
Add-WindowsCapability -Online -Name "Language.OCR~~~en-US~0.0.1.0"
```

`--cjk` 预设(zh-Hans, zh-Hant, zh-HK, en-US)需要粤语 OCR 包。

## 用法

```powershell
# 识别一张图(默认输出纯文本)
win_ocr_cli photo.png

# 用 JSON 输出并写入文件
win_ocr_cli shot.jpg --json -o result.json

# 改识别语言
win_ocr_cli menu.png -l zh-Hans

# 关闭语言纠错(适合代码 / 编号)
win_ocr_cli code.png --no-correction

# 切换到 CJK 预设(简/繁/港/英)
win_ocr_cli menu.png --cjk

# 在识别结果里搜索关键词
win_ocr_cli page.png -k "登录"

# 批量:多张图片
win_ocr_cli a.png b.png c.png

# 批量:递归扫描目录
win_ocr_cli --dir .\shots

# 批量:写到 JSON 文件
win_ocr_cli --dir .\shots --json -o all.json

# 批量:stdin 路径列表(`Get-ChildItem` 风格)
Get-ChildItem -Recurse -Filter *.png | ForEach-Object FullName | win_ocr_cli - --json -o all.json

# stdin 支持注释(#)和空行
type list.txt | win_ocr_cli - -l en-US
# list.txt 格式:
#   # 截图
#   C:\Users\me\Desktop\a.png
#
#   C:\Users\me\Desktop\b.png

# 截主屏幕并 OCR
win_ocr_cli --screen

# 截特定窗口(按标题或 app 名匹配)
win_ocr_cli --window "记事本" --save-screenshot $env:USERPROFILE\Desktop\shot.png

# 截屏幕区域(左上角原点)
win_ocr_cli --region 0 0 1200 800

# 列出所有可见窗口(用于查找要截的窗口)
win_ocr_cli --window-list

# 截图到剪贴板(粘到聊天/邮件直接用)
win_ocr_cli --clipboard
win_ocr_cli --clipboard --region 0 0 1200 800
win_ocr_cli --clipboard --window "Chrome"

# 识别剪贴板里的图片(系统截图快捷键 Win+Shift+S 截到剪贴板)
win_ocr_cli --from-clipboard
win_ocr_cli --from-clipboard -k "订单号"

# 安装全局 Agent Skill(让 AI Agent 自动发现本工具的用法)
win_ocr_cli --install-skill
```

## 进度与静默

- **默认**:批量模式每张图完成时在 stderr 打印进度,最后一行汇总。
- **`--quiet` / `-q`**:抑制所有非错误输出。错误仍到 stderr,exit code 不变(批量有失败仍为 2)。
- **`--output` 与 `--quiet` 正交**:`--quiet --json -o result.json --dir batch\` 会把 JSON 写到文件,stdout 完全干净。

## 输出结构(JSON)

**单文件模式**(输入只有 1 张图,schema 与 macOS 版保持兼容):

```json
{
  "imagePath": "C:\\path\\to.png",
  "lines": [
    {
      "index": 0,
      "text": "示例文本",
      "confidence": 1.0,
      "boundingBox": { "x": 0.1, "y": 0.2, "width": 0.5, "height": 0.05 }
    }
  ]
}
```

**批量模式**(≥ 2 张图或 `--dir`):

```json
{
  "items": [
    {
      "imagePath": "C:\\path\\a.png",
      "status": "ok",
      "lines": [ { "index": 0, "text": "...", "confidence": 1.0, "boundingBox": {...} } ]
    },
    {
      "imagePath": "C:\\path\\b.png",
      "status": "failed",
      "errorMessage": "图片解码失败(文件可能已损坏): ..."
    }
  ]
}
```

- `boundingBox` 字段均为 `[0, 1]` 归一化值,原点在图像**左下角**。
  Windows OCR 内部使用左上原点 + 像素单位,本工具在输出时做了翻转 + 归一化。
- `confidence` 总是 `1.0`,因为 Windows OCR 不暴露单行置信度。
- 批量模式并发上限为 `min(4, 核数)`,输出顺序与输入一致。
- 批量模式在任一项失败时以 exit code **2** 退出。

## 与 macOS 版的差异

| 维度 | macOS 版 | Windows 版 |
|---|---|---|
| 识别引擎 | Apple Vision (`VNRecognizeTextRequest`) | Windows.Media.Ocr (`OcrEngine`) |
| 引擎语言 | 多语言并行 (reviseLanguages) | 单语言 (按 `--lang` 第一个;失败回落) |
| 识别等级 / 语言纠错 | `recognitionLevel` / 自动纠错 | 不支持(`--level` / `--no-correction` 为兼容保留,但不生效) |
| 单行置信度 | 有(`bestCandidate.confidence`) | 无(固定 `1.0`) |
| 图片格式 | PNG / JPEG / HEIC / TIFF / GIF / WebP / PDF | PNG / JPEG / BMP / GIF / TIFF |
| 截图 | `CGDisplayCreateImage` / `CGWindowListCreateImage` | `Graphics.CopyFromScreen` / `PrintWindow` |
| 坐标原点在输出时 | 左下(Vision 原生) | 左下(本工具从 Windows 的左上翻转) |
| 编译器 | Swift 5.9+ / Xcode 15+ | csc.exe v4.0.30319(C# 5 / .NET 4.0) |
| 产物大小 | ~ | 45 KB 单 exe |
| 系统要求 | macOS 13+ | Windows 10+ |

## 实现要点

- **WinRT 投影**:`.NET Framework 4.5` 的 csc.exe 不内置 WinRT 投影。我们引用
  `C:\Windows\Microsoft.NET\Framework\v4.0.30319\System.Runtime.WindowsRuntime.dll` +
  Windows SDK 10.0.22621.0 的合并 `Windows.winmd`,直接使用 `Windows.Media.Ocr.OcrEngine`、
  `Windows.Graphics.Imaging.SoftwareBitmap` 等强类型 API;WinRT 异步操作用
  `WindowsRuntimeSystemExtensions.AsTask(IAsyncOperation<T>)` 同步化。
- **图像解码**:`System.Drawing.Bitmap` 加载 GDI+ 支持的图片,编码为 PNG 字节,
  写入 `Windows.Storage.Streams.InMemoryRandomAccessStream`,由 `BitmapDecoder.CreateAsync`
  解码成 `SoftwareBitmap` 喂给 OcrEngine。
- **图像坐标**:OcrLine 没有自己的 BoundingBox,我们用所有 OcrWord.BoundingRect 的
  并集作为行框;再把 Windows 的「左上原点 + 像素」转换为「左下原点 + [0,1] 归一化」。
- **JSON 序列化**:手写 `JsonWriter`(C# 5 没有 `System.Text.Json`)。
- **截图**:`Graphics.CopyFromScreen` 截主屏 / 区域;`user32!PrintWindow` + DWM
  合成标志(0x2)截窗口。窗口列表用 `EnumWindows` + `GetWindowText` + 进程名。
- **并发**:批量模式用 `Task.Run` + `SemaphoreSlim`,并发上限 = `min(4, 核数)`,与 macOS 版一致。

## 依赖

零运行时依赖 —— 仅使用系统内置的 .NET Framework + Windows Runtime:

- `System.dll`, `System.Core.dll`, `System.Drawing.dll`, `System.Windows.Forms.dll`
- `System.Runtime.WindowsRuntime.dll`(.NET 4.5+ 自带,提供 WinRT 投影)
- `Windows.winmd`(Windows SDK 10.0.22621.0,合并所有 WinRT 类型元数据)
