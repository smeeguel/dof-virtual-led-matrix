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

:buildMenu
set /a menuCount=0

REM Enumerate only child directories and map each numeric index to the directory name.
for /d %%D in ("%templatesRoot%\*") do (
    set /a menuCount+=1
    set "menuDir!menuCount!=%%~nxD"

    REM Convert folder names to user-facing labels (strip leading NN- and replace separators).
    set "rawLabel=%%~nxD"
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

:promptSelection
set "selection="
set /p "selection=Select a template number [1-!menuCount!]: "

REM Validate input is numeric and within range before accepting it.
for /f "delims=0123456789" %%X in ("!selection!") do set "selectionNonNumeric=1"
if not defined selection (
    echo Invalid selection. Enter a number from 1 to !menuCount!.
    set "selectionNonNumeric="
    goto :promptSelection
)
if defined selectionNonNumeric (
    echo Invalid selection. Enter a number from 1 to !menuCount!.
    set "selectionNonNumeric="
    goto :promptSelection
)
if !selection! LSS 1 (
    echo Invalid selection. Enter a number from 1 to !menuCount!.
    goto :promptSelection
)
if !selection! GTR !menuCount! (
    echo Invalid selection. Enter a number from 1 to !menuCount!.
    goto :promptSelection
)

set "selectedFolder=!menuDir%selection%!"
echo Selected template: !selectedFolder!

exit /b 0
