@echo off
setlocal enabledelayedexpansion

if "%~1"=="" goto usage
if /i "%~1"=="esp32s3" goto esp32s3
if /i "%~1"=="esp32"   goto esp32
echo.
echo Unknown target: %~1
goto usage

:esp32s3
set TARGET=esp32s3
set DEFAULTS=sdkconfig.defaults;sdkconfig.defaults.esp32s3
if "%~2"=="" (set PORT=COM9) else (set PORT=%~2)
goto check_target

:esp32
set TARGET=esp32
set DEFAULTS=sdkconfig.defaults;sdkconfig.defaults.esp32
if "%~2"=="" (set PORT=COM8) else (set PORT=%~2)
goto check_target

:check_target
rem If sdkconfig exists from a different target, clean before reconfiguring.
if not exist sdkconfig goto build
findstr /c:"CONFIG_IDF_TARGET=\"%TARGET%\"" sdkconfig >nul 2>&1
if errorlevel 1 (
    echo.
    echo Target changed to %TARGET% -- cleaning build directory...
    idf.py fullclean
    if exist sdkconfig del sdkconfig
    echo.
)

:build
echo ============================================================
echo  Target : %TARGET%
echo  Port   : %PORT%
echo ============================================================
echo.
idf.py -DIDF_TARGET=%TARGET% "-DSDKCONFIG_DEFAULTS=%DEFAULTS%" -p %PORT% build flash monitor
goto end

:usage
echo.
echo Usage: build.cmd ^<target^> [port]
echo.
echo   target   esp32    -- ESP32 DevKit
echo                        UART1: TX=GPIO12  RX=GPIO4   RTS=GPIO13  CTS=GPIO15
echo                        LED  : GPIO14 (single-colour blue, plain GPIO)
echo                        COM  : COM8 (default)
echo.
echo            esp32s3  -- ESP32-S3 DevKit (S3-N16R8)
echo                        UART1: TX=GPIO17  RX=GPIO8   RTS=GPIO21  CTS=GPIO47
echo                        LED  : GPIO48 (WS2812 RGB)
echo                        COM  : COM9 (default)
echo.
echo   port     COM port to flash and monitor (overrides default for the target)
echo.
echo Examples:
echo   build.cmd esp32s3
echo   build.cmd esp32s3 COM5
echo   build.cmd esp32
echo   build.cmd esp32 COM3
echo.
echo Prerequisite: ESP-IDF environment must be active in this shell.
echo   Run once per shell session:
echo   C:\esp\.espressif\v6.0\esp-idf\export.bat
echo.
echo Switching targets: build.cmd automatically runs 'idf.py fullclean' when
echo   the target changes so stale CMake cache and sdkconfig are removed.
echo   A full rebuild is triggered as a result.
echo.

:end
endlocal
