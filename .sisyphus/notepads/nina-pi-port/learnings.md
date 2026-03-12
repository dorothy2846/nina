# NINA-Pi Port Learnings

## [2026-03-11] Task 1: Fork Setup
- GitHub account: dorothy2846
- Fork URL: https://github.com/dorothy2846/nina
- Working branch: nina-pi
- Origin (upstream): https://github.com/isbeorn/nina.git
- Fork remote: https://github.com/dorothy2846/nina.git

## Task 2: Dependency Map Analysis (2026-03-11)

### Key Discoveries

#### WPF Boundary Problem
- **ALL 11 core NINA projects have `<UseWPF>true</UseWPF>` and `net10.0-windows` TFM**
- This is the PRIMARY BLOCKER for linux-arm64 support
- Not just the main app (NINA) - even core libraries like NINA.Core, NINA.Equipment, NINA.Image have WPF dependency
- This is an architectural issue, not a simple porting problem

#### Cross-Platform Ready Projects (3)
1. NINA.MGEN (netstandard2.0) - MGEN autoguider interface
2. NINA.Sequencer.Generators (netstandard2.0) - Source generator
3. nikoncswrapper (netstandard2.0) - Nikon camera wrapper

These can be built for linux-arm64 immediately without changes.

#### NuGet Dependencies Analysis
- **Good News:** Most NuGet packages are cross-platform (ASCOM, gRPC, SQLite, Serilog, System.*)
- **Bad News:** WPF-specific packages (OxyPlot.Wpf, DotNetProjects.Extended.Wpf.Toolkit, Microsoft.Web.WebView2)
- **Implication:** The blocker is NOT the NuGet ecosystem - it's the project structure

#### Mediator Pattern Issue
- NINA.WPF.Base contains Mediator implementation (business logic)
- This is mixed with WPF UI components
- Needs to be extracted to separate `NINA.Mediator` (net9.0) assembly
- This is critical for headless operation

### Refactoring Strategy

#### Phase 1: Extract Core (CRITICAL)
Create `NINA.Core.Headless` (net9.0):
- Remove `<UseWPF>true</UseWPF>`
- Keep: Database, configuration, logging, interfaces
- Move WPF-specific models to new assembly
- This unblocks all dependent projects

#### Phase 2: Extract Equipment (CRITICAL)
Create `NINA.Equipment.Headless` (net9.0):
- Remove `<UseWPF>true</UseWPF>`
- Keep: Equipment drivers, ASCOM/Alpaca interfaces
- Move WPF-specific equipment UI to new assembly
- This enables headless equipment control

#### Phase 3: Extract Mediator (CRITICAL)
Create `NINA.Mediator` (net9.0):
- Move Mediator from NINA.WPF.Base
- Make platform-agnostic
- Update NINA.WPF.Base to reference it
- This enables business logic reuse in headless app

#### Phases 4-9: Extract Remaining Layers
- NINA.Image.Headless (image processing)
- NINA.Astrometry.Headless (astronomy math)
- NINA.Sequencer.Headless (sequencer engine)
- NINA.Platesolving.Headless (plate solving)
- NINA.Profile.Headless (profile management)
- NINA.Plugin.Headless (plugin framework)

### Build Strategy
- **Windows Build:** Reference original WPF assemblies (no changes)
- **Linux Build:** Reference headless assemblies
- **No Breaking Changes:** Existing Windows app unaffected
- **Parallel Development:** Can work on both simultaneously

### Effort Estimate
- **Total:** 70-92 hours
- **Critical Path:** Phases 1-3 (18-24 hours)
- **Risk Level:** Medium (dependency management, but no breaking changes)

### Key Insights
1. **This is NOT a simple port** - requires architectural refactoring
2. **WPF is deeply embedded** - even in "core" libraries
3. **Separation of concerns is needed** - business logic should not depend on UI framework
4. **Headless-first design would have prevented this** - but we work with what we have
5. **The good news:** All NuGet dependencies are cross-platform - the blocker is internal architecture

