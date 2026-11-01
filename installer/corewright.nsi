; NSIS 3 Unicode, per-machine x64 setup. Build through scripts/package.ps1.
; /DVERSION /DVI_VERSION /DSRC /DOUTFILE; SRC contains the complete publish folder.
!macro REQUIRE name
  !ifndef ${name}
    !error "Pass /D${name}=..."
  !endif
!macroend
!insertmacro REQUIRE VERSION
!insertmacro REQUIRE VI_VERSION
!insertmacro REQUIRE SRC
!insertmacro REQUIRE OUTFILE

Unicode true
ManifestDPIAware true
RequestExecutionLevel admin
SetCompressor /SOLID lzma
!include MUI2.nsh
!include LogicLib.nsh
!include x64.nsh
!include FileFunc.nsh
!include nsDialogs.nsh
!include TextFunc.nsh
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Corewright"
!define RUN_KEY "Software\Microsoft\Windows\CurrentVersion\Run"

Name "Corewright"
OutFile "${OUTFILE}"
InstallDir "$PROGRAMFILES64\Corewright"
BrandingText "Corewright ${VERSION}"
VIProductVersion "${VI_VERSION}"
VIAddVersionKey "ProductName" "Corewright"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "FileDescription" "Corewright ${VERSION} setup"
VIAddVersionKey "LegalCopyright" "rennerdo30"
!define MUI_ICON "..\assets\corewright.ico"
!define MUI_UNICON "..\assets\corewright.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_FUNCTION StartTray
!define MUI_FINISHPAGE_RUN_TEXT "Start Corewright"
!insertmacro MUI_PAGE_WELCOME
Page custom OptionsPage OptionsLeave
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
UninstPage custom un.OptionsPage un.OptionsLeave
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Var Autostart
Var Checkbox
Var RemoveData
Var DataDir
Var UserDataDir
Var ServiceState
Var ScCommand
Var ScResult
Var TargetProcess
Var ProcessFound
Var Countdown
Var LogPath
Var IsUpgrade
Var WasTrayRunning
Var SameUser
Var ServicePid
Var ServiceWasRunning
Var FilesReplaced
Var ManifestHandle
Var ManifestSize
Var ServiceTouched
Var DataPathUnsafe

!macro FAIL text
  DetailPrint "ERROR: ${text}"
  FileOpen $9 "$LogPath" a
  FileWrite $9 "ERROR: ${text}$\r$\n"
  FileClose $9
  MessageBox MB_OK|MB_ICONSTOP "${text}$\r$\nSee $LogPath." /SD IDOK
  SetErrorLevel 1
  Abort
!macroend

!macro COMMON_FUNCTIONS prefix
; No process is terminated. UI components receive their own graceful exit signals.
Function ${prefix}CloseApps
  System::Call 'user32::FindWindowW(w "CorewrightTray", p 0) p.r0'
  ${If} $0 != 0
    System::Call 'user32::PostMessageW(p r0, i 0x0010, p 0, p 0)'
  ${EndIf}
  System::Call 'kernel32::OpenEventW(i 2, i 0, w "Local\Corewright.Dashboard.Exit") p.r0'
  ${If} $0 != 0
    System::Call 'kernel32::SetEvent(p r0)'
    System::Call 'kernel32::CloseHandle(p r0)'
  ${EndIf}
FunctionEnd

; The installer stub is 32-bit even on x64: PROCESSENTRY32W is 556 bytes,
; with szExeFile at offset 36. Snapshot includes processes in other sessions.
Function ${prefix}FindProcess
  StrCpy $ProcessFound 0
  System::Call 'kernel32::CreateToolhelp32Snapshot(i 2, i 0) p.r0'
  ${If} $0 == -1
    !insertmacro FAIL "Cannot enumerate running applications."
  ${EndIf}
  System::Alloc 556
  Pop $1
  System::Call '*$1(i 556)'
  System::Call 'kernel32::Process32FirstW(p r0, p r1) i.r2'
  ${DoWhile} $2 != 0
    IntOp $3 $1 + 36
    System::Call '*$3(&w260 .r4)'
    ${If} $4 == $TargetProcess
      System::Call '*$1(i, i, i.r6)'
      ${If} $6 != $ServicePid
        StrCpy $ProcessFound 1
        ${ExitDo}
      ${EndIf}
    ${EndIf}
    System::Call 'kernel32::Process32NextW(p r0, p r1) i.r2'
  ${Loop}
  System::Free $1
  System::Call 'kernel32::CloseHandle(p r0)'
