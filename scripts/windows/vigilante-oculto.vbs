' Lanza vigilante.ps1 SIN ventana, desde la carpeta de este script.
' Lo usa la tarea "FitGymHuellaVigilante" (cada 1 minuto) de instalar-vigilante.bat.
Dim fso, sh, dir
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh  = CreateObject("WScript.Shell")
dir = fso.GetParentFolderName(WScript.ScriptFullName)
sh.CurrentDirectory = dir
sh.Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & dir & "\vigilante.ps1""", 0, False
