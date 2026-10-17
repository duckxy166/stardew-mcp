"""pytest setup for the runner tests: no game, no network, no real `claude`."""

import json
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import pytest

RUNNER = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(RUNNER))          # `import usage_limit`, `import run_days`


class FakeGame:
    """Stands in for the mod's HTTP API: /api/state, POST /api/meta, and POST /test/next_day (the AI slept)."""

    def __init__(self):
        self.day = 1
        self.metas: list[dict] = []
        self.lock = threading.Lock()
        game = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def _send(self, obj):
                data = json.dumps(obj).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            def do_GET(self):
                if self.path == "/api/state":
                    with game.lock:
                        self._send({"ready": True, "day": game.day, "saveFolder": "Farm_1"})
                else:
                    self.send_error(404)

            def do_POST(self):
                body = self.rfile.read(int(self.headers.get("Content-Length") or 0)) or b"{}"
                with game.lock:
                    if self.path == "/api/meta":
                        game.metas.append(json.loads(body))
                    elif self.path == "/test/next_day":
                        game.day += 1
                    else:
                        self.send_error(404)
                        return
                self._send({"ok": True})

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.port = self.server.server_address[1]
        self.url = f"http://127.0.0.1:{self.port}"
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def statuses(self) -> list[str]:
        return [m["runnerStatus"] for m in self.metas if "runnerStatus" in m]

    def events(self) -> list[str]:
        return [m["event"] for m in self.metas if "event" in m]

    def close(self):
        self.server.shutdown()
        self.server.server_close()


@pytest.fixture
def game():
    g = FakeGame()
    yield g
    g.close()


@pytest.fixture(autouse=True)
def _mock_watchdog_for_runner_tests(monkeypatch, request):
    if "test_game_watchdog" not in request.node.nodeid:
        import game_watchdog
        old_from_args = game_watchdog.from_args

        def fake_from_args(args, log_dir, api_get, api=game_watchdog.DEFAULT_API):
            if not getattr(args, "game_cmd", "") and getattr(args, "game_process", "") == game_watchdog.default_process_name():
                return game_watchdog.NullWatchdog()
            return old_from_args(args, log_dir, api_get, api)

        monkeypatch.setattr(game_watchdog, "from_args", fake_from_args)

    if "test_pass_out" not in request.node.nodeid:
        import pass_out
        old_let_time = pass_out.let_time_run_out

        def fake_let_time(day, get_state, post, sleep=time.sleep, *, timeout=1800.0, **kwargs):
            return old_let_time(day, get_state, post, sleep, timeout=min(timeout, 0.0), **kwargs)

        monkeypatch.setattr(pass_out, "let_time_run_out", fake_let_time)


