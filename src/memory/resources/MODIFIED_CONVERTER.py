"""Prepare immutable, content-addressed UnityFS file caches without changing VARs."""
from __future__ import annotations

import argparse
from contextlib import contextmanager
import ctypes
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
from typing import Iterator
import uuid
import zipfile
import zlib
import lzma

import fingerprint
import unityfs

SCHEMA = 1


def json_write(path: Path, value: object) -> None:
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".pending")
    try:
        temporary.write_text(json.dumps(value, ensure_ascii=True, indent=2) + "\n", encoding="utf-8")
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


@contextmanager
def lock(root: Path, digest: str) -> Iterator[None]:
    """OS-owned lock releases even if a cache worker is killed."""
    locks = root / "locks"
    locks.mkdir(exist_ok=True)
    with (locks / (digest + ".lock")).open("a+b") as stream:
        stream.seek(0)
        if os.name == "nt":
            import msvcrt
            import time
            while True:
                try:
                    msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
                    break
                except OSError as error:
                    if error.errno not in (13, 36):
                        raise
                    time.sleep(0.1)
            try:
                yield
            finally:
                stream.seek(0)
                msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
        else:
            import fcntl
            fcntl.flock(stream, fcntl.LOCK_EX)
            try:
                yield
            finally:
                fcntl.flock(stream, fcntl.LOCK_UN)


def valid_hit(root: Path, digest: str) -> dict | None:
    index = root / (digest + ".json")
    if not index.exists():
        return None
    try:
        record = json.loads(index.read_text(encoding="utf-8"))
        filename = record["filename"]
        target = root / filename
        if Path(filename).name != filename or target.is_symlink() or not target.is_file():
            return None
        if record["schema"] != SCHEMA or record["inputSHA256"] != digest:
            return None
        if target.stat().st_size != record["output"]["archiveBytes"]:
            return None
        if fingerprint.sha(target) != record["output"]["archiveSHA256"]:
            return None
        if not set(record["output"]["compressionCodes"]) <= {0, 2, 3}:
            return None
        return record
    except (ValueError, KeyError, OSError, TypeError):
        return None


def cache(source: Path, root: Path, input_hash: str | None = None) -> dict:
    root.mkdir(parents=True, exist_ok=True)
    root = root.resolve()
    digest = input_hash or fingerprint.sha(source)
    with lock(root, digest):
        if record := valid_hit(root, digest):
            return dict(record, path=str(root / record["filename"]), hit=True)
        with source.open("rb") as stream:
            info = unityfs.header(stream)
        required = 2 * sum(b[0] for b in info["blocks"]) + source.stat().st_size + 64 * 1024 * 1024
        if shutil.disk_usage(root).free < required:
            raise OSError("insufficient disk space for immutable cache preparation")
        with tempfile.TemporaryDirectory(prefix="build_", dir=root) as temporary:
            temporary = Path(temporary)
            target = temporary / "output.assetbundle"
            if any(b[2] & 63 == 1 for b in info["blocks"]):
                original = fingerprint.Fingerprint(info)
                unityfs.encode(source, target, temporary / "blocks", original)
                before = dict(**original.result(), version=info["version"], player=info["player"][:-1].decode(),
                              engine=info["engine"][:-1].decode(), digest=info["digest"].hex(),
                              directorySHA256=hashlib.sha256(info["directoryBytes"]).hexdigest())
                converted = True
            else:
                before = fingerprint.audit(source)
                shutil.copyfile(source, target)
                converted = False
            after = fingerprint.audit(target)
            fingerprint.assert_equal(before, after)
            if 1 in after["compressionCodes"] or (converted and after["largestDeclaredBlock"] > unityfs.CHUNK):
                raise ValueError("cache still requires whole LZMA decompression")
            filename = digest + "." + after["archiveSHA256"] + ".assetbundle"
            published = root / filename
            if published.exists():
                if published.is_symlink() or fingerprint.sha(published) != after["archiveSHA256"]:
                    filename = digest + "." + uuid.uuid4().hex + ".assetbundle"
                    published = root / filename
            if not published.exists():
                os.rename(target, published)
            record = dict(schema=SCHEMA, inputSHA256=digest, filename=filename, converted=converted, output=after,
                          nativeFilesNeverDeleted=True)
            json_write(root / (digest + ".json"), record)
            if fingerprint.sha(published) != after["archiveSHA256"]:
                raise ValueError("published cache hash mismatch")
            return dict(record, path=str(published), hit=False)