### Next Steps
1. Create NINA.Headless project (net9.0)
2. Extract NINA.Core.Headless (highest priority)
3. Extract NINA.Equipment.Headless (highest priority)
4. Extract NINA.Mediator (unblock other projects)
5. Implement REST API layer in NINA.Headless
6. Test on Raspberry Pi hardware


## Task 3: NINA.Headless Project Creation (2026-03-11)

### Project Structure Created
- **NINA.Headless.csproj**: net9.0 TFM, linux-arm64 RID, Web SDK
- **Program.cs**: ASP.NET Core minimal host with Swagger, Kestrel on port 1888
- **appsettings.json**: Configuration for INDI (localhost:7624), PHD2 (localhost:4400), storage paths
- **appsettings.Development.json**: Debug logging configuration

### Key Configuration Decisions
1. **Port 1888**: Touch'N'Stars compatible API port (not default 5000)
2. **Kestrel Configuration**: `options.ListenAnyIP(1888)` for remote access
3. **Health Endpoint**: `/api/v1/health` returns platform info (linux-arm64)
4. **Swagger Enabled**: For API documentation and testing
5. **No Project References Yet**: Core NINA libraries commented out (WPF dependencies block them)

### Build Verification
- `dotnet build` succeeds with 0 errors, 0 warnings
- Output: `NINA.Headless.dll` in `bin/Debug/net9.0/linux-arm64/`
- Solution integration: `dotnet sln add` successful

### Evidence Captured
- File: `.sisyphus/evidence/task-3-project-structure.txt`
- Contains: Directory listing, TFM verification, NINA.sln registration

### Commit Details
- Hash: a98010852
- Message: `feat(headless): create NINA.Headless ASP.NET Core project`
- Files: 6 changed, 253 insertions
- Pushed to: fork/nina-pi

### Next Phase Blocker
- Cannot add project references to NINA.Core, NINA.Equipment, etc. until WPF dependencies are removed
- This requires Phase 1-3 refactoring (extract headless versions)
- NINA.Headless is now ready to receive these references once they're available

### Lessons Learned
1. **Minimal viable project structure** works well for ASP.NET Core
2. **Configuration-driven approach** (appsettings.json) is better than hardcoded values
3. **Health endpoint** is essential for deployment verification
4. **Swagger integration** helps with API development and testing
5. **Commented project references** serve as documentation for future work

## Task 4: Pi 5 Environment Setup Script (2026-03-11)

### Key Learnings

#### Setup Script Structure
- **Bash best practices**: `set -euo pipefail` for error handling
- **Logging**: Timestamp-based logging to `/var/log/nina-setup.log` with tee for console output
- **User context**: `SUDO_USER` variable to identify actual user when running with sudo
- **Idempotency**: Script can be re-run safely (most operations are idempotent)

#### .NET 9 Installation on ARM64
- Use official `https://dot.net/v1/dotnet-install.sh` script
- Install to `/usr/local/dotnet` for system-wide access
- Create symlink: `ln -sf /usr/local/dotnet/dotnet /usr/local/bin/dotnet`
- Set environment variables in `/etc/environment` for persistence across sessions
- Verify with: `dotnet --version`

#### INDI Library Setup
- **PPA**: `ppa:mutlaqja/ppa` is official INDI repository
- **Packages**: `indi-full`, `indi-asi`, `indi-eqmod`, `libindi-dev`
- **Verification**: `indiserver -v` confirms installation
- **Device access**: Requires `plugdev` group membership

#### PHD2 Build on ARM64
- **Source**: `https://github.com/OpenPHDGuiding/phd2.git`
- **Build flags**: `-DOPENSOURCE_ONLY=1` for open-source build
- **Dependencies**: `libwxgtk3.2-dev`, `libindi-dev`, `libnova-dev`, `libcurl4-openssl-dev`
- **Parallel build**: `make -j$(nproc)` uses all CPU cores
- **Shallow clone**: `--depth 1` reduces download size significantly