FunctionEnd

; Query SCM directly: numeric state is independent of Windows display language.
; 0 = absent, 1 = stopped, 4 = running. Fail closed on access/query errors.
Function ${prefix}QueryService
  StrCpy $ServiceState 0
  StrCpy $ServicePid 0
  System::Call 'advapi32::OpenSCManagerW(p 0, p 0, i 1) p.r0'
  ${If} $0 == 0
    !insertmacro FAIL "Cannot open the Windows Service Control Manager."
  ${EndIf}
  System::Call 'advapi32::OpenServiceW(p r0, w "Corewright", i 4) p.r1 ?e'
  Pop $2
  ${If} $1 == 0
    System::Call 'advapi32::CloseServiceHandle(p r0)'
    ${If} $2 == 1072
      StrCpy $ServiceState -1 ; marked for deletion, wait for all handles to close
      Return
    ${EndIf}
    ${If} $2 != 1060
      !insertmacro FAIL "Cannot query the Corewright service (Windows error $2)."
    ${EndIf}
    Return
  ${EndIf}
  System::Alloc 36
  Pop $2
  System::Call 'advapi32::QueryServiceStatusEx(p r1, i 0, p r2, i 36, *i) i.r3'
  System::Call '*$2(i, i .r4, i, i, i, i, i, i .r5)'
  StrCpy $ServiceState $4
  StrCpy $ServicePid $5
  System::Free $2
  System::Call 'advapi32::CloseServiceHandle(p r1)'
  System::Call 'advapi32::CloseServiceHandle(p r0)'
  ${If} $3 == 0
    !insertmacro FAIL "Cannot read the Corewright service state."
  ${EndIf}
FunctionEnd

; nsExec's timeout bounds each sc.exe call. Output goes to the details pane;
; command and exit code are also appended to a durable diagnostic log.
Function ${prefix}Sc
  DetailPrint "sc.exe $ScCommand"
  nsExec::ExecToLog /TIMEOUT=10000 '"$SYSDIR\sc.exe" $ScCommand'
  Pop $ScResult
  FileOpen $9 "$LogPath" a
  FileWrite $9 "sc.exe $ScCommand => $ScResult$\r$\n"
  FileClose $9
FunctionEnd

Function ${prefix}StopService
  Call ${prefix}QueryService
  ${If} $ServiceState == 0
  ${OrIf} $ServiceState == 1
    Return
  ${EndIf}
  ${If} $ServiceState != 3 ; SERVICE_STOP_PENDING already has a stop in flight
  ${AndIf} $ServiceState != -1
    StrCpy $ServiceTouched 1
    StrCpy $ScCommand 'stop Corewright'
    Call ${prefix}Sc
    ${If} $ScResult != 0
    ${AndIf} $ScResult != 1062
      !insertmacro FAIL "Could not stop Corewright (sc.exe: $ScResult). No files were removed."
    ${EndIf}
  ${EndIf}
  StrCpy $Countdown 60
  ${Do}
    Call ${prefix}QueryService
    ${If} $ServiceState == 0
    ${OrIf} $ServiceState == 1
      Return
    ${EndIf}
    Sleep 500
    IntOp $Countdown $Countdown - 1
  ${LoopWhile} $Countdown > 0
  !insertmacro FAIL "Corewright did not stop within 30 seconds. No files were removed."
FunctionEnd

