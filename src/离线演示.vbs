Set shell = CreateObject("WScript.Shell")
Set fs = CreateObject("Scripting.FileSystemObject")
base = fs.GetParentFolderName(WScript.ScriptFullName)
exe = fs.BuildPath(base, "JFWorkbench.exe")
If fs.FileExists(exe) Then
    shell.Run Chr(34) & exe & Chr(34) & " --demo", 1, False
Else
    MsgBox "JFWorkbench.exe not found. Please open the release folder.", 48, "JFlash"
End If
