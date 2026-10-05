; ============================================================================
;  PeakGymLector.iss — el instalador de Windows del lector de huella.
;
;  POR QUE EXISTE
;  --------------
;  Hasta la v1.4 se distribuia un ZIP con 17 archivos sueltos (bats, vbs, ps1, el
;  driver, el exe) y la instruccion "clic derecho en instalar.bat -> Ejecutar como
;  administrador". Un dueño de gimnasio lo abria y no sabia que tocar. Ademas el
;  programa quedaba instalado DONDE se descomprimio (casi siempre Descargas): borrar
;  esa carpeta mataba el lector sin aviso.
;
;  Esto es un instalador comun: doble clic, Windows pide permiso solo, Siguiente,
;  Instalar, Finalizar. Aparece en Configuracion -> Aplicaciones con su desinstalador.
;
;  DONDE INSTALA, Y POR QUE NO EN "Program Files"
;  ----------------------------------------------
;  En C:\PeakGym\LectorHuella, con permiso de escritura para los usuarios. El agente
;  se auto-actualiza desde launch.ps1, que corre con el usuario de recepcion (sin
;  permisos de administrador) y reemplaza HuellaAgent.exe en su propia carpeta. En
;  Program Files no podria: cada actualizacion pediria un administrador, o sea que
;  nunca se actualizaria. Es el mismo criterio de Chrome y VS Code "por usuario".
;
;  Lo compila scripts/publicar.ps1 (no se compila a mano): le pasa la version y la
;  carpeta del paquete ya verificado.
; ============================================================================

#ifndef AppVersion
  #error "Falta /DAppVersion=x.y.z (lo pasa scripts/publicar.ps1)"
#endif
#ifndef PkgDir
  #error "Falta /DPkgDir=<carpeta del paquete> (lo pasa scripts/publicar.ps1)"
#endif

#define AppName "PeakGym Lector de Huella"
#define PanelUrl "https://my.peakgym.app/configuracion?tab=integraciones&int=huella"

