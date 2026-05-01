# Release workflow and package manifest

This repository ships stable releases and internal test builds through a manually triggered GitHub Actions workflow.

## Manual release workflow

Workflow file: `.github/workflows/manual-release.yml`.

### Trigger and branch rules

- Trigger type: `workflow_dispatch` (manual only).
- Stable releases are allowed from `main` only.
- Internal test builds can run from any branch selected in the workflow dispatch UI.
- Stable tag format: `vX.Y.Z`.
- Test-build tag format: `test/vX.Y.Z-<suffix>`.
- Only stable builds create GitHub Release entries.

### Inputs

- `release_kind`: `test` or `stable`.
  - `test` (default): package a test build **without bumping** the app version.
  - `stable`: publish the next normal semver release.
- `bump`: `patch`, `minor`, or `major` (used when `release_kind=stable` and `version_override` is empty).
- `version_override`: optional explicit app version in `N.N.N` format (example: `0.2.0`).
- `test_build_suffix`: optional identifier used when `release_kind=test` (example: `qa.3`).
  - If omitted, the workflow uses `build.<run_number>.<run_attempt>`.

### Version behavior

Two release modes are now supported:

1. **Stable release mode (`release_kind=stable`)**
   - If `version_override` is set, that explicit version is used.
   - Otherwise, the workflow finds the latest stable `vX.Y.Z` tag and increments it using `bump`.
   - The first stable run with no existing stable tags and default `patch` will produce `v0.0.1`.
   - The workflow creates a `vX.Y.Z` tag and publishes a normal GitHub Release entry with the ZIP and installer assets.
   - Use this mode only when the build should appear on the repository Releases page.

2. **Test build mode (`release_kind=test`)**
   - Test builds can be run from feature branches before the branch is merged to `main`.
   - The app version stays on the selected base version:
     - `version_override` if provided, otherwise latest stable version (or `0.0.0` if none exist).
   - The workflow appends a test suffix to make the tag unique:
     - `test/vX.Y.Z-<suffix>`
   - The app window displays a pre-release style informational version:
     - `vX.Y.Z-test.<suffix>`
   - The workflow uploads the app ZIP and installer EXE as separate workflow run artifacts instead of creating a GitHub Release entry.
   - The workflow creates the `test/...` tag at the branch commit that was built.
   - This supports multiple packaged test builds between stable versions (for example, several builds between `v0.0.9` and `v0.0.10`).

### Downloading internal test builds

Test builds do not appear on the GitHub Releases page.

To download a test build:

1. Open the completed **Manual Release** workflow run.
2. Download either or both artifacts from the run summary:
   - `virtual-dof-matrix-test-vX.Y.Z-<suffix>-win-x64-zip`
   - `virtual-dof-matrix-test-vX.Y.Z-<suffix>-installer`
3. Extract the downloaded artifact locally; each artifact contains its file at the top level.

Test build artifacts are retained for 30 days.

### Common failure: `tag already exists`

- The workflow intentionally fails if the computed tag already exists in the repository.
- Check existing tags with:
  - `git tag --list "v*"`
  - `git tag --list "test/*"`
- If you want to publish again, choose a new version:
  - for stable releases, set `version_override` to an unused `N.N.N` (or choose a bump that advances), or
  - for test builds, keep the same `version_override` and provide a new `test_build_suffix`.

### Build and package flow

1. `dotnet publish` builds `VirtualDofMatrix.App` for `win-x64` self-contained release output.
2. The workflow verifies the published app executable exists and generates an effective manifest by prepending an executable mapping to the base `release-manifest.json`.
3. The staging folder is initialized empty.
4. The effective manifest is treated as authoritative; only mapped files/directories are copied.
5. The final zip is created as:
   - stable: `virtual-dof-matrix-vX.Y.Z-win-x64.zip`
   - test: `virtual-dof-matrix-test-vX.Y.Z-<suffix>-win-x64.zip`
6. The workflow creates and pushes the resolved tag.
7. Stable builds publish a GitHub Release with auto-generated notes; test builds upload separate app ZIP and installer artifacts only.

## Release manifest

Manifest file: `release-manifest.json`.

Use the `mappings` array to define files to copy into the release zip staging folder.

### Supported mapping types

- `file`: copy one specific file.
- `directory`: copy a directory recursively, optionally filtered by include/exclude patterns.
- `glob`: copy files matching a wildcard pattern.

Any missing source path or empty required match fails the release with a specific error.

### Manifest schema examples

```json
{
  "mappings": [
    {
      "type": "file",
      "from": "DirectOutput-master/bin/x86/Release/DirectOutput.dll",
      "to": "DOF/x86/DirectOutput.dll"
    },
    {
      "type": "directory",
      "from": "examples",
      "to": "examples",
      "include": ["**/*"],
      "exclude": ["**/*.tmp"]
    },
    {
      "type": "glob",
      "from": "docs/**/*.md",
      "to": "docs"
    }
  ]
}
```

### Mapping field reference

- `type` (required): `file`, `directory`, or `glob`.
- `from` (required): source file path, source directory path, or glob pattern relative to repo root.
  - For `file` and `directory` mappings, `from` can also be relative to the publish output folder passed to `package-release.ps1`.
- `to` (required): destination path inside release zip staging root.
- `include` (optional, `directory` only): array of wildcard filters.
- `exclude` (optional, `directory` only): array of wildcard filters.

## Current manifest in this repo

Current `release-manifest.json` includes:

- `DOF` -> `DOF`
- `docs/instructions.html` -> `./instructions.html`

At release time, the workflow generates `artifacts/release-manifest.effective.json` that prepends:

- `VirtualDofMatrix.App.exe` (resolved from publish output) -> `VirtualDofMatrix.App.exe`

Add or update mappings as release packaging requirements evolve.
