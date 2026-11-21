@echo off
setlocal
rem Builds the whole ActualLab.Kvasar solution in Release (single-target net11.0).
rem Extra args are forwarded to dotnet build, so all target frameworks (net11.0;net10.0;net9.0)
rem can be validated with: Build.cmd -p:UseMultitargeting=true
dotnet build "%~dp0ActualLab.Kvasar.slnx" -c Release %*
exit /b %ERRORLEVEL%