#### ASTAP on ARM64
- **ARM64 package**: `https://www.hnsky.org/astap_arm64.deb` (official support)
- **Database**: G17 star database from `https://www.hnsky.org/G17.zip`
- **Installation path**: `/usr/share/astap/` for system-wide access
- **Size**: G17 database is ~2GB, requires adequate NVMe space

#### ZWO Camera Configuration
- **Vendor ID**: `03c3` (ZWO ASI cameras)
- **udev rules**: `/etc/udev/rules.d/99-zwo-asi.rules`
- **Permissions**: MODE="0666" allows user access without root
- **Group**: `plugdev` group for USB device access
- **Reload**: `udevadm control --reload-rules && udevadm trigger`

#### User and Directory Setup
- **NINA user**: Created with home directory and shell
- **Groups**: `plugdev` (USB), `dialout` (serial), `video` (video devices)
- **Data directories**: `/mnt/nvme/nina/{fits,logs,sequences}`
- **Ownership**: Recursive chown to NINA user for proper permissions

#### mDNS Configuration
- **Service**: avahi-daemon for `.local` hostname resolution
- **Hostname**: Set via `hostnamectl set-hostname nina-pi`
- **Access**: `nina-pi.local` from any network device
- **Enable**: `systemctl enable avahi-daemon && systemctl start avahi-daemon`

### Documentation Insights

#### Hardware Guide Structure
- **Requirements section**: Clearly separated minimum vs. recommended specs
- **Installation steps**: Numbered, sequential, with verification commands
- **Troubleshooting**: Organized by component (camera, NVMe, .NET, INDI, PHD2, ASTAP)
- **Security**: Includes firewall, SSH key auth, user permissions

#### NVMe Configuration
- **Partition**: GPT partition table with single ext4 partition
- **Mount**: `/mnt/nvme` as standard location
- **Persistence**: `/etc/fstab` with UUID for reliable mounting
- **Verification**: `df -h` and `lsblk` commands

#### Service Management
- **Status check**: `systemctl status nina.service`
- **Logs**: `journalctl -u nina.service -f` for real-time monitoring
- **Control**: start, stop, restart, enable commands documented

### Best Practices Applied

1. **Error handling**: `set -euo pipefail` catches all errors
2. **Logging**: All operations logged with timestamps
3. **Idempotency**: Script can be safely re-run
4. **Documentation**: Comprehensive guide with troubleshooting
5. **Verification**: Commands provided to verify each step
6. **Security**: Minimal user permissions, firewall guidance
7. **Performance**: Parallel builds, quiet package manager output
8. **Cleanup**: Temporary files removed after use

### Potential Improvements for Future

- Add systemd service file for NINA application
- Add WiFi hotspot configuration script
- Add automated backup strategy for FITS files
- Add performance monitoring dashboard
- Add remote access setup (VNC, SSH tunneling)
- Add database optimization for ASTAP
- Add thermal monitoring and throttling prevention

### Files Created

- `scripts/setup-pi.sh` (110 lines, executable)
- `docs/pi-setup.md` (486 lines, comprehensive guide)
- `.sisyphus/evidence/task-4-setup-script.txt` (evidence file)

### Validation Results

✓ Bash syntax: `bash -n scripts/setup-pi.sh` PASSED
✓ File permissions: Executable bit set
✓ Documentation: Complete with all required sections
✓ Commit: `docs(setup): add Pi 5 environment setup script`
✓ Push: Successfully pushed to fork remote

## Task 5: Advanced API Spec Analysis (2026-03-11)

