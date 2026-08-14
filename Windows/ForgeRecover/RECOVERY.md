# ForgeRecover — encrypted backup and SQLite recovery layer

This layer extends ForgeRecover with two deliberately separate capabilities:

1. **Authorized encrypted-backup unlock** using the backup password already chosen by the device owner.
2. **SQLite history recovery** from format-valid database free space and checksum-valid WAL frames.

Neither capability bypasses an iPhone passcode, Activation Lock, iCloud authentication, or an unknown backup password.

## Encrypted Apple backup unlock

ForgeRecover keeps the original encrypted backup untouched. The recovery CLI invokes the pinned helper in `tools/forge_ios_backup_unlock.py`, supplies the password through redirected **stdin**, and creates a **derived working copy** containing decrypted `Manifest.db`, decrypted payloads under their original `fileID` shard layout, copied backup metadata, and `unlock-report.json` with hashes and explicit evidence semantics.

Passwords are never accepted as command-line arguments. Interactive entry is preferred; automation can use `--password-env NAME`.

```powershell
cd Windows\ForgeRecover\tools
.\setup-unlock.ps1

..\artifacts\forge-recover-windows-x64\recovery\forge-recover-recovery.exe unlock-backup `
  --backup "D:\Evidence\EncryptedBackup" `
  --out "D:\Cases\working\unlocked" `
  --mode core
```

`core` targets Messages, Call History, Contacts, and the Photos database plus SQLite sidecars when present. `all` reconstructs every regular payload exposed by the backup manifest and can be much larger.

## SQLite/WAL/freelist recovery

```powershell
forge-recover-recovery recover-sqlite `
  --db "D:\Cases\working\sms.db" `
  --wal "D:\Cases\working\sms.db-wal" `
  --out "D:\Cases\analysis\sms-recovery.json"
```

The engine emits fragments classified as:

- `FreelistLeafPage`
- `FreelistTrunkUnused`
- `BTreeFreeblock`
- `BTreeUnallocatedRegion`
- `WalCommittedHistoricalPage`
- `WalCurrentCommittedPage` (only with `--include-current-wal`)
- `WalUncommittedHistoricalPage` (only with `--include-uncommitted-wal`)

Each fragment records source file, byte offset, page/frame identity, commit state, encoding, fragment SHA-256, stable fragment ID, confidence band, and recovery status.

### Evidence semantics

A recovered **fragment is not automatically a deleted row**. It proves that bytes exist in a particular SQLite free-space or page-history location. Full row reconstruction requires a later layer that understands b-tree cells, varints, serial types, overflow pages, and table schema.

By default ForgeRecover validates WAL salts and cumulative checksums, considers committed transactions only, collapses multiple writes to the same page within one transaction to the final visible image, excludes the latest committed WAL image for each page because it belongs to current logical database state, suppresses WAL text already present in the main database page, and caps output for damaged/adversarial inputs.

## Corpus validation

A large-corpus claim must be earned with known-positive and known-negative cases. Each controlled case has its database/WAL and a `*.forge-recovery.json` manifest:

```json
{
  "id": "ios18-sms-delete-001",
  "database": "sms.db",
  "wal": "sms.db-wal",
  "expected_contains": ["CONTROLLED_DELETED_MARKER_001"],
  "expected_absent": ["NEVER_CREATED_MARKER_001"],
  "minimum_text_characters": 8,
  "include_uncommitted_wal": false
}
```

Run:

```powershell
forge-recover-recovery validate-corpus `
  --root "D:\ForgeCorpus" `
  --out "D:\ForgeCorpus\validation-report.json"
```

The command returns non-zero if any controlled case misses an expected marker or emits a forbidden marker.

Real-device corpus material must be owned by the examiner or supplied under explicit authorization. Raw personal backups should not be committed to this public repository. Keep real corpus cases in controlled local/encrypted research storage with acquisition hashes and provenance.

## Current boundary

This release establishes page-history and text-fragment recovery. The next maturity gate is schema-aware SQLite record reconstruction:

`page -> b-tree cell -> payload/overflow -> record header -> serial types -> columns -> row identity -> artifact correlation`

Until that gate is implemented and independently validated, ForgeRecover must not present a freelist/WAL fragment as a reconstructed SMS, iMessage, contact, call, or photo record.
