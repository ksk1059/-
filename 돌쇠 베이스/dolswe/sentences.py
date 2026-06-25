_ENDERS = ".!?…\n"

class SentenceSplitter:
    def __init__(self, max_len=60):
        self._buf = ""
        self._max_len = max_len

    def feed(self, token: str) -> list[str]:
        self._buf += token
        out = []
        while True:
            idx = next((i for i, c in enumerate(self._buf) if c in _ENDERS), -1)
            if idx >= 0:
                sentence = self._buf[: idx + 1].strip()
                self._buf = self._buf[idx + 1 :]
                if sentence:
                    out.append(sentence)
                continue
            if len(self._buf) >= self._max_len:
                out.append(self._buf.strip())
                self._buf = ""
                continue
            break
        return out

    def flush(self):
        rem = self._buf.strip()
        self._buf = ""
        return rem or None
