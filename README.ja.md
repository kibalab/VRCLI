# VRCLI

[English](README.md) · [한국어](README.ko.md) · [日本語](README.ja.md)

```text
 ___ ___  ______  ______  _____    _______
|   |   ||   __ \|      ||     |_ |_     _|
|   |   ||      <|   ---||       | _|   |_
 \_____/ |___|__||______||_______||_______|
                                   by KIBA_
```

VRCLI は、ターミナルや CI ランナーから VRChat ワールドとアバターをビルド、チェック、アップロードします。`deploy` と `check` はプロジェクトの VPM 依存関係からコンテンツ種別を自動判定します。

## 対応するアップロード

ワールドとアバターの両方で同じ `vrcli deploy` コマンドを使用します。Unity プロジェクトを指定すると、インストール済みの Worlds SDK または Avatars SDK を自動判定するため、ワールド／アバター専用のコマンドを使い分ける必要はありません。

- **ワールド:** 既存ワールドのアップロードまたは新しいプライベートワールドの作成に対応し、`StandaloneWindows64` と `Android` をサポートします。
- **アバター:** 既存アバターのアップロードまたは新しいプライベートアバターの作成に対応し、`StandaloneWindows64` と `Android` をサポートします。1 つのシーンに複数のアバターがある場合は、Hierarchy パスまたは Blueprint ID で対象を選択できます。

> 依存関係について: VRCLI は独立したコンテンツビルド／アップロード実装ではなく、自動化レイヤーです。対象プロジェクトにインストールされた互換性のある Unity Editor と、対応する VRChat Worlds または Avatars SDK に全面的に依存し、これらの製品を代替または再配布するものではありません。
>
> VRChat Inc. とは関係のないコミュニティプロジェクトです。使用権限を持つコンテンツのみアップロードしてください。

## はじめる前に

次の環境が必要です。

