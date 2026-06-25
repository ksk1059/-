import threading
from dolswe.shared_state import SharedContext

def test_defaults():
    ctx = SharedContext()
    snap = ctx.snapshot()
    assert snap == {"people_count": 0, "color": None, "scene": None}

def test_update_and_snapshot_isolated():
    ctx = SharedContext()
    ctx.update(people_count=2, color="빨간")
    snap = ctx.snapshot()
    snap["people_count"] = 99  # snapshot은 복사본이어야 함
    assert ctx.snapshot()["people_count"] == 2

def test_concurrent_updates():
    ctx = SharedContext()
    def worker():
        for _ in range(1000):
            ctx.update(people_count=1)
    threads = [threading.Thread(target=worker) for _ in range(8)]
    for t in threads: t.start()
    for t in threads: t.join()
    assert ctx.snapshot()["people_count"] == 1
