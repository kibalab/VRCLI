# VRCLI

[English](README.md) · [한국어](README.ko.md) · [日本語](README.ja.md)

```text
 ___ ___  ______  ______  _____    _______
|   |   ||   __ \|      ||     |_ |_     _|
|   |   ||      <|   ---||       | _|   |_
 \_____/ |___|__||______||_______||_______|
                                   by KIBA_
```

VRCLI builds, checks, and uploads VRChat worlds and avatars from a terminal or CI runner. `deploy` and `check` automatically detect the content type from the project's VPM dependencies.

## Supported uploads

The same `vrcli deploy` command works with both content types. Point VRCLI at a Unity project and it automatically uses the installed Worlds or Avatars SDK—no separate world/avatar command is required.

- **Worlds:** upload an existing world or create a new private world, for `StandaloneWindows64` or `Android`.
- **Avatars:** upload an existing avatar or create a new private avatar, for `StandaloneWindows64` or `Android`. When a scene contains several avatars, select one by its Hierarchy path or Blueprint ID.

> Dependency notice: VRCLI is an automation layer, not a standalone content build or upload implementation. It depends entirely on a compatible Unity Editor and the matching VRChat Worlds or Avatars SDK installed in the target project; it does not replace or redistribute either product.
>
> Community project; not affiliated with VRChat Inc. Only upload content you have the right to use.

## Before you start

You need:

