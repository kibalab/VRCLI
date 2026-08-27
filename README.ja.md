# VRCLI

[English](README.md) · [한국어](README.ko.md) · [日本語](README.ja.md)

[![CI](https://github.com/kibalab/VRCLI/actions/workflows/ci.yml/badge.svg)](https://github.com/kibalab/VRCLI/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

VRCLI は、ターミナルや CI パイプラインから VRChat ワールドをビルド、検証、アップロードするためのツールです。Windows と Android（Quest）、既存・新規ワールド、メタデータのみの更新、アップロード準備状況のチェックに対応しています。

> VRCLI はコミュニティプロジェクトであり、VRChat Inc. との関連や公式な承認はありません。予告なく変更される可能性がある VRChat のサービスおよび SDK の動作に依存しています。アップロードする権限を持つワールドとコンテンツにのみ使用してください。

## 主な機能

- `deploy`、`meta`、`check` 用のフルスクリーン対話型 TUI
- GitHub Actions、Jenkins などの CI に適した追記型 `--plain` 出力
- `StandaloneWindows64` および `Android` のワールドビルド
- `--blueprint` またはシーンの `PipelineManager` を使った既存ワールドのデプロイ
- タイトル、サムネイル、定員、タグを指定した新規プライベートワールドの作成
- Unity を起動しないメタデータのみの更新
- Unity コンパイルと VRChat SDK のアップロード阻害要因を確認する dry-run チェック
- パスワード、ワンタイムコード、自動 TOTP 認証
- 構造化された JSON 結果と安定したプロセス終了コード

## 必要環境

- Windows
- VCC/VPM で管理された VRChat Worlds プロジェクト
- VRChat Worlds SDK 3.9.0 以降
- プロジェクトの `ProjectSettings/ProjectVersion.txt` に記録された Unity Editor バージョン
- `vpm` コマンドとして利用できる [VPM CLI](https://vcc.docs.vrchat.com/vpm/cli/)。依存関係が解決済みの場合は `--skip-vpm-resolve` を使用可能
- ソースからビルドする場合は .NET 8 SDK

デプロイを実行するマシンで Unity がアクティベートされている必要があります。CI ランナーにも非対話で利用できる有効な Unity ライセンス設定が必要です。

## ソースからビルド

```powershell
git clone https://github.com/kibalab/VRCLI.git
cd VRCLI
dotnet publish src/VRCLI/VRCLI.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output artifacts/win-x64
```

`artifacts\win-x64\VRCLI.exe` を実行するか、そのディレクトリを `PATH` に追加してください。

## 対話型で使う

ローカルターミナルでオプションなしのコマンドを実行すると TUI が開きます。

```powershell
vrcli deploy
vrcli meta
vrcli check
```

- `deploy`: 認証、プロジェクトとシーンの選択、コンテンツ権利の同意、ビルド、アップロードを案内します。
- `meta`: 認証セッションを維持するため、再ログインせずに複数のワールドを続けて編集できます。
- `check`: 読み取り専用の事前チェックを行い、バンドルのビルドやアップロードは行いません。

`Esc` でキャンセルできます。実行中の処理は `Ctrl+C` を 2 回押すとキャンセルされます。

## 非対話で使う

認証情報は環境変数または CI のシークレットストアに保存してください。

```powershell
$env:VRCLI_USERNAME = "アカウント名またはメール"
$env:VRCLI_PASSWORD = "アカウントパスワード"
$env:VRCLI_TOTP_SECRET = "BASE32_TOTP_セットアップシークレット"
```

### 既存ワールドをデプロイ

```powershell
vrcli deploy `
  --project "C:\Unity\MyWorld" `
  --scene "Assets/Scenes/Main.unity" `
  --platform StandaloneWindows64 `
  --yes `
  --plain
```

`--blueprint` を省略すると、選択したシーンの `PipelineManager` に設定された `wrld_...` Blueprint ID を使用します。`--blueprint wrld_...` を指定するとシーンの値より優先されます。

同じデプロイでメタデータも更新できます。

```powershell
vrcli deploy --project "C:\Unity\MyWorld" `
  --blueprint "wrld_..." `
  --platform Android `
  --title "更新後のタイトル" `
  --capacity 40 `
  --recommended-capacity 20 `
  --yes --plain
```

### 新規プライベートワールドを作成

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

新しいワールドはプライベートで作成されます。VRCLI が Blueprint ID を生成し、`--blueprint-output` で保存して別プラットフォームのビルドに利用できます。

### メタデータのみ更新

```powershell
vrcli meta `
  --blueprint "wrld_..." `
  --title "新しいタイトル" `
  --description "新しい説明" `
  --capacity 48 `
  --recommended-capacity 24 `
  --thumbnail "C:\Assets\thumbnail.png" `
  --plain
```

`meta` は VRChat と直接通信するため Unity を起動しません。指定したフィールドだけが変更されます。既存のタグ一覧に追加する場合は `--tag` を繰り返し指定してください。

### アップロード準備状況をチェック

```powershell
vrcli check `
  --project "C:\Unity\MyWorld" `
  --scene "Assets/Scenes/Main.unity" `
  --platform Android `
  --plain
```

`check` は Unity のコンパイルエラーと警告、プロジェクト設定の問題、VRChat SDK の検証結果、所有権、アップロード同意の状態を報告します。ビルドやアップロードは行いません。

## ログインと二要素認証

VRCLI は最初に指定されたユーザー名/メールとパスワードでログインを試みます。有効な保存済みセッションを利用できる場合、二要素認証は要求しません。

- ローカル TUI: VRChat が要求した場合のみ、現在の認証アプリコードまたはメールコードを入力します。
- 一回限りの自動化: `--two-factor-code` を指定するか、`VRCLI_TWO_FACTOR_CODE` を設定します。
- 無人 CI: 認証アプリ登録時の永続的な Base32 セットアップシークレットを `VRCLI_TOTP_SECRET` に保存します。VRCLI が現在のコードをメモリ上で生成します。

`VRCLI_TOTP_SECRET` は 6 桁のコードではなくセットアップシークレットです。パスワードと同様に扱ってください。認証情報、TOTP シークレット、またはシークレットを含む設定ファイルをコミットしないでください。`--password` の値はシェル履歴やプロセス一覧に表示される可能性があるため、CI のシークレット環境変数を推奨します。

## プロジェクト設定ファイル

VRCLI はデフォルトで現在のディレクトリにある `vrcli.json` を読み込みます。別のファイルは `--config` で指定できます。

```json
{
  "project": "C:/Unity/MyWorld",
  "scene": "Assets/Scenes/Main.unity",
  "platform": "StandaloneWindows64",
  "login": "アカウント名またはメール",
  "timeout": 3600,
  "plain": true,
  "yes": true
}
```

コマンドラインオプション、環境変数、設定ファイルの順に優先されます。パスワードと TOTP シークレットは意図的に `vrcli.json` ではサポートされません。

## Windows と Android の並列デプロイ

Unity は同じプロジェクトディレクトリを 2 つのプロセスから安全に開けないため、別々のワークスペースを使用してください。CI マトリックスならジョブごとに独立したチェックアウトを用意できます。

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

各セルフホステッドランナーには Unity、VPM CLI、VRCLI、対象プラットフォームモジュール、有効な Unity ライセンスが必要です。2 つのジョブを同時に実行するには、少なくとも 2 台の利用可能なランナーが必要です。

## 結果 JSON

すべての非対話操作は最後に JSON 結果を出力します。

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

終了コード: `0` 成功、`2` 不正な引数、`10` 不正なプロジェクト、`20` 依存関係の復元失敗、`30` 認証失敗、`40` 検証/ビルド失敗、`50` アップロード失敗、`60` 所有権失敗、`70` ネットワーク/API 失敗、`124` タイムアウト、`125` 予期しない失敗。

全オプションは `vrcli --help` で確認できます。

## ライセンス

[MIT](LICENSE)
