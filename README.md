# VRCLI

[English](README.md) · [한국어](README.ko.md) · [日本語](README.ja.md)

[![CI](https://github.com/kibalab/VRCLI/actions/workflows/ci.yml/badge.svg)](https://github.com/kibalab/VRCLI/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

VRCLI builds, validates, and uploads VRChat worlds from a terminal or CI pipeline. It supports Windows and Android (Quest) builds, existing and new worlds, metadata-only updates, and upload-readiness checks.

> VRCLI is a community project and is not affiliated with or endorsed by VRChat Inc. It relies on VRChat services and SDK behavior that may change without notice. Use it only with worlds and content you are authorized to upload.

## Features

- Full-screen interactive TUI for `deploy`, `meta`, and `check`
- Append-only `--plain` output for GitHub Actions, Jenkins, and other CI systems
- `StandaloneWindows64` and `Android` world builds
- Existing-world deployment using either `--blueprint` or the scene's `PipelineManager`
- New private-world creation with title, thumbnail, capacity, and tags
- Metadata-only updates without opening Unity
- Dry-run checks for Unity compilation and VRChat SDK upload blockers
- Password, one-time-code, and automatic TOTP authentication
- Structured JSON results and stable process exit codes

## Requirements

- Windows
- A VRChat Worlds project managed by VCC/VPM
- VRChat Worlds SDK 3.9.0 or newer
- The Unity Editor version recorded in the project's `ProjectSettings/ProjectVersion.txt`
- [VPM CLI](https://vcc.docs.vrchat.com/vpm/cli/) available as `vpm`, unless dependencies are already resolved and `--skip-vpm-resolve` is used
- .NET 8 SDK to build VRCLI from source

Unity must be activated on the machine running deployment. CI runners also need a valid non-interactive Unity license setup.

## Build from source

```powershell
git clone https://github.com/kibalab/VRCLI.git
cd VRCLI
dotnet publish src/VRCLI/VRCLI.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output artifacts/win-x64
```

Run `artifacts\win-x64\VRCLI.exe`, or add that directory to `PATH`.

## Interactive use

Run a command without options in a local terminal to open its TUI:

```powershell
vrcli deploy
vrcli meta
vrcli check
```

- `deploy` guides you through authentication, project and scene selection, ownership consent, build, and upload.
- `meta` keeps the authenticated session open so multiple worlds can be edited without signing in again.
- `check` performs a read-only preflight and never builds or uploads a bundle.

Press `Esc` to cancel. Press `Ctrl+C` twice to cancel an active operation.

## Non-interactive use

Store credentials in environment variables or your CI secret store:

```powershell
$env:VRCLI_USERNAME = "account-name-or-email"
$env:VRCLI_PASSWORD = "account-password"
$env:VRCLI_TOTP_SECRET = "BASE32_TOTP_SETUP_SECRET"
```

### Deploy an existing world

```powershell
vrcli deploy `
  --project "C:\Unity\MyWorld" `
  --scene "Assets/Scenes/Main.unity" `
  --platform StandaloneWindows64 `
  --yes `
  --plain
```

When `--blueprint` is omitted, VRCLI uses the `wrld_...` Blueprint ID assigned to the selected scene's `PipelineManager`. An explicit `--blueprint wrld_...` overrides the scene value.

Metadata options may be included in the same deployment:

```powershell
vrcli deploy --project "C:\Unity\MyWorld" `
  --blueprint "wrld_..." `
  --platform Android `
  --title "Updated title" `
  --capacity 40 `
  --recommended-capacity 20 `
  --yes --plain
```

### Create a new private world

```powershell
vrcli deploy `
  --project "C:\Unity\MyWorld" `
  --scene "Assets/Scenes/Main.unity" `
  --new `
  --title "My World" `
  --thumbnail "C:\Assets\thumbnail.png" `
  --capacity 32 `
  --recommended-capacity 16 `
  --platform StandaloneWindows64 `
  --blueprint-output "C:\Build\blueprint.txt" `
  --yes --plain
```

The new world is created as private. VRCLI generates its Blueprint ID and can save it with `--blueprint-output` for later platform builds.

### Update metadata only

```powershell
vrcli meta `
  --blueprint "wrld_..." `
  --title "New title" `
  --description "New description" `
  --capacity 48 `
  --recommended-capacity 24 `
  --thumbnail "C:\Assets\thumbnail.png" `
  --plain
```

`meta` talks directly to VRChat and does not start Unity. Only supplied fields are changed. Repeat `--tag` to merge tags into the existing tag list.

### Check upload readiness

```powershell
vrcli check `
  --project "C:\Unity\MyWorld" `
  --scene "Assets/Scenes/Main.unity" `
  --platform Android `
  --plain
```

`check` reports Unity compiler errors and warnings, project configuration problems, VRChat SDK validation results, ownership, and upload-consent status. It does not build or upload.

## Authentication and two-factor verification

VRCLI first tries the supplied username/email and password. If the account already has a valid saved session, no two-factor prompt is shown.

- Local TUI: enter a current authenticator or email code only when VRChat requests it.
- One-off automation: pass `--two-factor-code` or set `VRCLI_TWO_FACTOR_CODE`.
- Unattended CI: store the permanent Base32 authenticator setup secret as `VRCLI_TOTP_SECRET`. VRCLI generates the current code in memory.

`VRCLI_TOTP_SECRET` is the setup secret, not the six-digit code. Treat it like a password. Never commit credentials, TOTP secrets, or `vrcli.json` files containing secrets. Prefer CI secret variables over `--password`, because command-line values may appear in shell history and process listings.

## Project configuration

VRCLI reads `vrcli.json` from the current directory by default, or another file supplied with `--config`:

```json
{
  "project": "C:/Unity/MyWorld",
  "scene": "Assets/Scenes/Main.unity",
  "platform": "StandaloneWindows64",
  "login": "account-name-or-email",
  "timeout": 3600,
  "plain": true,
  "yes": true
}
```

Command-line options take precedence over environment variables, which take precedence over configuration values. Passwords and TOTP secrets are intentionally not supported in `vrcli.json`.

## Parallel Windows and Android deployment

Use separate workspaces because Unity cannot safely open the same project directory in two processes. A CI matrix naturally provides one checkout per job:

```yaml
name: Deploy VRChat world

on:
  push:
    tags: ["world-v*"]

jobs:
  deploy:
    strategy:
      matrix:
        platform: [StandaloneWindows64, Android]
    runs-on: self-hosted
    steps:
      - uses: actions/checkout@v4
      - name: Deploy
        shell: pwsh
        env:
          VRCLI_USERNAME: ${{ secrets.VRCHAT_USERNAME }}
          VRCLI_PASSWORD: ${{ secrets.VRCHAT_PASSWORD }}
          VRCLI_TOTP_SECRET: ${{ secrets.VRCHAT_TOTP_SECRET }}
        run: >-
          C:\Tools\VRCLI\VRCLI.exe deploy
          --project .
          --scene Assets/Scenes/Main.unity
          --platform ${{ matrix.platform }}
          --yes --plain
```

Each self-hosted runner must have Unity, VPM CLI, VRCLI, the required platform module, and a valid Unity license. Running both matrix jobs at the same time requires at least two available runners.

## Result JSON

Every non-interactive operation prints a final JSON result:

```json
{
  "Success": true,
  "ExitCode": 0,
  "Blueprint": "wrld_...",
  "Created": false,
  "Platform": "StandaloneWindows64",
  "Stage": "complete",
  "Message": "World build and upload completed."
}
```

Exit codes: `0` success, `2` invalid arguments, `10` invalid project, `20` dependency restore failure, `30` authentication failure, `40` validation/build failure, `50` upload failure, `60` ownership failure, `70` network/API failure, `124` timeout, and `125` unexpected failure.

Run `vrcli --help` for the complete option list.

## License

[MIT](LICENSE)
