# dolswe/stt.py
import threading
import queue
import numpy as np
import sounddevice as sd
from faster_whisper import WhisperModel
from dolswe import config

SAMPLE_RATE = 16000
BLOCK = 1600          # 0.1s
SILENCE_RMS = config.STT_SILENCE_RMS  # 발화 끝/시작 판정 임계 (배경 잡음 게이트)
SILENCE_BLOCKS = 8    # 0.8s 무음이면 발화 종료

class SttWorker:
    def __init__(self, on_partial, on_final, mic_index=None):
        self._on_partial = on_partial
        self._on_final = on_final
        self._mic_index = mic_index
        self._model = WhisperModel(config.STT_MODEL, device="cpu", compute_type="int8")
        self._audio_q = queue.Queue()
        self._running = False
        self._enabled = True   # 마이크 on/off
        self._thread = None

    def start(self):
        self._running = True
        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._thread.start()

    def stop(self):
        self._running = False

    def set_enabled(self, on):
        self._enabled = bool(on)

    def is_enabled(self):
        return self._enabled

    def _callback(self, indata, frames, time_info, status):
        self._audio_q.put(indata.copy())

    def _loop(self):
        buf = []
        silence = 0
        with sd.InputStream(samplerate=SAMPLE_RATE, channels=1, dtype="float32",
                            blocksize=BLOCK, device=self._mic_index, callback=self._callback):
            while self._running:
                try:
                    block = self._audio_q.get(timeout=0.5)
                except queue.Empty:
                    continue
                if not self._enabled:   # 마이크 off → 버퍼 비우고 무시
                    buf, silence = [], 0
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
        if text:
            self._on_partial(text)
            self._on_final(text)
