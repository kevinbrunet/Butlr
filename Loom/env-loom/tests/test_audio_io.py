from __future__ import annotations

import threading

from loom_orchestrator.audio_io import DropOldestBridge


def test_drop_oldest_bridge_preserves_order_under_normal_load() -> None:
    bridge = DropOldestBridge(maxsize=10)
    for i in range(5):
        bridge.put_from_callback(bytes([i]))

    received = [bridge.get_blocking()[0] for _ in range(5)]

    assert received == [0, 1, 2, 3, 4]
    assert bridge.dropped == 0


def test_drop_oldest_bridge_drops_oldest_when_full() -> None:
    bridge = DropOldestBridge(maxsize=3)
    for i in range(5):
        bridge.put_from_callback(bytes([i]))

    # Les 2 plus anciens (0, 1) ont été perdus — seuls 2, 3, 4 restent.
    received = [bridge.get_blocking()[0] for _ in range(3)]

    assert received == [2, 3, 4]
    assert bridge.dropped == 2


def test_drop_oldest_bridge_thread_safe_producer_never_blocks() -> None:
    # Simule le thread callback audio (jamais la boucle asyncio) qui pousse plus vite que la
    # taille de la file — valide que le producteur ne bloque ni ne lève jamais, et que le
    # compte final (restant dans la file + droppé) reste cohérent avec ce qui a été poussé.
    bridge = DropOldestBridge(maxsize=50)
    n_items = 200

    def _producer() -> None:
        for i in range(n_items):
            bridge.put_from_callback(i.to_bytes(2, "big"))

    thread = threading.Thread(target=_producer)
    thread.start()
    thread.join(timeout=5.0)

    assert thread.is_alive() is False
    assert bridge.qsize() + bridge.dropped == n_items
