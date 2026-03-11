@echo off
setlocal EnableExtensions

rem Usage:
rem   create_hidden_partition.bat C [file_name] ["file content"]
rem Example:
rem   create_hidden_partition.bat C secret.txt "Hello from hidden partition"

if "%~1"=="" goto :usage

rem Require Administrator rights (diskpart + fsutil need elevation)
net session >nul 2>&1
if not "%errorlevel%"=="0" (
    echo [ERROR] Run this script as Administrator.
    exit /b 1
)

set "TARGET_VOL=%~1"
set "TARGET_VOL=%TARGET_VOL::=%"
if "%TARGET_VOL%"=="" goto :usage

rem Normalize to first character only (drive letter)
set "TARGET_VOL=%TARGET_VOL:~0,1%"

set "FILE_NAME=%~2"
if "%FILE_NAME%"=="" set "FILE_NAME=hidden_note.txt"

set "FILE_CONTENT=%~3"
if "%FILE_CONTENT%"=="" set "FILE_CONTENT=Created on %DATE% %TIME%"

rem Validate source volume is NTFS
set "FS_LINE="
for /f "tokens=1,* delims=:" %%A in ('fsutil fsinfo volumeinfo %TARGET_VOL%: 2^>nul ^| findstr /i "File System Name"') do (
    set "FS_LINE=%%B"
)

if not defined FS_LINE (
    echo [ERROR] Could not read filesystem info for %TARGET_VOL%:.
    echo         Make sure the drive letter exists.
    exit /b 1
)

echo %FS_LINE% | findstr /i "NTFS" >nul
if not "%errorlevel%"=="0" (
    echo [ERROR] Volume %TARGET_VOL%: is not NTFS.
    exit /b 1
)

rem Pick a temporary free drive letter for the new partition
set "TMP_LETTER="
for %%L in (R S T U V W X Y Z Q P O N M L K J I H G F E D) do (
    if /i not "%%L"=="%TARGET_VOL%" (
        if not exist "%%L:\" (
            set "TMP_LETTER=%%L"
            goto :got_letter
        )
    )
)

:got_letter
if not defined TMP_LETTER (
    echo [ERROR] Could not find a free temporary drive letter.
    exit /b 1
)

set "DP_CREATE=%TEMP%\diskpart_create_%RANDOM%%RANDOM%.txt"
set "DP_HIDE=%TEMP%\diskpart_hide_%RANDOM%%RANDOM%.txt"
set "DP_REMOVE_ONLY=%TEMP%\diskpart_remove_%RANDOM%%RANDOM%.txt"

(
    echo select volume %TARGET_VOL%
    echo shrink desired=10 minimum=10
    echo create partition primary size=10
    echo format fs=ntfs quick label=HiddenPartition
    echo assign letter=%TMP_LETTER%
) > "%DP_CREATE%"

echo [INFO] Shrinking %TARGET_VOL%: by 10 MB and creating partition...
diskpart /s "%DP_CREATE%"
if not "%errorlevel%"=="0" (
    echo [ERROR] DiskPart create step failed.
    call :cleanup
    exit /b 1
)

if not exist "%TMP_LETTER%:\" (
    echo [ERROR] New partition was not mounted as %TMP_LETTER%:.
    call :cleanup
    exit /b 1
)

echo [INFO] Writing file "%FILE_NAME%" to %TMP_LETTER%:\ ...
> "%TMP_LETTER%:\%FILE_NAME%" (
    echo %FILE_CONTENT%
)

(
    echo select volume %TMP_LETTER%
    echo gpt attributes=0x8000000000000001
    echo select volume %TMP_LETTER%
    echo set id=27 override
    echo select volume %TMP_LETTER%
    echo remove letter=%TMP_LETTER%
) > "%DP_HIDE%"

echo [INFO] Hiding the partition...
diskpart /s "%DP_HIDE%" >nul 2>&1

if exist "%TMP_LETTER%:\" (
    rem If one of hide commands failed, ensure at least drive letter removal
    (
        echo select volume %TMP_LETTER%
        echo remove letter=%TMP_LETTER%
    ) > "%DP_REMOVE_ONLY%"
    diskpart /s "%DP_REMOVE_ONLY%" >nul 2>&1
)

if exist "%TMP_LETTER%:\" (
    echo [WARN] Partition created and file written, but drive letter is still visible.
    echo        You may need to remove letter manually in DiskPart.
) else (
    echo [OK] Hidden partition created and file written successfully.
)

call :cleanup
exit /b 0

:cleanup
if exist "%DP_CREATE%" del /q "%DP_CREATE%" >nul 2>&1
if exist "%DP_HIDE%" del /q "%DP_HIDE%" >nul 2>&1
if exist "%DP_REMOVE_ONLY%" del /q "%DP_REMOVE_ONLY%" >nul 2>&1
exit /b 0

:usage
echo Usage: %~nx0 ^<NTFS_VOLUME_LETTER^> [file_name] ["file content"]
echo Example: %~nx0 C secret.txt "Hello from hidden partition"
exit /b 1
