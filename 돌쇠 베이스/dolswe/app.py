# dolswe/app.py
import os, threading, queue, json, time, faulthandler
faulthandler.enable()  # 네이티브 크래시(segfault) 발생 시 C 스택을 stderr(→로그)에 덤프
from dolswe import config

# torch/BLAS가 전 코어를 독식하지 않게 캡 (torch import 전에 env 세팅). 대화 중 비전·TTS·
# LLM이 코어를 두고 오버서브스크립션되는 걸 막아 간헐 끊김 완화.
for _v in ("OMP_NUM_THREADS", "MKL_NUM_THREADS", "OPENBLAS_NUM_THREADS"):
    os.environ.setdefault(_v, str(config.TORCH_NUM_THREADS))
try:
    import torch
    torch.set_num_threads(config.TORCH_NUM_THREADS)
except Exception:
    pass

import webview
import cv2
from dolswe.shared_state import SharedContext
from dolswe.brain import Brain
from dolswe.tts import TtsWorker
from dolswe.stt import SttWorker
from dolswe.vision_world import VisionWorld
from dolswe.perception import Perception
from dolswe.teach import TeachStore, parse_label, is_teaching
from dolswe.memory import extract_pin
from dolswe import preview

import re as _re
# 사용자가 "본 것"을 물을 때만 카메라 컨텍스트 주입 (평소엔 빼서 혼잣말 묘사 방지)
_VISION_Q = _re.compile(
    r"보여|보이|뭐.{0,3}(있|보|들)|무슨\s*색|몇.{0,2}명|들고|입었|입고|손|앞에|누구|이거|저거|장면")


def _wants_vision(text):
    return bool(_VISION_Q.search(text or ""))


