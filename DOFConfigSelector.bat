@echo off
setlocal enabledelayedexpansion

REM Resolve the repository root from the script location (%~dp0 always ends with a backslash).
set "scriptDir=%~dp0"

REM Build the templates root path expected by this selector utility.
set "templatesRoot=%scriptDir%DOF\Config\templates"

REM Fail fast with a clear message if the templates folder is not present.
if not exist "%templatesRoot%\" (
    echo Error: Templates directory not found.
    echo Expected path: "%templatesRoot%"
    exit /b 1
)

REM Parse optional advanced override: --dof-config "X:\DirectOutput\Config"
set "hasOverride="
set "overrideDestination="
if /i "%~1"=="--dof-config" (
    if "%~2"=="" (
        echo Error: --dof-config requires a destination path.
        echo Example: DOFConfigSelector.bat --dof-config "C:\DirectOutput\Config"
        exit /b 1
    )

    set "hasOverride=1"
    set "overrideDestination=%~2"
) else if not "%~1"=="" (
    echo Error: Unknown argument "%~1".
    echo Usage: DOFConfigSelector.bat [--dof-config "X:\DirectOutput\Config"]
    exit /b 1
)

echo.
echo ===== DOF Config Template Selector =====
echo.

call :selectTemplate
if errorlevel 1 exit /b 1

call :resolveDestination
if errorlevel 1 exit /b 1

echo.
echo Template selected: "%selectedTemplate%"
echo Destination selected: "%selectedDestination%"
echo.
echo Warning: Files in the destination DOF Config folder may be overwritten.

:confirmCopy
set "confirmCopyInput="
set /p "confirmCopyInput=Copy this template into the destination now? [Y/N]: "
if /i "!confirmCopyInput!"=="Y" goto :copyTemplate
if /i "!confirmCopyInput!"=="N" (
    echo Copy cancelled. No files were changed.
    exit /b 0
)
echo Please enter Y or N.
goto :confirmCopy

:copyTemplate
REM Copy template contents (not the parent folder) into destination using robocopy for reliable logging.
set "templateSource=%templatesRoot%\%selectedTemplate%"
REM Use trailing backslash directory checks (more reliable across junctions/symlinks than \NUL checks).
if not exist "%templateSource%\" (
    echo Error: Selected template folder was not found.
    echo Expected: "%templateSource%"
    exit /b 1
)

call :promptBackupChoice
if errorlevel 1 exit /b 1

set "copyLog=%TEMP%\DOFConfigSelector_%RANDOM%_%RANDOM%.log"
echo Copying files...
robocopy "%templateSource%" "%selectedDestination%" *.* /E /R:1 /W:1 /NP /TEE /LOG:"%copyLog%"
set "robocopyExit=%errorlevel%"

REM Robocopy success codes are 0-7; 8 and above indicate at least one failure.
if %robocopyExit% GEQ 8 (
    echo.
    echo Error: File copy failed. Robocopy exit code: %robocopyExit%.
    call :printRoboFailureReason %robocopyExit%
    echo Review the copy log for details: "%copyLog%"
    exit /b 1
)

echo.
call :printKeyFileSummary
echo Success: Template files were copied to "%selectedDestination%".
echo Next step: Launch VirtualDofMatrix.App.exe and continue the setup guide.
echo Reminder: If your DOF install is in a custom location, confirm GlobalConfig_B2SServer.xml still points to your active ^<InstallDir^>\Config folder.
if exist "%copyLog%" del /q "%copyLog%" >nul 2>&1
exit /b 0

:selectTemplate
set /a menuCount=0
echo Available templates:

REM Enumerate only child directories and map each numeric index to the directory name.
for /d %%D in ("%templatesRoot%\*") do (
    set /a menuCount+=1
    set "menuDir!menuCount!=%%~nxD"

    REM Convert folder names to user-facing labels (strip leading NN- and replace separators).
    set "rawLabel=%%~nxD"
    set "prefix="
    set "remainder="
    for /f "tokens=1,* delims=-" %%A in ("!rawLabel!") do (
        set "prefix=%%A"
        set "remainder=%%B"
    )

    REM Remove only a numeric prefix like 01- or 12-; keep full name otherwise.
    set "displayLabel=!rawLabel!"
    if defined remainder (
        set "allDigits=1"
        for /f "delims=0123456789" %%X in ("!prefix!") do set "allDigits="
        if defined allDigits set "displayLabel=!remainder!"
    )

    set "displayLabel=!displayLabel:-= !"
    set "displayLabel=!displayLabel:_= !"

    echo !menuCount!^) !displayLabel!
)

if !menuCount! EQU 0 (
    echo Error: No template folders found under "%templatesRoot%".
    exit /b 1
)

:promptTemplateSelection
set "selection="
set "selectionNonNumeric="
set /p "selection=Select a template number [1-!menuCount!]: "

