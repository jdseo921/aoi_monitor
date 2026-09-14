using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AOI_Monitor.Data;

namespace AOI_Monitor.Services;

public enum UiLanguage
{
    English,
    Korean,
}

/// <summary>
/// Standard is the single supported preset. The Compact (14 px) / Standard (15 px) /
/// Large (17 px) window-default presets were retired 2026-08-23 when the typography floor
/// rose to true 14 pt (18.67 DIP): every preset sat below the customer-spec floor, and
/// per-station scaling is Windows DPI's job (the layout audit validates 100/125/150%).
/// Legacy settings files carrying retired values normalize to Standard on load.
/// </summary>
public enum UiFontPreset
{
    Standard,
}

/// <summary>
/// Industrial Dark is the single supported theme. The light theme was removed 2026-08-23:
/// it predated the token system, shipped with unreadable text (measured 1.05:1 worst case),
/// and no station used it. Settings files that still carry the retired value fall back to
/// dark on load. The dark palette's readability is machine-checked by HmiThemePaletteTests.
/// </summary>
public enum UiTheme
{
    IndustrialDark,
}

public enum UiResolutionPreset
{
    FullHd1920x1080,
    Qhd2560x1440,
    Uhd3840x2160,
}

public sealed class UiPreferences
{
    public UiLanguage Language { get; set; } = UiLanguage.English;
    public UiFontPreset FontPreset { get; set; } = UiFontPreset.Standard;
    public UiTheme Theme { get; set; } = UiTheme.IndustrialDark;
    public UiResolutionPreset ResolutionPreset { get; set; } = UiResolutionPreset.FullHd1920x1080;
    public string ConsoleTitle { get; set; } = UiPreferenceDefaults.ConsoleTitle;
    public string StationDisplayName { get; set; } = UiPreferenceDefaults.StationDisplayName;
    public string StationSubtitle { get; set; } = UiPreferenceDefaults.StationSubtitle;
    public string AccentColor { get; set; } = UiPreferenceDefaults.AccentColor;
    public string BrandLogoPath { get; set; } = string.Empty;
}

public static class UiPreferenceDefaults
{
    public const string ConsoleTitle = "PCBA AOI REVIEW CONSOLE";
    public const string StationDisplayName = "AOI-LIB";
    public const string StationSubtitle = "local review console / prototype";
    public const string AccentColor = "#5CA0D3";
}

