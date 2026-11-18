@echo off
REM build.cmd - 编译 win_ocr_cli
REM 使用 .NET Framework 4.5+ 自带的 csc.exe (C# 5)
REM 输出: bin\win_ocr_cli.exe
REM
REM 需要的额外引用:
REM   System.Runtime.WindowsRuntime.dll  - WinRT 投影 (IAsyncOperation → Task 转换)
REM   Windows.winmd                      - 合并所有 WinRT 类型的框架程序集
REM     (Windows.Foundation / Windows.Media / Windows.Graphics /
REM      Windows.Globalization / Windows.Storage 都在里面)

setlocal

set CSC="C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
set OUT=bin\win_ocr_cli.exe

REM ---- 定位合并 WinMD(包含 Windows.winmd 的目录)----
REM 探测顺序:
REM   1) 环境变量 WINRT_OVERRIDE —— 直接指定目录
REM   2) %LOCALAPPDATA%\winmd\UnionMetadata\<版本>\  —— 免安装 SDK 的本地缓存
REM   3) C:\Program Files (x86)\Windows Kits\10\UnionMetadata\<版本>\  —— 官方 SDK
set WINRT=
if defined WINRT_OVERRIDE set WINRT=%WINRT_OVERRIDE%
if not defined WINRT for /d %%D in ("%LOCALAPPDATA%\winmd\UnionMetadata\*") do if exist "%%~fD\Windows.winmd" set WINRT=%%~fD
if not defined WINRT for /d %%D in ("C:\Program Files (x86)\Windows Kits\10\UnionMetadata\*") do if exist "%%~fD\Windows.winmd" set WINRT=%%~fD
if not defined WINRT for /d %%D in ("C:\Program Files\Windows Kits\10\UnionMetadata\*") do if exist "%%~fD\Windows.winmd" set WINRT=%%~fD

if not defined WINRT (
    echo *** 找不到 Windows.winmd ***
    echo 请安装 Windows SDK 10.0.22621.0 ^(https://developer.microsoft.com/windows/downloads/windows-sdk/^),
    echo 或把合并 WinMD 放到 %%LOCALAPPDATA%%\winmd\UnionMetadata\<版本>\Windows.winmd,
    echo 或设置环境变量 WINRT_OVERRIDE 指向包含 Windows.winmd 的目录。
    exit /b 1
)

if not exist bin mkdir bin

echo === Compiling win_ocr_cli (WinMD: %WINRT%) ===
%CSC% /nologo /target:exe /platform:anycpu /out:%OUT% ^
    /reference:System.dll ^
    /reference:System.Core.dll ^
    /reference:System.Drawing.dll ^
    /reference:System.Windows.Forms.dll ^
    /reference:System.Runtime.dll ^
    /reference:System.Runtime.WindowsRuntime.dll ^
    /reference:mscorlib.dll ^
    /reference:"%WINRT%\Windows.winmd" ^
    OcrTypes.cs Cli.cs ImageLoader.cs OcrEngine.cs ScreenCapture.cs ClipboardHelper.cs OutputRenderer.cs SkillInstaller.cs SelfInstaller.cs Program.cs

if %ERRORLEVEL% NEQ 0 (
    echo *** Build FAILED ***
    exit /b 1
)

echo === Build OK: %OUT% ===
endlocal
