@echo off
REM build.cmd - 编译 win_ocr_cli
REM 使用 .NET Framework 4.0 自带的 csc.exe (C# 5)
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
set WINRT=C:\PROGRA~2\Windows Kits\10\UnionMetadata\10.0.22621.0

if not exist bin mkdir bin

echo === Compiling win_ocr_cli ===
%CSC% /nologo /target:winexe /platform:anycpu /out:%OUT% ^
    /reference:System.dll ^
    /reference:System.Core.dll ^
    /reference:System.Drawing.dll ^
    /reference:System.Windows.Forms.dll ^
    /reference:System.Runtime.dll ^
    /reference:System.Runtime.WindowsRuntime.dll ^
    /reference:mscorlib.dll ^
    /reference:"%WINRT%\Windows.winmd" ^
    OcrTypes.cs Cli.cs ImageLoader.cs OcrEngine.cs ScreenCapture.cs ClipboardHelper.cs OutputRenderer.cs Program.cs

if %ERRORLEVEL% NEQ 0 (
    echo *** Build FAILED ***
    exit /b 1
)

echo === Build OK: %OUT% ===
endlocal
