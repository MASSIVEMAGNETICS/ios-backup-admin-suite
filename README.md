# OpenUltData: Commercial-Grade iOS Recovery Tool

This is an open-source clone of commercial iOS data recovery tools (like Tenorshare UltData). It features a modern PySide6 GUI, universal deleted data recovery engine, and AI-powered artifact filtering.

## Features
- **3 Modes:** Recover from Device, iTunes Backup, and iCloud.
- **Universal Recovery:** Supports 35+ data types via extensible SQLite parsing.
- **Deleted Data Carving:** Recovers "soft deleted" (flagged) and "hard deleted" (carved) data.
- **AI Filtering:** Uses a lightweight PyTorch neural network to filter carved string artifacts.
- **Privacy First:** Custom exports (JSON/CSV/HTML) with optional Anonymization and Encryption.

## Prerequisites (Windows)

1.  **Install Python 3.10+** from [python.org](https://www.python.org/downloads/). Ensure you check "Add Python to PATH" during installation.
2.  **Install iTunes** (Desktop version preferred over Store version) to ensure iOS device drivers are present.

## Quick Build (Recommended)

To avoid issues with unrelated libraries on your system (like the Numba/Selenium warnings you might see), use the included build script. It creates a clean, isolated environment and installs a lightweight version of the AI engine.

1.  Double-click **`setup_and_build.bat`**.
2.  Wait for the process to finish.
3.  Your app will be in the `dist/` folder.

## Manual Installation

If you prefer to run from source manually:

1.  Open Command Prompt (cmd) or PowerShell in this folder.
2.  Install dependencies:
    ```bash
    pip install -r requirements.txt
    ```
    *Tip: To save space, install the CPU version of PyTorch first:*
    ```bash
    pip install torch --index-url https://download.pytorch.org/whl/cpu
    ```

## Usage

Run the script directly:
```bash
python main.py
```

### Instructions
1.  **Select Mode:** Choose Device, iTunes, or iCloud tab.
2.  **Select Data:** Check the boxes for the data types you want (Messages, Photos, etc.).
3.  **Scan:** Click "Start Scan".
    *   *Device Mode:* Ensure your iPhone is connected via USB and you have "Trusted" the computer.
    *   *iTunes Mode:* Point to your backup folder if not auto-detected.
4.  **Preview:** Results will appear in the tree view. "High" confidence items are from SQL queries; "Medium" are AI-filtered carvings.
5.  **Export:** Click "Export Results" to save as JSON, CSV, or HTML.

## Troubleshooting

-   **Build Warnings (Numba, Selenium, etc.):** If you see warnings about missing hidden imports for libraries you don't use, it means your global Python environment is cluttered. Use `setup_and_build.bat` to fix this.
-   **Connection Failed:** Ensure iTunes recognizes your device.
-   **iCloud 2FA:** The CLI prompt for 2FA might be hidden in the console; check the terminal window if the GUI hangs during iCloud login.
