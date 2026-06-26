import os

# 모델
STT_MODEL = "small"         # faster-whisper, int8, ko (base→small: 한국어 정확도 큰 향상)
LLM_MODEL = "exaone3.5:2.4b"  # Ollama (대안: "qwen2.5:3b")

# LLM 속도 옵션 (Ollama)
LLM_KEEP_ALIVE = "30m"      # 모델 메모리 상주 (턴 사이 재로드 방지 = 핵심)
LLM_NUM_PREDICT = 150       # 응답 최대 토큰 (짧게 → 빠르게)
LLM_NUM_CTX = 8192          # 컨텍스트 길이 = "잔" 윈도우 (확보한 자원만큼 키움)
LLM_NUM_THREAD = 6          # CPU 스레드. 생성 중 비전(mediapipe/YOLO/CLIP)·TTS 굶김 방지
                            # (너무 높이면 대화 중 카메라 인식이 끊김)
LLM_TEMPERATURE = 0.5       # 낮을수록 일관·집중 (주제 이탈/횡설수설 감소)

# "잔" 메모리 (대화 누적·압축·소멸)
_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MEM_PATH = os.path.join(_ROOT, "data", "memory.json")  # 재시작해도 누적 유지
MEM_TOKEN_BUDGET = 6000     # 윈도우 토큰 예산 (num_ctx에서 응답/여유 제외분)
MEM_HOT_MAX_TURNS = 16      # 원문 보존 최근 턴 수 (넘으면 요약으로 밀림)
MEM_COMPRESS_BATCH = 4      # 한 번에 요약할 오래된 턴 묶음 크기
MEM_SUMMARY_NUM_PREDICT = 60  # 요약 길이 상한

# 가르칠 수 있는 지각 (teachable perception) — 라벨 미리 안 정함, 사용자가 가르침
TEACH_PATH = os.path.join(_ROOT, "data", "teach.json")  # 배운 예시 영속
PERCEPT_FPS = 6             # 지각 처리 FPS (손/사물 특징)
PERCEPT_STABLE_FRAMES = 8   # 같은 미지 특징이 이만큼 지속되면 "물어볼 만큼 안정"
PERCEPT_ASK_INTERVAL = 30.0  # "이거 뭐야?" 질문 최소 간격(초)
TEACH_PENDING_TTL = 20.0    # 질문 후 사용자 답을 가르침으로 받는 유효시간(초)
HAND_MATCH_THRESH = 0.65    # 손 포즈 L2 거리 임계 (작을수록 엄격)
OBJ_MATCH_THRESH = 0.86     # 사물 CLIP 코사인 임계 (클수록 엄격)
CLIP_MODEL = "ViT-B/32"     # 사물 임베딩용 (lazy 로드)

# STT 잡음 억제
STT_VAD_FILTER = True       # whisper 내장 VAD로 비음성 구간 제거
STT_BEAM_SIZE = 5           # 빔서치 폭 (1=그리디 빠름/부정확, 5=느려도 정확)
STT_MIN_SECONDS = 0.4       # 이보다 짧은 소리는 무시
STT_MAX_NOSPEECH = 0.7      # no_speech_prob 이상이면 잡음으로 보고 버림
STT_MIN_LOGPROB = -1.2      # avg_logprob 이하면 신뢰 낮음 → 버림 (너무 높으면 정상발화 손실)
STT_SILENCE_RMS = 0.020     # 발화 시작/끝 RMS 게이트 (배경잡음↑ 환경이면 올림)

# 타이밍
CAPTION_SECONDS = 3.0       # 사용자 자막 표시 시간

# 장면/사람/옷색 (YOLO-World, 오픈보캐브 디텍션)
WORLD_MODEL = "yolov8s-worldv2.pt"  # models/ 아래 (최초 자동 다운로드)
WORLD_FPS = 2               # YOLO-World 처리 FPS (무거움 → 낮게)
PERSON_CONF = 0.25          # 사람 카운트 신뢰도 임계
SCENE_CONF = 0.30           # 사물 감지 신뢰도 임계 (0.10은 헛검출 폭주 → 올림)
# 감지할 항목 (영문 프롬프트 → 한국어 라벨). person은 인원수로 별도 처리.
SCENE_ITEMS = [
    ("person", "사람"), ("glasses", "안경"), ("sunglasses", "선글라스"),
    ("hat", "모자"), ("cap", "모자"), ("mask", "마스크"),
    ("cell phone", "휴대폰"), ("cup", "컵"), ("bottle", "병"),
    ("backpack", "가방"), ("handbag", "가방"), ("headphones", "헤드폰"),
    ("camera", "카메라"),
]

# 선제 인사 (사람 오면 한 번)
GREET_ON_ARRIVAL = True     # False면 먼저 말 걸지 않음 (입력에만 반응)
PRESENT_FRAMES = 4          # 이만큼 연속 감지되면 "사람 있음" 으로 확정 (4fps ≈ 1초)
ABSENT_FRAMES = 16          # 이만큼 연속 미감지(4fps ≈ 4초)면 "없음" → 재인사 가능
                            # 깜빡임으로 인한 반복 인사 방지용 히스테리시스
GREET_MIN_INTERVAL = 25.0   # 인사 사이 최소 간격(초) — 연속 인사 절대 차단 백스톱

# 비전 스무딩 (오인/깜빡임 완화 — 단발 판정 대신 최근 N프레임 다수결)
COLOR_SMOOTH_FRAMES = 12    # 옷색 다수결 창
COUNT_SMOOTH_FRAMES = 5     # 사람수 다수결 창

# 색
ACCENT = "#004AFF"
# (h_low, h_high, s_min, v_min, 이름) — OpenCV HSV: H 0~179
COLOR_TABLE = [
    (0, 10, 80, 80, "빨간"),
    (170, 179, 80, 80, "빨간"),
    (11, 25, 80, 80, "주황"),
    (26, 34, 80, 80, "노란"),
    (35, 85, 60, 60, "초록"),
    (86, 100, 60, 60, "하늘"),
    (101, 130, 60, 60, "파란"),
    (131, 160, 60, 60, "보라"),
    (161, 169, 60, 60, "분홍"),
]
GRAY_NAME = "무채색"        # 채도 낮을 때
WHITE_V = 200              # 밝고 채도낮음 → 흰
BLACK_V = 70               # 이보다 어두우면 검은 (실내 검은옷 V는 50~110 → 50은 못 잡음)
SAT_GRAY = 50              # 이보다 채도 낮으면 무채색/흰
CHROMA_V_MIN = 90          # 색조 신뢰 최소 밝기 (어두우면 hue 노이즈 → 검은 처리)
CHROMA_S_MIN = 70          # 색조 신뢰 최소 채도 (약한 채도는 색 단정 안 함)

# 큐 크기
INPUT_QUEUE_MAX = 8
SENTENCE_QUEUE_MAX = 16
