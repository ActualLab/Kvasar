@echo off
setlocal
rem Runs the full ActualLab.Kvasar test suite in Release (single-target net10.0).
rem Extra args are forwarded to dotnet test, e.g.:
rem   Run-Tests.cmd --filter "FullyQualifiedName~SmokeTests"
rem   Run-Tests.cmd -p:UseMultitargeting=true    (runs on net10.0 and net9.0)
dotnet test "%~dp0ActualLab.Kvasar.slnx" -c Release %*
exit /b %ERRORLEVEL%
