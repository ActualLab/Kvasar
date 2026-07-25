@echo off
setlocal
rem Packs ActualLab.Kvasar into a multi-target (net10.0;net9.0) NuGet package.
rem Output: artifacts\nupkg (see PackageOutputPath in Directory.Build.props).
rem Extra args are forwarded to dotnet pack (e.g. Pack.cmd -p:Version=1.2.3).
rem The output folder is wiped first: Publish.cmd pushes artifacts\nupkg\*.nupkg, so a leftover package
rem from an earlier build would be published too.
if exist "%~dp0artifacts\nupkg" rmdir /S /Q "%~dp0artifacts\nupkg"
dotnet pack "%~dp0src\ActualLab.Kvasar\ActualLab.Kvasar.csproj" -c Release -p:UseMultitargeting=true %*
exit /b %ERRORLEVEL%
