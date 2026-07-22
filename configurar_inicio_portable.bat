@echo off
title Configurar Middleware Biometrico en Inicio (Segundo Plano)
echo ======================================================
echo CONFIGURACION DE MIDDLEWARE BIOMETRICO (PORTABLE)
echo ======================================================
echo.

:: 1. Intentar detener el servicio antiguo (si existe) y procesos previos
echo [1/4] Limpiando configuraciones previas...
sc stop BioMiddlewareService >nul 2>&1
sc config BioMiddlewareService start= disabled >nul 2>&1
taskkill /f /im bio_middleware.exe >nul 2>&1

:: 2. Crear el lanzador silencioso (VBS)
echo [2/4] Creando lanzador invisible...
set VBS_LAUNCHER="%~dp0bio_oculto.vbs"
set EXE_PATH=%~dp0bio_middleware.exe
:: Escapar barras invertidas para el VBS
set EXE_PATH=%EXE_PATH:\=\\%
echo Set WshShell = CreateObject("WScript.Shell") > %VBS_LAUNCHER%
echo WshShell.CurrentDirectory = "%~dp0" >> %VBS_LAUNCHER%
echo WshShell.Run "bio_middleware.exe", 0, False >> %VBS_LAUNCHER%

:: 3. Crear acceso directo al Lanzador VBS en Inicio
echo [3/4] Registrando en el Inicio de Windows...
set SCRIPT="%TEMP%\%RANDOM%-%RANDOM%-%RANDOM%-%RANDOM%.vbs"
echo Set oWS = WScript.CreateObject("WScript.Shell") >> %SCRIPT%
echo sLinkFile = "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\BioMiddleware.lnk" >> %SCRIPT%
echo Set oLink = oWS.CreateShortcut(sLinkFile) >> %SCRIPT%
echo oLink.TargetPath = "wscript.exe" >> %SCRIPT%
echo oLink.Arguments = """%~dp0bio_oculto.vbs""" >> %SCRIPT%
echo oLink.WorkingDirectory = "%~dp0" >> %SCRIPT%
echo oLink.Description = "Bio Middleware Bridge (Invisible)" >> %SCRIPT%
echo oLink.Save >> %SCRIPT%
cscript /nologo %SCRIPT%
del %SCRIPT%

:: 4. Iniciar ahora mismo de forma invisible
echo [4/4] Iniciando el Middleware ahora (en segundo plano)...
wscript "%~dp0bio_oculto.vbs"

echo.
echo ======================================================
echo ¡LISTO! El middleware se ejecutara solo al iniciar sesion.
echo NO VERAS NINGUNA VENTANA. El sistema correra en segundo plano.
echo Para verificar puedes ir a: http://localhost:8081/api/status
echo ======================================================
pause

