# VRCLI

[English](README.md) · [한국어](README.ko.md) · [日本語](README.ja.md)

```text
 ___ ___  ______  ______  _____    _______
|   |   ||   __ \|      ||     |_ |_     _|
|   |   ||      <|   ---||       | _|   |_
 \_____/ |___|__||______||_______||_______|
                                   by KIBA_
```

VRCLI는 터미널이나 CI 러너에서 VRChat 월드와 아바타를 빌드하고, 검사하고, 업로드합니다. `deploy`와 `check`는 프로젝트의 VPM 의존성으로 콘텐츠 유형을 자동 판별합니다.

> 의존성 안내: VRCLI는 독립적인 콘텐츠 빌드 또는 업로드 구현체가 아닌 자동화 계층입니다. 대상 프로젝트에 설치된 호환 Unity Editor와 해당 VRChat Worlds 또는 Avatars SDK에 전적으로 의존하며, 이 제품들을 대체하거나 재배포하지 않습니다.
>
> VRChat Inc.와 관련 없는 커뮤니티 프로젝트입니다. 본인이 사용할 권한이 있는 콘텐츠만 업로드하세요.

## 시작하기 전에

다음 환경이 필요합니다.

- VRChat Worlds SDK 또는 Avatars SDK 3.9.0 이상을 사용하는 VCC/VPM 프로젝트
- `ProjectSettings/ProjectVersion.txt`에 기록된 버전의 Unity
- `vpm` 명령으로 실행할 수 있는 [VPM CLI](https://vcc.docs.vrchat.com/vpm/cli/)
- VRCLI 빌드에 필요한 .NET 8 SDK
- 월드 또는 아바타를 업로드할 수 있는 VRChat 계정

Windows는 테스트를 완료했습니다. macOS 지원은 실험 단계입니다. Apple Silicon과 Intel Mac용 CLI 빌드는 가능하지만 Mac에서 실제 배포 전체 과정은 아직 검증하지 않았습니다. 자동 Unity 탐색은 현재 Windows만 지원하므로 `UNITY_EDITOR_PATH` 또는 `--unity`를 사용해야 합니다.

## 설치

저장소를 복제하고 독립 실행 파일을 빌드합니다.

Windows:

```powershell
git clone https://github.com/kibalab/VRCLI.git
cd VRCLI
dotnet publish src/VRCLI/VRCLI.csproj -c Release -r win-x64 `
  --self-contained true -o artifacts/win-x64
```

Apple Silicon Mac(Intel은 `osx-x64`):

```bash
git clone https://github.com/kibalab/VRCLI.git
cd VRCLI
dotnet publish src/VRCLI/VRCLI.csproj -c Release -r osx-arm64 \
  --self-contained true -o artifacts/osx-arm64

export UNITY_EDITOR_PATH="/Applications/Unity/Hub/Editor/<version>/Unity.app/Contents/MacOS/Unity"
```

Windows에서는 `VRCLI.exe`, macOS에서는 `./VRCLI`를 실행합니다.

## 릴리스

`Directory.Build.props`의 `VersionPrefix`를 변경하고 커밋한 뒤, 일치하는 `vX.Y.Z` 태그를 푸시합니다.

```bash
git tag -a v0.18.0 -m "VRCLI v0.18.0"
git push origin v0.18.0
```

GitHub Actions가 태그 시점의 커밋을 테스트하고, 자체 포함된 Windows 및 macOS 압축 파일과 SHA-256 체크섬을 GitHub Release에 게시합니다. 태그와 `VersionPrefix`가 다르면 릴리스를 만들지 않고 실패합니다.

## 대화형으로 사용

로컬 터미널에서 원하는 명령을 실행하세요.

```text
vrcli deploy
vrcli meta
vrcli check
```

- `deploy`: 프로젝트 유형을 판별한 뒤 월드 또는 아바타를 빌드하고 업로드합니다.
- `meta`: Unity를 열지 않고 기존 메타데이터를 수정하며 로그인 세션을 유지합니다.
- `check`: 프로젝트 유형을 판별하고 빌드나 업로드 없이 컴파일 및 SDK 업로드 문제를 보고합니다.

TUI에서 프로젝트, 씬, 계정과 필요한 인증 코드를 입력할 수 있습니다. Windows에서는 인증된 세션을 Windows 자격 증명 관리자에 저장하며, 다음 실행부터 저장된 계정을 선택하거나 새 계정으로 로그인할 수 있습니다.

## CI 또는 스크립트에서 사용

인증정보는 환경변수에 저장하세요.

```text
VRCLI_USERNAME=계정-이름-또는-이메일
VRCLI_PASSWORD=계정-비밀번호
VRCLI_TOTP_SECRET=BASE32_TOTP_설정_시크릿
```

`VRCLI_TOTP_SECRET`은 6자리 코드가 아니라 영구 인증 앱 설정 시크릿입니다. 보호된 CI 시크릿으로 저장하고 절대 커밋하지 마세요.

CI 러너에는 저장된 로컬 세션이 필요하지 않습니다. VRCLI가 아이디와 비밀번호로 로그인하고 TOTP 요청을 감지한 뒤, 현재 코드를 메모리에서 생성하여 사용자 입력 없이 계속 진행합니다. CI에서는 `--interactive-two-factor`를 사용하지 마세요.

로그인 정보를 파라미터로 직접 입력할 수도 있습니다.

```powershell
vrcli deploy `
  --login "계정-이름-또는-이메일" `
  --password "계정-비밀번호" `
  --interactive-two-factor
```

일회용 인증 코드는 `--two-factor-code <현재-코드>`와 함께 `--two-factor-method totp`, `emailOtp` 또는 `otp`를 지정합니다. 명령행 비밀번호는 셸 기록이나 프로세스 목록에 남을 수 있으므로 CI에서는 환경변수가 더 안전합니다.

### 기존 월드 배포

```powershell
vrcli deploy `
  --project "C:\Unity\MyWorld" `
  --scene "Assets/Scenes/Main.unity" `
  --platform StandaloneWindows64 `
  --yes --plain
```

`--blueprint`를 생략하면 씬의 `PipelineManager`에 설정된 Blueprint를 사용합니다. 다른 Blueprint는 `--blueprint wrld_...`로 지정합니다. Quest는 `--platform Android`를 사용합니다.

### 신규 월드 생성

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

새 월드는 비공개로 생성됩니다. `--blueprint-output`은 생성된 Blueprint를 이후 빌드에 사용할 수 있도록 저장합니다.

### 아바타 배포

```powershell
vrcli deploy `
  --project "C:\Unity\MyAvatar" `
  --scene "Assets/Avatar.unity" `
  --target "Avatars/KIBA_" `
  --platform StandaloneWindows64 `
  --yes --plain
```

VRCLI가 Avatars SDK를 판별하고 씬의 아바타를 선택합니다. `--target`은 아바타 GameObject의 정확한 Unity Hierarchy 경로이고, `--blueprint avtr_...`는 서버에 존재하는 아바타를 식별합니다. 둘 중 하나로 여러 아바타 중 대상을 고를 수 있으며, 함께 쓰면 같은 아바타를 가리켜야 합니다. 둘 다 생략했을 때 씬에 아바타가 하나면 자동 선택하고, 여러 개면 대화형 실행에서는 선택 목록을 표시합니다. `--plain`과 `--json`에서는 안전하게 중단하고 `Targets` 후보 목록을 반환합니다. 선택한 아바타에 Blueprint가 없으면 비공개 신규 아바타로 생성하며 `--title`, `--thumbnail`, `--yes`가 필요합니다.

### 메타데이터만 갱신

```powershell
vrcli meta --blueprint "wrld_..." `
  --title "새 이름" `
  --capacity 48 `
  --recommended-capacity 24 `
  --remove-tag "author_tag_old" `
  --plain
```

입력한 필드만 변경하며 Unity는 실행되지 않습니다. 이미 동일한 상태라면 서버 요청 없이 성공으로 종료합니다.

### 업로드 전 검사

```powershell
vrcli check `
  --project "C:\Unity\MyWorld" `
  --scene "Assets/Scenes/Main.unity" `
  --platform Android `
  --plain
```

Unity 컴파일, 프로젝트 설정, SDK 검증, 소유권과 업로드 동의를 확인합니다. 번들을 빌드하거나 업로드하지 않습니다.

## Windows와 Android 병렬 배포

Jenkins 컨트롤러나 GitHub Actions 서비스는 Linux에서 실행해도 되지만, `deploy`와 `check` 작업은 Windows 러너에서 실행해야 합니다. VRChat SDK는 Linux를 공식 지원하지 않으므로 Linux 월드 또는 아바타 업로드는 보장할 수 없습니다. Unity를 실행하지 않는 `meta`는 Linux에서도 사용할 수 있습니다. Unity는 하나의 프로젝트 디렉터리를 두 프로세스에서 안전하게 열 수 없으므로 별도의 작업공간을 사용하세요.

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

각 Windows 러너에는 Unity, VRCLI, VPM CLI, 대상 플랫폼 모듈과 유효한 Unity 라이선스가 필요합니다. 두 작업을 동시에 실행하려면 `vrchat-unity` 라벨을 가진 러너가 두 대 필요합니다.

## 결과

`--json`을 사용하면 stdout에는 결과 객체 하나만 출력되고 진단 로그는 stderr로 분리됩니다.

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

아바타 선택이 모호하면 비대화형 실패 결과의 `Targets`에 각 후보의 `Name`, Hierarchy `Selector`, 선택적 `Blueprint`가 포함됩니다. 원하는 `Selector`를 `--target`으로 지정하세요.

전체 파라미터는 `vrcli --help`에서 확인할 수 있습니다.

## 라이선스

[MIT](LICENSE)
