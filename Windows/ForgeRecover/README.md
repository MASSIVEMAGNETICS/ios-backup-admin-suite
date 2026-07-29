# ForgeRecover for Windows

ForgeRecover is the production Windows forensic subsystem for `ios-backup-admin-suite`. It creates verifiable case vaults from authorized Apple Devices, iTunes, imported, and libimobiledevice logical backups; resolves payloads through `Manifest.db`; extracts supported artifacts; previews and selectively exports results; and preserves an auditable evidence trail.

The Windows package contains two products built on the same core engine:

- `workbench/ForgeRecover.Workbench.exe` — investigator desktop interface
- `cli/forge-recover.exe` — command-line automation and batch processing

## Current capability

| Layer | Implemented |
|---|---|
| Windows workbench | Backup discovery, case creation, preview/search, item selection, selective export, verification, device acquisition |
| CLI | Discover, acquire, ingest, verify, extract, one-command pipeline, device listing/info |
| Backup discovery | Apple Devices and desktop iTunes backup roots, plus imported custom roots |
| Authorized device acquisition | `idevice_id`, `ideviceinfo`, and `idevicebackup2` orchestration without a command shell |
| Evidence preservation | Streamed copy, source/destination SHA-256 comparison, write-through flush |
| Chain of custody | Append-only JSON Lines event ledger |
| Integrity verification | Full evidence-file SHA-256 revalidation and tamper reporting |
| Backup resolution | Modern `Manifest.db` / `Files` table and direct or sharded payload lookup |
| Messages | SMS/iMessage records present in `sms.db`, handles, direction, service, status metadata |
| Call history | Core Data `ZCALLRECORD` schema |
| Contacts | `ABPerson` and `ABMultiValue` schemas |
| Photos and videos | Camera-roll payload inventory and SHA-256 hashes |
| Selective analysis | Run one or more artifact plugins |
| Exports | Per-plugin JSON, CSV, HTML, combined CSV, and selective workbench exports |
| Testing | Generated SQLite fixtures, integrity/tamper tests, schema-tolerance tests, output-escaping tests |
| CI | Windows CLI/workbench builds, tests, coverage, smoke test, SHA-256 release manifest, x64 publishing |

## What ForgeRecover does not do

ForgeRecover does not bypass device passcodes, Activation Lock, Apple encryption, iCloud authentication, or backup passwords. It does not scrape another person's account, exploit a device, or perform raw SQLite free-page/WAL carving for deleted-content fragments.

A record is labeled as recovered only when it is present in the authorized backup database or payload being analyzed. Empty text is not automatically called deleted.

## Requirements

### Running a published package

- Windows 10 or Windows 11
- .NET 8 Desktop Runtime for the default framework-dependent release
- An Apple Devices/iTunes/imported backup containing `Manifest.db`
- Optional libimobiledevice command-line tools for connected-device logical acquisition
- Device owner consent or other explicit legal authority

### Building

- .NET 8 SDK
- PowerShell 7 or Windows PowerShell 5.1

## Build, test, and publish

```powershell
cd Windows\ForgeRecover
.\build.ps1
```

To produce a larger package that includes the .NET runtime:

```powershell
.\build.ps1 -Configuration Release -Runtime win-x64 -SelfContained
```

Published output:

```text
Windows\ForgeRecover\artifacts\forge-recover-windows-x64\
├── cli\forge-recover.exe
├── workbench\ForgeRecover.Workbench.exe
├── README.md
├── WORKBENCH.md
├── cli-smoke-test.txt
└── SHA256SUMS.txt
```

## Workbench

Run:

```text
workbench\ForgeRecover.Workbench.exe
```

The workbench supports:

1. discovery or manual selection of Apple backup folders;
2. case identity, examiner, output folder, and authorization notes;
3. Messages, call-history, contacts, and media plugins;
4. verified evidence copy before analysis;
5. artifact search, preview, selection, and subset export;
6. full case hash verification;
7. paired-device detection and authorized logical acquisition.

See [WORKBENCH.md](WORKBENCH.md) for operational details.

## CLI commands

### Discover local backups

```powershell
forge-recover discover
forge-recover discover --root "E:\ImportedBackups" --json
```

### Create a forensic case vault

This copies the complete backup, hashes every source and destination file, records chain-of-custody events, and writes `case.json`.

```powershell
forge-recover ingest `
  --source "C:\Users\Bando\Apple\MobileSync\Backup\000081..." `
  --out "D:\Cases" `
  --case "Authorized iPhone Review" `
  --examiner "Brandon Emery"
```

Use `--reference` only when a copy is impossible. A reference case remains dependent on the external source and is weaker evidence than a verified copy.

### Verify a case

```powershell
forge-recover verify --case-root "D:\Cases\Authorized_iPhone_Review_..."
```

Any missing or modified evidence file produces a non-zero exit code.

### Extract artifacts

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

The pipeline refuses to analyze a copied evidence set when hash verification fails.

### Connected devices

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

The device must be unlocked, trusted, connected by USB, and authorized by its owner. ForgeRecover passes arguments directly to `idevicebackup2` through `ProcessStartInfo.ArgumentList` and does not invoke a shell.

## Case layout

```text
Case_Name_YYYYMMDD_HHMMSS_GUID/
├── case.json
├── evidence/
│   └── original/          exact verified backup copy
├── working/               reserved for working copies
├── analysis/
│   ├── working/           copied SQLite databases and available sidecars
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

- a stable SHA-256-derived artifact identifier;
- artifact kind;
- UTC timestamp when the source schema supports one;
- source database or backup path;
- source row ID when applicable;
- extractor-specific metadata;
- `recovery_status`: `present_in_backup_database` or `present_in_backup_payload`.

ForgeRecover reports what the evidence proves and does not inflate uncertainty into a fake recovery claim.

## Architecture

```text
Connected device / authorized Apple backup
                    │
                    ▼
        Acquisition / backup discovery
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
              ┌─────┴─────┐
              ▼           ▼
       WPF workbench     CLI
              │           │
              └─────┬─────┘
                    ▼
          JSON / CSV / HTML exports
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

This branch delivers the real Windows engine, CLI, investigator workbench, tests, and CI publishing foundation. A public commercial release still needs a code-signing certificate, signed MSIX/MSI installer, encrypted-backup unlock support using a user-supplied password, wider iOS-version schema fixtures, accessibility and localization testing, licensing/update infrastructure, privacy and support policies, and an independent security review.
