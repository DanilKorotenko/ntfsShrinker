@echo off
setlocal EnableExtensions

rem Usage:
rem   read_hidden_partition_file.bat <file_name> [partition_label]
rem Example:
rem   read_hidden_partition_file.bat secret.txt HiddenPartition

if "%~1"=="" goto :usage

set "FILE_NAME=%~1"
set "PARTITION_LABEL=%~2"
if "%PARTITION_LABEL%"=="" set "PARTITION_LABEL=HiddenPartition"

rem Require Administrator rights (mountvol/disk operations need elevation)
net session >nul 2>&1
if not "%errorlevel%"=="0" (
    echo [ERROR] Run this script as Administrator.
    exit /b 1
)

set "VOLUME_GUID="
for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command "$v = Get-CimInstance Win32_Volume | Where-Object { $_.Label -eq '%PARTITION_LABEL%' -and $_.FileSystem -eq 'NTFS' } | Select-Object -First 1; if ($v) { $v.DeviceID }"`) do (
    set "VOLUME_GUID=%%V"
)

if not defined VOLUME_GUID (
    echo [ERROR] Could not find hidden NTFS partition with label "%PARTITION_LABEL%".
    exit /b 1
)

set "EXISTING_DRIVE="
for /f "usebackq delims=" %%D in (`powershell -NoProfile -Command "$v = Get-CimInstance Win32_Volume | Where-Object { $_.Label -eq '%PARTITION_LABEL%' -and $_.FileSystem -eq 'NTFS' } | Select-Object -First 1; if ($v -and $v.DriveLetter) { $v.DriveLetter.TrimEnd(':') }"`) do (
    set "EXISTING_DRIVE=%%D"
)

set "ACCESS_DRIVE="
set "MOUNTED_TEMP=0"

if defined EXISTING_DRIVE (
    set "ACCESS_DRIVE=%EXISTING_DRIVE%"
) else (
    set "TMP_LETTER="
    for %%L in (R S T U V W X Y Z Q P O N M L K J I H G F E D C B A) do (
        if not defined TMP_LETTER (
            if not exist "%%L:\" (
                set "TMP_LETTER=%%L"
            )
        )
    )
    if not defined TMP_LETTER (
        echo [ERROR] Could not find a free drive letter for temporary mount.
        exit /b 1
    )

    mountvol %TMP_LETTER%: %VOLUME_GUID% >nul 2>&1
    if not "%errorlevel%"=="0" (
        echo [ERROR] Failed to mount hidden partition to %TMP_LETTER%:.
        exit /b 1
    )

    set "ACCESS_DRIVE=%TMP_LETTER%"
    set "MOUNTED_TEMP=1"
)

set "TARGET_FILE=%ACCESS_DRIVE%:\%FILE_NAME%"

if not exist "%TARGET_FILE%" (
    echo [ERROR] File "%FILE_NAME%" not found on hidden partition.
    if "%MOUNTED_TEMP%"=="1" mountvol %ACCESS_DRIVE%: /D >nul 2>&1
    exit /b 1
)

echo [OK] Reading "%TARGET_FILE%"
echo ----- FILE CONTENT START -----
type "%TARGET_FILE%"
echo.
echo ----- FILE CONTENT END -----

if "%MOUNTED_TEMP%"=="1" (
    mountvol %ACCESS_DRIVE%: /D >nul 2>&1
    if not "%errorlevel%"=="0" (
        echo [WARN] Could not unmount %ACCESS_DRIVE%:. Remove it manually with:
        echo        mountvol %ACCESS_DRIVE%: /D
        exit /b 2
    )
)

exit /b 0

:usage
echo Usage: %~nx0 ^<file_name^> [partition_label]
echo Example: %~nx0 secret.txt HiddenPartition
exit /b 1
