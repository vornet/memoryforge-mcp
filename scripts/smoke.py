"""No third-party Python dependencies. Exercises real MCP stdio against a separate process.
Usage: python scripts/smoke.py --server path/to/memoryforge[.exe] --fixture path/to/fixture[.exe]
"""
import argparse
import json
import os
import queue
import subprocess
import tempfile
import threading
import time


def reader(stream, output):
    for line in stream:
        output.put(line)
    output.put(None)


class Child:
    def __init__(self, command):
        self.errors = tempfile.TemporaryFile(mode="w+t", encoding="utf-8")
        self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=self.errors, text=True, encoding="utf-8")
        self.lines = queue.Queue()
        threading.Thread(target=reader, args=(self.process.stdout, self.lines), daemon=True).start()

    def send(self, value):
        self.process.stdin.write(value + "\n")
        self.process.stdin.flush()

    def receive(self):
        line = self.lines.get(timeout=30)
        if line is None:
            self.errors.seek(0)
            raise RuntimeError("Child exited: " + self.errors.read())
        return json.loads(line)

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()
        self.process.stdout.close()
        self.errors.close()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--server", required=True)
    parser.add_argument("--fixture", required=True)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="memoryforge-smoke-") as directory:
        fixture = Child([os.path.abspath(args.fixture)])
        server = Child([os.path.abspath(args.server), "--audit-log", os.path.join(directory, "audit.jsonl")])
        try:
            ready = fixture.receive()
            next_id = 0

            def rpc(method, params):
                nonlocal next_id
                next_id += 1
                server.send(json.dumps({"jsonrpc": "2.0", "id": next_id, "method": method, "params": params}))
                while True:
                    result = server.receive()
                    if result.get("id") == next_id:
                        assert "error" not in result, result
                        return result["result"]

            initialized = rpc("initialize", {"protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {"name": "memoryforge-smoke", "version": "1"}})
            assert initialized["serverInfo"]["name"] == "memoryforge"
            server.send(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}))
            tools = rpc("tools/list", {})["tools"]
            names = {tool["name"] for tool in tools}
            assert {"start_scan", "write_memory", "resolve_pointer_chain", "observe_memory"} <= names

            def call(tool_name, expect_error=False, **arguments):
                result = rpc("tools/call", {"name": tool_name, "arguments": arguments})
                assert bool(result.get("isError")) == expect_error, result
                if expect_error:
                    return result
                if "structuredContent" in result:
                    structured = result["structuredContent"]
                    return structured.get("result", structured)
                return json.loads(result["content"][0]["text"])

            def field(obj, name):
                # SDK serialization preserves declared DTO casing; anonymous results are camelCase.
                return obj.get(name, obj.get(name[0].upper() + name[1:]))

            def wait(session, scan):
                deadline = time.monotonic() + 20
                while time.monotonic() < deadline:
                    status = call("scan_status", sessionId=session, scanId=scan)
                    state = field(status, "state")
                    if state not in ("queued", "running"):
                        assert state == "completed", status
                        return status
                    time.sleep(0.02)
                raise TimeoutError("Scan did not complete")

            capabilities = call("get_capabilities")
            assert capabilities.get("transport") == "stdio", capabilities
            call("list_processes", name="MemoryForge")
            readonly = call("attach_process", processId=ready["processId"])["sessionId"]
            readonly_error = call("write_memory", expect_error=True, sessionId=readonly, address=ready["target"], kind="Int32", value="10000", expected="1234")
            assert "read-only" in readonly_error["content"][0]["text"], readonly_error
            call("detach_process", sessionId=readonly)
            session = call("attach_process", processId=ready["processId"], writable=True)["sessionId"]
            regions = call("list_regions", sessionId=session, start=ready["start"], end=ready["end"])
            assert regions["total"] > 0
            call("list_modules", sessionId=session)
            scan = call("start_scan", sessionId=session, kind="Int32", value="1234", start=ready["start"], end=ready["end"])
            scan_id = field(scan, "scanId")
            assert field(wait(session, scan_id), "count") == 3
            fixture.send("spend")
            assert fixture.receive()["target"] == 1227
            call("refine_scan", sessionId=session, scanId=scan_id, comparison="DecreasedBy", value="7")
            assert field(wait(session, scan_id), "count") == 1
            found = call("scan_results", sessionId=session, scanId=scan_id)["values"]
            assert int(found[0]["address"], 16) == int(ready["target"], 16)
            chain = call("resolve_pointer_chain", sessionId=session, address=ready["pointer"], offsets=[0])
            assert int(chain["address"], 16) == int(ready["target"], 16)
            precondition_error = call("write_memory", expect_error=True, sessionId=session, address=ready["target"], kind="Int32", value="10000", expected="1234")
            assert "Precondition failed" in precondition_error["content"][0]["text"], precondition_error
            written = call("write_memory", sessionId=session, address=ready["target"], kind="Int32", value="10000", expected="1227")
            assert field(written, "state") == "verified"
            fixture.send("read")
            balances = fixture.receive()
            assert balances == {"target": 10000, "other": 1234, "cache": 1234}, balances
            call("observe_memory", sessionId=session, addresses=[ready["target"]], samples=2, intervalMs=20)
            call("undo_write", sessionId=session, writeId=field(written, "id"))
            assert call("read_memory", sessionId=session, address=ready["target"], kind="Int32")["value"] == "1227"
            pattern = call("start_scan", sessionId=session, pattern="DE AD ?? EF", start=ready["start"], end=ready["end"])
            assert field(wait(session, field(pattern, "scanId")), "count") == 1
            # Bad inputs become MCP tool errors without terminating the server.
            call("read_memory", expect_error=True, sessionId=session, address="0x0", kind="Bytes", length=8)
            call("read_memory", expect_error=True, sessionId=session, address=ready["target"], length=2_000_000)
            call("resolve_pointer_chain", expect_error=True, sessionId=session, address=ready["pointer"], offsets=[0], pointerSize=3)
            call("release_scan", sessionId=session, scanId=scan_id)
            call("detach_process", sessionId=session)
            call("read_memory", expect_error=True, sessionId=session, address=ready["target"], kind="Int32")
            with open(os.path.join(directory, "audit.jsonl"), encoding="utf-8") as stream:
                entries = [json.loads(line) for line in stream]
            assert any(field(entry, "state") == "prepared" for entry in entries)
            print(f"PASS: {len(tools)} MCP tools; separate-process scan/refine/write/verify/undo; pattern boundary; errors; audit")
        finally:
            fixture.close()
            server.close()


if __name__ == "__main__":
    main()
