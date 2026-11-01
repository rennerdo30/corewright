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
      StrCpy $ProcessFound 1
      ${ExitDo}
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
  System::Alloc 28
  Pop $2
  System::Call 'advapi32::QueryServiceStatus(p r1, p r2) i.r3'
  System::Call '*$2(i, i .r4)'
  StrCpy $ServiceState $4
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
  Call ${prefix}StopService
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
      Return
    ${EndIf}
    Sleep 500
    IntOp $Countdown $Countdown - 1
  ${LoopWhile} $Countdown > 0
  !insertmacro FAIL "Corewright tray or dashboard is still running. Close it in every signed-in session, then run setup again."
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
!macroend

Function .onInit
  !insertmacro INIT
  StrCpy $Autostart ${BST_CHECKED}
FunctionEnd
Function un.onInit
  !insertmacro INIT
  StrCpy $RemoveData ${BST_UNCHECKED}
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
Function un.RemovePayload
  ClearErrors
  FileOpen $8 "$INSTDIR\install-manifest.txt" r
  ${If} ${Errors}
    !insertmacro FAIL "The installed file manifest cannot be read. No files were removed."
  ${EndIf}
  ${Do}
    ClearErrors
    FileRead $8 $0
    ${If} ${Errors}
      ${ExitDo}
    ${EndIf}
    ${TrimNewLines} $0 $0
    StrCpy $4 $0 2
    StrCpy $0 $0 "" 2
    GetFullPathName $1 "$INSTDIR\$0"
    StrCpy $2 "$INSTDIR\"
    StrLen $3 $2
    StrCpy $3 $1 $3
    ${If} $3 != $2
      FileClose $8
      !insertmacro FAIL "The installed file manifest contains an unsafe path."
    ${EndIf}
    ${If} $4 == "F|"
      ${IfNot} ${FileExists} "$1"
        ${Continue} ; a previous upgrade may list the same file more than once
      ${EndIf}
      ClearErrors
      Delete "$1"
      ${If} ${Errors}
        FileClose $8
        !insertmacro FAIL "Could not remove $1. Close Corewright and retry uninstall."
      ${EndIf}
    ${ElseIf} $4 == "D|"
      RMDir "$1" ; unknown user files keep their directories
    ${Else}
      FileClose $8
      !insertmacro FAIL "The installed file manifest is invalid."
    ${EndIf}
  ${Loop}
  FileClose $8
  Delete "$INSTDIR\install-manifest.txt"
FunctionEnd

; Explorer delegates to the existing desktop shell, so the tray inherits the
; interactive user's unelevated token instead of the installer's admin token.
Function StartTray
  Exec '"$WINDIR\explorer.exe" "$INSTDIR\Corewright.Tray.exe"'
FunctionEnd

Section "Corewright"
  Call PrepareFiles
  SetOutPath "$INSTDIR"
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
  CreateDirectory "$DataDir"
  ${IfNot} ${FileExists} "$DataDir\rules.json"
  ${AndIf} ${FileExists} "$UserDataDir\rules.json"
    ; CopyFile with fail-if-exists also protects against a concurrent creator.
    System::Call 'kernel32::CopyFileW(w "$UserDataDir\rules.json", w "$DataDir\rules.json", i 1) i.r0'
    ${If} $0 == 0
      !insertmacro FAIL "Could not migrate your rules. The original rules were kept."
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
  ${If} $Autostart == ${BST_CHECKED}
    WriteRegStr HKCU "${RUN_KEY}" "Corewright" '"$INSTDIR\Corewright.Tray.exe"'
  ${Else}
    ReadRegStr $0 HKCU "${RUN_KEY}" "Corewright"
    ${If} ${Errors}
      ClearErrors ; no existing value is already the requested state
    ${Else}
      DeleteRegValue HKCU "${RUN_KEY}" "Corewright"
    ${EndIf}
  ${EndIf}
  ${If} ${Errors}
    !insertmacro FAIL "Could not update Corewright sign-in settings."
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
  Call un.PrepareFiles
  Call un.QueryService
  ${If} $ServiceState != 0
    StrCpy $ScCommand 'delete Corewright'
    Call un.Sc
    ${If} $ScResult != 0
    ${AndIf} $ScResult != 1060
      !insertmacro FAIL "Could not delete the Corewright service (sc.exe: $ScResult)."
    ${EndIf}
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
  Call un.RemovePayload
  Delete "$INSTDIR\uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\Corewright.lnk"
  DeleteRegValue HKCU "${RUN_KEY}" "Corewright"
  DeleteRegKey HKLM "${UNINSTALL_KEY}"
  ${If} $RemoveData == ${BST_CHECKED}
    RMDir /r "$DataDir"
    RMDir /r "$UserDataDir"
  ${EndIf}
SectionEnd
