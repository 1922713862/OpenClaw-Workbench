Set sh = CreateObject("WScript.Shell")
Dim node, jfctl
node = sh.ExpandEnvironmentStrings("%ProgramFiles%\nodejs\node.exe")
jfctl = sh.ExpandEnvironmentStrings("%USERPROFILE%\.openclaw\jf-shell\bin\jfctl.cjs")
sh.Run """" & node & """ """ & jfctl & """ logon", 0, False
