import os, re, threading, queue, tempfile
os.environ.setdefault("MECABRC", r"C:\mecabkodic\mecabrc")  # 안전장치(없어도 동작)
import numpy as np
import soundfile as sf
import sounddevice as sd
from melo.api import TTS
from dolswe.audio_utils import amplitude_envelope

# 한글/영문/숫자/기본 문장부호만 허용. 이모지·기호는 MeloTTS의 C++ g2p/MeCab를
# 네이티브 크래시(프로세스 종료, 트레이스백 없음)시킬 수 있어 합성 전에 제거.
_TTS_ALLOWED = re.compile(r"[^가-힣ㄱ-ㅣ0-9A-Za-z\s.,?!~…\-'\"()%]")


def clean_for_tts(text):
    return re.sub(r"\s+", " ", _TTS_ALLOWED.sub("", text or "")).strip()

class TtsWorker:
    def __init__(self, on_amplitude=None, on_done=None, on_speak=None):
        self._on_amplitude = on_amplitude or (lambda e: None)
        self._on_done = on_done or (lambda: None)
        self._on_speak = on_speak or (lambda t: None)
        self._model = TTS(language="KR", device="cpu")
        self._spk = self._model.hps.data.spk2id["KR"]
        self._tmp = os.path.join(tempfile.gettempdir(), "dolswe_tts.wav")
        self._q = queue.Queue()
        self._running = False
        self._interrupt = threading.Event()
        self._thread = None
        self._synth("안녕")  # 워밍업 (첫 호출 ~8s 지연 흡수)

    def _synth(self, text):
        self._model.tts_to_file(text, self._spk, self._tmp, speed=1.0, quiet=True)
        samples, rate = sf.read(self._tmp, dtype="int16")
        return samples, rate

    def start(self):
        self._running = True
        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._thread.start()

    def stop(self):
        self._running = False

    def say(self, text):
        self._q.put(text)

    def stop_current(self):
        # 인터럽트 신호만 set (sd.stop()을 외부 스레드에서 부르면 PortAudio
        # access violation으로 크래시). 재생 중단은 TTS 스레드가 직접 수행.
        self._interrupt.set()
        try:
            while True:
                self._q.get_nowait()
        except queue.Empty:
            pass

    def _play(self, samples, rate):
        # 자기(TTS) 스레드 소유 OutputStream에 청크로 write. 인터럽트 시 즉시 중단.
        ch = samples.shape[1] if samples.ndim > 1 else 1
        with sd.OutputStream(samplerate=rate, channels=ch, dtype=samples.dtype) as out:
            i, n = 0, len(samples)
            while i < n and not self._interrupt.is_set():
                out.write(samples[i:i + 2048])
                i += 2048

    def _loop(self):
        while self._running:
            try:
                text = self._q.get(timeout=0.3)
            except queue.Empty:
                continue
            self._interrupt.clear()
            speakable = clean_for_tts(text)
            if not speakable:
                self._on_speak(text)  # 말할 게 없어도(이모지뿐 등) 자막은 띄움
                continue
            try:
                samples, rate = self._synth(speakable)
            except Exception as e:
                print("TTS 합성 실패:", e)
                continue
            if self._interrupt.is_set():
                continue
            self._on_speak(text)  # 자막은 원문(읽기용), 실제 재생 시점에 띄움
            self._on_amplitude(amplitude_envelope(samples, n_bins=20))
            try:
                self._play(samples, rate)
            except Exception as e:
                print("TTS 재생 실패:", e)
            if self._q.empty():
                self._on_done()
