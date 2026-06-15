' Lanza HuellaAgent.exe SIN ventana de consola, desde la carpeta de este script.
' Lo usa la tarea programada (instalar-tarea.bat) para correr el agente al iniciar sesion.
Dim fso, sh, dir
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh  = CreateObject("WScript.Shell")
dir = fso.GetParentFolderName(WScript.ScriptFullName)
sh.CurrentDirectory = dir
sh.Run """" & dir & "\HuellaAgent.exe""", 0, False
