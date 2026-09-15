@echo off
rem =====================================================================
rem  JellyWallpaper 一键发布脚本（Windows）
rem  在装有 .NET 8 SDK 或 VS2022 的 Windows 机器上双击运行，
rem  输出独立单文件 EXE 到 dist\ 目录（绿色免安装，自带 .NET 运行时）。
rem =====================================================================
setlocal
cd /d "%~dp0"

echo [1/2] 开始发布独立单文件 EXE ...
dotnet publish src\JellyWallpaper.App\JellyWallpaper.App.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:WindowsAppSDKSelfContained=true ^
  -p:WindowsPackageType=None ^
  -o dist

if errorlevel 1 (
  echo.
  echo [错误] 发布失败。请确认已安装 .NET 8 SDK：
  echo   https://dotnet.microsoft.com/download/dotnet/8.0
  echo 或使用 VS2022 打开 JellyWallpaper.sln 后直接 Ctrl+P 发布。
  pause
  exit /b 1
)

echo.
echo [2/2] 完成！输出目录：%~dp0dist
echo 产物：dist\JellyWallpaper.exe （单文件，免安装）
echo.
echo 备用发布方式（若单文件与 WinAppSDK 出现兼容问题）：
echo   dotnet publish src\JellyWallpaper.App\JellyWallpaper.App.csproj -c Release -r win-x64 --self-contained true -o dist-folder
echo   （dist-folder 目录整体拷走即可，JellyWallpaper.exe 双击运行）
pause