Function ${prefix}PrepareFiles
  ; Keep the tray available until the user has exited a console-mode service.
  Call ${prefix}QueryService
  StrCpy $ServiceWasRunning 0
  ${If} $ServiceState == 4
    StrCpy $ServiceWasRunning 1
  ${EndIf}
  retry_console:
  StrCpy $TargetProcess "Corewright.Service.exe"
  Call ${prefix}FindProcess
  ${If} $ProcessFound == 1
    IfSilent console_abort
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "A console-mode Corewright service is still running. Use the tray's Exit Corewright command (in every signed-in session), then choose Retry." /SD IDCANCEL IDRETRY retry_console
    console_abort:
    !insertmacro FAIL "A Corewright console service is still running. Exit it before continuing."
  ${EndIf}
  Call ${prefix}CloseApps
  StrCpy $Countdown 30
  ${Do}
    StrCpy $TargetProcess "Corewright.Tray.exe"
    Call ${prefix}FindProcess
    StrCpy $5 $ProcessFound
    StrCpy $TargetProcess "Corewright.Dashboard.exe"
    Call ${prefix}FindProcess
    ${If} $5 == 0
    ${AndIf} $ProcessFound == 0
      Call ${prefix}CheckFileLocks
      Return
    ${EndIf}
    Sleep 500
    IntOp $Countdown $Countdown - 1
  ${LoopWhile} $Countdown > 0
  MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "Corewright is still running in this or another signed-in session. Exit its console service, tray and dashboard there, then Retry. The installed service has not been stopped." /SD IDCANCEL IDRETRY retry_console
  !insertmacro FAIL "Corewright tray or dashboard is still running. The installed service was left running."
FunctionEnd

Function ${prefix}CheckFileLocks
  retry_locks:
  !include "${SRC}\..\preflight-files.nsh"
  Return
  locked:
  MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "Corewright files are in use. Close the dashboard in every session, then Retry. The installed service has not been stopped." /SD IDCANCEL IDRETRY retry_locks
  !insertmacro FAIL "Corewright files are still in use."
FunctionEnd

Function ${prefix}RestoreService
  ${If} $ServiceWasRunning == 1
  ${AndIf} $ServiceTouched == 1
  ${AndIf} $FilesReplaced != 1
  ${AndIf} $DataPathUnsafe != 1
    StrCpy $Countdown 60
    ${Do}
      Call ${prefix}QueryService
      ${If} $ServiceState == 4
        Return
      ${EndIf}
      ${If} $ServiceState == 1
        ${ExitDo}
      ${EndIf}
      Sleep 500
      IntOp $Countdown $Countdown - 1
    ${LoopWhile} $Countdown > 0
    StrCpy $ScCommand 'start Corewright'
    Call ${prefix}Sc
    ${If} $ScResult != 0
    ${AndIf} $ScResult != 1056
      DetailPrint "ERROR: Could not restore the previously running service ($ScResult)."
    ${EndIf}
    StrCpy $Countdown 60
    ${Do}
      Call ${prefix}QueryService
      ${If} $ServiceState == 4
        Return
      ${EndIf}
      Sleep 500
      IntOp $Countdown $Countdown - 1
    ${LoopWhile} $Countdown > 0
    DetailPrint "ERROR: Restored service did not reach Running within 30 seconds."
  ${EndIf}
FunctionEnd
!macroend
!insertmacro COMMON_FUNCTIONS ""
!insertmacro COMMON_FUNCTIONS "un."

!macro INIT
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "Corewright requires x64 Windows." /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  ${If} ${IsNativeARM64}
    MessageBox MB_ICONSTOP "This Corewright installer requires an x64 PC." /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  SetRegView 64
  SetShellVarContext current
  StrCpy $UserDataDir "$LOCALAPPDATA\Corewright"
  SetShellVarContext all
  StrCpy $DataDir "$APPDATA\Corewright"
  ; File operations are restricted to the product's fixed installation folder.
  StrCpy $INSTDIR "$PROGRAMFILES64\Corewright"
  StrCpy $LogPath "$TEMP\Corewright-installer-${VERSION}.log"
  StrCpy $FilesReplaced 0
  StrCpy $ServiceWasRunning 0
  StrCpy $ServiceTouched 0
  StrCpy $DataPathUnsafe 0
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=machine-state.ps1 "machine-state.ps1"
  File /oname=secure-files.cs "secure-files.cs"
  SetOutPath "$TEMP"
  nsExec::ExecToLog /TIMEOUT=30000 '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\machine-state.ps1" -Action SameUser'
  Pop $SameUser
  ${If} $SameUser != 0
    FileOpen $9 "$LogPath" a
    FileWrite $9 "Per-user work skipped: elevated identity does not match the interactive session user (or could not be verified).$\r$\n"
    FileClose $9
  ${EndIf}
