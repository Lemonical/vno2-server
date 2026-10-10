#!/usr/bin/env python3
"""Real WebSocket regression checks. Requires Python 3 and the websockets package.

Run against a fresh Development guest server on port 5167, or use --self-host to
start temporary local servers and additionally check restart and moderation.
"""
import argparse
import asyncio
import contextlib
import json
import os
from pathlib import Path
import socket
import sqlite3
import subprocess
import tempfile
import urllib.request
import uuid

import websockets

URL = "ws://127.0.0.1:5167/ws"
HTTP_URL = "http://127.0.0.1:5167"


async def receive(socket, request_id=None, message_type=None):
    while True:
        message = json.loads(await asyncio.wait_for(socket.recv(), 5))
        if ((request_id is None or message.get("requestId") == request_id)
                and (message_type is None or message["type"] == message_type)):
            return message


async def request(socket, action, data, request_id=None):
    request_id = request_id or uuid.uuid4().hex
    await socket.send(json.dumps(dict(version=1, requestId=request_id, action=action, data=data)))
    while True:
        message = await receive(socket, request_id)
        if message["type"] in ("ack", "error", "welcome"):
            return message


async def snapshot(socket):
    request_id = uuid.uuid4().hex
    await request(socket, "snapshot.get", {}, request_id)
    return (await receive(socket, request_id, "snapshot"))["data"]


