# ForgeRecover — encrypted backup and SQLite recovery layer

This layer extends ForgeRecover with four deliberately separated capabilities:

1. **Authorized encrypted-backup unlock** using the existing iTunes/Finder backup password.
2. **SQLite fragment recovery** from free space and checksum-valid WAL history.
3. **Schema-aware SQLite row reconstruction** from table-leaf cells, typed record payloads, and overflow chains.
4. **iOS artifact correlation** that maps reconstructed rows into Messages, Call History, and Address Book candidates only when the table/schema evidence supports that mapping.

None of these capabilities bypass an iPhone passcode, Activation Lock, iCloud authentication, or an unknown backup password.

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

## Layer 1 — SQLite fragment recovery

```powershell
forge-recover-recovery recover-sqlite `
  --db "D:\Cases\working\sms.db" `
  --wal "D:\Cases\working\sms.db-wal" `
  --out "D:\Cases\analysis\sms-fragments.json"
```

Fragments are classified as:

- `FreelistLeafPage`
- `FreelistTrunkUnused`
- `BTreeFreeblock`
- `BTreeUnallocatedRegion`
- `WalCommittedHistoricalPage`
- `WalCurrentCommittedPage` (only with `--include-current-wal`)
- `WalUncommittedHistoricalPage` (only with `--include-uncommitted-wal`)

A recovered fragment is **not automatically a deleted row**. It proves that bytes exist in a specific SQLite free-space or page-history location.

## Layer 2 — schema-aware SQLite row reconstruction

```powershell
forge-recover-recovery recover-rows `
  --db "D:\Cases\working\sms.db" `
  --wal "D:\Cases\working\sms.db-wal" `
  --out "D:\Cases\analysis\sms-rows.json"
```

The row engine implements:

- SQLite 1–9 byte varints;
- table-leaf cell payload length and rowid decoding;
- SQLite record-header and serial-type decoding;
- `NULL`, signed integer, IEEE-754 REAL, TEXT, and BLOB storage classes;
- UTF-8, UTF-16LE, and UTF-16BE database text encodings;
- table-leaf local-payload calculation;
- overflow-page chain reconstruction with cycle and safety limits;
- schema loading from `sqlite_schema` and `PRAGMA table_xinfo`;
- table b-tree/root-page ownership mapping;
- current-row indexing for historical-vs-current comparison;
- checksum/salt/commit-valid WAL transaction reconstruction;
- freelist leaf and b-tree free-space row candidates;
- stable record/recovery hashes and source offsets.

Exact historical WAL rows are distinguished from heuristic free-space candidates. Important statuses include:

- `historical_row_absent_current`
- `historical_row_version`
- `historical_row_still_current` (only emitted when explicitly requested)
- `historical_row_candidate_unmapped`
- `wal_current_row` (only when current WAL rows are explicitly included)
- `freelist_leaf_row_candidate_*`
- `btree_freeblock_row_candidate_*`
- `btree_unallocated_row_candidate_*`

`historical_row_absent_current` means that a structurally decoded row existed in a prior committed SQLite state and the same rowid is not present in the currently mapped table. It does **not**, by itself, establish why the row disappeared or that a person deliberately deleted it.

### Current record-format exclusions

- `WITHOUT ROWID` tables are not reconstructed by the table-leaf row decoder.
- Virtual tables are not treated as ordinary table b-trees.
- Free-space candidates are lower-confidence than intact historical WAL cells.
- Damaged/missing overflow pages can prevent full record reconstruction.
- Artifact-specific encoded BLOBs such as attributed-message bodies require artifact decoders beyond generic SQLite record decoding.

## Layer 3 — iOS artifact correlation

```powershell
forge-recover-recovery recover-ios `
  --db "D:\Cases\working\sms.db" `
  --wal "D:\Cases\working\sms.db-wal" `
  --out "D:\Cases\analysis\sms-historical"
