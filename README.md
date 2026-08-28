# VRCLI

[English](README.md) · [한국어](README.ko.md) · [日本語](README.ja.md)

```text
 ___ ___  ______  ______  _____    _______
|   |   ||   __ \|      ||     |_ |_     _|
|   |   ||      <|   ---||       | _|   |_
 \_____/ |___|__||______||_______||_______|
                                   by KIBA_
```

VRCLI builds, checks, and uploads VRChat worlds from a terminal or CI runner.

> Community project; not affiliated with VRChat Inc. Only upload content you have the right to use.

## Before you start

You need:

- A VCC/VPM world project with VRChat Worlds SDK 3.9.0 or newer
- The Unity version recorded in `ProjectSettings/ProjectVersion.txt`
- [VPM CLI](https://vcc.docs.vrchat.com/vpm/cli/) available as `vpm`
- .NET 8 SDK to build VRCLI
- A VRChat account that can upload worlds

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

## Use it interactively

Run one of these commands in a local terminal:

```text
vrcli deploy
vrcli meta
vrcli check
```

- `deploy` builds and uploads a world.
- `meta` edits existing metadata without opening Unity and keeps the login session open.
- `check` reports compilation and SDK upload blockers without building or uploading.

The TUI asks for the project, scene, account, and any missing verification code. On Windows, verified sessions are stored in Windows Credential Manager; later runs let you choose a saved account or sign in with another one.

## Use it in CI or scripts

Keep credentials in environment variables:

```text
VRCLI_USERNAME=account-name-or-email
VRCLI_PASSWORD=account-password
VRCLI_TOTP_SECRET=BASE32_TOTP_SETUP_SECRET
```

`VRCLI_TOTP_SECRET` is the permanent authenticator setup secret, not a six-digit code. Store it as a protected CI secret and never commit it.

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

Use separate project workspaces. Unity cannot safely open one project directory in two processes.

```yaml
jobs:
  deploy:
    strategy:
      matrix:
        platform: [StandaloneWindows64, Android]
    runs-on: self-hosted
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

Each runner needs Unity, VRCLI, VPM CLI, the target platform module, and a valid Unity license. Two simultaneous jobs require two available runners.

## Result

Use `--json` to write exactly one result object to stdout while diagnostics go to stderr:

```json
{
  "Success": true,
  "ExitCode": 0,
  "Blueprint": "wrld_...",
  "Platform": "StandaloneWindows64",
  "Stage": "complete",
  "Message": "World build and upload completed."
}
```

Run `vrcli --help` for every available parameter.

## License

[MIT](LICENSE)