async def authority_and_reconnect():
    async with websockets.connect(URL) as alice, websockets.connect(URL) as bob:
        first = await request(alice, "authenticate", {"guestName": "Alice"})
        second = await request(bob, "authenticate", {"guestName": "Bob"})
        assert first["type"] == second["type"] == "welcome"
        alice_identity, bob_identity = first["data"], second["data"]
        alice_id, bob_id = alice_identity["accountId"], bob_identity["accountId"]
        assert (await request(alice, "room.join", {"roomId": "stage"}))["type"] == "ack"
        assert (await request(bob, "room.join", {"roomId": "stage"}))["type"] == "ack"
        assert (await request(alice, "character.select", {"characterId": "not-permitted"}))["data"]["code"] == "character_not_permitted"
        await request(alice, "character.select", {"characterId": "mira"})
        await request(bob, "character.select", {"characterId": "rowan"})
        assert (await request(alice, "dialogue.send", {"text": "Rejected media", "sound": "demo-music"}))["data"]["code"] == "media_not_permitted"
        assert (await request(alice, "dialogue.send", {"text": "Approved sound", "sound": "demo-sound", "effect": "shake"}))["type"] == "ack"
        assert (await request(alice, "media.configure", {"mediaId": "bad", "name": "Bad", "kind": "music", "contentReference": "demo.wav"}))["data"]["code"] == "forbidden"
        styled = await request(alice, "dialogue.send", {"spans": [
            {"text": "Bold", "bold": True, "color": "#3366FF", "pauseAfterMs": 250, "charactersPerSecond": 30},
            {"text": " and italic", "italic": True}]})
        assert styled["type"] == "ack"
        assert (await request(alice, "dialogue.send", {"text": "Mismatch", "spans": [{"text": "Other"}]}))["data"]["code"] == "dialogue_text_mismatch"
        invalid_spans = [None, [None], [{"text": "Bad", "fontSize": 999}],
                         [{"text": "Bad", "color": "red"}], [{"text": "Slow", "charactersPerSecond": 0}],
                         [{"text": "Wait", "pauseAfterMs": 5000}] * 4]
        for invalid in invalid_spans:
            assert (await request(alice, "dialogue.send", {"spans": invalid}))["type"] == "error"
        payload = json.dumps(dict(version=1, requestId="fragment", action="dialogue.send", data={"text": "Hello fragmented world", "position": "center"}))
        await alice.send([payload[:19], payload[19:55], payload[55:]])
        assert (await receive(alice, "fragment", "ack"))["type"] == "ack"
        assert (await request(bob, "dialogue.send", {"text": "Displace", "position": "center"}))["type"] == "ack"
        first_buy = await request(alice, "shop.buy", {"itemId": "potion", "quantity": 2}, "buy-once")
        duplicate = await request(alice, "shop.buy", {"itemId": "potion", "quantity": 2}, "buy-once")
        assert first_buy == duplicate
        conflict = await request(alice, "shop.buy", {"itemId": "potion", "quantity": 3}, "buy-once")
        assert conflict["data"]["code"] == "request_conflict"
        await request(alice, "credits.transfer", {"targetAccountId": bob_id, "amount": 25})
        denied = await request(alice, "credits.grant", {"targetAccountId": alice_id, "amount": 10000})
        assert denied["data"]["code"] == "forbidden"
        moderation = await request(alice, "moderation.ban", {"targetAccountId": bob_id})
        assert moderation["data"]["code"] == "forbidden"
        await request(alice, "inventory.use", {"itemId": "potion"})
        cooldown = await request(alice, "inventory.use", {"itemId": "potion"})
        assert cooldown["data"]["code"] == "cooldown"
        assert (await request(alice, "arithmetic.evaluate", {"expression": "2*(3+4)"}))["data"]["result"]["value"] == 14
        assert (await request(alice, "arithmetic.evaluate", {"expression": "1/0"}))["data"]["code"] == "division_by_zero"
        replay = await request(alice, "replay.get", {"afterSequence": 0})
        accepted_lines = [event for event in replay["data"]["result"]["events"] if event["kind"] == "dialogue.accepted"]
        assert accepted_lines
        styled_line = next(event["data"]["detail"] for event in accepted_lines if event["data"]["detail"]["text"] == "Bold and italic")
        assert styled_line["spans"][0]["bold"] and styled_line["spans"][1]["italic"]
        assert styled_line["spans"][0]["pauseAfterMs"] == 250
        current = await snapshot(alice)
        assert current["resources"]["credits"] == 55
        assert len(current["actors"]) == 1 and current["actors"][0]["accountId"] == bob_id
        assert (await request(alice, "dialogue.clear", {}))["data"]["code"] == "forbidden"
        cleared = await request(bob, "dialogue.clear", {}, "clear-once")
        assert cleared == await request(bob, "dialogue.clear", {}, "clear-once")
        after_clear = await snapshot(bob)
        assert after_clear.get("currentDialogue") is None and after_clear.get("speakerAccountId") is None
        assert len(after_clear["actors"]) == 1 and not after_clear["actors"][0]["isSpeaker"]
        assert (await request(alice, "stage.move", {"position": "left", "scaleX": -1}))["type"] == "ack"
        assert (await request(alice, "dialogue.send", {"text": "Mirrored dialogue", "position": "left", "scaleX": -1}))["type"] == "ack"
        assert next(actor for actor in (await snapshot(alice))["actors"] if actor["accountId"] == alice_id)["scaleX"] == -1
        assert (await request(alice, "dialogue.clear", {}))["type"] == "ack"
    async with websockets.connect(URL) as reconnected:
        welcome = await request(reconnected, "authenticate", {"guestToken": alice_identity["guestToken"]})
        assert welcome["data"]["accountId"] == alice_id
        assert welcome["data"]["snapshot"]["roomId"] == "stage"
        assert welcome["data"]["snapshot"]["resources"]["credits"] == 55
        assert welcome["data"]["snapshot"].get("currentDialogue") is None
    return alice_identity, bob_identity


async def protocol_rejections():
    probes = [
        (json.dumps(dict(version=99, requestId="wrong-version", action="authenticate", data={"guestName": "Invalid"})), "unsupported_version"),
        ("{not valid json", "invalid_json"),
        (b"binary", "text_required"),
        (json.dumps(dict(version=1, requestId="oversized", action="authenticate", data={"guestName": "x" * 66000})), "message_too_large"),
        (json.dumps(dict(version=1, requestId="anonymous", action="room.join", data={"roomId": "stage"})), "authentication_required"),
    ]
    for payload, expected in probes:
        async with websockets.connect(URL) as socket:
            await socket.send(payload)
            message = await receive(socket)
            assert message["type"] == "error" and message["data"]["code"] == expected, message