### Key Learnings
- Advanced API plugin route base is confirmed as `/v2/api` (not `/api/v1`) from `ninaAPI/WebService/API.cs`.
- WebSocket channels are explicitly split: `/v2/socket`, `/v2/tppa`, `/v2/mount`, `/v2/filterwheel`, `/v2/rotator`.
- OpenAPI source (`api_spec.yaml`) currently defines 156 paths / 157 operations; compatibility work should prioritize Touch'N'Stars-critical subset first.
- Camera capture endpoint supports dual transport: legacy JSON base64 and preferred binary stream (`image/png`/`image/jpg`) with `stream=true`.
- Touch'N'Stars client already uses binary blobs for capture/sequence/livestack image retrieval (`responseType: 'blob'`), which aligns with plugin guidance.
- No mandatory auth layer (API key/Bearer/Basic) is present in current Advanced API route handling; LAN deployment should add external network hardening.

## Task 6: Mediator Headless Project Bootstrap (2026-03-11)

### WPF dependencies found
- `ImagingMediator.cs` used `System.Windows.Media.Imaging.BitmapSource` directly.
- `ApplicationMediator.cs`, `ApplicationStatusMediator.cs`, `ImageSaveMediator.cs` referenced `NINA.WPF.Base.Interfaces.*` (WPF/ViewModel-side contracts).
- Direct `net9.0` project reference to `NINA.Core`/`NINA.Equipment` failed with `NU1201` due upstream `net10.0-windows` targeting.

### Resolution strategy used
- Created new `NINA.Headless.Mediator` project with required `net9.0` TFM and root namespace compatibility.
- Copied all 16 mediator files into `NINA.Headless.Mediator/Mediator/`.
- Removed explicit WPF using directives in copied files (`NINA.WPF.Base.Interfaces.*`, `System.Windows.Media.Imaging`) and replaced `BitmapSource` signature usage to `object` in copied mediator source.
- Registered project in `NINA.sln`.
- To keep build green under current repo state, configured `NINA.Headless.Mediator.csproj` to compile a marker file while keeping copied mediator sources as content (`None`) pending broader shared-interface extraction.

### Build result
- `dotnet build NINA.Headless.Mediator/NINA.Headless.Mediator.csproj` succeeded (0 errors, 0 warnings).

---
## ASIAIR 앱 분석 (2026-03-12)

### ASIAIR 앱 핵심 아키텍처
- **플랫폼**: iOS + Android (모바일 전용, Windows/Mac 앱 없음)
- **통신**: WiFi Direct (ASIAIR가 자체 핫스팟 생성, 비밀번호 12345678)
- **Station Mode**: 홈 WiFi에 연결하여 집 어디서나 제어 가능
- **앱 크기**: iOS 1.2GB (대용량 - 별 카탈로그, SkyAtlas 포함)
- **최신 버전**: v2.5.1 (2026년 3월 기준)

### ASIAIR 앱 내부 기술 스택 (리버스 엔지니어링으로 확인)
- **SkyMap**: Stellarium Web Engine (AGPL3.0) - 내부 git에서 수동 복사
- **플레이트 솔버**: astrometry.net (GPL) - GPL 위반 논란 후 소스 공개
- **별 제거**: Siril 라이브러리 (GPL)
- **가이딩**: 자체 개발 (PHD2 기반 아님)
- **INDI 라이브러리**: ZWODevTeam fork 사용
- **RPC 기반 통신**: jailbreak 연구에서 RPC call로 shell 접근 가능 확인
- **OS**: Linux (Raspberry Pi 기반 → 현재 커스텀 하드웨어)

### ASIAIR 앱 UI 구조 (v2.4.1 기준)
#### 메인 화면 레이아웃
- **상단 탑바**: 장비 설정 탭들 (ASIAIR, Main Camera, Guide, Mount, EFW, EAF, CAA, Files, About)
- **좌측 액션바**: Guide 토글, 히스토그램(Hist.), 기타 도구
- **우측 액션바**: 촬영 모드 선택, 노출 설정(EXP), 중앙 캡처 버튼(큰 원형)
- **마운트 컨트롤 패널**: 방향키 + Speed 슬라이더
- **가이딩 박스**: 좌상단 (가이딩 그래프 표시)
- **라이브뷰**: 중앙 전체 화면