REM Validate input is numeric and within range before accepting it.
for /f "delims=0123456789" %%X in ("!selection!") do set "selectionNonNumeric=1"
if not defined selection (
    echo Invalid selection. Enter a number from 1 to !menuCount!.
    goto :promptTemplateSelection
)
if defined selectionNonNumeric (
    echo Invalid selection. Enter a number from 1 to !menuCount!.
    goto :promptTemplateSelection
)
if !selection! LSS 1 (
    echo Invalid selection. Enter a number from 1 to !menuCount!.
    goto :promptTemplateSelection
)
if !selection! GTR !menuCount! (
    echo Invalid selection. Enter a number from 1 to !menuCount!.
    goto :promptTemplateSelection
)

set "selectedTemplate=!menuDir%selection%!"
exit /b 0

:resolveDestination
set "selectedDestination="
set /a matchCount=0
set /a checkedCount=0
set "checkedPaths="

if defined hasOverride (
    echo Using override destination from command line.
    call :normalizePath "%overrideDestination%" normalizedOverride

    if not exist "!normalizedOverride!\" (
        echo Error: The override path is not a valid directory.
        echo Provided: "%overrideDestination%"
        echo Hint: Pass the DOF Config folder, for example "C:\DirectOutput\Config".
        exit /b 1
    )

    set "selectedDestination=!normalizedOverride!"
    echo Selected destination: "!selectedDestination!"
    exit /b 0
)

echo Checking for your DOF Config folder...
call :trackChecked "C:\DirectOutput\Config"

REM Primary deterministic default location check.
if exist "C:\DirectOutput\Config\" (
    set "selectedDestination=C:\DirectOutput\Config"
    echo Found default DOF location: "!selectedDestination!"
    exit /b 0
)

echo Default path was not found. Running auto-detection fallback...
echo Tip: If you installed DOF somewhere else, that is okay. The script will search common locations next.

REM Scan order is deterministic: C, then D, then E when the drive exists.
for %%D in (C D E) do (
    if exist "%%D:\" (
        call :searchDrive "%%D:"
    )
)

echo.
echo Paths checked:
if !checkedCount! EQU 0 (
    echo - ^(none^)
) else (
    for /l %%I in (1,1,!checkedCount!) do echo - !checkedPath%%I!
)

echo.
if !matchCount! EQU 1 (
    set "selectedDestination=!matchPath1!"
    echo Found exactly one matching DOF Config folder.
    echo Selected destination: "!selectedDestination!"
    exit /b 0
)

if !matchCount! GTR 1 goto :promptMatchDestination

echo No DOF Config folders were detected automatically.
echo Troubleshooting:
echo - Confirm DOF is installed and that a Config folder exists.
echo - If Windows blocked this script, right-click it and choose Run anyway (or use the manual copy fallback in instructions).
call :promptManualDestination
exit /b %errorlevel%

:promptMatchDestination
echo Found multiple matching DOF Config folders:
for /l %%I in (1,1,!matchCount!) do echo %%I^) !matchPath%%I!

:promptMatchChoice
set "matchChoice="
set "matchChoiceNonNumeric="
set /p "matchChoice=Select destination number [1-!matchCount!] (or Q to cancel): "

if /i "!matchChoice!"=="Q" (
    echo Cancelled by user.
    exit /b 1
)

for /f "delims=0123456789" %%X in ("!matchChoice!") do set "matchChoiceNonNumeric=1"
if not defined matchChoice (
    echo Invalid selection. Enter a number from 1 to !matchCount!, or Q to cancel.
    goto :promptMatchChoice
)
if defined matchChoiceNonNumeric (
    echo Invalid selection. Enter a number from 1 to !matchCount!, or Q to cancel.
    goto :promptMatchChoice
)
if !matchChoice! LSS 1 (
    echo Invalid selection. Enter a number from 1 to !matchCount!, or Q to cancel.
    goto :promptMatchChoice
)
if !matchChoice! GTR !matchCount! (
    echo Invalid selection. Enter a number from 1 to !matchCount!, or Q to cancel.
    goto :promptMatchChoice
)

set "selectedDestination=!matchPath%matchChoice%!"
echo Selected destination: "!selectedDestination!"
exit /b 0

:searchDrive
set "scanDrive=%~1"

REM First do quick checks for common install layouts before slower recursive scanning.
for %%S in (
    "\DirectOutput\Config"
    "\Visual Pinball\DirectOutput\Config"
    "\VPinball\DirectOutput\Config"
    "\Pinball\DirectOutput\Config"
) do (
    call :trackChecked "%scanDrive%%%~S"
    if exist "%scanDrive%%%~S\" call :addMatch "%scanDrive%%%~S"
)

REM Then perform bounded recursive search for \DirectOutput\Config on this drive.
call :trackChecked "%scanDrive%\**\DirectOutput\Config (recursive)"
for /f "delims=" %%P in ('dir "%scanDrive%\DirectOutput\Config" /s /b /ad 2^>nul') do (
    call :addMatch "%%~fP"
)
exit /b 0

:addMatch
call :normalizePath "%~1" normalizedCandidate
if not defined normalizedCandidate exit /b 0

