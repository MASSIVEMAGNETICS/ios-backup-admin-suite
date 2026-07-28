# ForgeRecover for Windows

ForgeRecover is the production Windows forensic engine for `ios-backup-admin-suite`. It creates verifiable case vaults from authorized Apple Devices/iTunes/libimobiledevice logical backups, resolves backup payloads through `Manifest.db`, extracts supported artifacts, and exports reviewable JSON, CSV, and HTML reports.

## Current capability

| Layer | Implemented |
|---|---|
| Backup discovery | Apple Devices and desktop iTunes backup roots, plus custom roots |
| Authorized device acquisition | `idevice_id`, `ideviceinfo`, and `idevicebackup2` orchestration |
| Evidence preservation | Streamed copy, source/destination SHA-256 comparison, write-through flush |
| Chain of custody | Append-only JSON Lines event ledger |
| Integrity verification | Full evidence-file SHA-256 revalidation |
| Backup resolution | Modern `Manifest.db` / `Files` table and sharded payload lookup |
| Messages | SMS/iMessage records present in `sms.db`, handles, direction, status metadata |
| Call history | Core Data `ZCALLRECORD` schema |
| Contacts | `ABPerson` and `ABMultiValue` schemas |
| Photos and videos | Camera-roll payload inventory and SHA-256 hashes |
| Selective analysis | Run one or more artifact plugins |
| Exports | Per-plugin JSON, CSV, HTML, plus combined CSV |
| Testing | Deterministic database fixtures, integrity/tamper tests, escaping tests |
| CI | Windows build, test, coverage collection, and x64 artifact publishing |

## What ForgeRecover does not do

ForgeRecover does not bypass device passcodes, Activation Lock, Apple encryption, iCloud authentication, or backup passwords. It does not scrape another person's account, exploit a device, or perform raw SQLite free-page/WAL carving for deleted-content fragments.

A record is labeled as recovered only when it is present in the authorized backup database or payload being analyzed. Empty text is not automatically called deleted.

## Requirements

- Windows 10 or Windows 11
- .NET 8 SDK for building
- An Apple Devices/iTunes backup folder containing `Manifest.db`
- Optional: libimobiledevice command-line tools for connected-device logical acquisition
- Device owner consent or other explicit legal authority

## Build and test

```powershell
cd Windows\ForgeRecover
.\build.ps1
```

The published executable is written to:

```text
Windows\ForgeRecover\artifacts\forge-recover-win-x64\forge-recover.exe
```

## Commands

### Discover local backups

```powershell
forge-recover discover
forge-recover discover --root "E:\ImportedBackups" --json
```

### Create a forensic case vault

This copies the entire backup, hashes every source and destination file, records chain-of-custody events, and writes `case.json`.

```powershell
forge-recover ingest `
  --source "C:\Users\Bando\Apple\MobileSync\Backup\000081..." `
  --out "D:\Cases" `
  --case "Authorized iPhone Review" `
  --examiner "Brandon Emery"
```

Use `--reference` only when a copy is impossible. Reference cases remain dependent on the external source and are weaker forensic evidence than a verified copy.

### Verify a case

```powershell
forge-recover verify --case-root "D:\Cases\Authorized_iPhone_Review_..."
```

Any missing or modified evidence file returns a non-zero exit code.

### Extract artifacts from an existing backup

```powershell
forge-recover extract `
  --backup "D:\Acquisitions\000081..." `
  --out "D:\Analysis" `
  --type messages,calls,contacts,media
```

Omit `--type` to run every registered extractor.

### One-command preservation and analysis

```powershell
forge-recover pipeline `
  --source "D:\Acquisitions\000081..." `
  --out "D:\Cases" `
  --case "Phone Review" `
  --examiner "Brandon Emery" `
  --type messages,calls,contacts,media
```

The pipeline refuses to analyze the copy when evidence verification fails.

### List connected paired devices

```powershell
forge-recover devices --tool-dir "C:\Tools\libimobiledevice"
forge-recover device-info --udid "00008110..." --tool-dir "C:\Tools\libimobiledevice"
```

### Acquire a full logical backup

```powershell
forge-recover acquire `
  --out "D:\Acquisitions\iPhone" `
  --udid "00008110..." `
  --tool-dir "C:\Tools\libimobiledevice"
```

The device must be unlocked, trusted, connected by USB, and authorized by its owner. ForgeRecover passes arguments directly to `idevicebackup2` without invoking a shell.

## Case layout

```text
Case_Name_YYYYMMDD_HHMMSS_GUID/
├── case.json
├── evidence/
│   └── original/          exact verified backup copy
├── working/               reserved for working copies
├── analysis/
│   ├── working/           SQLite databases and sidecars copied for parsing
│   └── exports/
│       ├── messages.json
│       ├── messages.csv
│       ├── messages.html
│       ├── calls.*
│       ├── contacts.*
│       ├── media.*
│       ├── all-artifacts.csv
│       └── extraction-summary.json
└── logs/
    └── chain-of-custody.jsonl
```

## Evidence semantics

Every artifact includes:

- stable SHA-256-derived artifact identifier
- artifact kind
- UTC timestamp when the source schema supports one
- source database or backup path
- source row ID when applicable
- extractor-specific metadata
- `recovery_status`, currently either `present_in_backup_database` or `present_in_backup_payload`

This distinction matters. ForgeRecover reports what the evidence proves and does not inflate uncertainty into a fake recovery claim.

## Architecture

```text
Connected device / Apple backup
            │
            ▼
     Acquisition adapter
     (libimobiledevice)
            │
            ▼
      Forensic case vault
  SHA-256 + chain of custody
            │
            ▼
     Manifest.db resolver
            │
            ▼
   Read-only working copies
            │
            ▼
 Pluggable artifact extractors
            │
            ▼
     JSON / CSV / HTML
```

## Extension contract

Implement `IArtifactExtractor` and register it in `ArtifactExtractorRegistry`. Extractors must:

1. operate on copied/read-only evidence;
2. never modify the original backup;
3. preserve source paths and row IDs;
4. identify assumptions in metadata or warnings;
5. return no artifacts rather than fabricate unsupported results;
6. respond to cancellation.

## Commercial hardening still required before public sale

This branch delivers the real forensic engine and CLI foundation. A commercial release still needs code signing, an installer, a WPF review UI, encrypted-backup unlock support using a user-supplied password, expanded schema fixtures across iOS versions, localization, accessibility testing, support policy, privacy terms, and independent security review.
