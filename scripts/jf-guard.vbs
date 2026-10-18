Set sh = CreateObject("WScript.Shell")
Dim node, jfctl
node = sh.ExpandEnvironmentStrings("%ProgramFiles%\nodejs\node.exe")
jfctl = sh.ExpandEnvironmentStrings("%USERPROFILE%\.openclaw\jf-shell\bin\jfctl.cjs")
sh.Run """" & node & """ """ & jfctl & """ guard", 0, False