REM De-duplicate matches while preserving discovery order.
for /l %%I in (1,1,!matchCount!) do (
    if /i "!matchPath%%I!"=="!normalizedCandidate!" exit /b 0
)

set /a matchCount+=1
set "matchPath!matchCount!=!normalizedCandidate!"
exit /b 0

:trackChecked
call :normalizePath "%~1" normalizedChecked
if not defined normalizedChecked exit /b 0

REM Keep a de-duplicated list of checked paths so the user sees a concise summary.
for /l %%I in (1,1,!checkedCount!) do (
    if /i "!checkedPath%%I!"=="!normalizedChecked!" exit /b 0
)

set /a checkedCount+=1
set "checkedPath!checkedCount!=!normalizedChecked!"
exit /b 0

:promptManualDestination
echo Please type your DOF Config path manually.
echo Example: C:\DirectOutput\Config

:manualPromptLoop
set "manualDestination="
set /p "manualDestination=Destination path (or Q to cancel): "

if /i "!manualDestination!"=="Q" (
    echo Cancelled by user.
    exit /b 1
)

call :normalizePath "!manualDestination!" normalizedManual
if not exist "!normalizedManual!\" (
    echo That path is not a valid folder. Please try again.
    goto :manualPromptLoop
)

set "selectedDestination=!normalizedManual!"
echo Selected destination: "!selectedDestination!"
exit /b 0

:normalizePath
set "%~2=%~1"
if not defined %~2 exit /b 0

set "value=!%~2!"
if "!value:~-1!"=="\" (
    if not "!value:~1,1!"==":" set "value=!value:~0,-1!"
)
set "%~2=!value!"
exit /b 0

:promptBackupChoice
echo.
echo Optional safety backup:
echo Press B to create a backup of the current destination files before overwrite.
echo Press S to skip backup and continue.
echo Press Q to cancel.

:backupPromptLoop
set "backupChoice="
set /p "backupChoice=Your choice [B/S/Q]: "
if /i "!backupChoice!"=="Q" (
    echo Cancelled by user.
    exit /b 1
)
if /i "!backupChoice!"=="S" (
    echo Backup skipped.
    exit /b 0
)
if /i "!backupChoice!"=="B" goto :runBackup
echo Please enter B, S, or Q.
goto :backupPromptLoop

:runBackup
call :buildTimestamp backupStamp
set "backupFolder=%selectedDestination%\Config_backup_!backupStamp!"
echo Creating backup folder: "!backupFolder!"
mkdir "!backupFolder!" >nul 2>&1
if errorlevel 1 (
    echo Error: Could not create backup folder.
    echo Hint: Try running this script as Administrator.
    exit /b 1
)

set "backupLog=%TEMP%\DOFConfigSelector_backup_%RANDOM%_%RANDOM%.log"
robocopy "%selectedDestination%" "!backupFolder!" *.* /E /R:1 /W:1 /NP /TEE /LOG:"!backupLog!"
set "backupExit=!errorlevel!"
if !backupExit! GEQ 8 (
    echo Error: Backup failed. Robocopy exit code: !backupExit!.
    echo Hint: Check folder permissions, file locks, or run as Administrator.
    echo Backup log: "!backupLog!"
    exit /b 1
)

echo Backup complete: "!backupFolder!"
if exist "!backupLog!" del /q "!backupLog!" >nul 2>&1
exit /b 0

:buildTimestamp
REM Build a filesystem-safe timestamp for backup folder naming.
set "stamp="
for /f "skip=1 tokens=1 delims=." %%I in ('wmic os get localdatetime 2^>nul') do if not defined stamp set "stamp=%%I"
if defined stamp (
    set "stamp=!stamp:~0,8!_!stamp:~8,6!"
) else (
    set "stamp=%date:/=%_%time::=%"
    set "stamp=!stamp: =0!"
    set "stamp=!stamp:.=%"
)
set "%~1=!stamp!"
exit /b 0

:printKeyFileSummary
REM Report key configuration files so users can quickly verify expected outputs.
echo Key configuration files in destination:
for %%F in (
    "Cabinet.xml"
    "directoutputconfig30.ini"
    "tablemappings.xml"
    "DirectOutputShapes.xml"
    "DirectOutputShapes.png"
) do (
    if exist "%selectedDestination%\%%~F" (
        echo - %%~F [present]
    ) else (
        echo - %%~F [missing]
    )
)
exit /b 0

:printRoboFailureReason
REM Give a beginner-friendly failure hint based on common robocopy failure bits.
set "roboCode=%~1"
if %roboCode% GEQ 16 (
    echo Reason: Serious error. This is commonly caused by invalid paths or permissions.
    exit /b 0
)
if %roboCode% GEQ 8 (
    echo Reason: Some files or folders could not be copied.
    echo Hint: Check file locks, free space, and permissions.
    echo Hint: If needed, rerun this script as Administrator.
    exit /b 0
)
echo Reason: Unknown copy status.
exit /b 0