class Dolswe:
    def __init__(self):
        self.ctx = SharedContext()
        self.brain = Brain()
        self.input_q = queue.Queue(maxsize=config.INPUT_QUEUE_MAX)
        self.window = None
        self.ready = False
        self.cap = self._open_camera()
        self._latest_frame = None  # grabber가 채움 (두 비전 스레드의 동시 cap.read 경합 방지)
        self._running_cam = True   # grabber 루프 가동 플래그
        self.tts = None  # FIX 1: defer construction to start_workers
        self.vision_world = None   # YOLO-World: 인원/옷색/장면 + 인사 트리거
        self.perception = None     # teachable 지각 (손/사물 → 사용자가 가르침)
        self.store = TeachStore(config.TEACH_PATH)
        self._pending = None       # (kind, vec, ts) — "이거 뭐야?" 물은 뒤 답 대기
        self.stt = None
        self._gen = 0  # FIX 4: barge-in generation counter
        self._responding = False  # 응답 처리 중이면 인사 양보
        self._preview_on = False  # 카메라 프리뷰 창 on/off

    def _open_camera(self):
        # Win11에서 기본 MSMF 백엔드는 멈추거나 못 여는 경우 많음 → DSHOW 우선, 0~3 스캔
        for backend in (cv2.CAP_DSHOW, cv2.CAP_ANY):
            for idx in range(4):
                cap = cv2.VideoCapture(idx, backend)
                if cap.isOpened() and cap.read()[0]:
                    print(f"카메라 OK: index={idx} backend={backend}")
                    return cap
                cap.release()
        print("카메라 못 찾음 (다른 앱 점유/권한/장치 없음)")
        return cv2.VideoCapture(0)  # 폴백(아마 실패) — isOpened()로 이후 분기

    def _grab_loop(self):
        # 단일 스레드만 cap.read() (동시 호출은 멈춤/손상 유발). 최신 프레임만 보관.
        fails = 0
        while self._running_cam:
            ok, f = (self.cap.read() if self.cap and self.cap.isOpened() else (False, None))
            if ok:
                self._latest_frame = f
                fails = 0
            else:
                fails += 1
                if fails == 30:  # ~0.3s 연속 실패 → 카메라 끊김 추정, 재연결 시도
                    print("[cam] 프레임 실패 연속 → 재연결 시도", flush=True)
                    try:
                        if self.cap:
                            self.cap.release()
                    except Exception:
                        pass
                    self.cap = self._open_camera()
                    fails = 0
                time.sleep(0.01)

    def _frame(self):
        return self._latest_frame

    # --- 카메라 프리뷰 (메인 UI 통합, 토글) ---
    def toggle_preview(self):
        self._preview_on = not self._preview_on
        if not self._preview_on:
            self._js("dolswe.setPreview(null)")  # 끄면 창 숨김
        return self._preview_on

    def _preview_loop(self):
        while True:
            if not (self._preview_on and self.ready):
                time.sleep(0.2)
                continue
            frame = self._latest_frame
            if frame is None:
                time.sleep(0.1)
                continue
            snap = self.ctx.snapshot()
            info = [f"사람 {snap.get('people_count', 0)}명  옷색 {snap.get('color') or '-'}"]
            if snap.get("percept"):
                info.append(f"인식: {snap['percept']}")
            if snap.get("scene"):
                info.append(f"장면: {snap['scene']}")
            boxes = self.vision_world.last_boxes if self.vision_world else []
            pts = self.perception.last_hand_pts if self.perception else None
            try:
                b64 = preview.render(frame, boxes, pts, info)
            except Exception:
                b64 = None
            if b64:
                self._js(f"dolswe.setPreview('{b64}')")
            time.sleep(1 / 12.0)

    def _js(self, code):
        # FIX 2: guard on both window existence and page-ready flag
        if not self.window or not self.ready:
            return
        try:
            self.window.evaluate_js(code)
        except Exception:
            pass

    def _set_speaking(self, on):
        # FIX 4: helper so callers don't inline the JS string
        self._js(f"dolswe.setSpeaking({'true' if on else 'false'})")

    def _set_bot_caption(self, text):
        self._set_thinking(False)  # 첫 발화 시작 → 생각 중 숨김
        self._js(f"dolswe.setBotCaption({json.dumps(text, ensure_ascii=False)})")

    def _set_thinking(self, on):
        self._js(f"dolswe.setThinking({'true' if on else 'false'})")

    def _on_amp(self, env):
        # FIX 4: use _set_speaking helper; two separate calls for clarity
        level = max(env) if env else 0.0
        self._js("dolswe.setSpeaking(true)")
        self._js(f"dolswe.setAmplitude({level:.3f})")

    def _bump_generation(self):
        # FIX 4: increment gen and drain queue so in-flight response is abandoned
        self._gen += 1
        try:
            while True:
                self.input_q.get_nowait()
        except queue.Empty:
            pass

    # --- 입력 경로 ---
    def _user_input(self, text):
        # 사용자 입력은 항상 선점: 진행 중 발화나 대기 중 혼잣말(인사)을 끊고 바로 처리
        if self.tts:
            self.tts.stop_current()
        self._set_speaking(False)
        self._bump_generation()
        self._js(f"dolswe.setUserCaption({json.dumps(text, ensure_ascii=False)})")
        if self._try_teach(text):   # 가르침이면 LLM 안 거치고 바로 학습+확인
            return
        if self._try_pin(text):     # "기억해" 류 → 영구 고정 기억
            return
        # 카메라 컨텍스트는 "물어볼 때만" 주입 → 안 물으면 혼잣말로 장면 안 떠듦
        self._enqueue(text, use_context=_wants_vision(text))

    # --- teachable 지각 ---
    def _on_unknown_percept(self, kind, vec):
        # 모르는 게 안정적으로 보이고 봇이 한가하면 "이거 뭐야?" 물어봄
        if self._responding or not self.input_q.empty() or self._pending:
            return
        self._pending = (kind, vec, time.time())
        q = "이 손 모양 뭐야? 뭐라고 부르는지 알려줘." if kind in ("hand", "hand2") \
            else "저거 뭐야? 이름 알려줘."
        self._js(f"dolswe.setBotCaption({json.dumps(q, ensure_ascii=False)})")
        if self.tts:
            self.tts.say(q)

    def _try_teach(self, text):
        # pending(질문 후) 답이거나 명시적 가르침이면 그 특징에 라벨 바인딩
        kind = vec = None
        label = None
        if self._pending and time.time() - self._pending[2] < config.TEACH_PENDING_TTL:
            label = parse_label(text)
            kind, vec = self._pending[0], self._pending[1]
        if not label and is_teaching(text) and self.perception:
            label = parse_label(text)
            kind, vec = self.perception.current_feature()
        if label and kind and vec is not None:
            self.store.add(kind, vec, label)
            self._pending = None
            ack = f"아 이게 {label}구나. 기억했어."
            self._js(f"dolswe.setBotCaption({json.dumps(ack, ensure_ascii=False)})")
            if self.tts:
                self.tts.say(ack)
            return True
        return False

    def _try_pin(self, text):
        # "기억해/외워/잊지마" → 사실을 영구 CORE에 고정 (잔과 달리 소멸 안 함)
        fact = extract_pin(text)
        if not fact:
            return False
        added = self.brain.memory.pin(fact)
        ack = f"기억해뒀어: {fact}" if added else f"이미 기억하고 있어: {fact}"
        self._js(f"dolswe.setBotCaption({json.dumps(ack, ensure_ascii=False)})")
        if self.tts:
            self.tts.say(ack)
        return True

    def on_user_text(self, text):          # pywebview API (타이핑)
        self._user_input(text)

    def _on_stt_final(self, text):
        print(f"[input] STT -> {text!r}", flush=True)  # 출처 확인용 (mic off인데 들어오면 누수)
        self._user_input(text)

    def _on_stt_partial(self, text):
        # FIX 3: use json.dumps instead of repr
        self._js(f"dolswe.setUserCaption({json.dumps(text, ensure_ascii=False)})")

    def _on_new_person(self):
        # 사용자 대화가 진행 중이거나 대기 중이면 혼잣말(인사) 양보
        if self._responding or not self.input_q.empty():
            return
        # 인사 때만 카메라 컨텍스트(사람수/옷색) 주입 → 평소 대화 주제 이탈 방지
        self._enqueue("(앞에 사람이 왔다. 짧게 한 번만 인사해라. 질문을 쏟아내지 마라.)",
                      use_context=True)

    def _enqueue(self, text, use_context=False):
        try:
            self.input_q.put_nowait((text, use_context))
        except queue.Full:
            pass

    # --- 응답 루프 ---
    def _respond_loop(self):
        while True:
            text, use_context = self.input_q.get()
            gen = self._gen  # FIX 4: capture generation at start of response
            self._responding = True   # 인사가 끼어들지 못하게 표시
            self._set_thinking(True)  # 입력 처리 시작 → 생각 중 표시
            # 평소 대화엔 카메라 컨텍스트 주입 안 함(주제 이탈 방지). 인사 때만 주입.
            snap = self.ctx.snapshot() if use_context else {}
            produced = False
            try:
                for sentence in self.brain.respond(text, snap):
                    if gen != self._gen:  # FIX 4: barge-in → abandon in-flight response
                        break
                    produced = True
                    if self.tts:  # FIX 1: guard tts
                        # 자막은 TtsWorker on_speak가 재생 시점에 띄움 (음성과 싱크)
                        self.tts.say(sentence)
                    else:
                        # TTS 없으면 자막만 즉시 표시 (폴백)
                        self._set_bot_caption(sentence)
            finally:
                self._responding = False
            # TTS가 첫 발화 시점에 끔. 무응답이거나 TTS 없을 때만 여기서 끔.
            if not (self.tts and produced):
                self._set_thinking(False)

    def start_workers(self):
        # FIX 1: construct TtsWorker here (after window exists), isolate failure
        try:
            self.tts = TtsWorker(on_amplitude=self._on_amp,
                                 on_done=lambda: self._set_speaking(False),
                                 on_speak=self._set_bot_caption)
            self.tts.start()
        except Exception as e:
            print("TTS 비활성:", e)
            self.tts = None

        if self.cap.isOpened():
            # 카메라 읽기는 이 스레드 하나만 (비전 워커는 캐시된 프레임 사용)
            threading.Thread(target=self._grab_loop, daemon=True).start()
            deadline = time.time() + 3.0  # 첫 프레임 대기 (최대 3초, 실패해도 진행)
            while self._latest_frame is None and time.time() < deadline:
                time.sleep(0.02)
            try:
                self.vision_world = VisionWorld(self.ctx, self._frame, self._on_new_person)
                self.vision_world.start()
            except Exception as e:
                print("VisionWorld 비활성:", e)
                self.vision_world = None
            try:
                self.perception = Perception(self.ctx, self._frame, self.store,
                                             self._on_unknown_percept)
                self.perception.start()
            except Exception as e:
                print("Perception 비활성:", e)
                self.perception = None
        try:
            self.stt = SttWorker(self._on_stt_partial, self._on_stt_final)
            self.stt.start()
        except Exception as e:
            print("STT 비활성(마이크 없음?):", e)
            self.stt = None
        threading.Thread(target=self._respond_loop, daemon=True).start()
        threading.Thread(target=self._preview_loop, daemon=True).start()  # 프리뷰(토글 시만 송출)
        # LLM 미리 메모리에 올려 첫 응답 콜드스타트 제거
        threading.Thread(target=self.brain.warmup, daemon=True).start()

    def toggle_mic(self):
        if not self.stt:
            return False
        on = not self.stt.is_enabled()
        self.stt.set_enabled(on)
        return on

class _Api:
    def __init__(self, app):
        self._app = app
    def on_user_text(self, text):
        self._app.on_user_text(text)
    def toggle_mic(self):
        return self._app.toggle_mic()
    def toggle_preview(self):
        return self._app.toggle_preview()
    def quit(self):
        if self._app.window:
            self._app.window.destroy()

UI_HTML = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ui", "index.html")

def main():
    app = Dolswe()
    api = _Api(app)
    window = webview.create_window("돌쇠", UI_HTML,
                                   js_api=api, fullscreen=True)
    app.window = window
    # FIX 2: flip ready flag only after page has loaded
    window.events.loaded += lambda: setattr(app, "ready", True)
    webview.start(app.start_workers)
    # 창 닫히면(ESC/✕) 데몬 스레드 강제 종료
    os._exit(0)

if __name__ == "__main__":
    main()
