# dolswe/stt.py
import threading
import queue
import time
import numpy as np
import sounddevice as sd
from faster_whisper import WhisperModel
from dolswe import config

SAMPLE_RATE = 16000
BLOCK = 1600          # 0.1s
SILENCE_RMS = config.STT_SILENCE_RMS  # 발화 끝/시작 판정 임계 (배경 잡음 게이트)
SILENCE_BLOCKS = 8    # 0.8s 무음이면 발화 종료
_FILLER_CHARS = set("음어으흠응")  # 추임새/숨소리 환청. 이것들로만 이뤄지면 발화 아님


def _is_filler(text):
    core = "".join(c for c in text if c not in " .,?!~…ㅋㅎ")
    return bool(core) and all(c in _FILLER_CHARS for c in core)

class SttWorker:
    def __init__(self, on_partial, on_final, mic_index=None):
        self._on_partial = on_partial
        self._on_final = on_final
        self._mic_index = mic_index
        self._model = WhisperModel(config.STT_MODEL, device="cpu", compute_type="int8",
                                   cpu_threads=config.STT_CPU_THREADS)
        self._audio_q = queue.Queue()
        self._running = False
        self._enabled = True   # 마이크 on/off
        self._flush = False    # 토글 경계에서 버퍼 폐기 신호
        self._thread = None

    def start(self):
        self._running = True
        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._thread.start()

    def stop(self):
        self._running = False

    def set_enabled(self, on):
        self._enabled = bool(on)
        self._flush = True   # 켜고/끄는 순간 진행 중 버퍼 폐기 (경계 누수 방지)
        if not self._enabled:
            self._drain()    # 끌 때 큐 잔여 오디오 비움 (_loop가 스트림도 닫음)
        print(f"[mic] {'ON' if self._enabled else 'OFF'}", flush=True)

    def is_enabled(self):
        return self._enabled

    def _callback(self, indata, frames, time_info, status):
        if not self._enabled:   # 마이크 off → 오디오를 파이프라인에 아예 안 넣음
            return
        self._audio_q.put(indata.copy())

    def _open_stream(self):
        s = sd.InputStream(samplerate=SAMPLE_RATE, channels=1, dtype="float32",
                           blocksize=BLOCK, device=self._mic_index, callback=self._callback)
        s.start()
        return s

    def _loop(self):
        buf = []
        silence = 0
        stream = None
        while self._running:
            # OFF면 오디오 장치 자체를 닫음 → OS 마이크 해제, 물리적으로 입력 불가
            if self._enabled and stream is None:
                stream = self._open_stream()
            if not self._enabled and stream is not None:
                stream.stop(); stream.close(); stream = None
                buf, silence = [], 0
                self._drain()
            if not self._enabled:
                time.sleep(0.1)
                continue
            try:
                block = self._audio_q.get(timeout=0.5)
            except queue.Empty:
                continue
            if self._flush:  # 토글 직후 진행 중 버퍼 폐기
                buf, silence = [], 0
                self._flush = False
                continue
            rms = float(np.sqrt(np.mean(block ** 2)))
            if rms >= SILENCE_RMS:
                buf.append(block)
                silence = 0
            elif buf:
                silence += 1
                buf.append(block)
                if silence >= SILENCE_BLOCKS:
                    self._transcribe(np.concatenate(buf).flatten())
                    buf, silence = [], 0
        if stream is not None:
            stream.stop(); stream.close()

    def _drain(self):
        try:
            while True:
                self._audio_q.get_nowait()
        except queue.Empty:
            pass

    def _transcribe(self, audio):
        if len(audio) < SAMPLE_RATE * config.STT_MIN_SECONDS:
            return  # 너무 짧은 소리 무시
        segments, _ = self._model.transcribe(
            audio, language="ko", beam_size=config.STT_BEAM_SIZE,
            vad_filter=config.STT_VAD_FILTER, condition_on_previous_text=False)
        parts = []
        for s in segments:
            # 잡음/저신뢰 구간 제거
            if getattr(s, "no_speech_prob", 0.0) > config.STT_MAX_NOSPEECH:
                continue
            if getattr(s, "avg_logprob", 0.0) < config.STT_MIN_LOGPROB:
                continue
            parts.append(s.text)
        text = "".join(parts).strip()
        if _is_filler(text):  # "음", "어어", "흠..." 등 추임새만이면 무시
            return
        if text:
            self._on_partial(text)
            self._on_final(text)
