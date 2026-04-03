# Release workflow and package manifest

This repository ships test builds through a manually triggered GitHub Actions workflow.

## Manual release workflow

Workflow file: `.github/workflows/manual-release.yml`.

### Trigger and branch rules

- Trigger type: `workflow_dispatch` (manual only).
- Allowed branch: `main` only.
- Tag format: `vX.Y.Z`.

### Inputs

- `bump`: `patch`, `minor`, or `major`.
- `version_override`: optional explicit version in `N.N.N` format (example: `0.2.0`).
- `is_prerelease`: boolean toggle.
  - `true`: publish as a pre-release.
  - `false`: publish as a stable release.

### Version behavior

- If `version_override` is set, that value is used.
- Otherwise, the workflow finds the latest `v*` tag and increments it using `bump`.
- The first run with no existing tags and default `patch` will produce `v0.0.1`.
  - To start from `v0.1.0`, run with `version_override: 0.1.0` for the first release.

### Common failure: `tag already exists`

- The workflow intentionally fails if the computed tag already exists in the repository.
- Check existing tags with:
  - `git tag --list "v*"`
- If you want to publish again, choose a new version:
  - `version_override` set to an unused value like `0.1.1`, or
  - leave `version_override` blank and choose a bump that advances past the latest existing tag.

### Build and package flow

1. `dotnet publish` builds `VirtualDofMatrix.App` for `win-x64` self-contained release output.
2. The workflow verifies the published app executable exists and generates an effective manifest by prepending an executable mapping to the base `release-manifest.json`.
3. The staging folder is initialized empty.
4. The effective manifest is treated as authoritative; only mapped files/directories are copied.
5. The final zip is created as:
   - `virtual-dof-matrix-vX.Y.Z-win-x64.zip`
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
