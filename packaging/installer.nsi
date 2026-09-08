; Media Porter installer
;
; Installs to Program Files, machine-wide, so it behaves like any other Windows
; application: elevation once at install, entry in Apps & Features, shortcuts for
; all users.
;
; The app detects that its own folder is read-only and moves everything it writes
; - settings, and the ~114 MB of ffmpeg/yt-dlp it downloads - to
; %LOCALAPPDATA%\Media Porter. The portable zip keeps all of it in one folder
; instead, since there the app CAN write next to itself.

Unicode true
SetCompressor /SOLID lzma

!define APPNAME       "Media Porter"
!define APPEXE        "MediaPorter.exe"
!define COMPANY       "Shadowjump"
!ifndef ROOT
  !define ROOT        "."
!endif
!ifndef VERSION
  !define VERSION     "1.0.0"
!endif
!define ABOUTURL      "https://github.com/Shadowjump/media-porter"
!define UNINSTKEY     "Software\Microsoft\Windows\CurrentVersion\Uninstall\MediaPorter"

Name "${APPNAME} ${VERSION}"
OutFile "${ROOT}\MediaPorter-${VERSION}-setup.exe"
InstallDir "$PROGRAMFILES64\${APPNAME}"
InstallDirRegKey HKLM "Software\${COMPANY}\MediaPorter" "InstallDir"
RequestExecutionLevel admin
ShowInstDetails show
ShowUninstDetails show

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName"     "${APPNAME}"
VIAddVersionKey "FileDescription" "${APPNAME} installer"
VIAddVersionKey "FileVersion"     "${VERSION}"
VIAddVersionKey "ProductVersion"  "${VERSION}"
VIAddVersionKey "LegalCopyright"  "MIT Licensed"
VIAddVersionKey "CompanyName"     "${COMPANY}"

!include "MUI2.nsh"
!include "FileFunc.nsh"
!insertmacro GetSize

!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "${APPNAME} ${VERSION}"
!define MUI_WELCOMEPAGE_TEXT "Get music and video onto classic iPods, iPhones and iPads without opening iTunes.$\r$\n$\r$\nInstalls to Program Files for all users, so Windows will ask for administrator permission once.$\r$\n$\r$\nSettings and the tools it downloads are kept in your own AppData folder, so the app never needs elevation again after this.$\r$\n$\r$\nOn first launch, open Settings and choose 'Copy tools into this folder' to fetch yt-dlp and ffmpeg. They are not bundled here."

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${ROOT}\LICENSE"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES

!define MUI_FINISHPAGE_RUN "$INSTDIR\${APPEXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Start ${APPNAME}"
!define MUI_FINISHPAGE_LINK "View the project on GitHub"
!define MUI_FINISHPAGE_LINK_LOCATION "${ABOUTURL}"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

Section "Media Porter" SecMain
  SectionIn RO
  SetShellVarContext all
  ; NSIS runs 32-bit; without this every HKLM write lands in WOW6432Node
  SetRegView 64
  SetOutPath "$INSTDIR"
  File "${ROOT}\${APPEXE}"
  File "${ROOT}\MediaPorter.exe.config"
  File "${ROOT}\LICENSE"
  File "${ROOT}\README.md"

  SetOutPath "$INSTDIR\ui"
  File "${ROOT}\ui\*.*"

  ; Shipped so the app's own "Rebuild app from source" works after install.
  SetOutPath "$INSTDIR\source"
  File "${ROOT}\source\*.*"

  SetOutPath "$INSTDIR"

  WriteRegStr HKLM "Software\${COMPANY}\MediaPorter" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "Software\${COMPANY}\MediaPorter" "Version"    "${VERSION}"

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayName"     "${APPNAME}"
  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayVersion"  "${VERSION}"
  WriteRegStr   HKLM "${UNINSTKEY}" "Publisher"       "${COMPANY}"
  WriteRegStr   HKLM "${UNINSTKEY}" "URLInfoAbout"    "${ABOUTURL}"
  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayIcon"     "$INSTDIR\${APPEXE}"
  WriteRegStr   HKLM "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr   HKLM "${UNINSTKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoRepair" 1

  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKLM "${UNINSTKEY}" "EstimatedSize" "$0"
SectionEnd

Section "Start Menu shortcut" SecStart
  SetShellVarContext all
  CreateDirectory "$SMPROGRAMS\${APPNAME}"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\${APPEXE}"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\Uninstall ${APPNAME}.lnk" "$INSTDIR\Uninstall.exe"
SectionEnd

Section /o "Desktop shortcut" SecDesktop
  SetShellVarContext all
  CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${APPEXE}"
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecMain}    "The application, its interface files and its source."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecStart}   "Add ${APPNAME} to the Start Menu."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "Put a shortcut on the desktop."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Section "Uninstall"
  SetShellVarContext all
  SetRegView 64
  Delete "$INSTDIR\${APPEXE}"
  Delete "$INSTDIR\MediaPorter.exe.config"
  Delete "$INSTDIR\LICENSE"
  Delete "$INSTDIR\README.md"
  Delete "$INSTDIR\Uninstall.exe"

  RMDir /r "$INSTDIR\ui"
  RMDir /r "$INSTDIR\source"

  ; Only removes the folder if nothing else is in it. Settings and the downloaded
  ; tools live in %LOCALAPPDATA%\Media Porter and are deliberately left behind -
  ; an uninstall should not silently bin ~114 MB of tools or someone's library
  ; paths. Deleting that folder by hand is all it takes.
  RMDir "$INSTDIR"

  Delete "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk"
  Delete "$SMPROGRAMS\${APPNAME}\Uninstall ${APPNAME}.lnk"
  RMDir  "$SMPROGRAMS\${APPNAME}"
  Delete "$DESKTOP\${APPNAME}.lnk"

  DeleteRegKey HKLM "${UNINSTKEY}"
  DeleteRegKey HKLM "Software\${COMPANY}\MediaPorter"
  ; leave the vendor key only if something else is under it
  DeleteRegKey /ifempty HKLM "Software\${COMPANY}"
SectionEnd

Function .onInit
  !insertmacro MUI_LANGDLL_DISPLAY
FunctionEnd