!macroend

Function .onInit
  !insertmacro INIT
  StrCpy $Autostart ${BST_CHECKED}
  StrCpy $IsUpgrade 0
  ReadRegStr $0 HKLM "${UNINSTALL_KEY}" "InstallLocation"
  ${IfNot} ${Errors}
    StrCpy $IsUpgrade 1
    StrCpy $Autostart ${BST_UNCHECKED}
    ${If} $SameUser == 0
      ReadRegStr $0 HKCU "${RUN_KEY}" "Corewright"
      ${IfNot} ${Errors}
        StrCpy $Autostart ${BST_CHECKED}
      ${EndIf}
    ${EndIf}
  ${EndIf}
  ClearErrors
  System::Call 'user32::FindWindowW(w "CorewrightTray", p 0) p.r0'
  StrCpy $WasTrayRunning $0
FunctionEnd
Function .onInstSuccess
  ${If} $IsUpgrade == 1
  ${AndIf} $WasTrayRunning != 0
    IfSilent 0 finished
    Call StartTray
  ${EndIf}
  finished:
FunctionEnd
Function un.onInit
  !insertmacro INIT
  StrCpy $RemoveData ${BST_UNCHECKED}
FunctionEnd
Function .onInstFailed
  Call RestoreService
FunctionEnd
Function un.onUninstFailed
  Call un.RestoreService
FunctionEnd

Function OptionsPage
  !insertmacro MUI_HEADER_TEXT "Corewright options" "The service runs automatically for all users."
  nsDialogs::Create 1018
  Pop $0
  ${NSD_CreateLabel} 0 0 100% 36u "Installs to $INSTDIR.$\r$\nThe dashboard requires .NET 10 Desktop Runtime (x64) and Microsoft Edge WebView2 Runtime."
  Pop $0
  ${NSD_CreateCheckbox} 0 48u 100% 14u "Start Corewright when I sign in"
  Pop $Checkbox
  ${NSD_SetState} $Checkbox $Autostart
  ${If} $IsUpgrade == 1
    EnableWindow $Checkbox 0 ; existing users' choices remain authoritative in Settings
  ${EndIf}
  nsDialogs::Show
FunctionEnd
Function OptionsLeave
  ${NSD_GetState} $Checkbox $Autostart
FunctionEnd
Function un.OptionsPage
  !insertmacro MUI_HEADER_TEXT "Rules and settings" "Your rules and settings are kept unless you choose to remove them."
  nsDialogs::Create 1018
  Pop $0
  ${NSD_CreateCheckbox} 0 12u 100% 14u "Also remove my rules and settings"
  Pop $Checkbox
  ${NSD_SetState} $Checkbox $RemoveData
  nsDialogs::Show
FunctionEnd
Function un.OptionsLeave
  ${NSD_GetState} $Checkbox $RemoveData
FunctionEnd

