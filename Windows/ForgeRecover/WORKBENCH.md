# ForgeRecover Workbench

`ForgeRecover.Workbench.exe` is the Windows desktop interface for the ForgeRecover forensic engine. It uses the same core services and evidence rules as the CLI; there is no separate demonstration or mock data path.

## Launch

Install the .NET 8 Desktop Runtime on Windows 10 or Windows 11, then run:

```text
workbench\ForgeRecover.Workbench.exe
```

The CI package is framework-dependent by default. Build with `-SelfContained` to create a larger package that carries its own .NET runtime:

```powershell
.\build.ps1 -Configuration Release -Runtime win-x64 -SelfContained
```

## Main workflow

1. **Select backup**
   - Refresh automatically discovered Apple Devices/iTunes backups.
   - Browse to an imported backup folder containing `Manifest.db`.

2. **Define the case**
   - Enter a case name and examiner.
   - Select a parent folder for case storage.
   - Add authorization, device, incident, or preservation notes.

3. **Choose artifact plugins**
   - Messages
   - Call history
   - Contacts
   - Photos and videos

4. **Create verified case and analyze**
   - Copies the complete backup into `evidence/original`.
   - Computes SHA-256 for every source file.
   - Computes SHA-256 again after copying and rejects mismatches.
   - Writes chain-of-custody events to `logs/chain-of-custody.jsonl`.
   - Re-verifies the completed evidence set.
   - Parses SQLite working copies, including available WAL/SHM sidecars.
   - Writes JSON, CSV, and HTML exports.
   - Loads artifacts into the preview grid.

5. **Review and selectively export**
   - Search across timestamps, type, handle/contact, service, direction, body, and source.
   - Mark individual artifacts or all visible filtered artifacts.
   - Export the selected subset to a separate timestamped folder.

## Case verification

Open an existing ForgeRecover case and choose **Verify hashes**. The workbench recomputes every evidence-file SHA-256 and reports missing or changed files.

A successful extraction does not override an integrity failure. The pipeline aborts before analysis when the evidence copy fails verification.

## Authorized connected-device acquisition

ForgeRecover can orchestrate an Apple-style logical backup through installed libimobiledevice tools:

- `idevice_id`
- `ideviceinfo`
- `idevicebackup2`

In the workbench:

1. Select the folder containing the libimobiledevice executables, or leave it blank when they are on `PATH`.
2. Choose **Detect paired devices**.
3. Select the device identifier.
4. Select an acquisition output parent folder.
5. Choose **Acquire full logical backup**.

The device must be unlocked, trusted, connected by USB, and used with the device owner's authorization. The workbench passes each argument through `ProcessStartInfo.ArgumentList`; it does not invoke a command shell.

## Evidence labels

ForgeRecover currently emits these explicit statuses:

- `present_in_backup_database`
- `present_in_backup_payload`

These labels mean that the item exists in the supplied authorized backup. ForgeRecover does not automatically label blank message text as deleted and does not claim hard-deleted recovery from unused SQLite pages.

## Output layout

```text
Case_Name_TIMESTAMP_GUID/
├── case.json
├── evidence/
│   └── original/
├── working/
├── analysis/
│   ├── working/
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

## Release integrity

Every published suite includes `SHA256SUMS.txt`. Verify it before deployment:

```powershell
$root = Resolve-Path .\forge-recover-windows-x64
Get-Content "$root\SHA256SUMS.txt" | ForEach-Object {
    if ($_ -notmatch '^([a-f0-9]{64})  (.+)$') { return }
    $expected = $Matches[1]
    $relative = $Matches[2].Replace('/', '\\')
    $actual = (Get-FileHash (Join-Path $root $relative) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw "Hash mismatch: $relative" }
}
'All published files match SHA256SUMS.txt.'
```

## Current limitations

- Modern `Manifest.db` backups are supported; legacy `Manifest.mbdb` parsing is not implemented.
- Encrypted local backups must first be unlocked through a supported, authorized workflow with the correct password.
- Apple schema changes may require additional parser profiles; unsupported schemas produce warnings.
- Selective export preserves artifacts outside the device. It does not inject SMS, iMessage, or call-history records back into iOS databases.
- A signed MSIX/MSI installer, code-signing certificate, automatic updates, and licensing service are release-engineering work beyond this foundation.