def compressed_identity(archive: zipfile.ZipFile, info: zipfile.ZipInfo) -> str:
    """Hash every compressed source byte; retain ZipFile.open's header checks."""
    identity = hashlib.sha256()
    identity.update(json.dumps([info.filename, info.compress_type, info.flag_bits, info.CRC,
                                info.file_size, info.compress_size], separators=(",", ":")).encode("utf-8"))
    with archive.open(info) as checked:
        raw = archive.fp
        if raw is None:
            raise ValueError("closed source archive")
        start = raw.tell()
        left = info.compress_size
        while left:
            data = raw.read(min(left, unityfs.CHUNK))
            if not data:
                raise EOFError("truncated compressed source")
            identity.update(data)
            left -= len(data)
        raw.seek(start)
    return identity.hexdigest()


def prepare_package(package: Path, entry: str, expected_size: int, root: Path) -> dict:
    root.mkdir(parents=True, exist_ok=True)
    before = package.stat()
    with zipfile.ZipFile(package) as archive:
        if sum(info.filename == entry for info in archive.infolist()) != 1:
            raise ValueError("missing/ambiguous ZIP entry")
        info = archive.getinfo(entry)
        if info.file_size != expected_size:
            raise ValueError("ZIP entry size differs from game catalogue")
        compressed = compressed_identity(archive, info)
    after = package.stat()
    if (after.st_size, after.st_mtime_ns, after.st_ino) != (before.st_size, before.st_mtime_ns, before.st_ino):
        raise ValueError("source changed during compressed validation")
    receipts = root / "entry-receipts"
    receipts.mkdir(exist_ok=True)
    receipt = receipts / (compressed + ".json")
    if receipt.exists():
        try:
            row = json.loads(receipt.read_text("utf-8"))
            digest = row["inputSHA256"]
            checksum = hashlib.sha256(json.dumps([row["schema"], row["compressedSHA256"], digest], separators=(",", ":")).encode("ascii")).hexdigest()
            if row["schema"] == 1 and row["compressedSHA256"] == compressed and row["checksum"] == checksum and isinstance(digest, str) and len(digest) == 64 and all(c in "0123456789abcdef" for c in digest):
                with lock(root, digest):
                    result = valid_hit(root, digest)
                    if result is not None:
                        final = package.stat()
                        if (final.st_size, final.st_mtime_ns, final.st_ino) != (before.st_size, before.st_mtime_ns, before.st_ino):
                            raise ValueError("source changed during cache validation")
                        return dict(result, path=str(root.resolve() / result["filename"]), hit=True,
                                    package=str(package), entry=entry, originalEntryBytes=expected_size,
                                    originalPackageUnchanged=True, compressedReceiptHit=True,
                                    extractedBytes=0, compressedBytesHashed=info.compress_size)
        except (ValueError, KeyError, OSError, TypeError):
            pass
    result = prepare_package_original(package, entry, expected_size, root)
    final = package.stat()
    if (final.st_size, final.st_mtime_ns, final.st_ino) != (before.st_size, before.st_mtime_ns, before.st_ino):
        raise ValueError("source changed before receipt publication")
    # Recheck compressed identity after the original decoder/CRC validation.
    # A concurrent equal-size/timestamp rewrite never pairs old and new bytes.
    with zipfile.ZipFile(package) as archive:
        if compressed_identity(archive, archive.getinfo(entry)) != compressed:
            raise ValueError("source content changed before receipt publication")
    checksum = hashlib.sha256(json.dumps([1, compressed, result["inputSHA256"]], separators=(",", ":")).encode("ascii")).hexdigest()
    json_write(receipt, dict(schema=1, compressedSHA256=compressed, inputSHA256=result["inputSHA256"], checksum=checksum))
    return dict(result, compressedReceiptHit=False, extractedBytes=expected_size,
                compressedBytesHashed=info.compress_size * 2)