- VRChat Worlds SDK または Avatars SDK 3.9.0 以降を使用する VCC/VPM プロジェクト
- `ProjectSettings/ProjectVersion.txt` に記録されたバージョンの Unity
- `vpm` コマンドとして利用できる [VPM CLI](https://vcc.docs.vrchat.com/vpm/cli/)
- VRCLI をソースからビルドする場合のみ必要な .NET 8 SDK
- ワールドまたはアバターをアップロードできる VRChat アカウント

Windows はエンドツーエンドで検証済みです。macOS は Apple Silicon／Intel ビルドと Unity Hub の自動検出に対応していますが、Mac 上での実アップロード全体はまだ未検証です。Unity が標準の Hub ディレクトリ外にある場合だけ `UNITY_EDITOR_PATH` または `--unity` を使用してください。

## インストール

[GitHub Releases](https://github.com/kibalab/VRCLI/releases/latest) から最新版をダウンロードできます。.NET は不要です。

### Windows

```powershell
irm https://github.com/kibalab/VRCLI/releases/latest/download/install-vrcli.ps1 -OutFile install-vrcli.ps1
powershell -ExecutionPolicy Bypass -File .\install-vrcli.ps1
```

- セットアップ: `VRCLI-x.y.z-win-x64-setup.exe`
- ポータブル: `VRCLI-x.y.z-win-x64.zip`
- カタログ公開後の WinGet: `winget install --id kibalab.VRCLI --exact`

### macOS

```bash
curl -fLO https://github.com/kibalab/VRCLI/releases/latest/download/install-vrcli.sh
sh install-vrcli.sh
```

- インストーラー: `VRCLI-x.y.z-osx-arm64.pkg` または `VRCLI-x.y.z-osx-x64.pkg`
- ポータブル: アーキテクチャに合う `.tar.gz`
- Tap 公開後の Homebrew: `brew install kibalab/tap/vrcli`

現在の macOS パッケージは未署名・未公証です。

## 対話型で使う

ローカルターミナルで目的のコマンドを実行してください。

```text
vrcli deploy
vrcli meta
vrcli check
```

- `deploy`: プロジェクト種別を判定し、ワールドまたはアバターをビルドしてアップロードします。
- `meta`: Unity を開かずに既存のメタデータを編集し、ログインセッションを維持します。
- `check`: プロジェクト種別を判定し、ビルドやアップロードを行わずコンパイルと SDK アップロードの問題を報告します。

TUI でプロジェクト、シーン、アカウント、必要な認証コードを入力できます。認証済みセッションは Windows 資格情報マネージャーまたは macOS Keychain に保存され、次回から保存済みアカウントまたは新しいアカウントを選択できます。

通常の CLI でも、`--password` なしで `--login <保存済みアカウント>` を指定すると保存セッションを利用できます。`vrcli auth list`、`vrcli auth logout <account>`、`vrcli auth logout --all` で確認・削除できます。`--password` を指定した場合は常に新規ログインします。

## CI またはスクリプトで使う

認証情報は環境変数に保存してください。

```text
VRCLI_USERNAME=アカウント名またはメール
VRCLI_PASSWORD=アカウントパスワード
VRCLI_TOTP_SECRET=BASE32_TOTP_セットアップシークレット
```

`VRCLI_TOTP_SECRET` は 6 桁コードではなく、永続的な認証アプリのセットアップシークレットです。保護された CI シークレットとして保存し、絶対にコミットしないでください。

CI ランナーに保存済みのローカルセッションは不要です。VRCLI はユーザー名とパスワードでログインし、TOTP 要求を検出すると現在のコードをメモリ内で生成して、ユーザー入力なしで処理を続行します。CI では `--interactive-two-factor` を使用しないでください。

ログイン情報はパラメータとして直接指定することもできます。

```powershell
vrcli deploy `
  --login "アカウント名またはメール" `
  --password "アカウントパスワード" `
  --interactive-two-factor
```

ワンタイムコードは `--two-factor-code <現在のコード>` とともに `--two-factor-method totp`、`emailOtp`、または `otp` を指定します。コマンドラインのパスワードはシェル履歴やプロセス一覧に残る可能性があるため、CI では環境変数の方が安全です。

ビルド成功後にアップロードまたはサーバー検証が失敗した場合、JSON の `Artifact.RecoveryFile` に復旧マニフェストが返ります。通常のログイン設定と `--resume <recovery.json>` を指定すると、再ビルドせず保存済みバンドルを再送できます。対象プラットフォームのバージョンをサーバーで確認した後にだけ復旧ファイルを削除します。

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

### アバターをデプロイ

```powershell
vrcli deploy `
  --project "C:\Unity\MyAvatar" `
  --scene "Assets/Avatar.unity" `
  --target "Avatars/KIBA_" `
  --platform StandaloneWindows64 `
  --yes --plain
```

VRCLI は Avatars SDK を判定し、シーンのアバターを選択します。`--target` はアバター GameObject の正確な Unity Hierarchy パス、`--blueprint avtr_...` はサーバー上の既存アバターを識別します。どちらか一方で複数のアバターから対象を選べ、両方を指定する場合は同じアバターを示す必要があります。どちらも省略し、シーン内に 1 体だけなら自動選択します。複数ある場合、対話実行では選択画面を表示し、`--plain` と `--json` では安全に停止して `Targets` 候補一覧を返します。選択したアバターに Blueprint がなければ新しいプライベートアバターとして作成され、`--title`、`--thumbnail`、`--yes` が必要です。

### メタデータのみ更新

```powershell
vrcli meta --blueprint "wrld_..." `
  --title "新しいタイトル" `
  --capacity 48 `
  --recommended-capacity 24 `
  --remove-tag "author_tag_old" `
  --plain
```

指定したフィールドだけを変更し、Unity は起動しません。すでに同じ状態の場合はサーバー更新を行わず成功で終了します。

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

Jenkins コントローラーや GitHub Actions サービスは Linux 上で実行できますが、`deploy` と `check` ジョブには Windows ランナーを使用してください。VRChat SDK は Linux を正式にサポートしていないため、Linux でのワールドまたはアバターアップロードは保証できません。Unity を起動しない `meta` は Linux でも使用できます。Unity は 1 つのプロジェクトディレクトリを 2 つのプロセスから安全に開けないため、別々のワークスペースを使用してください。

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

各 Windows ランナーには Unity、VRCLI、VPM CLI、対象プラットフォームモジュール、有効な Unity ライセンスが必要です。2 つのジョブを同時に実行するには、`vrchat-unity` ラベルを持つランナーが 2 台必要です。

## 結果

`--json` を使用すると stdout には結果オブジェクトを 1 つだけ出力し、診断ログは stderr に分離します。

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
  "VrcliVersion": "0.19.0",
  "UnityVersion": "2022.3.22f1",
  "SdkVersion": "3.10.1",
  "DurationMs": 120000,
  "Artifact": {
    "Size": 12345678,
    "Sha256": "..."
  }
}
```

デプロイ結果にはフェーズ別時間と以前／サーバー上のコンテンツバージョンも含まれ、CI ログだけでビルド・アップロード・検証対象を確認できます。

アバター選択が曖昧な場合、非対話実行の失敗結果には各候補の `Name`、Hierarchy `Selector`、任意の `Blueprint` を含む `Targets` が返ります。選ぶ `Selector` を `--target` に指定してください。

すべてのパラメータは `vrcli --help` で確認できます。

## ライセンス

[MIT](LICENSE)