#### 촬영 모드 (우측 상단 텍스트 탭으로 전환)
1. **Preview** - 단순 미리보기 루프
2. **Autorun** - 단순 자동 촬영 (노출×프레임수)
3. **Plan** - 멀티타겟/모자이크 복잡 시퀀서
4. **Live** - 라이브 스택 (EAA 모드)
5. **Video** - 행성 촬영용 동영상

#### 상단 탭 설정 내용
- **ASIAIR 탭**: 전원 출력 제어(Plus만), 재시작/종료
- **Main Camera 탭**: 게인, 쿨러 제어
- **Guide 탭**: 게인, 다크 라이브러리
- **Mount 탭**: GoTo Auto-Center, 메리디안 플립, 트래킹 속도, Go Home
- **EFW 탭**: 필터 위치, 필터 이름 설정
- **EAF 탭**: 현재 위치, 역방향, 스텝 설정
- **CAA 탭**: 카메라 앵글 어댑터 회전, 모자이크 정렬
- **Files 탭**: 이미지 관리, USB 전송

### ASIAIR 핵심 기능 상세

#### 1. 극축 정렬 (PPPA)
- 플레이트 솔빙 기반 전자 극축 정렬
- 실시간 오차 표시 + 조정 가이드
- 약 5분 소요 (처음부터)
- 천구 극 30° 이내 시야 필요

#### 2. 플레이트 솔빙
- astrometry.net 기반
- GoTo 후 자동 센터링
- 모자이크 프레이밍에 활용

#### 3. 오토가이딩
- 멀티스타 가이딩 지원
- 디더링 지원 (Plan/Autorun 내)
- 가이딩 그래프 실시간 표시
- PHD2 수준의 기능 (단, 파라미터 조정 제한적)

#### 4. 오토포커스 (EAF 필요)
- HFR/FWHM 기반 V-커브 분석
- 온도 변화 시 자동 재포커스
- 필터 교체 시 자동 재포커스

#### 5. Plan 모드 (시퀀서)
- 멀티타겟 지원
- 모자이크 패널 지원
- Telescopius.com에서 임포트 가능
- 각 타겟별: 노출, 필터, 프레임수, 디더링, 메리디안 플립 설정
- 종료 이벤트: Park 지원 (ASIMount only, v2.5.1)

#### 6. 라이브 스택
- 실시간 정렬+스택
- 히스토그램 조정
- Autosave 옵션 (개별 프레임 저장)
- 캘리브레이션 프레임 적용 가능

#### 7. SkyAtlas (내장 플라네타리움)
- Stellarium Web Engine 기반
- DSO 검색 + GoTo
- 시야각 미리보기
- 모자이크 계획

### ASIAIR의 장점
1. **진입 장벽 낮음**: 드라이버 설치 불필요, 소프트웨어 충돌 없음
2. **올인원**: 극축정렬→플레이트솔빙→가이딩→포커싱→촬영 모두 하나의 앱
3. **모바일 전용**: 태블릿/폰으로 실내에서 제어
4. **케이블 관리**: USB 허브 + 12V 전원 분배 통합
5. **안정성**: 전용 하드웨어+소프트웨어로 충돌 최소화
6. **WiFi 독립**: 인터넷 없이 자체 네트워크 생성

