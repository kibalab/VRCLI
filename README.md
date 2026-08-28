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

> Dependency notice: VRCLI is an automation layer, not a standalone content build or upload implementation. It depends entirely on a compatible Unity Editor and the matching VRChat Worlds or Avatars SDK installed in the target project; it does not replace or redistribute either product.
>
> Community project; not affiliated with VRChat Inc. Only upload content you have the right to use.

## Before you start

You need:

- A VCC/VPM project with either VRChat Worlds SDK or Avatars SDK 3.9.0 or newer
- The Unity version recorded in `ProjectSettings/ProjectVersion.txt`
- [VPM CLI](https://vcc.docs.vrchat.com/vpm/cli/) available as `vpm`
- .NET 8 SDK to build VRCLI
- A VRChat account that can upload worlds or avatars

Windows is tested. macOS support is experimental: the CLI builds for Apple silicon and Intel Macs, but has not yet completed an end-to-end deployment test on a Mac. Set `UNITY_EDITOR_PATH` or use `--unity` because automatic Unity discovery currently covers Windows only.

## Install

Clone the repository and publish a standalone executable.

Windows:

```powershell
git clone https://github.com/kibalab/VRCLI.git
cd VRCLI
dotnet publish src/VRCLI/VRCLI.csproj -c Release -r win-x64 `
  --self-contained true -o artifacts/win-x64
```

macOS Apple silicon (`osx-x64` for Intel):

```bash
git clone https://github.com/kibalab/VRCLI.git
cd VRCLI
dotnet publish src/VRCLI/VRCLI.csproj -c Release -r osx-arm64 \
  --self-contained true -o artifacts/osx-arm64

export UNITY_EDITOR_PATH="/Applications/Unity/Hub/Editor/<version>/Unity.app/Contents/MacOS/Unity"
```

Run `VRCLI.exe` on Windows or `./VRCLI` on macOS.

## Release

Set `VersionPrefix` in `Directory.Build.props`, commit it, then push the matching `vX.Y.Z` tag:

```bash
git tag -a v0.18.0 -m "VRCLI v0.18.0"
git push origin v0.18.0
```

GitHub Actions tests the tagged commit and publishes self-contained Windows and macOS archives with SHA-256 checksums to a GitHub Release. A tag that does not match `VersionPrefix` fails without creating a release.

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

The TUI asks for the project, scene, account, and any missing verification code. On Windows, verified sessions are stored in Windows Credential Manager; later runs let you choose a saved account or sign in with another one.

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
      - uses: actions/checkout@v4
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
  "Message": "World build and upload completed."
}
```

When avatar selection is ambiguous, a non-interactive failure includes `Targets` entries with each candidate's `Name`, Hierarchy `Selector`, and optional `Blueprint`; pass one `Selector` back through `--target`.

Run `vrcli --help` for every available parameter.

## License

[MIT](LICENSE)
