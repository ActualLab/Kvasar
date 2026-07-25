@echo off
setlocal
rem Runs the Kvasar vs SQLCipher benchmarks in Release; see docs\BENCHMARKS.md.
rem Args are forwarded to the benchmark itself (after --):
rem   --n <count> --value <bytes|sweep> --threads <n> --lookups <n> --engines kvasar|sqlite|both --pagesize <bytes>
rem With no args the documented default sweep is used.
rem Run this on an otherwise-idle machine: concurrent load has been measured to skew results by 20-60%%.
set "ARGS=%*"
if "%ARGS%"=="" set "ARGS=--n 100000 --value sweep --threads 8 --lookups 500000 --engines both"
dotnet run -c Release --project "%~dp0benchmarks\ActualLab.Kvasar.Benchmarks\ActualLab.Kvasar.Benchmarks.csproj" -- %ARGS%
exit /b %ERRORLEVEL%
