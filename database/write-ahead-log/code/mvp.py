"""
Write-Ahead Log demo: a tiny key-value store proving the log-before-page invariant.

Every mutation is (1) serialized as a log record, (2) appended to the WAL file
AND fsynced, and only THEN (3) applied to the in-memory "page" dict. If the
process dies at any point, restart replays WAL records after the last checkpoint
to rebuild the in-memory state exactly as it was at the last committed write.

The scenario at the bottom writes a few keys, spawns a child that simulates a
crash mid-run with os._exit (bypassing all Python-level cleanup — mimics kill -9
or a power cut), then reopens the store and shows the recovered state.
"""

import os
import struct
import subprocess
import sys
import zlib

WAL_PATH = "/tmp/wal_demo.log"
SNAP_PATH = "/tmp/wal_demo.snap"

# Record layout: [lsn: u64][crc: u32][op: u8][klen: u16][vlen: u16][key][value]
# op codes: 1 = PUT, 2 = DELETE. The CRC covers (op, klen, vlen, key, value)
# so we can detect a torn tail write from a crash mid-append and stop replay.
HDR = struct.Struct(">QIBHH")


class KVStore:
    def __init__(self, wal_path=WAL_PATH, snap_path=SNAP_PATH):
        self.wal_path = wal_path
        self.snap_path = snap_path
        self.pages = {}   # the "buffer pool" — in-memory state
        self.lsn = 0      # monotonically increasing Log Sequence Number
        self._recover()

    # ---------- recovery: snapshot load + WAL redo pass ----------
    def _recover(self):
        # 1. Load the last checkpoint snapshot (the durable "data file" equivalent).
        base_lsn = 0
        if os.path.exists(self.snap_path):
            with open(self.snap_path, "rb") as f:
                base_lsn = struct.unpack(">Q", f.read(8))[0]
                n = struct.unpack(">I", f.read(4))[0]
                for _ in range(n):
                    kl, vl = struct.unpack(">HH", f.read(4))
                    # Bind key BEFORE value: Python evaluates the RHS of
                    # d[k] = v first, which would swap read order otherwise.
                    k = f.read(kl).decode()
                    self.pages[k] = f.read(vl).decode()
        self.lsn = base_lsn

        # 2. REDO pass: replay every WAL record with lsn > checkpoint lsn.
        # This is ARIES' "repeat history" step, in miniature.
        if not os.path.exists(self.wal_path):
            return
        with open(self.wal_path, "rb") as f:
            while True:
                hdr = f.read(HDR.size)
                if len(hdr) < HDR.size:
                    break  # clean EOF or torn header — stop
                lsn, crc, op, kl, vl = HDR.unpack(hdr)
                body = f.read(kl + vl)
                if len(body) < kl + vl:
                    break  # torn body — this record never fully reached disk
                if zlib.crc32(hdr[12:] + body) != crc:
                    break  # corrupted tail — we've replayed all durable records
                if lsn <= base_lsn:
                    continue  # already reflected in the snapshot; skip
                k, v = body[:kl].decode(), body[kl:].decode()
                if op == 1:
                    self.pages[k] = v
                elif op == 2:
                    self.pages.pop(k, None)
                self.lsn = lsn

    # ---------- the core WAL rule: log-before-page ----------
    def _append(self, op, key, value):
        # THE WAL RULE lives in this method's ordering:
        # (a) build record, (b) write it, (c) fsync it, (d) THEN mutate memory.
        # Reversing (c) and (d) would violate durability — a crash between them
        # would leave in-memory state ahead of what's on disk.
        self.lsn += 1
        kb, vb = key.encode(), value.encode()
        body = kb + vb
        payload = struct.pack(">BHH", op, len(kb), len(vb)) + body
        crc = zlib.crc32(payload)
        hdr = HDR.pack(self.lsn, crc, op, len(kb), len(vb))
        with open(self.wal_path, "ab") as f:
            f.write(hdr + body)
            f.flush()
            os.fsync(f.fileno())  # the durability moment — commit is real HERE

    def put(self, key, value):
        self._append(1, key, value)
        self.pages[key] = value  # only reached after fsync returned

    def delete(self, key):
        self._append(2, key, "")
        self.pages.pop(key, None)

    # ---------- checkpoint: snapshot pages, then truncate the WAL ----------
    def checkpoint(self):
        # Order matters: snapshot must be durable BEFORE we forget the log,
        # otherwise a crash between the two loses committed data.
        tmp = self.snap_path + ".tmp"
        with open(tmp, "wb") as f:
            f.write(struct.pack(">Q", self.lsn))
            f.write(struct.pack(">I", len(self.pages)))
            for k, v in self.pages.items():
                kb, vb = k.encode(), v.encode()
                f.write(struct.pack(">HH", len(kb), len(vb)) + kb + vb)
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp, self.snap_path)          # atomic swap on POSIX
        open(self.wal_path, "wb").close()        # truncate — old records are redundant


# ------------------------- crash-simulation scenario -------------------------

def child_writer():
    """Runs in a subprocess: writes keys, then hard-exits mid-workload."""
    store = KVStore()
    store.put("account:alice", "100")
    store.put("account:bob", "50")
    store.checkpoint()                          # baseline is durably in the snapshot
    store.put("account:alice", "80")            # post-checkpoint mutation #1
    store.put("account:bob", "70")              # post-checkpoint mutation #2
    store.put("account:carol", "999")           # post-checkpoint mutation #3
    # Simulate a power cut: no atexit hooks, no finally blocks, no stdio flush.
    # Only what fsync'd to WAL survives. This is the whole point of the demo.
    os._exit(137)


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "child":
        child_writer()
        return

    # Fresh run: wipe any prior state so the demo is deterministic.
    for p in (WAL_PATH, SNAP_PATH):
        if os.path.exists(p):
            os.remove(p)

    print("[parent] launching writer that will crash mid-workload...")
    r = subprocess.run([sys.executable, __file__, "child"])
    print(f"[parent] writer exited with code {r.returncode} (simulated crash)\n")

    print(f"[parent] on disk after crash:  WAL={os.path.getsize(WAL_PATH)}B  "
          f"snapshot={os.path.getsize(SNAP_PATH)}B\n")

    print("[parent] reopening store (triggers snapshot load + WAL redo pass)...")
    recovered = KVStore()
    print("[parent] recovered state:")
    for k in sorted(recovered.pages):
        print(f"    {k} = {recovered.pages[k]}")
    print(f"[parent] highest LSN replayed: {recovered.lsn}")


if __name__ == "__main__":
    main()