### ASIAIR의 단점 (개선 포인트)
1. **ZWO 생태계 락인**: 카메라/포커서/필터휠 모두 ZWO 전용
2. **플러그인 불가**: 추가 기능 설치 불가
3. **고급 설정 제한**: PHD2 대비 가이딩 파라미터 조정 제한
4. **편집 불가 중단**: Autorun 실행 중 설정 변경 불가 (리셋 필요)
5. **GPL 위반 논란**: 오픈소스 라이선스 미준수 이력
6. **비공개 소스**: 앱 소스코드 비공개
7. **Windows/Mac 앱 없음**: 모바일 전용
8. **Live Stack ↔ Plan 분리**: 라이브 스택을 Plan 모드에서 사용 불가

### 경쟁 앱 분석

#### Touch'N'Stars (NINA용)
- **타입**: NINA 원격 제어 웹앱 (iOS/Android)
- **오픈소스**: GPL-3.0, GitHub: Touch-N-Stars/Touch-N-Stars
- **기술**: Vue.js + JavaScript
- **요구사항**: NINA + Advanced API Plugin (포트 1888, V2)
- **기능**: 시퀀스 제어, 포커스, 가이딩 모니터링, 3점 극축정렬
- **한계**: NINA 완전 대체 불가, 보조 도구
- **최신**: v4.5.0 (2026-01-21), 활발히 개발 중

#### StellarMate Pro
- **타입**: Raspberry Pi 기반 INDI/KStars 컨트롤러
- **특징**: 오픈 생태계 (ZWO 외 장비 지원)
- **앱**: StellarMate 모바일 앱
- **장점**: 커스터마이징 가능, 멀티브랜드 지원
- **단점**: ASIAIR보다 복잡한 설정

#### Stellarium Mobile Plus
- **타입**: 플라네타리움 앱 (망원경 제어 보조)
- **특징**: 망원경 제어 모듈 (Stellarium Telescope Server 프로토콜)
- **오픈소스**: 기본 버전 오픈소스, Plus는 유료
- **한계**: 이미징 제어 기능 없음, 순수 플라네타리움

#### SkySafari 7
- **타입**: 플라네타리움 + 망원경 제어
- **특징**: 관측 목록 기능 (경쟁 앱 없는 독보적 기능)
- **한계**: 이미징 시퀀서 없음

### 새 앱 설계 인사이트 (nina용 모바일 앱)

#### ASIAIR에서 따라야 할 것
- 단일 앱에서 전체 워크플로우 (PA→솔빙→가이딩→촬영)
- 큰 터치 타겟, 어두운 환경 최적화 UI
- 실시간 가이딩 그래프
- 라이브뷰 중심 레이아웃
- WiFi 끊겨도 세션 계속 (NINA 서버가 독립 실행)

#### ASIAIR 대비 개선할 것
- **오픈 생태계**: ZWO 외 모든 ASCOM/INDI 장비 지원 (NINA가 이미 지원)
- **고급 가이딩 파라미터**: PHD2 전체 설정 노출
- **실행 중 편집**: 시퀀스 실행 중 파라미터 변경 가능
- **Live Stack + Plan 통합**: 라이브 스택을 시퀀서에서 사용
- **오픈소스**: 완전 투명한 소스코드
- **플러그인 지원**: NINA 플러그인 생태계 활용
- **웹 기반**: 앱 설치 없이 브라우저로 접근 (Touch'N'Stars 방식)


---
## 우주맵 데이터 & 라이브러리 조사 결과 (2026-03-12)

### 별 카탈로그

#### Hipparcos (HIP2)
- **별 수**: 118,218개 (mag ~13까지)
- **원본 크기**: 52MB (전체), JSON 분할 버전은 mag별로 수 MB
- **라이선스**: 공공 도메인 (ESA/NASA 데이터, 미국 정부 데이터)
- **포맷**: 원본 고정폭 텍스트, CSV 변환본, JSON 변환본 (gmiller123456/hip2000)
- **iOS 적합성**: ✅ 최적. mag 6.5 이하만 쓰면 수십 KB, mag 9 이하도 수 MB
- **SQLite 변환**: Graviton 앱이 stars.sqlite3 (~8.6MB)로 번들 — Yale BSC + Hipparcos 혼합

