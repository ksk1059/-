from dolswe.tts import TtsWorker
import time
t = TtsWorker(on_amplitude=lambda e: print("amp", round(max(e), 2)))
t.start()
t.say("안녕 반가워.")
t.say("오늘 행사 재밌지?")
time.sleep(12)
t.stop()
