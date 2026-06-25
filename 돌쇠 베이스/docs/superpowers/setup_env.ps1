$ErrorActionPreference = "Stop"
$py = "C:\Users\User\AppData\Local\Programs\Python\Python312\python.exe"
$venv = "C:\dolswe_env"
$pip = "$venv\Scripts\python.exe"

if (Test-Path $venv) { Remove-Item $venv -Recurse -Force }
& $py -m venv $venv
& $pip -m pip install --upgrade pip

# core app deps. mediapipe = teachable 지각의 손 특징 추출기 (하드코딩 라벨 아님).
& $pip -m pip install faster-whisper==1.0.3 requests ollama==0.3.3 mediapipe==0.10.14 opencv-python==4.10.0.84 sounddevice==0.4.7 numpy==1.26.4 pywebview==5.2 pillow pytest==8.3.2

# melo core + korean runtime deps (transformers pinned to wheel-having version)
& $pip -m pip install torch torchaudio "transformers==4.44.2" num2words pykakasi cn2an pypinyin jieba g2p_en anyascii jamo g2pkk librosa cached_path tqdm soundfile
& $pip -m pip install fugashi unidic-lite eunjeon mecab-ko-dic
# 비전: YOLO-World(인원·옷색·장면) + MediaPipe Hands(손흔들기).
# 모델(yolov8s-worldv2.pt)은 최초 실행 시 자동 다운로드. YOLO-World는 CLIP 필요(아래 git).
& $pip -m pip install ultralytics
& $pip -m pip install "git+https://github.com/ultralytics/CLIP.git"

# melotts source (no deps; we manage deps above)
& $pip -m pip install --no-deps "git+https://github.com/myshell-ai/MeloTTS.git"

# pin protobuf LAST. melo/google deps pull protobuf 7.x which can trip transformers
# message handling; 4.25.x is a known-good baseline. (google-api-core warns; harmless.)
& $pip -m pip install "protobuf==4.25.8"

Write-Output "DONE_SETUP"