- A VCC/VPM project with either VRChat Worlds SDK or Avatars SDK 3.9.0 or newer
- The Unity version recorded in `ProjectSettings/ProjectVersion.txt`
- [VPM CLI](https://vcc.docs.vrchat.com/vpm/cli/) available as `vpm`
- .NET 8 SDK only if you want to build VRCLI from source
- A VRChat account that can upload worlds or avatars

Windows is tested end to end. macOS builds and Unity Hub discovery support Apple silicon and Intel Macs, but a complete Mac upload has not yet been verified. Use `UNITY_EDITOR_PATH` or `--unity` only when Unity is installed outside the standard Hub directories.

## Install

Download the latest version from [GitHub Releases](https://github.com/kibalab/VRCLI/releases/latest). .NET is not required.

### Windows

```powershell
irm https://github.com/kibalab/VRCLI/releases/latest/download/install-vrcli.ps1 -OutFile install-vrcli.ps1
powershell -ExecutionPolicy Bypass -File .\install-vrcli.ps1
```

- Setup: `VRCLI-x.y.z-win-x64-setup.exe`
- Portable: `VRCLI-x.y.z-win-x64.zip`
- WinGet, pending Microsoft catalog approval: `winget install --id kibalab.VRCLI --exact`

### macOS

```bash
brew install kibalab/tap/vrcli
```

Without Homebrew:

```bash
curl -fLO https://github.com/kibalab/VRCLI/releases/latest/download/install-vrcli.sh
sh install-vrcli.sh
```

- Installer: `VRCLI-x.y.z-osx-arm64.pkg` or `VRCLI-x.y.z-osx-x64.pkg`
- Portable: matching `.tar.gz`

macOS packages are currently unsigned and not notarized.

## Use it interactively

Run one of these commands in a local terminal:

```text
vrcli deploy
vrcli meta
vrcli check
```

- `deploy` detects the project type, then builds and uploads a world or avatar.
- `meta` edits existing metadata without opening Unity and keeps the login session open.
- `check` detects the project type and reports compilation and SDK upload blockers without building or uploading.

The TUI asks for the project, scene, account, and any missing verification code. Verified sessions are stored in Windows Credential Manager or macOS Keychain; later runs let you choose a saved account or sign in with another one.

Saved sessions also work in plain CLI mode. Pass `--login <saved-account>` without `--password`, or inspect and remove sessions with `vrcli auth list`, `vrcli auth logout <account>`, and `vrcli auth logout --all`. Supplying `--password` always performs a fresh login.

## Use it in CI or scripts

Keep credentials in environment variables:

```text
VRCLI_USERNAME=account-name-or-email
VRCLI_PASSWORD=account-password
VRCLI_TOTP_SECRET=BASE32_TOTP_SETUP_SECRET
```

`VRCLI_TOTP_SECRET` is the permanent authenticator setup secret, not a six-digit code. Store it as a protected CI secret and never commit it.

A CI runner does not need a saved local session. VRCLI signs in with the username and password, detects the TOTP challenge, generates the current code in memory, and continues unattended. Do not use `--interactive-two-factor` in CI.

Credentials can also be passed directly:

```powershell
vrcli deploy `
  --login "account-name-or-email" `
  --password "account-password" `
  --interactive-two-factor
```

Use `--two-factor-code <current-code>` together with `--two-factor-method totp`, `emailOtp`, or `otp`. Command-line passwords may remain in shell history or process listings, so environment variables are safer for CI.

If a build succeeds but upload or server verification fails, the JSON result includes `Artifact.RecoveryFile`. Retry the preserved bundle without rebuilding by passing `--resume <recovery.json>` together with the normal login options. Recovery files are removed only after the server confirms the expected platform version.

### Deploy an existing world

```powershell
vrcli deploy `
  --project "C:\Unity\MyWorld" `
  --scene "Assets/Scenes/Main.unity" `
  --platform StandaloneWindows64 `
  --yes --plain
```

If `--blueprint` is omitted, VRCLI uses the Blueprint assigned to the scene's `PipelineManager`. Use `--blueprint wrld_...` to override it. Use `--platform Android` for Quest.

### Create a new world

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

The world is created as private. `--blueprint-output` saves its generated Blueprint for later builds.

### Deploy an avatar

```powershell
vrcli deploy `
  --project "C:\Unity\MyAvatar" `
  --scene "Assets/Avatar.unity" `
  --target "Avatars/KIBA_" `
  --platform StandaloneWindows64 `
  --yes --plain
```

VRCLI detects the Avatars SDK and selects the scene avatar. `--target` is the exact Unity Hierarchy path of the avatar GameObject; `--blueprint avtr_...` identifies an existing server avatar. Either option can select among several avatars, and using both requires them to identify the same avatar. If both are omitted, one scene avatar is selected automatically; an interactive run shows a picker when several are found, while `--plain` and `--json` stop and report a `Targets` candidate list. If the selected avatar has no Blueprint, VRCLI creates a private avatar and requires `--title`, `--thumbnail`, and `--yes`.

### Update metadata only

```powershell
vrcli meta --blueprint "wrld_..." `
  --title "New title" `
  --capacity 48 `
  --recommended-capacity 24 `
  --remove-tag "author_tag_old" `
  --plain
```

Only supplied fields are changed. Unity is not started, and an already up-to-date world exits successfully without sending an update.

### Check before uploading

```powershell
vrcli check `
  --project "C:\Unity\MyWorld" `
  --scene "Assets/Scenes/Main.unity" `
  --platform Android `
  --plain
```

This checks Unity compilation, project configuration, SDK validation, ownership, and upload consent. It never builds or uploads a bundle.

## Deploy Windows and Android in parallel

The Jenkins controller or GitHub Actions service may run on Linux, but `deploy` and `check` jobs should use Windows runners. The VRChat SDK does not officially support Linux, so Linux world or avatar uploads cannot be guaranteed; `meta` does not start Unity and can run there. Use separate project workspaces because Unity cannot safely open one project directory in two processes.

```yaml
jobs:
  deploy:
    strategy:
      matrix:
        platform: [StandaloneWindows64, Android]
    runs-on: [self-hosted, windows, x64, vrchat-unity]
    steps:
      - uses: actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803 # v6
      - shell: pwsh
        env:
          VRCLI_USERNAME: ${{ secrets.VRCHAT_USERNAME }}
          VRCLI_PASSWORD: ${{ secrets.VRCHAT_PASSWORD }}
          VRCLI_TOTP_SECRET: ${{ secrets.VRCHAT_TOTP_SECRET }}
        run: >-
          C:\Tools\VRCLI\VRCLI.exe deploy
          --project .
          --scene Assets/Scenes/Main.unity
          --platform ${{ matrix.platform }}
          --yes --json
```

Each Windows runner needs Unity, VRCLI, VPM CLI, the target platform module, and a valid Unity license. Two simultaneous jobs require two available runners with the `vrchat-unity` label.

## Result

Use `--json` to write exactly one result object to stdout while diagnostics go to stderr:

```json
{
  "Success": true,
  "ExitCode": 0,
  "Blueprint": "wrld_...",
  "ContentType": "World",
  "Platform": "StandaloneWindows64",
  "Stage": "complete",
  "Message": "World build and upload completed.",
  "Verified": true,
  "VrcliVersion": "0.20.0",
  "UnityVersion": "2022.3.22f1",
  "SdkVersion": "3.10.1",
  "DurationMs": 120000,
  "Artifact": {
    "Size": 12345678,
    "Sha256": "..."
  }
}
```

Deployment results also include per-phase timings and previous/server content versions, making a CI log sufficient to identify what was built, uploaded, and verified.

When avatar selection is ambiguous, a non-interactive failure includes `Targets` entries with each candidate's `Name`, Hierarchy `Selector`, and optional `Blueprint`; pass one `Selector` back through `--target`.

Run `vrcli --help` for every available parameter.

## License

[MIT](LICENSE)
