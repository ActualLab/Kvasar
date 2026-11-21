---
allowed-tools: Read, Edit, Bash, Glob, Grep, AskUserQuestion, Skill
description: Publish the ActualLab.Kvasar NuGet package via the Publish GitHub workflow, then update the changelog and the GitHub release
argument-hint: "[-test|-ci-test|-no-test] [-changelog|-no-changelog]"
---

# Publish

Publish ActualLab.Kvasar to NuGet. The `Publish` GitHub workflow
(`.github/workflows/Publish.yml`) runs the tests, packs the multi-target
(net11.0;net10.0;net9.0) package, pushes it to nuget.org via trusted publishing,
creates the `v<version>` GitHub release and mirrors the package to GitHub Packages.

**Publishing is a critical, effectively irreversible action** — a pushed package
version can't be replaced, and consumers pick it up immediately. So the bar for
guessing is much higher here than in normal work: **if you're in doubt about
anything that may negatively impact the outcome** — an unexpected diff or version,
a failed push, a dirty working tree, a test you're tempted to shrug off,
a workaround you're about to invent — **stop and ask the user** (AskUserQuestion)
how to resolve it instead of picking an answer yourself. A pause costs a minute;
a wrong publish costs a version.

## Instructions

### Step 1: Parse arguments

`$ARGUMENTS` holds flags; **the default is `-test -changelog`**.

- `-test` (default) → the full local test pass on all target frameworks (Step 4),
  then the workflow's tests (Step 5)
- `-ci-test` → just the workflow's tests: skip the local pass
- `-no-test` → no tests at all: skip both
- `-changelog` (default) / `-no-changelog` → write the changelog entry and put it
  into the GitHub release after publishing (Step 6), or skip that

### Step 2: Check the branch and the environment

**Publish only from `main`, fully in sync with `origin/main`.** `version.json`'s
`publicReleaseRefSpec` makes other branches stamp a git-hash version like
`1.0.3-g59e65c53cc`, and pushing that to NuGet burns a bogus version permanently.

```powershell
git rev-parse --abbrev-ref HEAD                            # must be: main
git status --short                                         # must be empty
git fetch origin
git rev-list --left-right --count origin/main...main       # must be: 0    0
dotnet tool restore
dotnet nbgv get-version -v NuGetPackageVersion             # must have no -g<hash> suffix
```

- **Not on `main`** → STOP and ask. Never switch branches on your own.
- **Dirty working tree** → STOP and ask. Never stash, discard, or commit on your own.
- **Behind `origin/main`** → `git pull --ff-only`; if it isn't a fast-forward, STOP and ask.
- **Ahead of `origin/main`** → push the commits first (`git push`) and say what you pushed.
- **Diverged** → STOP and ask; don't rebase or merge on your own.
- **Version has a `-g<hash>` suffix on `main`** → STOP and ask.

Locally you only need `pwsh` and a `gh` CLI that can run workflows in `ActualLab/Kvasar`.
`GH_TOKEN` is often another organization's fine-grained token that gets 403 here, so
`Publish.ps1` switches to `GH_TOKEN_FUSION` when it's set. If it isn't set
(`pwsh -c '[bool]$env:GH_TOKEN_FUSION'`), STOP and tell the user to set it — a token owned
by `ActualLab`, covering `ActualLab/Kvasar`, with "Actions: Read and write".

### Step 3: Check there's something to publish

Find the latest published version (the last entry of
`https://api.nuget.org/v3-flatcontainer/actuallab.kvasar/index.json`), map it to its commit
(`dotnet nbgv get-commits <version>`, or the hash in the topmost `docs/CHANGELOG.md` entry),
and list what changed since:
`git log <commit>..HEAD --oneline -- src/ Directory.Packages.props Directory.Build.props version.json`.
If nothing shipping changed, **skip publishing**: report what was checked and stop.
Otherwise report the version→commit mapping and a one-line diff summary, then proceed.

### Step 4: Local tests (skip with `-ci-test` or `-no-test`)

Run the full suite, slow tests included, on every target framework:

```powershell
dotnet build ActualLab.Kvasar.slnx -c Release -p:UseMultitargeting=true
dotnet test  ActualLab.Kvasar.slnx -c Release -p:UseMultitargeting=true --no-build
```

The build must have 0 warnings and every test must pass on net11.0, net10.0 and net9.0.
If something fails, fix it, re-run, commit and push the fix — never skip or suppress a failure.

### Step 5: Publish

Run `Publish.cmd` once, adding `-no-test` when the command got `-no-test` (it sets the
workflow's `skip-tests` input). It re-checks that `main` is clean and matches `origin/main`,
starts the `Publish` workflow on that commit, and waits for it (`gh run watch`), printing the
failed step's log on failure.

**The run waits for an approval.** Its `release` environment requires one of its reviewers
(set in the repository settings) to approve it before the job starts. The script opens the
run's page in the browser and prints `Approve the run to start publishing: <url>` — relay
that URL to the user right away. Never approve it yourself.

The workflow:

1. runs the tests except the `Slow` category (skipped with `-no-test`);
2. packs via `Pack.cmd`, gets a 1-hour NuGet key via `NuGet/login`, and pushes the package;
3. then, in separate jobs, creates the `v<version>` GitHub release and mirrors the package to
   GitHub Packages. A failure there doesn't undo the publish — report it, and re-run just the
   failed job (`gh run rerun <id> --failed`).

Use a long Bash timeout (or run it in the background).

If a publish fails:

- **Test or build errors** → fix them, run the affected tests, commit and push the fix, then
  re-run the publish script.
- **Auth errors** (`NuGet/login` failure, 403 on push) → report to the user and stop; the
  trusted publishing policy on nuget.org or the `NUGET_USER` secret needs fixing. Never work
  around credentials.
- **Transient push errors** → re-run once before reporting (the push uses `--skip-duplicate`).

### Step 6: Changelog and release notes (skip with `-no-changelog`)

NuGet pushes aren't instantly visible on nuget.org, so take the version from nbgv:
`dotnet nbgv get-version -v NuGetPackageVersion`. Invoke the `changelog-update` command with
that exact version. Commit the entry and push it to `main`.

**Credit external contributors.** For every pull request merged since the last release whose
author isn't a maintainer (`GH_TOKEN="$GH_TOKEN_FUSION" gh pr list --repo ActualLab/Kvasar --state merged`),
thank its author in the entry's intro and link the PR on the bullet it produced, e.g.
`([#12](https://github.com/ActualLab/Kvasar/pull/12) by Jane Doe)`; use `@login` in the
release notes so GitHub notifies them.

Then put the entry into the GitHub release. The workflow's `release` job created
`v<version>` on the published commit with placeholder notes; replace them with the entry's
body (everything under its `##` header; turn relative `*.md` links into
`https://github.com/ActualLab/Kvasar/blob/main/docs/*.md` ones), followed by
`More details: https://github.com/ActualLab/Kvasar/blob/main/docs/CHANGELOG.md`.

```bash
GH_TOKEN="$GH_TOKEN_FUSION" gh release view "v<version>" --repo ActualLab/Kvasar   # must exist
GH_TOKEN="$GH_TOKEN_FUSION" gh release edit "v<version>" --repo ActualLab/Kvasar --notes-file tmp/release-notes.md
```

If the release is missing (the `release` job failed), report it to the user rather than
creating it from your machine: a tag created locally may point at a different commit.

Finish with the published version, the nuget.org and GitHub release links, and anything
that needed a retry.
