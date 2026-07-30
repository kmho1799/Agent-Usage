# Agent Usage

Codex와 Claude의 남은 사용량과 초기화 시간을 한눈에 보여주는 Windows 데스크톱 위젯입니다.

## 주요 기능

- Codex 주간 남은 사용량 표시
- Claude 5시간 세션 및 주간 남은 사용량 표시
- 초기화까지 남은 시간과 날짜 표시
- 5분마다 자동 갱신 및 수동 새로고침
- 항상 위, 트레이 숨김·복원 지원

## 설치

저장소 전체가 필요하지 않다면 아래 설치 파일 하나만 다운로드해 실행하세요.

[AgentUsage-Setup-1.0.0.0.exe 다운로드](https://github.com/kmho1799/Agent-Usage/releases/latest/download/AgentUsage-Setup-1.0.0.0.exe)

.NET을 별도로 설치할 필요는 없습니다.

Codex와 Claude 사용량을 확인하려면 Codex CLI와 Claude Code가 각각 설치되고 로그인되어 있어야 합니다.

## 소스에서 실행

- Windows 10 또는 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- 로그인된 Codex CLI
- 로그인된 Claude Code

```powershell
dotnet run --project .\AgentUsage.csproj
```

## 설치 파일 만들기

[Inno Setup](https://jrsoftware.org/isdl.php)을 설치한 뒤 다음 명령을 실행합니다.

```powershell
.\installer\build.ps1
```

설치 파일은 `installer\Output`에 생성됩니다.
이 폴더에서는 `AgentUsage-Setup-*.exe`만 Git에 포함됩니다.

> 설치 파일에 코드 서명을 하지 않았으므로 Windows에서 알 수 없는 게시자 경고가 표시될 수 있습니다.

## 인증과 개인정보

- Codex 인증은 로컬 Codex CLI를 통해 사용하며 앱에 저장하지 않습니다.
- Claude 인증은 사용자 폴더의 `.claude\.credentials.json`을 읽기만 하며 복사하거나 수정하지 않습니다.
- 토큰, 이메일, 사용자 이름 같은 개인정보는 저장소에 포함하지 않습니다.
- 로컬 개발 도구 기록, 디자인 산출물, 실제 계정 화면 캡처는 `.gitignore`로 제외합니다.

> Claude와 Codex의 내부 사용량 응답 형식이 변경되면 조회 기능이 동작하지 않을 수 있습니다.
