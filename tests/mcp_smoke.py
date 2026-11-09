"""Smoke test for the MCP server: protocol basics and tool errors, no car needed.

usage: python -I tests/mcp_smoke.py <server command...>
  e.g. python -I tests\\mcp_smoke.py dist\\yftlivery.exe mcp
       python -I tests\\mcp_smoke.py dist\\YftLiveryMaker.exe --mcp
"""
import json
import subprocess
import sys
import threading

if len(sys.argv) < 2:
    sys.exit(__doc__)

p = subprocess.Popen(sys.argv[1:], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
stderr = []
threading.Thread(target=lambda: stderr.extend(p.stderr.read().decode("utf-8", "replace").splitlines()), daemon=True).start()

failures = 0
raw_lines = []
next_id = 0


def send(obj):
    p.stdin.write((json.dumps(obj) + "\n").encode("utf-8"))
    p.stdin.flush()


def request(method, params=None):
    global next_id
    next_id += 1
    msg = {"jsonrpc": "2.0", "id": next_id, "method": method}
    if params is not None:
        msg["params"] = params
    send(msg)
    line = p.stdout.readline().decode("utf-8")
    raw_lines.append(line)
    reply = json.loads(line)
    assert reply.get("id") == next_id, f"reply id {reply.get('id')} != {next_id}"
    return reply


def step(name, check):
    global failures
    try:
        check()
        print("PASS", name)
    except Exception as e:  # a bad reply, a dead server or a failed assert all count as a failure
        failures += 1
        print("FAIL", name, "-", repr(e))


def tool(name, args):
    return request("tools/call", {"name": name, "arguments": args})["result"]


def initialize():
    r = request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "smoke", "version": "1"}})["result"]
    assert r["serverInfo"]["name"] == "yft-livery-maker", r
    assert r["protocolVersion"] == "2025-06-18", r


def ping():
    assert request("ping")["result"] == {}


def tools_list():
    names = [t["name"] for t in request("tools/list")["result"]["tools"]]
    assert names == ["find_cars", "make_liveries"], names


def relative_path():
    r = tool("find_cars", {"folder": "cars\\mycar"})
    assert r["isError"] is True, r
    assert "full Windows path" in r["content"][0]["text"], r


def missing_folder():
    assert tool("find_cars", {"folder": "C:\\__yft_no_such_folder__"})["isError"] is True


def unknown_tool():
    err = request("tools/call", {"name": "no_such_tool", "arguments": {}}).get("error")
    assert err and err["code"] == -32602, err


def finish():
    p.stdin.close()
    code = p.wait(timeout=30)
    assert code == 0, f"exit code {code}"
    for line in raw_lines:
        json.loads(line)  # nothing but JSON may reach stdout
    assert p.stdout.read() == b"", "extra output after the last reply"


step("initialize", initialize)
send({"jsonrpc": "2.0", "method": "notifications/initialized"})
step("ping", ping)
step("tools/list", tools_list)
step("find_cars rejects a relative path", relative_path)
step("find_cars on a missing folder is an error", missing_folder)
step("unknown tool is a JSON-RPC error", unknown_tool)
step("clean exit, stdout is only JSON", finish)

if failures:
    print("stderr log:", *stderr, sep="\n  ")
    sys.exit(1)
print("all passed")