public static class UiPreferencesService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly DependencyProperty OriginalTextProperty =
        DependencyProperty.RegisterAttached("OriginalText", typeof(string), typeof(UiPreferencesService));
    private static readonly DependencyProperty OriginalContentProperty =
        DependencyProperty.RegisterAttached("OriginalContent", typeof(string), typeof(UiPreferencesService));
    private static readonly DependencyProperty OriginalHeaderProperty =
        DependencyProperty.RegisterAttached("OriginalHeader", typeof(string), typeof(UiPreferencesService));
    private static readonly DependencyProperty OriginalToolTipProperty =
        DependencyProperty.RegisterAttached("OriginalToolTip", typeof(string), typeof(UiPreferencesService));
    private static readonly DependencyProperty LastAppliedTextProperty =
        DependencyProperty.RegisterAttached("LastAppliedText", typeof(string), typeof(UiPreferencesService));
    private static readonly DependencyProperty LastAppliedContentProperty =
        DependencyProperty.RegisterAttached("LastAppliedContent", typeof(string), typeof(UiPreferencesService));
    private static readonly DependencyProperty LastAppliedHeaderProperty =
        DependencyProperty.RegisterAttached("LastAppliedHeader", typeof(string), typeof(UiPreferencesService));
    private static readonly DependencyProperty LastAppliedToolTipProperty =
        DependencyProperty.RegisterAttached("LastAppliedToolTip", typeof(string), typeof(UiPreferencesService));
    private static readonly Dictionary<string, string> KoreanText = new(StringComparer.Ordinal)
    {
        ["PCBA AOI REVIEW CONSOLE"] = "PCBA AOI 검사 콘솔",
        ["Demo Mode"] = "데모 모드",
        ["Pilot Mode"] = "파일럿 모드",
        ["Production Mode"] = "운영 모드",
        ["Profile "] = "프로필 ",
        ["User "] = "사용자 ",
        ["Engine "] = "엔진 ",
        ["File"] = "파일",
        ["Report Issue"] = "이슈 보고",
        ["Layout Stress"] = "레이아웃 점검",
        ["Support Bundle"] = "지원 번들",
        ["Session and admin access are available in the collapsed Access panel."] = "세션 및 관리자 기능은 접힌 접근 패널에서 사용할 수 있습니다.",
        ["Access / User Management"] = "접근 / 사용자 관리",
        ["Utilities / Access"] = "유틸리티 / 접근",
        ["AOI Workflow"] = "AOI 워크플로",
        ["Home"] = "홈",
        ["module map"] = "모듈 맵",
        ["Run Inspection"] = "검사 실행",
        ["board execution"] = "보드 실행",
        ["embedded run, large image view"] = "내장 실행, 큰 이미지 보기",
        ["Review Defects"] = "결함 검토",
        ["evidence and disposition"] = "증빙 및 처분",
        ["Analyze Yield"] = "수율 분석",
        ["accuracy and false calls"] = "정확도 및 허위 검출",
        ["Recipe & Model"] = "레시피 / 모델",
        ["ROI, tolerances, lifecycle"] = "ROI, 공차, 수명주기",
        ["Export / Trace"] = "내보내기 / 추적",
        ["logs, reports, MES"] = "로그, 보고서, MES",
        ["System Health"] = "시스템 건강",
        ["hardware, quality, access"] = "하드웨어, 품질, 접근",
        ["Board & Images"] = "보드 / 이미지",
        ["library, folders, golden refs"] = "라이브러리, 폴더, 기준 이미지",
        ["Golden Compare"] = "기준 비교",
        ["template match, difference score"] = "템플릿 매칭, 차이 점수",
        ["embedded compare, large image view"] = "내장 비교, 큰 이미지 보기",
        ["Defect Review"] = "결함 검토",
        ["queue, evidence, disposition"] = "대기열, 증빙, 처분",
        ["Recipe Rules"] = "레시피 규칙",
        ["ROI, masks, tolerances"] = "ROI, 마스크, 공차",
        ["AI / Models"] = "AI / 모델",
        ["model checks, false calls"] = "모델 점검, 허위 검출",
        ["Yield Analytics"] = "수율 분석",
        ["SPC, Pareto, trends"] = "SPC, 파레토, 추세",
        ["Export & Trace"] = "내보내기 / 추적",
        ["CSV, PDF, audit, MES"] = "CSV, PDF, 감사, MES",
        ["2D transform, Stage 2 prep"] = "2D 변환, 2단계 준비",
        ["3D Profile"] = "3D 프로파일",
        ["height data, acceptance"] = "높이 데이터, 승인",
        ["Hardware Readiness"] = "하드웨어 준비성",
        ["camera, lighting, robot gates"] = "카메라, 조명, 로봇 게이트",
        ["System Settings"] = "시스템 설정",
        ["display, storage, security"] = "화면, 저장소, 보안",
        ["Database"] = "데이터베이스",
        ["Image Vault"] = "이미지 보관소",
        ["Engine"] = "엔진",
        ["Camera"] = "카메라",
        ["Lighting"] = "조명",
        ["Robot"] = "로봇",
        ["MES / Trace"] = "MES / 추적",
        ["E-Stop"] = "비상정지",
        ["Connected"] = "연결됨",
        ["Available"] = "사용 가능",
        ["Ready"] = "준비됨",
        ["Simulated"] = "시뮬레이션",
        ["Error"] = "오류",
        ["Not Connected"] = "연결 안 됨",
        ["Not Available"] = "사용 불가",
        ["MAIN INSPECTION / REVIEW WORKFLOW"] = "메인 검사 / 검토 워크플로",
        ["Main Inspection"] = "메인 검사",
        ["Recipe Editor"] = "레시피 편집",
        ["AI Model Test"] = "AI 모델 테스트",
        ["Log & Export"] = "로그 / 내보내기",
        ["Pilot Wizard"] = "파일럿 마법사",
        ["3D Profile Viewer"] = "3D 프로파일 뷰어",
        ["Calibration"] = "보정",
        ["Settings / Guide"] = "설정 / 가이드",
        ["Station AOI-LIB-01"] = "스테이션 AOI-LIB-01",
        ["Camera: Stage 1 folder source"] = "카메라: 1단계 폴더 소스",
        ["Lighting: simulated boundary"] = "조명: 시뮬레이션 경계",
        ["DB: SQLite local cache"] = "DB: SQLite 로컬 캐시",
        ["Robot/MES: not production-connected"] = "로봇/MES: 운영 연결 아님",
        ["Product"] = "제품",
        ["Recipe"] = "레시피",
        ["Inspection Basis"] = "검사 기준",
        ["Image validation PoC"] = "이미지 검증 PoC",
        ["Primary Modules"] = "주요 모듈",
        ["Focused Workflow Menus"] = "집중 워크플로 메뉴",
        ["AOI Inspection Pipeline"] = "AOI 검사 파이프라인",
        ["Input"] = "입력",
        ["PNG/JPG, AOI files, golden images"] = "PNG/JPG, AOI 파일, 기준 이미지",
        ["Register"] = "정합",
        ["Fiducials, rotation, X/Y offset"] = "피듀셜, 회전, X/Y 오프셋",
        ["Component, solder, polarity, surface"] = "부품, 솔더, 극성, 표면",
        ["Evidence, coordinates, IPC class, MES"] = "증빙, 좌표, IPC 등급, MES",
        ["Boards inspected"] = "검사 보드",
        ["First-pass yield"] = "초회 통과율",
        ["Pending review"] = "검토 대기",
        ["Critical defects"] = "치명 결함",
        ["Capabilities represented in the redesigned workflow"] = "재설계 워크플로에 반영된 기능",
        ["Capability groups"] = "기능 그룹",
        ["Board and image management, defect detection algorithms, processing and tolerance rules, and output traceability are split into focused menus so dense inspection work does not crowd the main review screen. Image-heavy panels provide a separate large-image viewer when operators need more detail."] = "보드/이미지 관리, 결함 검출 알고리즘, 처리/공차 규칙, 출력 추적성을 집중 메뉴로 분리하여 복잡한 검사 작업이 메인 검토 화면을 혼잡하게 만들지 않도록 합니다. 자세한 확인이 필요한 이미지 중심 패널은 별도의 큰 이미지 보기를 제공합니다.",
        ["Recent Jobs"] = "최근 작업",
        ["Current Evidence Boundary"] = "현재 증빙 경계",
        ["Stage 1 image-validation evidence is visible and usable. Real camera, lighting, robot, and MES readiness must be validated through Hardware Readiness evidence before it can be represented as production hardware readiness."] = "1단계 이미지 검증 증빙은 표시되고 사용할 수 있습니다. 실제 카메라, 조명, 로봇, MES 준비성은 운영 하드웨어 준비성으로 표현하기 전에 하드웨어 준비성 증빙으로 검증되어야 합니다.",
        ["Focused workflow design rule"] = "집중 워크플로 설계 규칙",
        ["Use the home map to enter the focused menu needed for the task. New features should become a focused page, tab, or image viewer dialog when they would crowd inspection, review, settings, or export workflows."] = "홈 맵에서 작업에 필요한 집중 메뉴로 이동하십시오. 새 기능이 검사, 검토, 설정 또는 내보내기 워크플로를 혼잡하게 만들 경우 집중 페이지, 탭 또는 이미지 보기 대화 상자로 분리해야 합니다.",
        ["Operator Guardrails"] = "작업자 보호 규칙",
        ["Operator comfort defaults that do not hide audit or hardware boundary information."] = "감사 또는 하드웨어 경계 정보를 숨기지 않는 작업자 편의 기본값입니다.",
        ["Comfort settings cannot suppress critical alarms, hide simulated hardware banners, remove export verification, or bypass audit evidence."] = "편의 설정은 치명 알람 억제, 시뮬레이션 하드웨어 배너 숨김, 내보내기 검증 제거, 감사 증빙 우회를 허용하지 않습니다.",
        ["Keep critical alarms pinned"] = "치명 알람 고정 유지",
        ["Keep simulated hardware boundary visible"] = "시뮬레이션 하드웨어 경계 표시 유지",
        ["Keep failed quality gates visible"] = "실패한 품질 게이트 표시 유지",
        ["review workflow"] = "검토 워크플로",
        ["ROI/rules"] = "ROI/규칙",
        ["stage 1 validation"] = "1단계 검증",
        ["history/package"] = "이력/패키지",
        ["customer evidence"] = "고객 증빙",
        ["sample CSV mode"] = "샘플 CSV 모드",
        ["stage 2 prep"] = "2단계 준비",
        ["setup/docs"] = "설치/문서",
        ["ACTIVE ALARMS"] = "활성 알람",
        ["No active alarms."] = "활성 알람 없음.",
        ["All severities"] = "전체 심각도",
        ["Info"] = "정보",
        ["Warning"] = "경고",
        ["Alarm"] = "알람",
        ["Critical"] = "치명",
        ["Severity high first"] = "심각도 높은 순",
        ["Newest first"] = "최신순",
        ["Oldest first"] = "오래된 순",
        ["Severity low first"] = "심각도 낮은 순",
        ["Acknowledge"] = "확인",
        ["Acknowledge All"] = "모두 확인",
        ["Details"] = "상세",
        ["Export Log"] = "로그 내보내기",
        ["Sample "] = "샘플 ",
        ["Golden "] = "기준 ",
        ["Score "] = "점수 ",
        ["Verdict "] = "판정 ",
        ["Refresh"] = "새로고침",
        ["Lock Recipe"] = "레시피 잠금",
        ["Export"] = "내보내기",
        ["Load a board image, review the marked defects, then save the result. Purple panels mean the action is simulated or mock-only."] = "보드 이미지를 불러오고 표시된 결함을 검토한 뒤 결과를 저장합니다. 보라색 패널은 시뮬레이션 또는 목 전용 동작을 의미합니다.",
        ["Disposition"] = "판정 처리",
        ["Fiducial alignment"] = "피듀셜 정렬",
        ["Golden template"] = "기준 템플릿",
        ["ROI masks"] = "ROI 마스크",
        ["Tolerance rules"] = "공차 규칙",
        ["Lighting boundary"] = "조명 경계",
        ["Image Library"] = "이미지 라이브러리",
        ["Image / Simulated Live Feed"] = "이미지 / 시뮬레이션 실시간 피드",
        ["No image loaded"] = "이미지 없음",
        ["View"] = "보기",
        ["Top"] = "상면",
        ["Side"] = "측면",
        ["Bottom"] = "하면",
        ["Defect overlay layer"] = "결함 오버레이",
        ["Top Folder"] = "상면 폴더",
        ["Side Folder"] = "측면 폴더",
        ["Bottom Folder"] = "하면 폴더",
        ["Auto-save"] = "자동 저장",
        ["Simulated Robot / Handler"] = "시뮬레이션 로봇 / 핸들러",
        ["Simulation only. No real robot, handler, PLC, or safety hardware is connected."] = "시뮬레이션 전용입니다. 실제 로봇, 핸들러, PLC 또는 안전 하드웨어가 연결되어 있지 않습니다.",
        ["Ready: simulation only"] = "준비됨: 시뮬레이션 전용",
        ["No simulated board loaded"] = "시뮬레이션 보드 없음",
        ["No real robot connected"] = "실제 로봇 미연결",
        ["Last cycle --"] = "마지막 사이클 --",
        ["Robot acceptance: not validated"] = "로봇 승인: 미검증",
        ["Fault: none"] = "고장: 없음",
        ["Load"] = "로드",
        ["Inspect"] = "검사",
        ["Unload"] = "언로드",
        ["Cancel"] = "취소",
        ["Reset"] = "재설정",
        ["E-Stop Sim"] = "비상정지 시뮬",
        ["Run Cycle"] = "사이클 실행",
        ["Run Robot Cell Acceptance Test"] = "로봇 셀 승인 테스트 실행",
        ["Export Report"] = "보고서 내보내기",
        ["Select a folder or load an image from Image Library, then press Start / Next Board."] = "폴더를 선택하거나 이미지 라이브러리에서 이미지를 불러온 뒤 시작 / 다음 보드를 누르십시오.",
        ["Start"] = "시작",
        ["Stop"] = "정지",
        ["Next Board"] = "다음 보드",
        ["Save Result"] = "결과 저장",
        // Hotkey-labelled operator buttons (Monitor). Keys must match the XAML literals exactly.
        ["Start (F5)"] = "시작 (F5)",
        ["Stop (F6)"] = "정지 (F6)",
        ["Next Board (F7)"] = "다음 보드 (F7)",
        ["Save Result (Ctrl+S)"] = "결과 저장 (Ctrl+S)",
        // Review disposition controls (hotkey-labelled) and hint line.
        ["Disposition Controls"] = "판정 처리 제어",
        ["Confirm NG (1)"] = "NG 확정 (1)",
        ["Mark False Call (2)"] = "허위 검출 표시 (2)",
        ["Mark Possible Escape (3)"] = "유출 가능 표시 (3)",
        ["Hold for 2nd Review (4)"] = "2차 검토 보류 (4)",
        ["Queue Candidate"] = "학습 후보 등록",
        // Recipe editor centroid import.
        ["Import Centroid CSV"] = "센트로이드 CSV 가져오기",
        ["Board / Status"] = "보드 / 상태",
        ["Station"] = "스테이션",
        ["Board / Model"] = "보드 / 모델",
        ["Lot ID"] = "로트 ID",
        ["Operator"] = "작업자",
        ["Model Version"] = "모델 버전",
        ["2D Cal Profile"] = "2D 보정 프로파일",
        ["Frame"] = "프레임",
        ["Timing"] = "타이밍",
        ["Result"] = "결과",
        ["Defect List"] = "결함 목록",
        ["No"] = "번호",
        ["Type"] = "유형",
        ["ROI"] = "ROI",
        ["ROI Type"] = "ROI 유형",
        ["Placement"] = "배치",
        ["Solder Volume"] = "솔더 체적",
        ["Surface Defect"] = "표면 결함",
        ["Presence"] = "부품 유무",
        ["Polarity"] = "극성",
        ["Solder Bridge"] = "솔더 브리지",
        ["Height"] = "높이",
        ["Anomaly"] = "이상",
        ["Severity"] = "심각도",
        ["Score"] = "점수",
        ["X"] = "X",
        ["Y"] = "Y",
        ["Board X mm"] = "보드 X mm",
        ["Board Y mm"] = "보드 Y mm",
        ["Alarm / Event Log"] = "알람 / 이벤트 로그",
        ["Time"] = "시간",
        ["Event"] = "이벤트",
        ["Message"] = "메시지",
        ["STOPPED"] = "정지됨",
        ["Settings / Guide Work Areas"] = "설정 / 가이드 작업 영역",
        ["Settings Control Center"] = "설정 제어 센터",
        ["Basics"] = "기본",
        ["QOL"] = "편의",
        ["AI"] = "AI",
        ["Hardware"] = "하드웨어",
        ["Traceability"] = "추적성",
        ["Evidence"] = "증빙",
        ["Resolution"] = "해상도",
        ["Theme"] = "테마",
        ["Industrial Dark"] = "산업용 다크",
        ["1920 x 1080 minimum HMI"] = "1920 x 1080 최소 HMI",
        ["2560 x 1440 engineering monitor"] = "2560 x 1440 엔지니어링 모니터",
        ["3840 x 2160 wall display"] = "3840 x 2160 벽면 디스플레이",
        ["Apply writes settings and refreshes the shell. Cancel restores the last saved values."] = "적용은 설정을 저장하고 셸을 새로 고칩니다. 취소는 마지막 저장 값을 복원합니다.",
        ["Processing & Tolerance Rules"] = "처리 및 공차 규칙",
        ["X/Y tolerance mm"] = "X/Y 공차 mm",
        ["Rotation tolerance deg"] = "회전 공차 deg",
        ["IPC acceptability class"] = "IPC 허용 등급",
        ["IPC Class 1"] = "IPC 등급 1",
        ["IPC Class 2"] = "IPC 등급 2",
        ["IPC Class 3"] = "IPC 등급 3",
        ["Lighting profile"] = "조명 프로파일",
        ["Top bright field"] = "상면 명시야",
        ["Low-angle dark field"] = "저각 암시야",
        ["Side marking light"] = "측면 마킹 조명",
        ["Solder fillet highlight"] = "솔더 필렛 하이라이트",
        ["False-call policy"] = "허위 검출 정책",
        ["Strict: hold every suspected defect"] = "엄격: 모든 의심 결함 보류",
        ["Balanced: review benign visual noise"] = "균형: 무해한 시각 노이즈 검토",
        ["Sensitive: reduce possible escapes"] = "민감: 유출 가능성 감소",
        ["Display / Language"] = "화면 / 언어",
        ["Language"] = "언어",
        ["Font Size"] = "글자 크기",
        ["Inspection stage indicators (read-only):"] = "\uAC80\uC0AC \uB2E8\uACC4 \uD45C\uC2DC\uAE30(\uC77D\uAE30 \uC804\uC6A9):",
        ["Standard - 14 pt operator floor"] = "표준 - 14pt 운영자 최소 크기",
        ["Program Assets"] = "프로그램 자산",
        ["Console Title"] = "콘솔 제목",
        ["Station Label"] = "스테이션 라벨",
        ["Station Subtitle"] = "스테이션 설명",
        ["Accent Color"] = "강조 색상",
        ["Logo Image"] = "로고 이미지",
        ["Browse"] = "찾아보기",
        ["Apply"] = "적용",
        ["Test Model Configuration"] = "모델 설정 테스트",
        ["Run Setup Wizard Again"] = "설정 마법사 다시 실행",
        ["Export Diagnostics Report"] = "진단 보고서 내보내기",
        ["Backup Configuration"] = "설정 백업",
        ["Restore Configuration Preview"] = "설정 복원 미리보기",
        ["Apply Restore"] = "복원 적용",
        ["Rollback Last Restore"] = "마지막 복원 되돌리기",
        ["Redact image/storage paths"] = "이미지/저장 경로 숨김",
        ["Include model files"] = "모델 파일 포함",
        ["Export Support Bundle"] = "지원 번들 내보내기",
        // --- Main Inspection (MonitorView) ---
        ["Large Image"] = "큰 이미지",
        ["Learned Evidence"] = "학습 증빙",
        ["Learned visual model: not active."] = "학습 시각 모델: 비활성.",
        ["No Camera Connected"] = "카메라 연결 안 됨",
        ["No frame"] = "프레임 없음",
        ["Lighting: Not Connected"] = "조명: 연결 안 됨",
        ["Operate"] = "운영",
        ["Analyze & Evidence"] = "분석 / 증빙",
        ["Machine & System"] = "장비 / 시스템",
        ["Run Layout Stress Test"] = "레이아웃 스트레스 테스트 실행",
        ["Disabled / Not Connected"] = "비활성 / 연결 안 됨",
        ["Alignment steps - planned, not yet interactive"] = "정렬 단계 - 계획된 기능, 아직 동작하지 않음",
        ["Board X"] = "보드 X",
        ["Board Y"] = "보드 Y",
        ["Simulated handler load step only."] = "시뮬레이션 핸들러 로드 단계 전용입니다.",
        ["Simulated handler inspect step only."] = "시뮬레이션 핸들러 검사 단계 전용입니다.",
        ["Simulated handler unload step only."] = "시뮬레이션 핸들러 언로드 단계 전용입니다.",
        ["Simulated emergency-stop behavior. This is not wired to real safety hardware."] = "시뮬레이션 비상정지 동작입니다. 실제 안전 하드웨어에 연결되어 있지 않습니다.",
        ["Acceptance means evidence review against configured criteria; it does not certify production readiness by itself."] = "승인은 구성된 기준에 대한 증빙 검토를 의미하며, 그 자체로 운영 준비성을 인증하지 않습니다.",
        ["2D calibration profile / Stage 2 preparation only. Approximate board-mm coordinates are not robot-ready."] = "2D 보정 프로파일 / 2단계 준비 전용입니다. 근사 보드 mm 좌표는 로봇 적용 준비 상태가 아닙니다.",
        ["ROI means region of interest: the image area checked for a defect."] = "ROI는 관심 영역, 즉 결함을 검사하는 이미지 영역을 의미합니다.",
        // --- Defect Review (ReviewView) ---
        ["Prioritized Review Queue"] = "우선순위 검토 대기열",
        ["Demo Data"] = "데모 데이터",
        ["Closest Matching Defect Images"] = "가장 유사한 결함 이미지",
        ["Default: closest known false-negative cases first"] = "기본: 알려진 미검출 사례와 가장 유사한 순",
        ["False Negative History"] = "미검출 이력",
        ["Demo Board Illustration"] = "데모 보드 일러스트",
        ["Current Sample Image"] = "현재 샘플 이미지",
        ["Overlay: AI+GT"] = "오버레이: AI+GT",
        ["Zoom 125%"] = "확대 125%",
        ["ROI Crop"] = "ROI 자르기",
        ["History"] = "이력",
        ["Sample"] = "샘플",
        ["Sample ID"] = "샘플 ID",
        ["Board"] = "보드",
        ["Risk"] = "위험",
        ["Demo Metadata"] = "데모 메타데이터",
        ["Sample Metadata"] = "샘플 메타데이터",
        ["Inspection Result"] = "검사 결과",
        ["Ground Truth"] = "실측 판정",
        ["Possible Escape"] = "유출 가능",
        // --- Recipe Editor (RecipeView) ---
        // NOTE: RoiTypeCombo / IpcClassCombo / LightingProfileCombo / FalseCallPolicyCombo item
        // contents are intentionally NOT translated: RecipeView code-behind round-trips those
        // ComboBoxItem.Content strings as persisted recipe data (SelectedRoiType/ComboContent).
        ["Recipe ID"] = "레시피 ID",
        ["Board Program"] = "보드 프로그램",
        ["Revision Lock"] = "리비전 잠금",
        ["Load a PCB image to start editing."] = "PCB 이미지를 불러와 편집을 시작하십시오.",
        ["Load Image"] = "이미지 불러오기",
        ["Test Run"] = "테스트 실행",
        ["Save Recipe"] = "레시피 저장",
        ["Drag to draw. Wheel zooms. Right-drag pans."] = "드래그로 그리기, 휠로 확대/축소, 우클릭 드래그로 이동합니다.",
        ["Recipe Background + ROI Editor"] = "레시피 배경 + ROI 편집기",
        ["Zoom -"] = "축소",
        ["Zoom +"] = "확대",
        ["Fit"] = "맞춤",
        ["No board image loaded"] = "보드 이미지 없음",
        ["Use Load Image to place a PCB image here, then draw ROI masks and tolerance rules."] = "이미지 불러오기로 PCB 이미지를 배치한 뒤 ROI 마스크와 공차 규칙을 그리십시오.",
        ["ROI Setup"] = "ROI 설정",
        ["AI Score Threshold"] = "AI 점수 임계값",
        ["Height Min"] = "높이 최소",
        ["Height Max"] = "높이 최대",
        ["Volume Min"] = "체적 최소",
        ["Volume Max"] = "체적 최대",
        ["Delete ROI"] = "ROI 삭제",
        ["Revision Notes"] = "리비전 노트",
        ["No saved revision loaded."] = "저장된 리비전이 없습니다.",
        ["Active ROI is yellow. Saved ROI is green. Unsaved ROI is blue. Stage 1 evidence remains image-based until real camera, lighting, robot, and MES acceptance records exist."] = "활성 ROI는 노란색, 저장된 ROI는 녹색, 저장되지 않은 ROI는 파란색입니다. 실제 카메라, 조명, 로봇, MES 승인 기록이 존재할 때까지 1단계 증빙은 이미지 기반으로 유지됩니다.",
        ["Dimensional tolerance for placement offset evidence."] = "배치 오프셋 증빙에 대한 치수 공차입니다.",
        ["Skew tolerance for component placement evidence."] = "부품 배치 증빙에 대한 회전(스큐) 공차입니다.",
        // --- AI Model Test (AIModelTestView) ---
        ["Stage 1 Validation"] = "1단계 검증",
        ["AI Training Setup"] = "AI 학습 설정",
        ["Prototype / sample evidence only"] = "프로토타입 / 샘플 증빙 전용",
        ["Stage 1 Customer Validation"] = "1단계 고객 검증",
        ["Test Image Folder"] = "테스트 이미지 폴더",
        ["Ground Truth CSV (optional)"] = "실측 판정 CSV (선택)",
        ["Select"] = "선택",
        ["Run Dataset Preflight"] = "데이터셋 사전 점검 실행",
        ["Open Manifest Template"] = "매니페스트 템플릿 열기",
        ["Run Batch Inspection"] = "일괄 검사 실행",
        ["Accuracy"] = "정확도",
        ["Precision"] = "정밀도",
        ["Recall"] = "재현율",
        ["Good Boards Marked Defect"] = "양품 결함 판정 (허위 검출)",
        ["Good Board Flagged"] = "양품 결함 표시 (허위 검출)",
        ["Possible Missed Defect"] = "결함 미검출 가능 (유출)",
        ["Verified NG"] = "확정 NG",
        ["Unknown / Unlabeled"] = "알 수 없음 / 라벨 없음",
        ["Avg Time / Image"] = "이미지당 평균 시간",
        ["Max Time"] = "최대 시간",
        ["Min Time"] = "최소 시간",
        ["Over 1 Second"] = "1초 초과",
        ["Dataset Check"] = "데이터셋 점검",
        ["Preflight: not run"] = "사전 점검: 미실행",
        ["Blocking Failures"] = "차단 실패",
        ["Warnings"] = "경고",
        ["Reduce Good-Board Flags"] = "양품 오검출 감소",
        ["Balanced"] = "균형",
        ["Reduce Missed Defects"] = "미검출 감소",
        ["Analyze Review Tradeoff"] = "검토 트레이드오프 분석",
        ["Create Threshold Profile Draft"] = "임계값 프로파일 초안 생성",
        ["Apply Recommended Threshold"] = "권장 임계값 적용",
        ["Good-Board Flags / Review Labor"] = "양품 오검출 / 검토 작업량",
        ["Shows where changing the review threshold may reduce unnecessary operator review without hiding likely defects."] = "검토 임계값 변경으로 결함 가능성을 숨기지 않으면서 불필요한 작업자 검토를 줄일 수 있는 지점을 보여줍니다.",
        ["Run a batch, then analyze threshold candidates."] = "일괄 검사를 실행한 뒤 임계값 후보를 분석하십시오.",
        ["Threshold"] = "임계값",
        ["Good Flag"] = "양품 오검출",
        ["Escape"] = "유출",
        ["Review"] = "검토",
        ["Manual Min"] = "수동 검토(분)",
        ["Status"] = "상태",
        ["Defect Class"] = "결함 분류",
        ["Side/View"] = "면/뷰",
        ["Worst Good-Board Flags"] = "양품 오검출 상위 원인",
        ["Possible Escapes"] = "유출 가능 항목",
        ["Class"] = "분류",
        ["Wrong"] = "오분류",
        ["Wrong Side"] = "면 불일치",
        ["Total"] = "합계",
        ["Source"] = "원인",
        ["Batch Results"] = "일괄 검사 결과",
        ["No batch run loaded"] = "일괄 실행 기록 없음",
        ["Image"] = "이미지",
        ["Verdict"] = "판정",
        ["Defect"] = "결함",
        ["Lot"] = "로트",
        ["Notes"] = "비고",
        ["Select a folder to run Stage 1 validation."] = "1단계 검증을 실행할 폴더를 선택하십시오.",
        ["Report: not generated"] = "보고서: 미생성",
        ["Preview Selected"] = "선택 항목 미리보기",
        ["Export CSV"] = "CSV 내보내기",
        ["Export Annotated Images"] = "주석 이미지 내보내기",
        ["Export Stage 1 Validation Package"] = "1단계 검증 패키지 내보내기",
        ["Ground truth is the reviewed answer used to compare model output."] = "실측 판정은 모델 출력과 비교하기 위해 검토된 정답입니다.",
        ["Pixel Difference, ONNX, or Learned PCB Visual Model. Learned visual models are image-only Stage 1 evidence."] = "픽셀 차이, ONNX 또는 학습된 PCB 시각 모델입니다. 학습된 시각 모델은 이미지 전용 1단계 증빙입니다.",
        ["Checks image files, labels, golden images, and inspection-area metadata before running client validation."] = "고객 검증 실행 전에 이미지 파일, 라벨, 기준 이미지, 검사 영역 메타데이터를 점검합니다.",
        ["False Call: the system marked a good board as a defect."] = "허위 검출: 시스템이 양품 보드를 결함으로 표시했습니다.",
        ["Possible Escape: the system may have missed a real defect."] = "유출 가능: 시스템이 실제 결함을 놓쳤을 수 있습니다.",
        ["False Call: good board marked defect. Possible Escape: real defect may be missed."] = "허위 검출: 양품이 결함으로 표시됨. 유출 가능: 실제 결함이 누락될 수 있음.",
        ["Threshold controls when a score becomes OK, REVIEW, or NG. Drafts still need review before use."] = "임계값은 점수가 OK, REVIEW 또는 NG로 판정되는 기준을 제어합니다. 초안은 사용 전 검토가 필요합니다.",
        ["Applies the recommended threshold to the current draft; verify before production use."] = "권장 임계값을 현재 초안에 적용합니다. 운영 사용 전에 확인하십시오.",
        ["Threshold candidates show the tradeoff between good boards marked defect, possible missed defects, and manual review time."] = "임계값 후보는 양품 결함 판정, 결함 미검출 가능성, 수동 검토 시간 간의 트레이드오프를 보여줍니다.",
        ["ROI means inspection area on the board image checked by the model."] = "ROI는 모델이 검사하는 보드 이미지의 검사 영역을 의미합니다.",
        ["AI means inspection result. GT means ground truth."] = "AI는 검사 결과, GT는 실측 판정을 의미합니다.",
        ["Exports the complete Stage 1 package with manifest, preflight, reports, benchmark evidence, limitations, CSV, and annotated image samples."] = "매니페스트, 사전 점검, 보고서, 벤치마크 증빙, 제한 사항, CSV, 주석 이미지 샘플을 포함한 완전한 1단계 패키지를 내보냅니다.",
        // --- Readiness & QA (ReadinessQaView) ---
        ["Readiness & QA"] = "준비도 / 품질",
        ["Readiness Filters"] = "준비도 필터",
        ["Readiness & QA Evidence"] = "준비도 / 품질 증빙",
        ["Filter management dashboard, pilot issues, and customer package evidence by date, board, and operator."] = "날짜, 보드, 작업자로 관리 대시보드, 파일럿 이슈, 고객 패키지 증빙을 필터링합니다.",
        ["Filtered inspection rows (select one, then capture Good Board Marked Defect or Possible Missed Defect)"] = "필터링된 검사 행 (하나를 선택한 뒤 양품 결함 판정 또는 결함 미검출 가능을 캡처)",
    };

    /// <summary>
    /// Read-only view of the English-to-Korean UI dictionary, exposed for localization parity tests.
    /// </summary>
    public static IReadOnlyDictionary<string, string> KoreanTranslations => KoreanText;

    private static UiPreferences? _cached;

    public static event Action? PreferencesChanged;

    public static string SettingsPath => Path.Combine(StorageRootSettingsService.SettingsDirectory, "ui_preferences.json");

    public static UiPreferences Load()
    {
        if (_cached is not null)
            return Clone(_cached);

        try
        {
            if (File.Exists(SettingsPath))
            {
                _cached = JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(SettingsPath)) ?? new UiPreferences();
                Normalize(_cached);
                return Clone(_cached);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"UI preferences could not be loaded; defaults will be used: {ex.Message}");
        }

        _cached = new UiPreferences();
        return Clone(_cached);
    }

    public static void Save(UiPreferences preferences)
    {
        Normalize(preferences);
        Directory.CreateDirectory(StorageRootSettingsService.SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(preferences, JsonOptions));
        _cached = Clone(preferences);
        ApplyToApplication(_cached);
        PreferencesChanged?.Invoke();
    }

    public static void ResetForTests()
    {
        _cached = null;
        PreferencesChanged = null;
    }

    public static string Text(string english, string korean)
        => Load().Language == UiLanguage.Korean ? korean : english;

    public static bool AreEquivalent(UiPreferences left, UiPreferences right)
    {
        Normalize(left);
        Normalize(right);
        return left.Language == right.Language &&
            left.FontPreset == right.FontPreset &&
            left.Theme == right.Theme &&
            left.ResolutionPreset == right.ResolutionPreset &&
            string.Equals(left.ConsoleTitle, right.ConsoleTitle, StringComparison.Ordinal) &&
            string.Equals(left.StationDisplayName, right.StationDisplayName, StringComparison.Ordinal) &&
            string.Equals(left.StationSubtitle, right.StationSubtitle, StringComparison.Ordinal) &&
            string.Equals(left.AccentColor, right.AccentColor, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.BrandLogoPath, right.BrandLogoPath, StringComparison.OrdinalIgnoreCase);
    }

    public static void ApplyToApplication(UiPreferences? preferences = null, bool resizeWindowToPreset = false)
    {
        var current = preferences ?? Load();
        var culture = current.Language == UiLanguage.Korean ? new CultureInfo("ko-KR") : new CultureInfo("en-US");
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        var app = Application.Current;
        if (app is null)
            return;

        if (!app.Dispatcher.CheckAccess())
        {
            try
            {
                app.Dispatcher.BeginInvoke(() => ApplyWindowPreferences(current, culture, resizeWindowToPreset));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"UI preferences could not be dispatched to the application: {ex.Message}");
            }

            return;
        }

        ApplyWindowPreferences(current, culture, resizeWindowToPreset);
    }

    private static void ApplyWindowPreferences(UiPreferences current, CultureInfo culture, bool resizeWindowToPreset)
    {
        ApplyThemeResources(current.Theme);

        if (Application.Current?.MainWindow is not Window mainWindow)
            return;

        mainWindow.Language = XmlLanguage.GetLanguage(culture.IetfLanguageTag);
        mainWindow.FontFamily = current.Language == UiLanguage.Korean
            ? new FontFamily("Malgun Gothic, Segoe UI")
            : new FontFamily("Segoe UI");

        if (mainWindow.Content is FrameworkElement root)
            root.LayoutTransform = Transform.Identity;

        // Inherited default for any text without an explicit size: the operator floor.
        _ = current.FontPreset;
        mainWindow.FontSize = 18.67;

        var (width, height) = current.ResolutionPreset switch
        {
            UiResolutionPreset.Qhd2560x1440 => (2560d, 1440d),
            UiResolutionPreset.Uhd3840x2160 => (3840d, 2160d),
            _ => (1920d, 1080d),
        };

        mainWindow.MinWidth = 1180;
        mainWindow.MinHeight = 720;
        if (mainWindow.WindowState == WindowState.Normal)
        {
            if (resizeWindowToPreset)
            {
                mainWindow.Width = Math.Min(width, SystemParameters.WorkArea.Width);
                mainWindow.Height = Math.Min(height, SystemParameters.WorkArea.Height);
            }
            else
            {
                mainWindow.Width = Math.Min(mainWindow.Width, SystemParameters.WorkArea.Width);
                mainWindow.Height = Math.Min(mainWindow.Height, SystemParameters.WorkArea.Height);
            }
        }

        ApplyLocalization(mainWindow, current.Language);
    }

    private static void ApplyThemeResources(UiTheme theme)
    {
        // Single-theme: the XAML token definitions (FactoryHmiLayout/App.xaml) are the source
        // of truth and already carry the dark values, so no brush needs rewriting. Reasserting
        // the palette here keeps a settings file that predates light-theme removal from leaving
        // stale app-level overrides behind.
        _ = theme;
        foreach (var token in HmiThemePalette.Tokens)
            SetBrush(token.Key, token.Dark);
    }

    private static void SetBrush(string key, string color)
    {
        if (Application.Current is null)
            return;

        if (Application.Current.TryFindResource(key) is SolidColorBrush existing)
        {
            if (existing.IsFrozen)
            {
                Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
                return;
            }

            existing.Color = (Color)ColorConverter.ConvertFromString(color);
            return;
        }

        Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    public static void ApplyLocalization(DependencyObject root)
        => ApplyLocalization(root, Load().Language);

    public static void ApplyLocalization(DependencyObject root, UiLanguage language)
    {
        var visited = new HashSet<DependencyObject>();
        ApplyLocalizationCore(root, language, visited);
    }

    private static void ApplyLocalizationCore(DependencyObject root, UiLanguage language, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(root))
            return;

        if (root is TextBlock textBlock && textBlock.Name != "PageTitleText")
        {
            var original = ResolveTranslationKey(textBlock, OriginalTextProperty, LastAppliedTextProperty, textBlock.Text);
            var translated = Translate(original, language);
            textBlock.Text = translated;
            textBlock.SetValue(LastAppliedTextProperty, translated);
        }

        if (root is ContentControl contentControl && contentControl.Content is string content)
        {
            var original = ResolveTranslationKey(contentControl, OriginalContentProperty, LastAppliedContentProperty, content);
            var translated = Translate(original, language);
            contentControl.Content = translated;
            contentControl.SetValue(LastAppliedContentProperty, translated);
        }

        if (root is HeaderedContentControl headered && headered.Header is string header)
        {
            var original = ResolveTranslationKey(headered, OriginalHeaderProperty, LastAppliedHeaderProperty, header);
            var translated = Translate(original, language);
            headered.Header = translated;
            headered.SetValue(LastAppliedHeaderProperty, translated);
        }

        if (root is HeaderedItemsControl headeredItems && headeredItems.Header is string itemHeader)
        {
            var original = ResolveTranslationKey(headeredItems, OriginalHeaderProperty, LastAppliedHeaderProperty, itemHeader);
            var translated = Translate(original, language);
            headeredItems.Header = translated;
            headeredItems.SetValue(LastAppliedHeaderProperty, translated);
        }

        if (root is FrameworkElement element && element.ToolTip is string toolTip)
        {
            var original = ResolveTranslationKey(element, OriginalToolTipProperty, LastAppliedToolTipProperty, toolTip);
            var translated = Translate(original, language);
            element.ToolTip = translated;
            element.SetValue(LastAppliedToolTipProperty, translated);
        }

        if (root is DataGrid dataGrid)
        {
            foreach (var column in dataGrid.Columns)
            {
                if (column.Header is not string columnHeader)
                    continue;

                var original = ResolveTranslationKey(column, OriginalHeaderProperty, LastAppliedHeaderProperty, columnHeader);
                var translated = Translate(original, language);
                column.Header = translated;
                column.SetValue(LastAppliedHeaderProperty, translated);
            }
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            ApplyLocalizationCore(child, language, visited);

        if (root is not Visual and not Visual3D)
            return;

        var visualChildren = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < visualChildren; i++)
            ApplyLocalizationCore(VisualTreeHelper.GetChild(root, i), language, visited);
    }

    private static string GetOrStore(DependencyObject owner, DependencyProperty property, string current)
    {
        if (owner.GetValue(property) is string original)
            return original;

        owner.SetValue(property, current);
        return current;
    }

    /// <summary>
    /// Returns the translation key for a localized element without destroying runtime text.
    /// Static literals keep their first-seen text as the key so EN/KO switching works, but when
    /// the app has rewritten the value since the localizer last touched it (dynamic status text
    /// such as alarm counts or footer values), the current value becomes the new key so a
    /// re-apply never stomps live data back to the XAML default.
    /// </summary>
    private static string ResolveTranslationKey(
        DependencyObject owner,
        DependencyProperty originalProperty,
        DependencyProperty lastAppliedProperty,
        string current)
    {
        var original = GetOrStore(owner, originalProperty, current);
        if (owner.GetValue(lastAppliedProperty) is string lastApplied &&
            !string.Equals(current, lastApplied, StringComparison.Ordinal))
        {
            owner.SetValue(originalProperty, current);
            return current;
        }

        return original;
    }

    private static string Translate(string original, UiLanguage language)
        => language == UiLanguage.Korean && KoreanText.TryGetValue(original, out var translated)
            ? translated
            : original;

    private static readonly Lazy<Dictionary<string, string>> KoreanToEnglish = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (english, korean) in KoreanText)
        {
            if (string.Equals(english, korean, StringComparison.Ordinal) || ambiguous.Contains(korean))
                continue;

            if (map.TryGetValue(korean, out var existing))
            {
                if (!string.Equals(existing, english, StringComparison.Ordinal))
                {
                    map.Remove(korean);
                    ambiguous.Add(korean);
                }
            }
            else
            {
                map[korean] = english;
            }
        }

        return map;
    });

    /// <summary>
    /// Maps a Korean UI display string back to the English token it was translated from.
    /// Returns the input unchanged when the value is not a uniquely reversible Korean
    /// translation. Used to heal data persisted by builds that predate ComboBoxTokens,
    /// where the localization walker rewrote ComboBoxItem.Content and views persisted the
    /// translated string (e.g. recipe ROI types saved in Korean mode).
    /// </summary>
    public static string ToEnglishUiToken(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        return KoreanToEnglish.Value.TryGetValue(value, out var english) ? english : value;
    }

    public static Brush AccentBrush(UiPreferences? preferences = null)
    {
        var current = preferences ?? Load();
        Normalize(current);
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(current.AccentColor));
    }

    private static void Normalize(UiPreferences preferences)
    {
        if (!Enum.IsDefined(preferences.Language))
            preferences.Language = UiLanguage.English;
        if (!Enum.IsDefined(preferences.FontPreset))
            preferences.FontPreset = UiFontPreset.Standard;
        if (!Enum.IsDefined(preferences.Theme))
            preferences.Theme = UiTheme.IndustrialDark;
        if (!Enum.IsDefined(preferences.ResolutionPreset))
            preferences.ResolutionPreset = UiResolutionPreset.FullHd1920x1080;
        preferences.ConsoleTitle = NormalizeDisplayText(preferences.ConsoleTitle, UiPreferenceDefaults.ConsoleTitle);
        preferences.StationDisplayName = NormalizeDisplayText(preferences.StationDisplayName, UiPreferenceDefaults.StationDisplayName);
        preferences.StationSubtitle = NormalizeDisplayText(preferences.StationSubtitle, UiPreferenceDefaults.StationSubtitle);
        preferences.AccentColor = NormalizeColor(preferences.AccentColor);
        preferences.BrandLogoPath = preferences.BrandLogoPath?.Trim() ?? string.Empty;
    }

    private static UiPreferences Clone(UiPreferences source)
        => new()
        {
            Language = source.Language,
            FontPreset = source.FontPreset,
            Theme = source.Theme,
            ResolutionPreset = source.ResolutionPreset,
            ConsoleTitle = source.ConsoleTitle,
            StationDisplayName = source.StationDisplayName,
            StationSubtitle = source.StationSubtitle,
            AccentColor = source.AccentColor,
            BrandLogoPath = source.BrandLogoPath,
        };

    private static string NormalizeDisplayText(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string NormalizeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return UiPreferenceDefaults.AccentColor;

        try
        {
            _ = (Color)ColorConverter.ConvertFromString(value.Trim());
            return value.Trim();
        }
        catch (FormatException)
        {
            return UiPreferenceDefaults.AccentColor;
        }
        catch (NotSupportedException)
        {
            return UiPreferenceDefaults.AccentColor;
        }
    }
}
