' Lanza launch.ps1 SIN ventana (oculto), desde la carpeta de este script.
' launch.ps1 auto-actualiza el agente (chequea GitHub) y despues lo arranca oculto.
' Lo usa la tarea programada (instalar-tarea.bat / instalar.bat) al iniciar sesion.
Dim fso, sh, dir, ps1
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh  = CreateObject("WScript.Shell")
dir = fso.GetParentFolderName(WScript.ScriptFullName)
sh.CurrentDirectory = dir
ps1 = dir & "\launch.ps1"
sh.Run "powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & ps1 & """", 0, False
