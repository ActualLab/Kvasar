@echo off
setlocal
rem Packs (via Pack.cmd) then pushes the resulting .nupkg files to NuGet.
rem Requires the ActualChat_NuGet_API_Key environment variable (same one ActualLab.Fusion uses);
rem override the feed with NUGET_SOURCE.
rem Usage: Publish.cmd            (packs + pushes to nuget.org using %ActualChat_NuGet_API_Key%)
set "SOURCE=%NUGET_SOURCE%"
if "%SOURCE%"=="" set "SOURCE=https://api.nuget.org/v3/index.json"
if "%ActualChat_NuGet_API_Key%"=="" (
  echo ActualChat_NuGet_API_Key is not set. Set it, or push manually with: dotnet nuget push ... --api-key ^<key^>
  exit /b 1
)

call "%~dp0Pack.cmd" || exit /b %ERRORLEVEL%
dotnet nuget push "%~dp0artifacts\nupkg\*.nupkg" --source "%SOURCE%" --api-key "%ActualChat_NuGet_API_Key%" --skip-duplicate
exit /b %ERRORLEVEL%