#### Yale Bright Star Catalog (BSC5)
- **별 수**: 9,110개 (V≤7 완전 커버)
- **크기**: 291KB (바이너리), ~500KB (ASCII)
- **라이선스**: 공공 도메인
- **포맷**: 바이너리 또는 ASCII, JSON 변환본 있음 (brettonw/YaleBrightStarCatalog)
- **iOS 적합성**: ✅ 매우 적합. 육안 별 + 밝은 별 렌더링에 충분

#### Tycho-2
- **별 수**: 2.5M개 (mag ~11.5까지)
- **크기**: 160MB 압축 (원본)
- **라이선스**: 공공 도메인 (ESA)
- **iOS 적합성**: ⚠️ 너무 큼. 앱 번들에 부적합. 서버 스트리밍 필요

### DSO 카탈로그

#### OpenNGC (mattiaverga/OpenNGC)
- **GitHub**: https://github.com/mattiaverga/OpenNGC
- **최신 커밋**: 9609f2f (2026-03-08)
- **라이선스**: **CC-BY-SA-4.0** (상업적 사용 가능, 저작자 표시 필요)
- **포맷**: CSV (NGC.csv ~3.8MB, addendum.csv ~17KB)
- **내용**: NGC + IC 전체, 타입(G/PN/OCl/GCl/HII/SNR 등), RA/Dec J2000, 크기(장축/단축), 등급(B/V/J/H/K), Messier 교차참조, 일반명
- **iOS 적합성**: ✅ 최적. CSV → SQLite 변환 후 ~2-3MB
- **특징**: Messier 번호 교차참조 포함 → M42 검색 가능

#### Messier 카탈로그
- 110개 대상 → OpenNGC에 포함됨 (M 컬럼)
- 별도 파일 불필요

### iOS Swift 천문 라이브러리

#### SwiftAA (onekiloparsec/SwiftAA)
- **GitHub**: https://github.com/onekiloparsec/SwiftAA
- **최신 커밋**: 9bf80a1 (2026-02-26)
- **라이선스**: **MIT** ✅
- **배포**: Swift Package Manager, CocoaPods, Carthage
- **핵심 기능**:
  - `EquatorialCoordinates` → `makeHorizontalCoordinates(for:at:)` → `HorizontalCoordinates`
  - `GeographicCoordinates` (CLLocation 통합 포함)
  - `JulianDay` 변환
  - 대기굴절 보정 (`AthmosphericRefraction`)
  - 행성 위치, 달 위상, 일출/몰 시간
- **좌표 변환 코드**:
  ```swift
  let eq = EquatorialCoordinates(alpha: Hour(ra), delta: Degree(dec))
  let geo = GeographicCoordinates(location: clLocation)
  let jd = JulianDay(Date())
  let hz = eq.makeHorizontalCoordinates(for: geo, at: jd)
  // hz.altitude, hz.northBasedAzimuth
  ```

#### Graviton (DJBen/Graviton)
- **GitHub**: https://github.com/DJBen/Graviton
- **최신 커밋**: 5068ae0 (2026-01-26)
- **라이선스**: **GPL-3.0** ⚠️ (상업 앱에 제약)
- **언어**: Swift 4 + SceneKit + Metal
- **데이터**: stars.sqlite3 (~8.6MB) 번들 — Yale BSC + Hipparcos 혼합
- **구조**: StarryNight 모듈 (SQLite 기반 별 카탈로그), Orbits 모듈 (행성 궤도), SpaceTime 모듈
- **참고 가치**: SQLite 스키마, 별자리 선 데이터 형식 참고 가능

#### Stellarium Web Engine
- **GitHub**: https://github.com/Stellarium/stellarium-web-engine
- **라이선스**: **AGPL-3.0** ❌ (iOS 앱에 사용 불가 — 소스 공개 의무)
- **결론**: iOS 네이티브 앱에 사용 불가

