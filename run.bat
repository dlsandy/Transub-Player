@echo off
cd /d "%~dp0"
dotnet run --project "%~dp0src\TransubPlayer\TransubPlayer.csproj" %*
