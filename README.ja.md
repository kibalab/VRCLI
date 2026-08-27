# VRCLI

[English](README.md) · [한국어](README.ko.md) · [日本語](README.ja.md)

VRCLI は、ターミナルや CI ランナーから VRChat ワールドをビルド、チェック、アップロードします。

> VRChat Inc. とは関係のないコミュニティプロジェクトです。使用権限を持つコンテンツのみアップロードしてください。

## はじめる前に

次の環境が必要です。

- VRChat Worlds SDK 3.9.0 以降を使用する VCC/VPM ワールドプロジェクト
- `ProjectSettings/ProjectVersion.txt` に記録されたバージョンの Unity
- `vpm` コマンドとして利用できる [VPM CLI](https://vcc.docs.vrchat.com/vpm/cli/)
- VRCLI のビルドに必要な .NET 8 SDK
- ワールドをアップロードできる VRChat アカウント

Windows ではテスト済みです。macOS 対応は実験段階です。Apple Silicon と Intel Mac 向けに CLI をビルドできますが、Mac 上でのデプロイ全体はまだ検証していません。Unity の自動検出は現在 Windows のみ対応しているため、`UNITY_EDITOR_PATH` または `--unity` を使用してください。

## インストール

リポジトリをクローンし、スタンドアロン実行ファイルをビルドします。

Windows:

```powershell
git clone https://github.com/kibalab/VRCLI.git
cd VRCLI
dotnet publish src/VRCLI/VRCLI.csproj -c Release -r win-x64 `
  --self-contained true -o artifacts/win-x64
```

Apple Silicon Mac（Intel は `osx-x64`）:

```bash
git clone https://github.com/kibalab/VRCLI.git
cd VRCLI
dotnet publish src/VRCLI/VRCLI.csproj -c Release -r osx-arm64 \
  --self-contained true -o artifacts/osx-arm64

export UNITY_EDITOR_PATH="/Applications/Unity/Hub/Editor/<version>/Unity.app/Contents/MacOS/Unity"
```

Windows では `VRCLI.exe`、macOS では `./VRCLI` を実行します。

## 対話型で使う

ローカルターミナルで目的のコマンドを実行してください。

```text
vrcli deploy
vrcli meta
vrcli check
```

- `deploy`: ワールドをビルドしてアップロードします。
- `meta`: Unity を開かずに既存のメタデータを編集し、ログインセッションを維持します。
- `check`: ビルドやアップロードを行わず、コンパイルと SDK アップロードの問題を報告します。

TUI でプロジェクト、シーン、アカウント、必要な認証コードを入力できます。

## CI またはスクリプトで使う

認証情報は環境変数に保存してください。

```text
VRCLI_USERNAME=アカウント名またはメール
VRCLI_PASSWORD=アカウントパスワード
VRCLI_TOTP_SECRET=BASE32_TOTP_セットアップシークレット
```

`VRCLI_TOTP_SECRET` は 6 桁コードではなく、永続的な認証アプリのセットアップシークレットです。保護された CI シークレットとして保存し、絶対にコミットしないでください。

### 既存ワールドをデプロイ

```powershell
vrcli deploy `
  --project "C:\Unity\MyWorld" `
  --scene "Assets/Scenes/Main.unity" `
  --platform StandaloneWindows64 `
  --yes --plain
```

`--blueprint` を省略すると、シーンの `PipelineManager` に設定された Blueprint を使用します。別の Blueprint は `--blueprint wrld_...` で指定します。Quest には `--platform Android` を使用します。

### 新規ワールドを作成

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

新しいワールドはプライベートで作成されます。`--blueprint-output` は生成された Blueprint を後のビルドで使えるように保存します。

### メタデータのみ更新

```powershell
vrcli meta --blueprint "wrld_..." `
  --title "新しいタイトル" `
  --capacity 48 `
  --recommended-capacity 24 `
  --plain
```

指定したフィールドだけを変更し、Unity は起動しません。

### アップロード前にチェック

```powershell
vrcli check `
  --project "C:\Unity\MyWorld" `
  --scene "Assets/Scenes/Main.unity" `
  --platform Android `
  --plain
```

Unity のコンパイル、プロジェクト設定、SDK 検証、所有権、アップロード同意を確認します。バンドルのビルドやアップロードは行いません。

## Windows と Android の並列デプロイ

別々のプロジェクトワークスペースを使用してください。Unity は 1 つのプロジェクトディレクトリを 2 つのプロセスから安全に開けません。

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
          --yes --plain
```

各ランナーには Unity、VRCLI、VPM CLI、対象プラットフォームモジュール、有効な Unity ライセンスが必要です。2 つのジョブを同時に実行するには、利用可能なランナーが 2 台必要です。

## 結果

非対話コマンドは CI で読み取れる JSON を最後に出力します。

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

すべてのパラメータは `vrcli --help` で確認できます。

## ライセンス

[MIT](LICENSE)
