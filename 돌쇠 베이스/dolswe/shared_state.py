import threading

class SharedContext:
    def __init__(self):
        self._lock = threading.Lock()
        self._data = {"people_count": 0, "color": None, "scene": None}

    def update(self, **kwargs):
        with self._lock:
            self._data.update(kwargs)

    def snapshot(self) -> dict:
        with self._lock:
            return dict(self._data)
