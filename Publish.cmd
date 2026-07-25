@echo off
setlocal
rem Packs (via Pack.cmd) then pushes the resulting .nupkg files to NuGet.
rem Requires the NUGET_API_KEY environment variable; override the feed with NUGET_SOURCE.
rem Usage: Publish.cmd            (packs + pushes to nuget.org using %NUGET_API_KEY%)
set "SOURCE=%NUGET_SOURCE%"
if "%SOURCE%"=="" set "SOURCE=https://api.nuget.org/v3/index.json"
if "%NUGET_API_KEY%"=="" (
  echo NUGET_API_KEY is not set. Set it, or push manually with: dotnet nuget push ... --api-key ^<key^>
  exit /b 1
)

call "%~dp0Pack.cmd" || exit /b %ERRORLEVEL%
dotnet nuget push "%~dp0artifacts\nupkg\*.nupkg" --source "%SOURCE%" --api-key "%NUGET_API_KEY%" --skip-duplicate
exit /b %ERRORLEVEL%
