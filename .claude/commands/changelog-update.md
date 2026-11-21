---
allowed-tools: Read, Edit, Bash, Glob, Grep
description: Generate a new docs/CHANGELOG.md entry from commits
argument-hint: '[version-number] - e.g., "0.3.12"'
---

# Changelog Entry Generator

Generate a new `docs/CHANGELOG.md` entry based on commits since the last release.

## Instructions

### Step 1: Determine the version

If `$ARGUMENTS` is provided, use it as the version. Otherwise use the greater of:

- the local package: `ls artifacts/nupkg/*.nupkg`;
- the latest published one:
  `curl -s https://api.nuget.org/v3-flatcontainer/actuallab.kvasar/index.json` (the last entry
  of `versions`).

Use the exact version you found; never invent one by incrementing it. If it isn't newer than
the topmost `docs/CHANGELOG.md` entry, there's nothing to add: say so and stop.

### Step 2: Find the commits since the last release

The topmost entry's header holds the last release's commit hash, e.g. `## 0.3.10+b63de71`.
List what changed since:

```bash
git log <last-hash>..HEAD --pretty=format:"%h %s"
git diff --stat <last-hash>..HEAD -- src/ Directory.Packages.props Directory.Build.props version.json
```

### Step 3: Check for breaking changes

A change is breaking if it alters the public API of `src/ActualLab.Kvasar` (a removed or
renamed type or member, a changed signature, a changed default in `KvasarOptions`), or the
on-disk format in a way an older store can't be opened by the new version or vice versa
(see `docs/SPEC.md` and `docs/DESIGN.md`). Also look for commits marked `BREAKING:` or `feat!:`.
Compare `git diff <last-hash>..HEAD -- src/` for public members, and read the format-related
diffs in `docs/`.

### Step 4: Categorize commits

| Prefix | Section |
|--------|---------|
| `feat:` | Added |
| `fix:` | Fixed |
| `perf:` | Performance |
| `refactor:` | Changed |
| `docs:` | Documentation (only major ones) |
| `test:` | Tests (only if significant) |
| `chore:`, build, CI | Infrastructure (only if significant) |
| `BREAKING:` / `feat!:` / `fix!:` | Breaking Changes |

### Step 5: Write the entry

```markdown
## <version>+<short-hash>

Release date: <YYYY-MM-DD>

<one or two sentences: what kind of release it is, and who should upgrade>

### Breaking Changes
- (with the migration path)

### Added
### Changed
### Performance
### Fixed
### Documentation
### Tests
### Infrastructure
```

`<short-hash>` is `git rev-parse --short HEAD` of the published commit. Omit empty sections.

Writing guidelines:

- Focus on what a user of the package notices; group related commits into one bullet.
- Skip trivial commits and anything under `.claude/` (it doesn't ship).
- Start bullets with a verb; use backticks for type and member names.
- Concrete numbers beat adjectives (benchmark deltas from `docs/BENCHMARKS.md` are good).
- On-disk format changes always get their own bullet, stating whether existing stores still open.

### Step 6: Insert the entry

Insert it at the top of `docs/CHANGELOG.md`, after the intro and before the first `## ` entry.

## Your Task

Generate a changelog entry for version: $ARGUMENTS