async def moderation_and_privacy(alice_identity, bob_identity):
    async with websockets.connect(URL) as alice, websockets.connect(URL) as bob, websockets.connect(URL) as observer:
        admin = await request(alice, "authenticate", {"guestToken": alice_identity["guestToken"]})
        assert admin["data"]["snapshot"]["role"] == "administrator"
        assert admin["data"]["snapshot"]["resources"]["credits"] == 55
        assert admin["data"]["snapshot"]["roomId"] == "stage"
        assert admin["data"]["snapshot"].get("currentDialogue") is None
        media = admin["data"]["snapshot"]["mediaCatalog"]
        assert {item["id"] for item in media} >= {"demo-music", "demo-sound", "shake", "flash"}
        assert (await request(alice, "director.music", {"reference": "demo-music"}))["type"] == "ack"
        assert (await request(alice, "director.music", {"reference": "demo.wav"}))["data"]["code"] == "media_not_permitted"
        assert (await snapshot(alice))["music"] == "demo-music"
        assert (await request(alice, "director.music", {"reference": None}))["type"] == "ack"
        assert (await snapshot(alice)).get("music") is None
        assert (await request(alice, "media.configure", {"mediaId": "bad-path", "name": "Bad", "kind": "image", "contentReference": "../secret.png"}))["data"]["code"] == "invalid_reference"
        assert (await request(alice, "director.cue.save", {"cueId": "invalid-cue", "name": "Invalid", "music": "demo-sound"}))["data"]["code"] == "media_not_permitted"
        assert (await request(alice, "director.cue.save", {"cueId": "valid-cue", "name": "Valid", "music": "demo-music", "effect": "flash"}))["type"] == "ack"
        assert (await request(alice, "media.configure", {"mediaId": "demo-music", "name": "Demo music", "kind": "music", "contentReference": "demo.wav", "enabled": False}))["type"] == "ack"
        assert (await request(alice, "director.cue.play", {"cueId": "valid-cue"}))["data"]["code"] == "media_not_permitted"
        await request(bob, "authenticate", {"guestToken": bob_identity["guestToken"]})
        observer_identity = (await request(observer, "authenticate", {"guestName": "Observer"}))["data"]
        await request(observer, "room.join", {"roomId": "stage"})
        bob_id = bob_identity["accountId"]
        await request(alice, "moderation.isolate", {"targetAccountId": bob_id, "reason": "staff-only-sensitive-reason"})
        assert (await request(bob, "dialogue.send", {"text": "isolated-secret-line", "position": "left"}))["type"] == "ack"
        visible = await snapshot(observer)
        assert all(actor["accountId"] != bob_id for actor in visible["actors"])
        assert all(person["accountId"] != bob_id for person in visible["participants"])
        assert "isolated-secret-line" not in json.dumps(visible.get("currentDialogue"))
        assert (await snapshot(bob))["currentDialogue"]["text"] == "isolated-secret-line"
        history = await request(observer, "replay.get", {"afterSequence": 0})
        encoded = json.dumps(history)
        assert "isolated-secret-line" not in encoded and "staff-only-sensitive-reason" not in encoded
        assert not any(event["kind"] == "inventory.used" for event in history["data"]["result"]["events"])
        await request(alice, "moderation.role", {"targetAccountId": observer_identity["accountId"], "role": "moderator"})
        assert "isolated-secret-line" not in json.dumps((await snapshot(observer)).get("currentDialogue"))
        private_history = await request(bob, "replay.get", {"afterSequence": 0})
        assert "isolated-secret-line" in json.dumps(private_history)
        assert (await request(bob, "room.join", {"roomId": "lobby"}))["data"]["code"] == "isolated"
        await request(alice, "character.select", {"characterId": "mira"})
        await request(alice, "stage.move", {"position": "right"})
        await request(alice, "moderation.role", {"targetAccountId": bob_id, "role": "director"})
        for action, data in [
            ("stage.clear", {}),
            ("credits.grant", {"targetAccountId": observer_identity["accountId"], "amount": 100}),
            ("inventory.grant", {"targetAccountId": observer_identity["accountId"], "itemId": "potion"}),
            ("resources.set", {"targetAccountId": observer_identity["accountId"], "hp": 0}),
            ("room.reserve", {"roomId": "lobby", "accountIds": [bob_id]})]:
            assert (await request(bob, action, data))["type"] == "error"
        assert any(actor["accountId"] == alice_identity["accountId"] for actor in (await snapshot(observer))["actors"])
        assert (await snapshot(observer))["resources"]["credits"] == 100
        assert (await snapshot(observer))["resources"]["hp"] == 100
        await request(alice, "moderation.role", {"targetAccountId": bob_id, "role": "moderator"})
        assert (await request(bob, "moderation.mute", {"targetAccountId": observer_identity["accountId"]}))["data"]["code"] == "isolated"
        assert (await request(bob, "moderation.lookup", {}))["data"]["code"] == "isolated"
        assert (await request(bob, "moderation.audit", {}))["data"]["code"] == "isolated"
        await request(alice, "moderation.mute", {"targetAccountId": bob_id})
        assert (await request(bob, "dialogue.send", {"text": "must-not-appear"}))["data"]["code"] == "muted"
        timer = await request(alice, "director.timer", {"label": "Smoke countdown", "seconds": 1})
        assert timer["type"] == "ack"
        await asyncio.sleep(2)
        assert (await snapshot(alice))["timers"] == []
        assert (await request(alice, "catalog.configure", {"characterId": "mira", "name": "Mira", "contentReference": "Demo/Mira", "expressions": ["neutral", "happy"], "reservedAccountId": observer_identity["accountId"]}))["type"] == "ack"
        assert all(actor["accountId"] != alice_identity["accountId"] for actor in (await snapshot(alice))["actors"])
        assert (await request(alice, "character.select", {"characterId": "mira"}))["data"]["code"] == "character_reserved"
        assert (await request(alice, "director.actor", {"targetAccountId": alice_identity["accountId"], "characterId": "mira"}))["data"]["code"] == "character_reserved"
        assert (await request(observer, "character.select", {"characterId": "mira"}))["type"] == "ack"
        assert (await request(alice, "catalog.configure", {"characterId": "mira", "name": "Mira", "contentReference": "Demo/Mira", "expressions": ["neutral", "happy"], "reservedAccountId": None}))["type"] == "ack"
        assert (await request(alice, "character.select", {"characterId": "mira"}))["type"] == "ack"
        await request(alice, "moderation.ban", {"targetAccountId": bob_id})
        await asyncio.sleep(0.1)
        async with websockets.connect(URL) as rejected:
            denied = await request(rejected, "authenticate", {"guestToken": bob_identity["guestToken"]})
            assert denied["type"] == "error" and denied["data"]["code"] == "banned"


