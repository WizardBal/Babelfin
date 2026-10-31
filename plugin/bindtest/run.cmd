@echo off
rem Builds the Babelfin plugin (against Jellyfin 12.0.0) and loads it the way Jellyfin 12.2 does.
rem Usage: plugin\bindtest\run.cmd   (from anywhere). Ends with "BIND TEST PASS" or exits non-zero.
setlocal
set DOTNET="%ProgramFiles%\dotnet\dotnet.exe"
if not exist %DOTNET% set DOTNET=dotnet
set HERE=%~dp0
set PLUGIN=%HERE%..\src\Jellyfin.Plugin.Babelfin
%DOTNET% build "%PLUGIN%\Jellyfin.Plugin.Babelfin.csproj" -c Release -nologo -v q || exit /b 1
%DOTNET% run --project "%HERE%app\App.csproj" -c Release -- "%PLUGIN%\bin\Release\net10.0" || exit /b 1
