# OpenUltData Changelog

## v2.0 - Enterprise Commercial Edition (Current)

This release represents a complete architectural rewrite of the GUI and core engine to match commercial-grade tools like Tenorshare UltData.

### 🗑️ Removed (Refactored)
*   **Old UI Architecture:** The basic `OpenUltData` class and its simple `QTabWidget` layout have been removed.
*   **Simple Previews:** The basic tree view for data has been replaced with a more robust, style-aware component.
*   **Limited Data Types:** The previous hardcoded map of ~8 data types has been replaced.

### ✨ Added (New Features)
*   **Enterprise GUI:** New `EnterpriseMainWindow` using a Sidebar (`QListWidget`) + Stacked Pages (`QStackedWidget`) layout.
*   **Splash Screen:** Professional startup screen with loading indicators.
*   **Expanded Engine:** `DATA_TYPES_DB_MAP` now supports **35+ data types** (WhatsApp, WeChat, Viber, Discord, Tinder, etc.).
*   **Audit Logging:** New on-screen console (`QPlainTextEdit`) for real-time forensic logs.
*   **System Repair:** Dedicated tab for iOS system fixes (DFU mode integration).
*   **Styling:** Full QSS (Qt Style Sheets) implementation for a Windows 10/11 native look.

## v1.0 - Basic Edition (Deprecated)
*   Initial release with basic SQLite parsing and limited UI.
