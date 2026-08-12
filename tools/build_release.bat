@echo off
REM ============================================================
REM  PotatoVN LibraryPlus one-click release build (VS alternative)
REM  Usage: double-click or run tools\build_release.bat
REM  Output: artifacts\plugin.pvnplugin.zip
REM  Note: English messages only - avoids cmd GBK encoding issues
REM ============================================================
setlocal
cd /d "%~dp0.."

echo [1/3] Cleaning incremental build cache (namespace stamping XAML quirk - full rebuild safest)
rmdir /s /q "PotatoVN.App.PluginBase\bin" 2>nul
rmdir /s /q "PotatoVN.App.PluginBase\obj" 2>nul

echo [2/3] Release build (auto-packs plugin.pvnplugin.zip)
dotnet build "PotatoVN.App.PluginBase\PotatoVN.App.PluginBase.csproj" -c Release
if errorlevel 1 (
    echo.
    echo [FAILED] Build error - check messages above
    exit /b 1
)

echo [3/3] Checking artifact
if exist "PotatoVN.App.PluginBase\artifacts\plugin.pvnplugin.zip" (
    echo.
    echo [OK] Artifact: PotatoVN.App.PluginBase\artifacts\plugin.pvnplugin.zip
    for %%F in ("PotatoVN.App.PluginBase\artifacts\plugin.pvnplugin.zip") do echo      Size: %%~zF bytes
) else (
    echo [WARN] plugin.pvnplugin.zip not found - check build log
)
endlocal
