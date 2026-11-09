"""Manual test: needs a real car with YFT liveries plus a folder of PNGs (the automated version, on a synthetic car,
is src/LiveryKit/EndToEndTest.cs).

Drives the MCP server over stdio the way an AI app does, on fresh copies of the car.

usage: python -I tests/mcp_e2e.py <work dir> <demo car dir> <art dir> <server command...>
"""
import json
import os
import shutil
import subprocess
import sys
import threading
from pathlib import Path

work, democar, art = Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3])
cmd = sys.argv[4:]
if work.exists():
    shutil.rmtree(work)
work.mkdir(parents=True)
car_export = work / "car_export"
car_install = work / "car_install"
shutil.copytree(democar, car_export)
shutil.copytree(democar, car_install)

# YFTLIVERY_NO_CONFIRM stops the install Yes/No box from blocking the run.
p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=dict(os.environ, YFTLIVERY_NO_CONFIRM="1"))
log = []
threading.Thread(target=lambda: log.extend(p.stderr.read().decode("utf-8", "replace").splitlines()), daemon=True).start()
next_id = 0


def send(obj):
    p.stdin.write((json.dumps(obj) + "\n").encode("utf-8"))
    p.stdin.flush()


def call(method, params=None):
    global next_id
    next_id += 1
    msg = {"jsonrpc": "2.0", "id": next_id, "method": method}
    if params is not None:
        msg["params"] = params
    send(msg)
    line = p.stdout.readline().decode("utf-8")
    reply = json.loads(line)  # anything that isn't one JSON message per line fails here
    assert reply["id"] == next_id, reply
    assert "\n" not in line.rstrip("\n")
    return reply


def tool(name, args):
    r = call("tools/call", {"name": name, "arguments": args})["result"]
    text = r["content"][0]["text"]
    print(f"\n--- {name} (isError={r['isError']}) ---\n{text}")
    return r["isError"], text


init = call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "e2e", "version": "1"}})["result"]
print("initialize:", init["protocolVersion"], init["serverInfo"])
send({"jsonrpc": "2.0", "method": "notifications/initialized"})
assert call("ping")["result"] == {}
tools = [t["name"] for t in call("tools/list")["result"]["tools"]]
print("tools:", tools)
assert tools == ["find_cars", "make_liveries"]

err, text = tool("find_cars", {"folder": str(car_export)})
assert not err and "democar_livery_4" in text and "next one is democar_livery_5" in text

err, text = tool("make_liveries", {
    "folder": str(car_export),
    "liveries": [
        {"image": str(art / "chequered_flag.png"), "name": "Chequered Flag"},
        {"image": str(art / "blue_wave.png"), "replace": "democar_livery_2"},
    ],
    "output_folder": str(work / "out"),
})
assert not err and "democar_livery_5.yft \"Chequered Flag\" (new" in text and "democar_livery_2.yft \"Racing Stripes\" (replaced" in text, text
assert "10 mipmaps" in text

# Bare path in the list, quotes as from Explorer's "Copy as path", straight into the resource.
err, text = tool("make_liveries", {
    "folder": f'"{car_install}"',
    "liveries": [str(art / "blue_wave.png")],
    "install": True,
    "output_folder": str(work / "out_install"),
})
assert not err and "straight into" in text and "democar_livery_5.yft" in text, text

err, text = tool("make_liveries", {"folder": str(car_export), "liveries": [{"image": str(art / "missing.png")}]})
assert err and "Image not found" in text, text

p.stdin.close()
code = p.wait(timeout=30)
print("\nexit code:", code)
print("stderr log:", *log, sep="\n  ")
assert code == 0
print("\nALL OK")