```

`recover-ios` executes row reconstruction, then correlates mapped rows against recognized schemas and writes:

```text
sms-historical/
├── sqlite-row-recovery.json
├── ios-artifact-correlation.json
└── artifact-exports/
    ├── extraction-summary.json
    ├── recovered-message-candidates.json/.csv/.html
    ├── recovered-call-candidates.json/.csv/.html
    ├── recovered-contact-candidates.json/.csv/.html
    └── all-artifacts.csv
```

The correlator currently understands:

### Messages

Table: `message`

Named fields include `guid`, `text`, `date`, `handle_id`, `service`, `is_from_me`, `subject`, and attributed-body metadata when present. Handles are resolved from recovered/current `handle` rows when available.

### Call History

Table: `ZCALLRECORD`

Named fields include `ZDATE`, `ZADDRESS`, `ZDURATION`, `ZORIGINATED`, `ZANSWERED`, `ZNAME`, `ZCALLTYPE`, and `ZUNIQUE_ID`.

### Contacts

Table: `ABPerson`, with `ABMultiValue` correlation when available.

Every correlated result retains the row-recovery ID, source kind, page, WAL frame/transaction, record hash, schema mapping method, and confidence. Correlated artifacts carry `deleted_claim=not_asserted`. A schema-consistent historical Messages row is a **historical message row candidate**, not automatically proof of a user-deleted iMessage.

## WAL evidence semantics

By default ForgeRecover:

1. validates WAL salts and cumulative checksums;
2. processes only committed transactions;
3. collapses repeated writes to one page inside a transaction to the final page image;
4. distinguishes historical committed page images from the latest/current logical page image;
5. excludes current WAL rows unless explicitly requested;
6. resolves overflow pages against the historical committed page state being reconstructed;
7. caps frame/page/output traversal for damaged or adversarial inputs.

## Corpus validation

### Fragment corpus

Fragment cases use `*.forge-recovery.json` manifests and run with:

```powershell
forge-recover-recovery validate-corpus `
  --root "D:\ForgeCorpus" `
  --out "D:\ForgeCorpus\fragment-validation.json"
```

### Typed row corpus

Row reconstruction cases use `case.forge-row-recovery.json` and can assert table, rowid, recovery status, minimum confidence, and typed column values:

```json
{
  "id": "ios18-sms-delete-001",
  "database": "sms.db",
  "wal": "sms.db-wal",
  "deviceModel": "iPhone16,1",
  "iosVersion": "18.x",
  "acquisitionSha256": "<case acquisition digest>",
  "expectedRows": [
    {
      "table": "message",
      "rowId": 440,
      "recoveryStatus": "historical_row_absent_current",
      "minimumConfidence": 0.9,
      "columns": [
        { "name": "text", "text": "CONTROLLED_DELETED_MARKER_001" },
        { "name": "is_from_me", "integer": 0 }
      ]
    }
  ],
  "expectedAbsentRows": [
    {
      "table": "message",
      "rowId": 440,
      "recoveryStatus": "historical_row_absent_current",
      "minimumConfidence": 0.9,
      "columns": [
        { "name": "text", "text": "NEVER_CREATED_MARKER_001" }
      ]
    }
  ]
}
```

Run:

```powershell
forge-recover-recovery validate-row-corpus `
  --root "D:\ForgeRowCorpus" `
  --out "D:\ForgeRowCorpus\row-validation.json"
```

The report records per-case database/WAL hashes, positive/negative assertion failures, reconstructed-row counts, device-model coverage, and iOS-version coverage.

## Real-corpus standard

A claim such as **“validated across iOS 16/17/18”** is not earned by generated fixtures. It requires authorized real-device cases with controlled known state transitions, retained source/acquisition hashes, documented iOS/device versions, known-positive and known-negative assertions, and repeatable validation results.

Raw personal backups must not be committed to this public repository. Store real corpus evidence in controlled encrypted research storage and retain provenance separately from public test fixtures.

## Evidence ladder

ForgeRecover intentionally keeps these states separate:

```text
recovered bytes
    ↓
structurally valid SQLite record
    ↓
schema-mapped historical row
    ↓
iOS artifact candidate
    ↓
corroborated recovered artifact
```

No lower level is silently promoted to a stronger evidentiary claim.
