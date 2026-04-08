# Release workflow and package manifest

This repository ships test builds through a manually triggered GitHub Actions workflow.

## Manual release workflow

Workflow file: `.github/workflows/manual-release.yml`.

### Trigger and branch rules

- Trigger type: `workflow_dispatch` (manual only).
- Allowed branch: `main` only.
- Stable tag format: `vX.Y.Z`.
- Test-build tag format: `vX.Y.Z-test.<suffix>`.

### Inputs

- `release_kind`: `test` or `stable`.
  - `test` (default): package a test build **without bumping** the app version.
  - `stable`: publish the next normal semver release.
- `bump`: `patch`, `minor`, or `major` (used when `release_kind=stable` and `version_override` is empty).
- `version_override`: optional explicit app version in `N.N.N` format (example: `0.2.0`).
- `test_build_suffix`: optional identifier used when `release_kind=test` (example: `qa.3`).
  - If omitted, the workflow uses `build.<run_number>.<run_attempt>`.
- `is_prerelease`: boolean toggle.
  - `true`: publish as a pre-release.
  - `false`: publish as a stable release.

### Version behavior

Two release modes are now supported:

1. **Stable release mode (`release_kind=stable`)**
   - If `version_override` is set, that explicit version is used.
   - Otherwise, the workflow finds the latest stable `vX.Y.Z` tag and increments it using `bump`.
   - The first stable run with no existing stable tags and default `patch` will produce `v0.0.1`.
   - Use this mode when you want to advance the app version.

2. **Test build mode (`release_kind=test`)**
   - The app version stays on the selected base version:
     - `version_override` if provided, otherwise latest stable version (or `0.0.0` if none exist).
   - The workflow appends a pre-release style test suffix to make the tag unique:
     - `vX.Y.Z-test.<suffix>`
   - This supports multiple packaged test builds between stable versions (for example, several builds between `v0.0.9` and `v0.0.10`).

### Common failure: `tag already exists`

- The workflow intentionally fails if the computed tag already exists in the repository.
- Check existing tags with:
  - `git tag --list "v*"`
- If you want to publish again, choose a new version:
  - for stable releases, set `version_override` to an unused `N.N.N` (or choose a bump that advances), or
  - for test builds, keep the same `version_override` and provide a new `test_build_suffix`.

### Build and package flow

1. `dotnet publish` builds `VirtualDofMatrix.App` for `win-x64` self-contained release output.
2. The workflow verifies the published app executable exists and generates an effective manifest by prepending an executable mapping to the base `release-manifest.json`.
3. The staging folder is initialized empty.
4. The effective manifest is treated as authoritative; only mapped files/directories are copied.
5. The final zip is created as:
   - `virtual-dof-matrix-<resolved-tag>-win-x64.zip`
6. The workflow creates and pushes the release tag, then publishes a GitHub Release with auto-generated notes.

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
- `examples/settings.sample.json` -> `examples/settings.sample.json`
- `docs/instructions.html` -> `docs/instructions.html`

At release time, the workflow generates `artifacts/release-manifest.effective.json` that prepends:

- `VirtualDofMatrix.App.exe` (resolved from publish output) -> `VirtualDofMatrix.App.exe`

Add or update mappings as release packaging requirements evolve.