async def reads_do_not_grow_ledger(database, identity):
    async with websockets.connect(URL) as socket:
        await request(socket, "authenticate", {"guestToken": identity["guestToken"]})
        with contextlib.closing(sqlite3.connect(database)) as connection:
            before = connection.execute("SELECT COUNT(*),COALESCE(SUM(LENGTH(reply)),0) FROM requests").fetchone()
        for _ in range(5):
            for action in ("replay.get", "snapshot.get", "ping", "moderation.audit", "moderation.lookup", "director.cue.list"):
                assert (await request(socket, action, {}))["type"] == "ack"
        with contextlib.closing(sqlite3.connect(database)) as connection:
            after = connection.execute("SELECT COUNT(*),COALESCE(SUM(LENGTH(reply)),0) FROM requests").fetchone()
        assert before == after, (before, after)


async def burst_capacity():
    sockets = await asyncio.gather(*(websockets.connect(URL) for _ in range(3)))
    try:
        replies = await asyncio.gather(*(request(connection, "authenticate", {"guestName": f"Capacity {index}"})
                                         for index, connection in enumerate(sockets)))
        admitted = [reply for reply in replies if reply["type"] == "welcome"]
        rejected = [reply for reply in replies if reply["type"] == "error"]
        assert len(admitted) == 2 and len(rejected) == 1 and rejected[0]["data"]["code"] == "server_full"
        async with websockets.connect(URL) as reconnect:
            resumed = await request(reconnect, "authenticate", {"guestToken": admitted[0]["data"]["guestToken"]})
            assert resumed["type"] == "welcome", resumed
    finally:
        await asyncio.gather(*(connection.close() for connection in sockets))