; Install appends each version's exact payload. Retaining older entries means
; uninstall also removes assets that disappeared during a later upgrade.
Function un.ValidateManifest
  ClearErrors
  FileOpen $ManifestHandle "$INSTDIR\install-manifest.txt" r
  ${If} ${Errors}
    !insertmacro FAIL "The installed file manifest cannot be read. No files were removed."
  ${EndIf}
  FileSeek $ManifestHandle 0 END $ManifestSize
  FileSeek $ManifestHandle 0 SET
  ${If} $ManifestSize == 0
    !insertmacro FAIL "The installed file manifest is empty. No files were removed."
  ${EndIf}
  ${Do}
    ClearErrors
    FileRead $ManifestHandle $0
    ${If} ${Errors}
      FileSeek $ManifestHandle 0 CUR $1
      ${If} $1 != $ManifestSize
        !insertmacro FAIL "The installed file manifest could not be read completely."
      ${EndIf}
      ${ExitDo}
    ${EndIf}
    ${TrimNewLines} $0 $0
    StrCpy $4 $0 2
    StrCpy $0 $0 "" 2
    ${If} $4 != "F|"
    ${AndIf} $4 != "D|"
      !insertmacro FAIL "The installed file manifest has an invalid entry."
    ${EndIf}
    System::Call 'shlwapi::PathIsRelativeW(w r0) i.r2'
    ${If} $2 == 0
      !insertmacro FAIL "The installed file manifest has an absolute path."
    ${EndIf}
    System::Call 'shlwapi::StrPBrkW(w r0, w "*?:$\"<>|") p.r2'
    ${If} $2 != 0
      !insertmacro FAIL "The installed file manifest contains invalid path characters."
    ${EndIf}
    GetFullPathName $1 "$INSTDIR\$0"
    StrCpy $2 "$INSTDIR\"
    StrLen $3 $2
    StrCpy $3 $1 $3
    ${If} $3 != $2
      !insertmacro FAIL "The installed file manifest contains an unsafe path."
    ${EndIf}
  ${Loop}
  FileSeek $ManifestHandle 0 SET
  ClearErrors
FunctionEnd

Function un.RemovePayload
  ${Do}
    ClearErrors
    FileRead $ManifestHandle $0
    ${If} ${Errors}
      ${ExitDo}
    ${EndIf}
    ${TrimNewLines} $0 $0
    StrCpy $4 $0 2
    StrCpy $0 $0 "" 2
    GetFullPathName $1 "$INSTDIR\$0"
    ${If} $4 == "F|"
      ${IfNot} ${FileExists} "$1"
        ${Continue} ; a previous upgrade may list the same file more than once
      ${EndIf}
      ClearErrors
      Delete "$1"
      ${If} ${Errors}
        !insertmacro FAIL "Could not remove $1. Close Corewright and retry uninstall."
      ${EndIf}
    ${ElseIf} $4 == "D|"
      RMDir "$1" ; unknown user files keep their directories
    ${Else}
      !insertmacro FAIL "The installed file manifest is invalid."
    ${EndIf}
  ${Loop}
  FileClose $ManifestHandle
  ClearErrors
  Delete "$INSTDIR\install-manifest.txt"
  ${If} ${Errors}
    !insertmacro FAIL "Could not remove the installed file manifest."
  ${EndIf}
FunctionEnd

; Explorer delegates to the existing desktop shell, so the tray inherits the
; interactive user's unelevated token instead of the installer's admin token.
Function StartTray
  Exec '"$WINDIR\explorer.exe" "$INSTDIR\Corewright.Tray.exe"'
FunctionEnd

