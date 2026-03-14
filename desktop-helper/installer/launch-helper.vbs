Dim shell
Dim fileSystem
Dim installDirectory
Dim helperExecutable
Dim helperDll
Dim localDotnet

Set shell = CreateObject("WScript.Shell")
Set fileSystem = CreateObject("Scripting.FileSystemObject")

installDirectory = fileSystem.GetParentFolderName(WScript.ScriptFullName)
helperExecutable = Chr(34) & installDirectory & "\LocalClipHelper.exe" & Chr(34)
helperDll = Chr(34) & installDirectory & "\LocalClipHelper.dll" & Chr(34)
localDotnet = Chr(34) & installDirectory & "\dotnet\dotnet.exe" & Chr(34)

If fileSystem.FileExists(installDirectory & "\dotnet\dotnet.exe") And fileSystem.FileExists(installDirectory & "\LocalClipHelper.dll") Then
    shell.Run localDotnet & " " & helperDll, 0, False
Else
    shell.Run helperExecutable, 0, False
End If