@contextlib.contextmanager
def server(database, administrator=None, capacity=64):
    environment = {key: value for key, value in os.environ.items() if not key.lower().startswith("community__")}
    environment.update(ASPNETCORE_ENVIRONMENT="Development", DOTNET_ENVIRONMENT="Development",
                       Community__AllowGuestAuthentication="true", Community__DatabasePath=str(database),
                       Community__MasterBaseUrl="", Community__ServerCredential="", Community__Id="local-community",
                       Community__LocalCapacity=str(capacity))
    if administrator:
        environment["Community__AdministratorAccountIds__0"] = administrator
    assembly = Path(__file__).resolve().parents[1] / "src/VNO2.Server/bin/Release/net10.0/VNO2.Server.dll"
    with tempfile.TemporaryFile() as log:
        process = subprocess.Popen(["dotnet", str(assembly), "--urls", HTTP_URL], env=environment, stdout=log, stderr=log)
        try:
            import time
            for _ in range(100):
                try:
                    urllib.request.urlopen(HTTP_URL + "/health", timeout=0.2).close()
                    break
                except OSError:
                    if process.poll() is not None:
                        log.seek(0)
                        raise RuntimeError(log.read().decode())
                    time.sleep(0.1)
            else:
                raise RuntimeError("Community did not become healthy")
            yield
        finally:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()


def main():
    global URL, HTTP_URL
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--self-host", action="store_true")
    arguments = parser.parse_args()
    if arguments.self_host:
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            port = probe.getsockname()[1]
        HTTP_URL = f"http://127.0.0.1:{port}"
        URL = f"ws://127.0.0.1:{port}/ws"
        with tempfile.TemporaryDirectory(prefix="vno2-community-smoke-") as directory:
            database = Path(directory) / "community.db"
            with server(database):
                identities = asyncio.run(authority_and_reconnect())
                asyncio.run(protocol_rejections())
            # Simulate the prior world format to check the non-destructive v1 -> v2 media migration
            with contextlib.closing(sqlite3.connect(database)) as connection, connection:
                world = json.loads(connection.execute("SELECT json FROM world WHERE id=1").fetchone()[0])
                world.pop("media", None)
                connection.execute("UPDATE world SET json=? WHERE id=1", (json.dumps(world),))
                connection.execute("PRAGMA user_version=1")
            with server(database, identities[0]["accountId"]):
                asyncio.run(moderation_and_privacy(*identities))
                asyncio.run(reads_do_not_grow_ledger(database, identities[0]))
            with server(Path(directory) / "capacity.db", capacity=2):
                asyncio.run(burst_capacity())
        print("PASS community authority, fragmented JSON, replay privacy, duplicate spending, malformed handshake, moderation, approved media/kind/revocation, structured dialogue, read-ledger stability, character reservations, burst capacity, schema migration, timer expiry and process-restart durability")
    else:
        asyncio.run(authority_and_reconnect())
        asyncio.run(protocol_rejections())
        print("PASS fragmented JSON, stage displacement, duplicate spending, transfers, permissions, cooldowns, arithmetic, replay, reconnect and protocol rejections")


if __name__ == "__main__":
    main()