def prepare_package_original(package: Path, entry: str, expected_size: int, root: Path) -> dict:
    root.mkdir(parents=True, exist_ok=True)
    stat = package.stat()
    with tempfile.TemporaryDirectory(prefix="input_", dir=root) as temporary:
        source = Path(temporary) / "input.assetbundle"
        digest, total = hashlib.sha256(), 0
        with zipfile.ZipFile(package) as archive:
            if sum(info.filename == entry for info in archive.infolist()) != 1:
                raise ValueError("missing/ambiguous ZIP entry")
            info = archive.getinfo(entry)
            if info.file_size != expected_size:
                raise ValueError("ZIP entry size differs from game catalogue")
            with archive.open(info) as incoming, source.open("xb") as outgoing:
                while data := incoming.read(unityfs.CHUNK):
                    digest.update(data)
                    outgoing.write(data)
                    total += len(data)
        after = package.stat()
        if total != expected_size or (after.st_size, after.st_mtime_ns, after.st_ino) != (stat.st_size, stat.st_mtime_ns, stat.st_ino):
            raise ValueError("source changed during package extraction")
        result = cache(source, root, digest.hexdigest())
        return dict(result, package=str(package), entry=entry, originalEntryBytes=total, originalPackageUnchanged=True)


def process_counters() -> ctypes.Structure | None:
    if os.name != "nt":
        return None
    class Counters(ctypes.Structure):
        _fields_ = [("cb", ctypes.c_ulong), ("faults", ctypes.c_ulong)] + [(name, ctypes.c_size_t) for name in (
            "peakWS", "ws", "peakPaged", "paged", "peakNonPaged", "nonPaged", "pagefile", "peakPagefile", "private")]
    counters = Counters()
    counters.cb = ctypes.sizeof(counters)
    kernel, psapi = ctypes.WinDLL("kernel32"), ctypes.WinDLL("psapi")
    kernel.GetCurrentProcess.restype = ctypes.c_void_p
    psapi.GetProcessMemoryInfo.argtypes = [ctypes.c_void_p, ctypes.POINTER(Counters), ctypes.c_ulong]
    if not psapi.GetProcessMemoryInfo(kernel.GetCurrentProcess(), ctypes.byref(counters), counters.cb):
        raise ctypes.WinError()
    return counters


def peak_working_set() -> int:
    counters = process_counters()
    return counters.peakWS if counters is not None else 0


def worker_private_bytes() -> int:
    counters = process_counters()
    return counters.private if counters is not None else 0


def main() -> None:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="mode", required=True)
    sub.add_parser("serve")
    prepare = sub.add_parser("prepare")
    prepare.add_argument("--package", type=Path, required=True)
    prepare.add_argument("--entry", required=True)
    prepare.add_argument("--size", type=int, required=True)
    prepare.add_argument("--cache", type=Path, required=True)
    file = sub.add_parser("file")
    file.add_argument("source", type=Path)
    file.add_argument("--cache", type=Path, required=True)
    audit = sub.add_parser("audit")
    audit.add_argument("source", type=Path)
    args = parser.parse_args()
    if args.mode == "serve":
        sys.stdin.reconfigure(encoding="utf-8-sig")
        for line in sys.stdin:
            try:
                request = json.loads(line)
                result = prepare_package(Path(request["package"]), request["entry"], int(request["size"]), Path(request["cache"]))
                result["workerPeakWorkingSetBytes"] = peak_working_set()
                result["workerPrivateBytes"] = worker_private_bytes()
                response = dict(ok=True, result=result)
            except (OSError, EOFError, ValueError, KeyError, TypeError, zipfile.BadZipFile, RuntimeError, zlib.error, lzma.LZMAError) as error:
                response = dict(ok=False, error=type(error).__name__ + ": " + str(error))
            print(json.dumps(response, ensure_ascii=True, separators=(",", ":")), flush=True)
        return
    if args.mode == "prepare":
        result = prepare_package(args.package, args.entry, args.size, args.cache)
    elif args.mode == "file":
        result = cache(args.source, args.cache)
    else:
        result = fingerprint.audit(args.source)
    result["workerPeakWorkingSetBytes"] = peak_working_set()
    print(json.dumps(result, ensure_ascii=True, separators=(",", ":")), flush=True)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
