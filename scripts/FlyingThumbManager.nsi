Unicode true
!include "MUI2.nsh"
!include "x64.nsh"
!define AppName "Flying Thumb Manager"
!define UninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\FlyingThumbManager"
Name "${AppName}"
OutFile "${OutputFile}"
InstallDir "$LOCALAPPDATA\Programs\Flying Thumb Manager"
RequestExecutionLevel user
SetCompressor /SOLID lzma
!define MUI_ICON "..\manager\assets\flying-thumb.ico"
!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "Flying Thumb Manager requires 64-bit Windows."
    Abort
  ${EndIf}
FunctionEnd

Section
  SetShellVarContext current
  SetOutPath "$INSTDIR"
  File "${PackageDir}\FlyingThumbManager.exe"
  File "${PackageDir}\FlyingThumbEsptool.exe"
  File "${PackageDir}\FlyingThumb-v2-*.bin"
  File "${PackageDir}\firmware*-version.txt"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortcut "$SMPROGRAMS\Flying Thumb Manager.lnk" "$INSTDIR\FlyingThumbManager.exe"
  WriteRegStr HKCU "${UninstallKey}" "DisplayName" "${AppName}"
  WriteRegStr HKCU "${UninstallKey}" "DisplayVersion" "${ManagerVersion}"
  WriteRegStr HKCU "${UninstallKey}" "DisplayIcon" "$INSTDIR\FlyingThumbManager.exe"
  WriteRegStr HKCU "${UninstallKey}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "${UninstallKey}" "NoModify" 1
  WriteRegDWORD HKCU "${UninstallKey}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  Delete "$SMPROGRAMS\Flying Thumb Manager.lnk"
  Delete "$INSTDIR\FlyingThumbManager.exe"
  Delete "$INSTDIR\FlyingThumbEsptool.exe"
  Delete "$INSTDIR\FlyingThumb-v2-full.bin"
  Delete "$INSTDIR\FlyingThumb-v2-wifi-update.bin"
  Delete "$INSTDIR\FlyingThumb-v2-screenless-full.bin"
  Delete "$INSTDIR\FlyingThumb-v2-screenless-wifi-update.bin"
  Delete "$INSTDIR\FlyingThumb-v2-hardware-diagnostic.bin"
  Delete "$INSTDIR\firmware-version.txt"
  Delete "$INSTDIR\firmware-screenless-version.txt"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  DeleteRegKey HKCU "${UninstallKey}"
SectionEnd