[Setup]
; El AppId NO se cambia nunca: es lo que hace que una version nueva se instale ENCIMA
; de la vieja en vez de aparecer como un segundo programa.
AppId={{6F3B2C1E-8A4D-4F7B-9C2E-5D1A0B7E4C93}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=PeakGym
AppPublisherURL=https://peakgym.app
VersionInfoVersion={#AppVersion}
DefaultDirName=C:\PeakGym\LectorHuella
; La carpeta es fija: los scripts y las tareas la dan por sentada, y elegirla no le
; sirve a nadie en un gimnasio.
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=PeakGym-Lector-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\HuellaAgent.exe
; Cerrar el agente antes de copiar lo hace PrepareToInstall (abajo), no el Restart
; Manager: el agente corre oculto y el vigilante lo revive cada minuto.
CloseApplications=no
; El driver de ZKTeco, cuando YA estaba instalado, deja archivos "para reemplazar al
; reiniciar", y con eso Inno pide reiniciar Windows. Probado el 05-oct: el lector anda
; igual sin reiniciar. El pedido sobraba y asustaba, y en una PC de recepcion reiniciar
; en medio del turno no es gratis.
RestartIfNeededByRun=no

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Messages]
es.WelcomeLabel2=Esto instala el programa que conecta el lector de huella con PeakGym.%n%nTarda menos de un minuto. Si el lector ya está enchufado, mejor: al terminar te decimos si quedó funcionando.
es.FinishedHeadingLabel=Listo
es.ClickFinish=Haz clic en Finalizar para salir.

[Dirs]
Name: "{app}"; Permissions: users-modify
; Datos del agente (huellas en cache, vinculo con el gym, logs). Se crea con permiso
; de escritura para los usuarios porque el agente corre SIN administrador: si la creara
; este instalador con los permisos por defecto, quedaria de solo lectura para el y no
; podria guardar nada. Y NO se borra al desinstalar: reinstalar no obliga a re-vincular.
Name: "{commonappdata}\HuellaAgent"; Permissions: users-modify; Flags: uninsneveruninstall

[Files]
Source: "{#PkgDir}\HuellaAgent.exe";       DestDir: "{app}"; Flags: ignoreversion
Source: "{#PkgDir}\version.txt";           DestDir: "{app}"; Flags: ignoreversion
Source: "{#PkgDir}\launch.ps1";            DestDir: "{app}"; Flags: ignoreversion
Source: "{#PkgDir}\run-hidden.vbs";        DestDir: "{app}"; Flags: ignoreversion
Source: "{#PkgDir}\vigilante.ps1";         DestDir: "{app}"; Flags: ignoreversion
Source: "{#PkgDir}\vigilante-oculto.vbs";  DestDir: "{app}"; Flags: ignoreversion
Source: "{#PkgDir}\verificar.ps1";         DestDir: "{app}"; Flags: ignoreversion
Source: "{#PkgDir}\LEEME.txt";             DestDir: "{app}"; Flags: ignoreversion
; La config es la de ESE gimnasio (puerto del rele, torniquete): reinstalar no la pisa.
Source: "{#PkgDir}\appsettings.json";      DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
; El driver de ZKTeco tambien esta hecho con Inno Setup, asi que acepta /VERYSILENT:
; se instala sin mostrar su propio asistente en medio de este.
Source: "{#PkgDir}\setup-driver.exe";      DestDir: "{tmp}"; Flags: deleteafterinstall

[Run]
Filename: "{tmp}\setup-driver.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-"; StatusMsg: "Instalando el driver del lector..."; Flags: waituntilterminated

; Que Windows no apague el lector. Antes era un .bat aparte que nadie corria, y el
; castigo no es inmediato: el USB se suspende recien tras horas sin uso, asi que anda
; todo el primer dia y aparece muerto a la mañana siguiente.
;   USB selective suspend: desactivado (enchufado)
Filename: "powercfg.exe"; Parameters: "/SETACVALUEINDEX SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0"; StatusMsg: "Ajustando la energía..."; Flags: runhidden
;   La PC no se suspende ni hiberna enchufada (la pantalla si puede apagarse).
Filename: "powercfg.exe"; Parameters: "/change standby-timeout-ac 0"; Flags: runhidden
Filename: "powercfg.exe"; Parameters: "/change hibernate-timeout-ac 0"; Flags: runhidden
;   Laptop: cerrar la tapa enchufada no hace nada. Antes era una instruccion "a mano".
Filename: "powercfg.exe"; Parameters: "/SETACVALUEINDEX SCHEME_CURRENT SUB_BUTTONS LIDACTION 0"; Flags: runhidden
Filename: "powercfg.exe"; Parameters: "/SETACTIVE SCHEME_CURRENT"; Flags: runhidden

; Arranque automatico al iniciar sesion, y el vigilante cada minuto. Los mismos nombres
; de tarea de siempre: /f pisa los de una instalacion vieja (aunque apuntaran a otra
; carpeta, como Descargas) en vez de dejar dos agentes peleando por el puerto 8000.
; La ruta va sin comillas internas a proposito: {app} es fija y no tiene espacios.
Filename: "schtasks.exe"; Parameters: "/create /tn ""FitGymHuellaAgent"" /tr ""wscript.exe {app}\run-hidden.vbs"" /sc onlogon /f"; StatusMsg: "Dejando el lector listo para arrancar solo..."; Flags: runhidden
Filename: "schtasks.exe"; Parameters: "/create /tn ""FitGymHuellaVigilante"" /tr ""wscript.exe {app}\vigilante-oculto.vbs"" /sc minute /mo 1 /f"; Flags: runhidden

; Arranca YA, con el usuario normal y no como administrador: el agente elevado no
; comparte sesion con el navegador de recepcion como se espera.
Filename: "wscript.exe"; Parameters: """{app}\run-hidden.vbs"""; Flags: runasoriginaluser nowait

; Ultima pagina: abrir el panel en la tarjeta del lector, que es donde se vincula.
Filename: "{#PanelUrl}"; Description: "Abrir PeakGym para vincular el lector"; Flags: postinstall shellexec nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "schtasks.exe"; Parameters: "/delete /tn ""FitGymHuellaVigilante"" /f"; Flags: runhidden; RunOnceId: "BorrarVigilante"
Filename: "schtasks.exe"; Parameters: "/delete /tn ""FitGymHuellaAgent"" /f"; Flags: runhidden; RunOnceId: "BorrarTarea"
Filename: "taskkill.exe"; Parameters: "/f /im HuellaAgent.exe"; Flags: runhidden; RunOnceId: "CerrarAgente"

[Code]
var
  Resultado: String;
  YaVinculado: Boolean;

// Antes de copiar: apagar el vigilante y cerrar el agente. Si el vigilante sigue vivo,
// revive al agente a mitad de la copia y HuellaAgent.exe queda bloqueado: la
// instalacion falla con "no se puede reemplazar el archivo". schtasks /create /f del
// final lo vuelve a crear habilitado.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Codigo: Integer;
begin
  Exec('schtasks.exe', '/change /tn "FitGymHuellaVigilante" /disable', '', SW_HIDE, ewWaitUntilTerminated, Codigo);
  Exec('taskkill.exe', '/f /im HuellaAgent.exe', '', SW_HIDE, ewWaitUntilTerminated, Codigo);
  Sleep(800);
  Result := '';
end;

// Pregunta al agente si arranco y si ve el lector. Lo mismo que verificar.ps1, pero
// dicho en la ultima pantalla del instalador y no en una consola que nadie lee.
function ConsultarAgente(): String;
var
  Http: Variant;
  Intento: Integer;
begin
  Result := '';
  for Intento := 1 to 15 do
  begin
    try
      Http := CreateOleObject('WinHttp.WinHttpRequest.5.1');
      Http.Open('GET', 'http://localhost:8000/health', False);
      Http.SetTimeouts(2000, 2000, 2000, 2000);
      Http.Send('');
      if Http.Status = 200 then
      begin
        Result := Http.ResponseText;
        Exit;
      end;
    except
    end;
    Sleep(2000);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Json: String;
begin
  if CurStep <> ssPostInstall then Exit;
  WizardForm.StatusLabel.Caption := 'Comprobando que el lector responde...';
  Json := ConsultarAgente();
  // Reinstalacion: el vinculo vive en ProgramData y sobrevive, asi que no hay que
  // mandar a nadie a vincular de nuevo.
  YaVinculado := Pos('"durable":true', Json) > 0;
  if Json = '' then
    Resultado := 'El programa quedó instalado, pero todavía no respondió. Reinicia la PC: arranca solo al iniciar sesión.'
  else if Pos('"reader":"connected"', Json) > 0 then
    Resultado := 'El lector está funcionando.'
  else
    Resultado := 'El programa está funcionando, pero no ve el lector. Enchúfalo (o prueba otro puerto USB) y espera unos segundos.';
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID <> wpFinished) or (Resultado = '') then Exit;
  if YaVinculado then
    WizardForm.FinishedLabel.Caption := Resultado + #13#10#13#10 +
      'Ya estaba vinculado a tu gimnasio: no hace falta hacer nada más.'
  else
    WizardForm.FinishedLabel.Caption := Resultado + #13#10#13#10 +
      'Falta un paso: en PeakGym, Configuración → Integraciones → Lector de huella → Vincular a este gimnasio.';
end;