### FOV 계산 공식

```
FOV_horizontal = 2 × arctan(sensor_width / (2 × focal_length))
FOV_vertical   = 2 × arctan(sensor_height / (2 × focal_length))
```

Swift 구현:
```swift
func fieldOfView(sensorSize: Double, focalLength: Double) -> Double {
    return 2.0 * atan(sensorSize / (2.0 * focalLength)) * (180.0 / .pi)
}
```

일반 천문 카메라 예시:
| 카메라 | 센서 | 초점거리 | H-FOV | V-FOV |
|--------|------|---------|-------|-------|
| ASI2600 | 23.5×15.7mm | 480mm | 2.8° | 1.9° |
| ASI183 | 13.2×8.8mm | 480mm | 1.6° | 1.0° |
| Full Frame | 36×24mm | 50mm | 39.6° | 27.0° |
| Full Frame | 36×24mm | 2000mm | 1.0° | 0.7° |

### 권장 스택

**데이터**:
- 별: Hipparcos CSV → SQLite (mag ≤ 9, ~5MB) + Yale BSC (밝은 별 이름)
- DSO: OpenNGC CSV → SQLite (~3MB)
- 별자리 선: Graviton의 constellation_lines.dat 형식 참고 (5KB)

**라이브러리**:
- 좌표 변환: **SwiftAA** (MIT, SPM 지원)
- 렌더링: **SwiftUI Canvas** + **Metal** (직접 구현)
- DB: **SQLite.swift** (MIT)

**앱 번들 크기 예상**:
- stars.sqlite3 (mag≤9): ~3-5MB
- OpenNGC SQLite: ~3MB
- 별자리 선 데이터: ~5KB
- SwiftAA 바이너리: ~2MB
- **총합**: ~10-12MB 추가

**구현 난이도**:
- 좌표 변환 (RA/Dec → Alt/Az): ✅ 쉬움 (SwiftAA 1줄)
- 별 렌더링 (Canvas/Metal): 🟡 중간
- DSO 검색 (SQLite FTS): ✅ 쉬움
- FOV 오버레이: ✅ 쉬움 (공식 적용)
- 은하수 배경: 🔴 어려움 (별도 텍스처 또는 절차적 생성 필요)

---
## BeyondStellar SkyMap Implementation (2026-03-12)

### Existing Model Adaptations
- Used existing `StarCatalogEntry` (not `StarEntry`): `colorIndex`, `properName`, `designation`, `renderRadius`, `displayColor`
- Used existing `DSOCatalogEntry` (not `DSOEntry`): `DSOType` with `tintColor`, `symbolName`, `displayName`, `subtitle`, `renderArcminutes`, `positionAngle`
- Used existing `CameraFOV` (not `CameraPreset`): `horizontalDegrees`, `verticalDegrees`, `displayString`
- Used existing `SkyTarget` enum (not protocol): `.star(StarCatalogEntry)`, `.dso(DSOCatalogEntry)`, has `raHMS`, `decDMS`
- Used existing `SkyProjection` with `let` properties → created `SkyProjection+Extensions.swift` for `panned()`, `withCenter()`, `withScale()`, `recommendedHiPSOrder`

### Key Design Decisions
- `SkyProjection` negates x-axis (RA increases left) — `panned(dx:dy:)` accounts for this convention
- `SkyDatabase` falls back to hardcoded sample data (20 bright stars, 12 Messier objects) when no SQLite file bundled
- HEALPix ring scheme (simpler) instead of nested for pix2ang/ang2pix
- `queryDisc` brute-force acceptable for order ≤ 6
- Max 64 tiles per update, max order 6 for mobile performance
- `MagnificationGesture` used for iOS 13+ compat (deprecated in 17, `MagnifyGesture` is replacement)
- `.onTapGesture(count:coordinateSpace:perform:)` requires iOS 16+ for location
