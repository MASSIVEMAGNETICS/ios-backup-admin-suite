#!/usr/bin/env python3
"""ForgeRecover encrypted iOS-backup unlock helper.

Consumes the user-supplied iTunes/Finder backup password on stdin, never argv.
Produces a DERIVED working copy: decrypted Manifest.db plus decrypted payloads
stored under the original fileID shard layout so ForgeRecover's existing resolver
can analyze it. This does not bypass or guess a password.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import sys
import time

PINNED_VERSION = "0.9.0"
CORE_PATHS = {
    "Library/SMS/sms.db",
    "Library/SMS/sms.db-wal",
    "Library/SMS/sms.db-shm",
    "Library/CallHistoryDB/CallHistory.storedata",
    "Library/CallHistoryDB/CallHistory.storedata-wal",
    "Library/CallHistoryDB/CallHistory.storedata-shm",
    "Library/CallHistory/call_history.db",
    "Library/CallHistory/call_history.db-wal",
    "Library/CallHistory/call_history.db-shm",
    "Library/AddressBook/AddressBook.sqlitedb",
    "Library/AddressBook/AddressBook.sqlitedb-wal",
    "Library/AddressBook/AddressBook.sqlitedb-shm",
    "Media/PhotoData/Photos.sqlite",
    "Media/PhotoData/Photos.sqlite-wal",
    "Media/PhotoData/Photos.sqlite-shm",
}


def _load_package():
    try:
        import iphone_backup_decrypt  # type: ignore
        from iphone_backup_decrypt import EncryptedBackup  # type: ignore
        from iphone_backup_decrypt import utils  # type: ignore
    except Exception as exc:
        raise RuntimeError(
            "iphone-backup-decrypt is not installed. Run tools/setup-unlock.ps1 first."
        ) from exc

    version = getattr(iphone_backup_decrypt, "__version__", None)
    if version is not None and version != PINNED_VERSION:
        raise RuntimeError(
            f"Unsupported iphone-backup-decrypt version {version!r}; expected {PINNED_VERSION}."
        )
    return EncryptedBackup, utils, version or "unknown"


def _sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def _safe_reset_output(output: Path) -> None:
    output.mkdir(parents=True, exist_ok=True)
    marker = output / ".forge-derived-working-copy"
    if any(output.iterdir()) and not marker.exists():
        raise RuntimeError(
            "Output directory is not empty and is not a ForgeRecover derived working copy."
        )
    marker.write_text("derived working copy; not original evidence\n", encoding="utf-8")


def _copy_metadata_files(backup: Path, output: Path) -> None:
    for name in ("Info.plist", "Manifest.plist", "Status.plist"):
        src = backup / name
        if src.is_file():
            shutil.copy2(src, output / name)


def unlock(args: argparse.Namespace) -> int:
    EncryptedBackup, utils, package_version = _load_package()
    backup_dir = Path(args.backup).expanduser().resolve()
    output_dir = Path(args.out).expanduser().resolve()
    if not backup_dir.is_dir():
        raise RuntimeError(f"Backup directory does not exist: {backup_dir}")
    if not (backup_dir / "Manifest.plist").is_file():
        raise RuntimeError("Manifest.plist is missing; this is not a modern Apple backup directory.")

    password_text = sys.stdin.readline().rstrip("\r\n")
    if not password_text:
        raise RuntimeError("No backup password was supplied on stdin.")

    _safe_reset_output(output_dir)
    started = time.time()
    extracted = 0
    skipped = 0
    failures: list[dict[str, str]] = []

    encrypted = EncryptedBackup(backup_directory=str(backup_dir), passphrase=password_text)
    password_text = ""
    encrypted.test_decryption()
    encrypted.save_manifest_file(str(output_dir / "Manifest.db"))
    _copy_metadata_files(backup_dir, output_dir)

    # Version-pinned internal API is intentionally isolated to this helper. It is used
    # so output can retain the original fileID sharding expected by ForgeRecover.
    encrypted._read_and_unlock_keybag()  # noqa: SLF001
    with encrypted.manifest_db_cursor() as cursor:
        cursor.execute(
            "SELECT fileID, domain, relativePath, flags, file FROM Files "
            "WHERE flags=1 ORDER BY domain, relativePath"
        )
        rows = cursor.fetchall()

    for file_id, domain, relative_path, flags, file_bplist in rows:
        del flags
        if args.mode == "core" and relative_path not in CORE_PATHS:
            skipped += 1
            continue
        if not file_id or not isinstance(file_id, str) or len(file_id) < 2:
            skipped += 1
            continue

        source = backup_dir / file_id[:2] / file_id
        if not source.is_file():
            source = backup_dir / file_id
        if not source.is_file():
            failures.append({"file_id": file_id, "path": relative_path, "error": "payload missing"})
            continue

        destination = output_dir / file_id[:2] / file_id
        destination.parent.mkdir(parents=True, exist_ok=True)
        try:
            file_plist = utils.FilePlist(file_bplist)
            encryption_key = file_plist.encryption_key
            if encryption_key is None:
                shutil.copy2(source, destination)
            else:
                inner_key = encrypted._keybag.unwrapKeyForClass(  # noqa: SLF001
                    file_plist.protection_class,
                    encryption_key,
                )
                encrypted._decrypt_file_to_disk(  # noqa: SLF001
                    file_id=file_id,
                    key=inner_key,
                    file_plist=file_plist,
                    output_filepath=str(destination),
                )
            extracted += 1
        except Exception as exc:
            failures.append({
                "file_id": file_id,
                "domain": domain,
                "path": relative_path,
                "error": f"{type(exc).__name__}: {exc}",
            })

    report = {
        "format": "ForgeRecover.EncryptedBackupUnlockReport.v1",
        "source_backup": str(backup_dir),
        "derived_output": str(output_dir),
        "mode": args.mode,
        "dependency": f"iphone-backup-decrypt=={package_version}",
        "manifest_plist_sha256": _sha256(backup_dir / "Manifest.plist"),
        "encrypted_manifest_db_sha256": _sha256(backup_dir / "Manifest.db"),
        "decrypted_manifest_db_sha256": _sha256(output_dir / "Manifest.db"),
        "payloads_decrypted_or_copied": extracted,
        "payloads_skipped_by_mode": skipped,
        "failures": failures,
        "elapsed_seconds": round(time.time() - started, 3),
        "evidence_semantics": "derived_working_copy_not_original_evidence",
    }
    (output_dir / "unlock-report.json").write_text(
        json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8"
    )
    print(json.dumps({
        "ok": len(failures) == 0,
        "output": str(output_dir),
        "extracted": extracted,
        "failures": len(failures),
    }))
    return 0 if len(failures) == 0 else 5


def self_test() -> int:
    _, _, version = _load_package()
    print(json.dumps({"ok": True, "dependency_version": version, "pinned": PINNED_VERSION}))
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--self-test", action="store_true")
    sub = parser.add_subparsers(dest="command")
    unlock_parser = sub.add_parser("unlock")
    unlock_parser.add_argument("--backup", required=True)
    unlock_parser.add_argument("--out", required=True)
    unlock_parser.add_argument("--mode", choices=("core", "all"), default="core")
    args = parser.parse_args()

    try:
        if args.self_test:
            return self_test()
        if args.command == "unlock":
            return unlock(args)
        parser.error("choose unlock or --self-test")
        return 2
    except Exception as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 4


if __name__ == "__main__":
    raise SystemExit(main())