Section "Corewright"
  Call PrepareFiles
  ; Stop before snapshotting mutable data. Every machine object is recreated;
  ; rewriting ACLs cannot revoke WRITE_DAC handles held on the previous tree.
  Call StopService
  StrCpy $DataPathUnsafe 1 ; timeout/crash cannot certify the path for service restart
  nsExec::ExecToLog /TIMEOUT=30000 '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\machine-state.ps1" -Action SecureData -DataDirectory "$DataDir"'
  Pop $0
  ${If} $0 == 0
  ${OrIf} $0 == 1 ; helper certifies that the previous path is still intact
    StrCpy $DataPathUnsafe 0
  ${EndIf}
  ${If} $0 == 3
    StrCpy $DataPathUnsafe 1
    !insertmacro FAIL "Machine data swap was interrupted. The service stays stopped; recover the preserved data before retrying."
  ${EndIf}
  ${If} $0 != 0
    !insertmacro FAIL "Corewright data migration failed. Original data and any fresh copy were preserved."
  ${EndIf}
  ; The service binary cannot be probed while the service runs. If another
  ; handle still locks it now, the failure callback restores the original service.
  ${If} ${FileExists} "$INSTDIR\Corewright.Service.exe"
    ClearErrors
    FileOpen $7 "$INSTDIR\Corewright.Service.exe" a
    ${If} ${Errors}
      !insertmacro FAIL "The service executable is still locked. Restoring the running service."
    ${EndIf}
    FileClose $7
  ${EndIf}
  SetOutPath "$INSTDIR"
  StrCpy $FilesReplaced 1
  ClearErrors
  File /r "${SRC}\*.*"
  ${If} ${Errors}
    !insertmacro FAIL "Could not copy Corewright files."
  ${EndIf}
  ClearErrors
  WriteUninstaller "$INSTDIR\uninstall.exe"
  ${If} ${Errors}
    !insertmacro FAIL "Could not write the uninstaller."
  ${EndIf}
  FileOpen $8 "$INSTDIR\install-manifest.txt" a
  ${If} ${Errors}
    !insertmacro FAIL "Could not write the installed file manifest."
  ${EndIf}
  !include "${SRC}\..\install-manifest.nsh"
  ${If} ${Errors}
    FileClose $8
    !insertmacro FAIL "Could not save the installed file manifest."
  ${EndIf}
  FileClose $8
  ${If} $SameUser == 0
    ; Every source ancestor and file is opened without following reparse points.
    ; Copy bytes from that held handle, never reopen the validated source by path.
    nsExec::ExecToLog /TIMEOUT=30000 '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\machine-state.ps1" -Action MigrateUser -DataDirectory "$DataDir" -SourceDirectory "$UserDataDir"'
    Pop $0
    ${If} $0 != 0
      !insertmacro FAIL "Could not safely migrate your console data. Original data was kept."
    ${EndIf}
  ${EndIf}

  Call QueryService
  ${If} $ServiceState == 0
    StrCpy $ScCommand 'create Corewright binPath= "\$\"$INSTDIR\Corewright.Service.exe\$\"" DisplayName= "Corewright" obj= LocalSystem start= auto'
  ${Else}
    StrCpy $ScCommand 'config Corewright binPath= "\$\"$INSTDIR\Corewright.Service.exe\$\"" DisplayName= "Corewright" obj= LocalSystem start= auto'
  ${EndIf}
  Call Sc
  ${If} $ScResult != 0
    !insertmacro FAIL "Could not register Corewright (sc.exe: $ScResult)."
  ${EndIf}
  StrCpy $ScCommand 'description Corewright "Corewright process rule engine. Stopping the service reverts its process changes."'
  Call Sc
  ${If} $ScResult != 0
    !insertmacro FAIL "Could not set the service description (sc.exe: $ScResult)."
  ${EndIf}
  StrCpy $ScCommand 'failure Corewright reset= 86400 actions= restart/5000/restart/15000/restart/60000'
  Call Sc
  ${If} $ScResult != 0
    !insertmacro FAIL "Could not set service recovery (sc.exe: $ScResult)."
  ${EndIf}
  ClearErrors
  CreateShortcut "$SMPROGRAMS\Corewright.lnk" "$INSTDIR\Corewright.Dashboard.exe"
  ${If} ${Errors}
    !insertmacro FAIL "Could not create the Corewright Start menu shortcut."
  ${EndIf}
  ; Never write HKCU from elevation. The user's tray applies a first-run default.
  ${IfNot} ${FileExists} "$DataDir\defaults.json"
    FileOpen $7 "$DataDir\defaults.json" w
    ${If} $Autostart == ${BST_CHECKED}
    ${AndIf} $IsUpgrade == 0
      FileWrite $7 '{$\"autostart$\":true}'
    ${Else}
      FileWrite $7 '{$\"autostart$\":false}'
    ${EndIf}
    FileClose $7
    ${If} ${Errors}
      !insertmacro FAIL "Could not save the first-run sign-in default."
    ${EndIf}
  ${EndIf}
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayName" "Corewright"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "Publisher" "rennerdo30"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\corewright.ico"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr HKLM "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoRepair" 1
  ${If} ${Errors}
    !insertmacro FAIL "Could not write Corewright registration."
  ${EndIf}
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "EstimatedSize" $0
  ${If} ${Errors}
    !insertmacro FAIL "Could not record the Corewright installation size."
  ${EndIf}
  StrCpy $ScCommand 'start Corewright'
  Call Sc
  ${If} $ScResult != 0
  ${AndIf} $ScResult != 1056
    !insertmacro FAIL "Could not start Corewright (sc.exe: $ScResult)."
  ${EndIf}
  StrCpy $Countdown 60
  ${Do}
    Call QueryService
    ${If} $ServiceState == 4
      Return
    ${EndIf}
    Sleep 500
    IntOp $Countdown $Countdown - 1
  ${LoopWhile} $Countdown > 0
  !insertmacro FAIL "Corewright did not start within 30 seconds."
