# VRCLI

[English](README.md) · [한국어](README.ko.md) · [日本語](README.ja.md)

[![CI](https://github.com/kibalab/VRCLI/actions/workflows/ci.yml/badge.svg)](https://github.com/kibalab/VRCLI/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

VRCLI는 터미널이나 CI 파이프라인에서 VRChat 월드를 빌드하고, 검사하고, 업로드하는 도구입니다. Windows와 Android(Quest), 기존·신규 월드, 메타데이터 전용 갱신, 업로드 준비 상태 검사를 지원합니다.

> VRCLI는 커뮤니티 프로젝트이며 VRChat Inc.와 관련이 없고 공식 지원을 받지 않습니다. 예고 없이 변경될 수 있는 VRChat 서비스와 SDK 동작에 의존합니다. 본인이 업로드할 권한이 있는 월드와 콘텐츠에만 사용하세요.

## 주요 기능

- `deploy`, `meta`, `check`용 전체 화면 대화형 TUI
- GitHub Actions, Jenkins 등 CI를 위한 누적형 `--plain` 출력
- `StandaloneWindows64` 및 `Android` 월드 빌드
- `--blueprint` 또는 씬의 `PipelineManager`를 이용한 기존 월드 배포
- 이름, 썸네일, 인원수, 태그를 지정하는 신규 비공개 월드 생성
- Unity를 실행하지 않는 메타데이터 전용 갱신
- Unity 컴파일 및 VRChat SDK 업로드 차단 요소를 확인하는 dry-run 검사
- 비밀번호, 일회용 인증 코드, 자동 TOTP 인증
- 구조화된 JSON 결과와 고정된 프로세스 종료 코드

## 요구 사항

- Windows
- VCC/VPM으로 관리되는 VRChat Worlds 프로젝트
- VRChat Worlds SDK 3.9.0 이상
- 프로젝트의 `ProjectSettings/ProjectVersion.txt`에 기록된 Unity Editor 버전
- `vpm` 명령으로 실행 가능한 [VPM CLI](https://vcc.docs.vrchat.com/vpm/cli/). 의존성이 이미 해결되어 있다면 `--skip-vpm-resolve` 사용 가능
- 소스 빌드 시 .NET 8 SDK

배포를 실행하는 컴퓨터에서 Unity가 활성화되어 있어야 합니다. CI 러너에도 비대화형으로 사용할 수 있는 유효한 Unity 라이선스 구성이 필요합니다.

## 소스에서 빌드

```powershell
git clone https://github.com/kibalab/VRCLI.git
cd VRCLI
dotnet publish src/VRCLI/VRCLI.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output artifacts/win-x64
```

`artifacts\win-x64\VRCLI.exe`를 실행하거나 해당 디렉터리를 `PATH`에 추가하세요.

## 대화형 사용

로컬 터미널에서 옵션 없이 명령을 실행하면 TUI가 열립니다.

```powershell
vrcli deploy
vrcli meta
vrcli check
```

- `deploy`: 인증, 프로젝트와 씬 선택, 콘텐츠 권리 동의, 빌드, 업로드를 안내합니다.
- `meta`: 인증 세션을 유지하므로 다시 로그인하지 않고 여러 월드를 계속 수정할 수 있습니다.
- `check`: 읽기 전용 사전 검사를 수행하며 번들을 빌드하거나 업로드하지 않습니다.

`Esc`로 취소할 수 있습니다. 실행 중인 작업은 `Ctrl+C`를 두 번 눌러 취소합니다.

## 비대화형 사용

인증정보는 환경변수 또는 CI 시크릿 저장소에 보관하세요.

```powershell
$env:VRCLI_USERNAME = "계정-이름-또는-이메일"
$env:VRCLI_PASSWORD = "계정-비밀번호"
$env:VRCLI_TOTP_SECRET = "BASE32_TOTP_설정_시크릿"
```

### 기존 월드 배포

```powershell
vrcli deploy `
  --project "C:\Unity\MyWorld" `
  --scene "Assets/Scenes/Main.unity" `
  --platform StandaloneWindows64 `
  --yes `
  --plain
```

`--blueprint`를 생략하면 선택한 씬의 `PipelineManager`에 설정된 `wrld_...` Blueprint ID를 사용합니다. `--blueprint wrld_...`를 지정하면 씬의 값보다 우선합니다.

동일한 배포에서 메타데이터도 함께 갱신할 수 있습니다.

```powershell
vrcli deploy --project "C:\Unity\MyWorld" `
  --blueprint "wrld_..." `
  --platform Android `
  --title "변경된 이름" `
  --capacity 40 `
  --recommended-capacity 20 `
  --yes --plain
```

### 신규 비공개 월드 생성

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

새 월드는 비공개 상태로 생성됩니다. VRCLI가 Blueprint ID를 만들며, `--blueprint-output`으로 저장해 다른 플랫폼 빌드에 사용할 수 있습니다.

### 메타데이터만 갱신

```powershell
vrcli meta `
  --blueprint "wrld_..." `
  --title "새 이름" `
  --description "새 설명" `
  --capacity 48 `
  --recommended-capacity 24 `
  --thumbnail "C:\Assets\thumbnail.png" `
  --plain
```

`meta`는 VRChat과 직접 통신하므로 Unity를 실행하지 않습니다. 입력한 필드만 변경됩니다. 기존 태그 목록에 태그를 추가하려면 `--tag`를 반복해서 사용하세요.

### 업로드 준비 상태 검사

```powershell
vrcli check `
  --project "C:\Unity\MyWorld" `
  --scene "Assets/Scenes/Main.unity" `
  --platform Android `
  --plain
```

`check`는 Unity 컴파일 오류·경고, 프로젝트 설정 문제, VRChat SDK 검증 결과, 소유권, 업로드 동의 상태를 보고합니다. 빌드와 업로드는 수행하지 않습니다.

## 로그인과 2차 인증

VRCLI는 먼저 입력된 사용자 이름/이메일과 비밀번호로 로그인을 시도합니다. 이미 유효한 세션을 사용할 수 있으면 2차 인증을 묻지 않습니다.

- 로컬 TUI: VRChat이 요구할 때만 현재 인증 앱 코드 또는 이메일 코드를 입력합니다.
- 일회성 자동화: `--two-factor-code`를 사용하거나 `VRCLI_TWO_FACTOR_CODE`를 설정합니다.
- 무인 CI: 인증 앱 등록 시 받은 영구 Base32 설정 시크릿을 `VRCLI_TOTP_SECRET`에 저장합니다. VRCLI가 현재 코드를 메모리에서 생성합니다.

`VRCLI_TOTP_SECRET`은 6자리 코드가 아니라 설정 시크릿입니다. 비밀번호와 동일하게 취급하세요. 인증정보, TOTP 시크릿 또는 시크릿이 포함된 설정 파일을 커밋하지 마세요. `--password` 값은 셸 기록과 프로세스 목록에 노출될 수 있으므로 CI 시크릿 환경변수를 권장합니다.

## 프로젝트 설정 파일

VRCLI는 기본적으로 현재 디렉터리의 `vrcli.json`을 읽습니다. 다른 파일은 `--config`로 지정할 수 있습니다.

```json
{
  "project": "C:/Unity/MyWorld",
  "scene": "Assets/Scenes/Main.unity",
  "platform": "StandaloneWindows64",
  "login": "계정-이름-또는-이메일",
  "timeout": 3600,
  "plain": true,
  "yes": true
}
```

명령행 옵션, 환경변수, 설정 파일 순서로 우선합니다. 비밀번호와 TOTP 시크릿은 의도적으로 `vrcli.json`에서 지원하지 않습니다.

## Windows와 Android 병렬 배포

Unity는 동일한 프로젝트 디렉터리를 두 프로세스에서 안전하게 열 수 없으므로 별도의 작업공간을 사용하세요. CI 매트릭스를 사용하면 작업마다 독립된 체크아웃을 만들 수 있습니다.

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

각 자체 호스팅 러너에는 Unity, VPM CLI, VRCLI, 해당 플랫폼 모듈과 유효한 Unity 라이선스가 필요합니다. 두 작업을 동시에 실행하려면 사용 가능한 러너가 최소 두 대 필요합니다.

## 결과 JSON

모든 비대화형 작업은 마지막에 JSON 결과를 출력합니다.

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

종료 코드: `0` 성공, `2` 잘못된 인자, `10` 잘못된 프로젝트, `20` 의존성 복원 실패, `30` 인증 실패, `40` 검증/빌드 실패, `50` 업로드 실패, `60` 소유권 실패, `70` 네트워크/API 실패, `124` 시간 초과, `125` 예기치 않은 실패.

전체 옵션은 `vrcli --help`에서 확인할 수 있습니다.

## 라이선스

[MIT](LICENSE)
