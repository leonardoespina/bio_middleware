@echo off
title Biometric Middleware Service
echo Iniciando Servicio de Huellas (DigitalPersona 5160)...
cd /d "%~dp0"
dotnet run --project bio_middleware.csproj
pause