SectionEnd

Section "Uninstall"
  Call un.ValidateManifest
  Call un.PrepareFiles
  Call un.StopService
  Call un.QueryService
  ${If} $ServiceState != 0
    StrCpy $ScCommand 'delete Corewright'
    Call un.Sc
    ${If} $ScResult != 0
    ${AndIf} $ScResult != 1060
      !insertmacro FAIL "Could not delete the Corewright service (sc.exe: $ScResult)."
    ${EndIf}
    StrCpy $FilesReplaced 1
    StrCpy $Countdown 60
    ${Do}
      Call un.QueryService
      ${If} $ServiceState == 0
        ${ExitDo}
      ${EndIf}
      Sleep 500
      IntOp $Countdown $Countdown - 1
    ${LoopWhile} $Countdown > 0
    ${If} $ServiceState != 0
      !insertmacro FAIL "Corewright service deletion is pending. Close Services and retry uninstall."
    ${EndIf}
  ${EndIf}
  StrCpy $FilesReplaced 1 ; deleted service cannot be restored by failure callback
  Call un.RemovePayload
  ClearErrors
  ${If} ${FileExists} "$INSTDIR\uninstall.exe"
    Delete "$INSTDIR\uninstall.exe"
    ${If} ${Errors}
      !insertmacro FAIL "Could not remove the uninstaller."
    ${EndIf}
  ${EndIf}
  RMDir "$INSTDIR"
  ClearErrors
  ${If} ${FileExists} "$SMPROGRAMS\Corewright.lnk"
    Delete "$SMPROGRAMS\Corewright.lnk"
    ${If} ${Errors}
      !insertmacro FAIL "Could not remove the Corewright shortcut."
    ${EndIf}
  ${EndIf}
  ${If} $SameUser == 0
    System::Call 'advapi32::RegOpenKeyExW(p 0x80000001, w "${RUN_KEY}", i 0, i 0x103, *p.r1) i.r0'
    ${If} $0 == 0
      System::Call 'advapi32::RegDeleteValueW(p r1, w "Corewright") i.r0'
      System::Call 'advapi32::RegCloseKey(p r1)'
    ${EndIf}
    ${If} $0 != 0
    ${AndIf} $0 != 2
      !insertmacro FAIL "Could not remove the Corewright sign-in entry (Windows error $0)."
    ${EndIf}
  ${EndIf}
  ClearErrors
  System::Call 'advapi32::RegDeleteKeyExW(p 0x80000002, w "${UNINSTALL_KEY}", i 0x100, i 0) i.r0'
  ${If} $0 != 0
  ${AndIf} $0 != 2
    !insertmacro FAIL "Could not remove Corewright from Installed apps (Windows error $0)."
  ${EndIf}
  ${If} $RemoveData == ${BST_CHECKED}
    ClearErrors
    ${If} ${FileExists} "$DataDir"
      RMDir /r "$DataDir"
    ${EndIf}
    ${If} ${Errors}
      !insertmacro FAIL "Could not remove the requested machine rules and settings."
    ${EndIf}
    ${If} $SameUser == 0
      ${If} ${FileExists} "$UserDataDir"
        RMDir /r "$UserDataDir"
      ${EndIf}
      ${If} ${Errors}
        !insertmacro FAIL "Could not remove the requested user rules and settings."
      ${EndIf}
      ClearErrors
      ReadRegDWORD $0 HKCU "Software\Corewright" "AutostartDecided"
      ${IfNot} ${Errors}
        DeleteRegValue HKCU "Software\Corewright" "AutostartDecided"
        ${If} ${Errors}
          !insertmacro FAIL "Could not remove the requested user sign-in preference."
        ${EndIf}
      ${EndIf}
    ${EndIf}
  ${EndIf}
  SetErrorLevel 0
SectionEnd
