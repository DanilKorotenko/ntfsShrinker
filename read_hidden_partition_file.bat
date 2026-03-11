@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem Usage:
rem   read_hidden_partition_file.bat <file_name> [partition_label]
rem Example:
rem   read_hidden_partition_file.bat secret.txt HiddenPartition

if "%~1"=="" goto :usage

set "FILE_NAME=%~1"
set "PARTITION_LABEL=%~2"
if "%PARTITION_LABEL%"=="" set "PARTITION_LABEL=HiddenPartition"

rem Require Administrator rights (disk operations need elevation)
net session >nul 2>&1
if not "%errorlevel%"=="0" (
    echo [ERROR] Run this script as Administrator.
    exit /b 1
)

set "DP_LIST=%TEMP%\diskpart_list_%RANDOM%%RANDOM%.txt"
set "DP_LIST_OUT=%TEMP%\diskpart_list_out_%RANDOM%%RANDOM%.txt"
set "DP_ASSIGN=%TEMP%\diskpart_assign_%RANDOM%%RANDOM%.txt"
set "DP_REMOVE=%TEMP%\diskpart_remove_%RANDOM%%RANDOM%.txt"

(
    echo list volume
) > "%DP_LIST%"

diskpart /s "%DP_LIST%" > "%DP_LIST_OUT%" 2>&1
if not "%errorlevel%"=="0" (
    echo [ERROR] Failed to enumerate volumes with DiskPart.
    call :cleanup
    exit /b 1
)

set "MATCH_LINE="
for /f "usebackq delims=" %%L in (`findstr /I /C:"Volume" "%DP_LIST_OUT%" ^| findstr /I /C:"%PARTITION_LABEL%" ^| findstr /I /C:"NTFS"`) do (
    if not defined MATCH_LINE set "MATCH_LINE=%%L"
)

if not defined MATCH_LINE (
    echo [ERROR] Could not find hidden NTFS partition with label "%PARTITION_LABEL%".
    call :cleanup
    exit /b 1
)

set "VOL_NUM="
set "COL3="
for /f "tokens=1,2,3" %%A in ("!MATCH_LINE!") do (
    set "VOL_NUM=%%B"
    set "COL3=%%C"
)

if not defined VOL_NUM (
    echo [ERROR] Could not parse target volume number from DiskPart output.
    call :cleanup
    exit /b 1
)

set "EXISTING_DRIVE="
if defined COL3 (
    if "!COL3:~1,1!"=="" (
        if /I not "!COL3!"=="%PARTITION_LABEL%" (
            set "EXISTING_DRIVE=!COL3!"
        )
    )
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
        call :cleanup
        exit /b 1
    )

    (
        echo select volume %VOL_NUM%
        echo assign letter=%TMP_LETTER%
    ) > "%DP_ASSIGN%"

    diskpart /s "%DP_ASSIGN%" >nul 2>&1
    if not "%errorlevel%"=="0" (
        echo [ERROR] Failed to assign drive letter %TMP_LETTER%: with DiskPart.
        call :cleanup
        exit /b 1
    )

    set "ACCESS_DRIVE=%TMP_LETTER%"
    set "MOUNTED_TEMP=1"
)

set "TARGET_FILE=%ACCESS_DRIVE%:\%FILE_NAME%"

if not exist "%TARGET_FILE%" (
    echo [ERROR] File "%FILE_NAME%" not found on hidden partition.
    if "%MOUNTED_TEMP%"=="1" (
        (
            echo select volume %VOL_NUM%
            echo remove letter=%ACCESS_DRIVE%
        ) > "%DP_REMOVE%"
        diskpart /s "%DP_REMOVE%" >nul 2>&1
    )
    call :cleanup
    exit /b 1
)

echo [OK] Reading "%TARGET_FILE%"
echo ----- FILE CONTENT START -----
type "%TARGET_FILE%"
echo.
echo ----- FILE CONTENT END -----

if "%MOUNTED_TEMP%"=="1" (
    (
        echo select volume %VOL_NUM%
        echo remove letter=%ACCESS_DRIVE%
    ) > "%DP_REMOVE%"
    diskpart /s "%DP_REMOVE%" >nul 2>&1
    if not "%errorlevel%"=="0" (
        echo [WARN] Could not remove temporary drive letter %ACCESS_DRIVE%:.
        echo        Remove manually in DiskPart:
        echo        select volume %VOL_NUM%
        echo        remove letter=%ACCESS_DRIVE%
        call :cleanup
        exit /b 2
    )
)

call :cleanup
exit /b 0

:cleanup
if exist "%DP_LIST%" del /q "%DP_LIST%" >nul 2>&1
if exist "%DP_LIST_OUT%" del /q "%DP_LIST_OUT%" >nul 2>&1
if exist "%DP_ASSIGN%" del /q "%DP_ASSIGN%" >nul 2>&1
if exist "%DP_REMOVE%" del /q "%DP_REMOVE%" >nul 2>&1
exit /b 0

:usage
echo Usage: %~nx0 ^<file_name^> [partition_label]
echo Example: %~nx0 secret.txt HiddenPartition
exit /b 1
