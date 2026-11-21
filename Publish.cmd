@echo off
rem Usage: Publish.cmd [-no-test]
rem Runs the Publish workflow on GitHub, which runs the tests (unless -no-test) and publishes the NuGet package
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0Publish.ps1" %*
