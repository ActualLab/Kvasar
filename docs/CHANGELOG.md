# Changelog

All notable changes to **ActualLab.Kvasar** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

`+HexNumber` after version number is the commit hash of this version.
It isn't included into the NuGet package version.


## 1.0.3+e176d2d

Release date: 2026-11-21

The first 1.0 release. Kvasar has backed Voxt's client-side caches since July 2026, and the version
now says so. The library code and the on-disk format are unchanged since 0.3.10, so existing stores
open as before.

### Added

- A `net11.0` build. The package now targets `net11.0`, `net10.0` and `net9.0`.

### Infrastructure

- The package is published by the `Publish` GitHub workflow via nuget.org trusted publishing, so
  no long-lived API key exists. Each release also gets a GitHub release and is mirrored to GitHub
  Packages.


## 0.3.10+b63de71

Release date: 2026-07-30

The last release published before this changelog was started; see the git history for earlier changes.
