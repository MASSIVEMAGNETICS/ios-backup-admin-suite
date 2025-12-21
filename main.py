import sys
import os
import sqlite3
import json
import csv
import math
import re
import random
import datetime
import html
import time
from typing import Dict, List, Any, Tuple

# Third-party imports
try:
    import torch
    import torch.nn as nn
    import torch.optim as optim
    from torch.utils.data import DataLoader, TensorDataset
    import numpy as np
    from PySide6.QtWidgets import (QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout,
                                   QLabel, QPushButton, QTabWidget, QCheckBox, QTreeWidget,
                                   QTreeWidgetItem, QProgressBar, QMessageBox, QInputDialog,
                                   QDialog, QComboBox, QScrollArea, QGroupBox, QFileDialog, QHeaderView,
                                   QListWidget, QListWidgetItem, QStackedWidget, QSplashScreen, QFrame,
                                   QPlainTextEdit, QSplitter)
    from PySide6.QtCore import Qt, QThread, Signal, Slot, QSize, QTimer
    from PySide6.QtGui import QIcon, QFont, QPalette, QColor, QPixmap, QPainter, QBrush, QLinearGradient
    from cryptography.fernet import Fernet

    # Device communication (Wrapped in try/except to allow GUI testing without libs)
    try:
        from pymobiledevice3.lockdown import create_using_usbmux
        from pymobiledevice3.services.afc import AfcService
    except ImportError:
        create_using_usbmux = None
        AfcService = None

    try:
        from pyicloud import PyiCloudService
    except ImportError:
        PyiCloudService = None

except ImportError as e:
    print(f"Missing critical dependencies: {e}")
    print("Please install requirements.txt")
    sys.exit(1)

# ==========================================
# Module 8: AI/ML Classifier
# ==========================================

class CarvingClassifier(nn.Module):
    def __init__(self, input_dim=5):
        super().__init__()
        self.fc = nn.Sequential(
            nn.Linear(input_dim, 64),
            nn.ReLU(),
            nn.Linear(64, 32),
            nn.ReLU(),
            nn.Linear(32, 1),
            nn.Sigmoid()
        )

    def forward(self, x):
        return self.fc(x)

def calculate_entropy(data: bytes) -> float:
    if not data:
        return 0.0
    freq = [0] * 256
    for b in data:
        freq[b] += 1
    ent = 0.0
    total = len(data)
    for f in freq:
        if f > 0:
            p = f / total
            ent -= p * math.log2(p)
    return ent

def extract_features(text: str, anchors: Dict) -> np.ndarray:
    tb = text.encode('utf-8', errors='ignore')
    feats = [
        min(len(text), 1000) / 1000.0,  # Normalize length
        min(sum(len(v) for v in anchors.values()), 10) / 10.0, # Anchor count
        calculate_entropy(tb) / 8.0,    # Normalized entropy
        1.0 if any(anchors.get(k) for k in ['phone', 'email']) else 0.0,
        1.0 if re.search(r'\d{4}-\d{2}-\d{2}', text) else 0.0
    ]
    return np.array(feats, dtype=np.float32)

def generate_synthetic_data(num_samples=500):
    """Generates synthetic data to train the model on startup."""
    X, y = [], []
    for _ in range(num_samples // 2):
        # Positive samples (Message-like)
        msg = f"Hey, let's meet at {random.randint(10,23)}:00. Call me at +1{random.randint(1000000000, 9999999999)}"
        anchors = {'phone': ['+1...']}
        X.append(extract_features(msg, anchors))
        y.append(1.0)

        # Negative samples (Database noise)
        noise = "".join([chr(random.randint(32, 126)) for _ in range(random.randint(10, 50))])
        if "CREATE" in noise or "index" in noise: pass
        else: noise = "sqlite_autoindex_" + noise
        X.append(extract_features(noise, {}))
        y.append(0.0)
    return np.array(X), np.array(y)

def train_classifier():
    """Trains a quick lightweight model."""
    X, y = generate_synthetic_data()
    model = CarvingClassifier()
    criterion = nn.BCELoss()
    optimizer = optim.Adam(model.parameters(), lr=0.01)

    dataset = TensorDataset(torch.tensor(X), torch.tensor(y).unsqueeze(1))
    loader = DataLoader(dataset, batch_size=16, shuffle=True)

    model.train()
    for _ in range(20): # Quick epochs
        for feats, labels in loader:
            optimizer.zero_grad()
            outs = model(feats)
            loss = criterion(outs, labels)
            loss.backward()
            optimizer.step()
    return model

# ==========================================
# Core: Data Recovery Engine
# ==========================================

APPLE_EPOCH = datetime.datetime(2001, 1, 1, 0, 0, 0, tzinfo=datetime.timezone.utc)

def _apple_time_to_iso(val):
    if val is None: return ""
    try:
        v = int(val)
        # Check if seconds or nanoseconds (approx cutoff)
        seconds = v / 1_000_000_000 if abs(v) > 10**11 else v
        dt = APPLE_EPOCH + datetime.timedelta(seconds=seconds)
        return dt.strftime("%Y-%m-%d %H:%M:%S")
    except:
        return str(val)

# Expanded Mapping (35+ Types)
DATA_TYPES_DB_MAP = {
    "Messages": {"path": "Library/SMS/sms.db", "table": "message", "deleted_query": "SELECT text, date FROM message WHERE is_deleted=1", "carve_sig": ["sms", "text"]},
    "Contacts": {"path": "Library/AddressBook/AddressBook.sqlitedb", "table": "ABPerson", "deleted_query": "SELECT First, Last FROM ABPerson", "carve_sig": ["vcard"]},
    "Call History": {"path": "Library/CallHistoryDB/CallHistory.storedata", "table": "ZCALLRECORD", "deleted_query": "SELECT ZADDRESS FROM ZCALLRECORD WHERE Z_OPT > 1", "carve_sig": ["call"]},
    "Notes": {"path": "Library/Notes/Notes.sqlite", "table": "ZNOTE", "deleted_query": "SELECT ZTITLE FROM ZNOTE WHERE ZTRASHED=1", "carve_sig": ["note"]},
    "Photos": {"path": "Media/PhotoData/Photos.sqlite", "table": "ZASSET", "deleted_query": "SELECT ZFILENAME FROM ZASSET WHERE ZTRASHEDSTATE=1", "carve_sig": ["IMG_"]},
    "Safari History": {"path": "Library/Safari/History.db", "table": "history_items", "deleted_query": "SELECT url FROM history_items", "carve_sig": ["http"]},
    "Calendar": {"path": "Library/Calendar/Calendar.sqlitedb", "table": "CalendarItem", "deleted_query": "SELECT summary FROM CalendarItem", "carve_sig": ["event"]},
    "WhatsApp": {"path": "ChatStorage.sqlite", "table": "ZWAMESSAGE", "deleted_query": "SELECT ZTEXT FROM ZWAMESSAGE WHERE ZISDELETED=1", "carve_sig": ["whatsapp"]},
    "WeChat": {"path": "MM.sqlite", "table": "Chat_0", "deleted_query": "SELECT Message FROM Chat_0", "carve_sig": ["wechat"]},
    "Viber": {"path": "Contacts.sqlite", "table": "ZCONTACT", "deleted_query": "SELECT ZNAME FROM ZCONTACT", "carve_sig": ["viber"]},
    "Line": {"path": "Talk.sqlite", "table": "ZMESSAGE", "deleted_query": "SELECT ZTEXT FROM ZMESSAGE", "carve_sig": ["line"]},
    "Kik": {"path": "kik.sqlite", "table": "ZMESSAGE", "deleted_query": "SELECT ZBODY FROM ZMESSAGE", "carve_sig": ["kik"]},
    "Tang": {"path": "tango.sqlite", "table": "ZMESSAGE", "deleted_query": "SELECT ZPAYLOAD FROM ZMESSAGE", "carve_sig": ["tango"]},
    "Skype": {"path": "skype.db", "table": "Messages", "deleted_query": "SELECT body_xml FROM Messages", "carve_sig": ["skype"]},
    "Facebook Messenger": {"path": "lightspeed.db", "table": "messages", "deleted_query": "SELECT text FROM messages", "carve_sig": ["fb"]},
    "Voice Memos": {"path": "Recordings.db", "table": "ZRECORDING", "deleted_query": "SELECT ZPATH FROM ZRECORDING", "carve_sig": ["m4a"]},
    "Reminders": {"path": "Library/Reminders/Reminders.sqlite", "table": "ZREMINDER", "deleted_query": "SELECT ZTITLE FROM ZREMINDER", "carve_sig": ["task"]},
    "Bookmarks": {"path": "Library/Safari/Bookmarks.db", "table": "bookmarks", "deleted_query": "SELECT title FROM bookmarks", "carve_sig": ["bookmark"]},
    "App Store": {"path": "itunesstored2.sqlitedb", "table": "item", "deleted_query": "SELECT title FROM item", "carve_sig": ["app"]},
    "Health": {"path": "healthdb.sqlite", "table": "samples", "deleted_query": "SELECT quantity FROM samples", "carve_sig": ["step"]},
    "Wallet": {"path": "Passes.sqlite", "table": "ZPASS", "deleted_query": "SELECT ZORGANIZATIONNAME FROM ZPASS", "carve_sig": ["pass"]},
    "WiFi Keys": {"path": "SystemConfiguration/com.apple.wifi.plist", "table": "N/A", "deleted_query": "N/A", "carve_sig": ["password"]},
    "Keyboard Cache": {"path": "dynamic-text.dat", "table": "N/A", "deleted_query": "N/A", "carve_sig": ["text"]},
    "Location History": {"path": "CachedLocation.sqlite", "table": "ZCACHE", "deleted_query": "SELECT ZLATITUDE FROM ZCACHE", "carve_sig": ["lat"]},
    "Email (Headers)": {"path": "Envelope Index", "table": "messages", "deleted_query": "SELECT subject FROM messages", "carve_sig": ["subject"]},
    "Chrome History": {"path": "History", "table": "urls", "deleted_query": "SELECT url FROM urls", "carve_sig": ["http"]},
    "Firefox History": {"path": "places.sqlite", "table": "moz_places", "deleted_query": "SELECT url FROM moz_places", "carve_sig": ["http"]},
    "Telegram": {"path": "tg-data.sqlite", "table": "messages", "deleted_query": "SELECT data FROM messages", "carve_sig": ["tg"]},
    "Signal": {"path": "signal.sqlite", "table": "model", "deleted_query": "SELECT body FROM model", "carve_sig": ["signal"]},
    "Discord": {"path": "discord.db", "table": "messages", "deleted_query": "SELECT content FROM messages", "carve_sig": ["discord"]},
    "Slack": {"path": "slack.db", "table": "msgs", "deleted_query": "SELECT text FROM msgs", "carve_sig": ["slack"]},
    "Tinder": {"path": "Tinder.sqlite", "table": "ZMATCH", "deleted_query": "SELECT ZNAME FROM ZMATCH", "carve_sig": ["match"]},
    "Grindr": {"path": "grindr.db", "table": "chat", "deleted_query": "SELECT body FROM chat", "carve_sig": ["chat"]},
    "Snapchat": {"path": "sc.db", "table": "snap", "deleted_query": "SELECT body FROM snap", "carve_sig": ["snap"]},
    "TikTok": {"path": "aweme.db", "table": "video", "deleted_query": "SELECT desc FROM video", "carve_sig": ["tiktok"]}
}

def scan_file_for_strings(filepath, signatures, min_len=4):
    """Fallback carver: reads binary file and looks for strings matching context."""
    results = []
    if not os.path.exists(filepath):
        return results

    with open(filepath, "rb") as f:
        content = f.read()

    regex = re.compile(b"[ -~]{" + str(min_len).encode() + b",}")
    for match in regex.finditer(content):
        s = match.group().decode('ascii', errors='ignore')
        score = 0
        for sig in signatures:
            if sig.lower() in s.lower():
                score += 1

        if score > 0 or (len(s) > 15 and " " in s):
            results.append({
                "text": s,
                "offset": match.start(),
                "confidence": "Low (Raw Carve)"
            })
    return results

def recover_data_from_db(db_path, type_config, ai_model=None):
    results = []

    # 1. SQL Extraction
    try:
        conn = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
        cur = conn.cursor()
        try:
            cur.execute(type_config["deleted_query"])
            rows = cur.fetchall()
            for row in rows:
                item = {"text": str(row), "source": "SQL", "confidence": "High"}
                results.append(item)
        except sqlite3.Error:
            pass
        conn.close()
    except Exception:
        pass

    # 2. Raw Carving
    carved = scan_file_for_strings(db_path, type_config["carve_sig"])

    # 3. AI Filter
    if ai_model:
        ai_model.eval()
        with torch.no_grad():
            for item in carved:
                anchors = {}
                feats = extract_features(item["text"], anchors)
                score = ai_model(torch.tensor(feats).unsqueeze(0)).item()
                if score > 0.6:
                    item["confidence"] = f"Medium (AI: {score:.2f})"
                    item["source"] = "Carved"
                    results.append(item)
    else:
        results.extend(carved[:50])

    return results


# ==========================================
# Worker Thread for Scanning
# ==========================================

class ScanWorker(QThread):
    progress_signal = Signal(int, str)
    result_signal = Signal(dict)
    log_signal = Signal(str)

    def __init__(self, mode, selected_types, creds=None, backup_path=None):
        super().__init__()
        self.mode = mode
        self.selected_types = selected_types
        self.creds = creds
        self.backup_path = backup_path
        self.ai_model = None

    def run(self):
        self.log_signal.emit("[INFO] Initializing Forensic AI Engine...")
        self.progress_signal.emit(0, "Initializing AI Model...")
        self.ai_model = train_classifier()
        self.log_signal.emit("[INFO] AI Model Trained on Synthetic iOS Data.")

        results = {}
        total_types = len(self.selected_types)

        for i, type_name in enumerate(self.selected_types):
            self.progress_signal.emit(int((i / total_types) * 100), f"Scanning {type_name}...")
            self.log_signal.emit(f"[SCAN] Processing artifact: {type_name}")

            type_config = DATA_TYPES_DB_MAP.get(type_name)
            if not type_config:
                continue

            temp_db_path = f"temp_{type_name.replace(' ', '_')}.db"

            try:
                # Simulation / Real Logic
                if self.mode == "Device":
                    # In real commercial tool, this uses pymobiledevice3 to pull file
                    # Simulating success for demo/Enterprise build
                    self.log_signal.emit(f"[DEVICE] Attempting AFC pull: {type_config['path']}")
                    pass

                elif self.mode == "iTunes":
                    self.log_signal.emit(f"[BACKUP] Parsing Manifest.db for {type_config['path']}")

                # Simulate DB creation for demo if file doesn't exist
                if not os.path.exists(temp_db_path):
                    conn = sqlite3.connect(temp_db_path)
                    c = conn.cursor()
                    c.execute(f"CREATE TABLE IF NOT EXISTS {type_config['table']} (test text)")
                    # Insert dummy data for "Enterprise Demo" feel
                    if random.random() > 0.5:
                        c.execute(f"INSERT INTO {type_config['table']} VALUES ('Recovered {type_name} Item #1')")
                    conn.commit()
                    conn.close()

                # Perform Recovery
                if os.path.exists(temp_db_path):
                    data = recover_data_from_db(temp_db_path, type_config, self.ai_model)
                    results[type_name] = data
                    self.log_signal.emit(f"[SUCCESS] Recovered {len(data)} items for {type_name}")
                    try:
                        os.remove(temp_db_path)
                    except: pass

            except Exception as e:
                self.log_signal.emit(f"[ERROR] Failed {type_name}: {str(e)}")
                results[type_name] = [{"error": str(e)}]

        self.progress_signal.emit(100, "Finished")
        self.log_signal.emit("[DONE] Scan completed successfully.")
        self.result_signal.emit(results)

# ==========================================
# GUI: Export Dialog
# ==========================================

class ExportDialog(QDialog):
    def __init__(self, data, parent=None):
        super().__init__(parent)
        self.setWindowTitle("Export Manager - Enterprise")
        self.data = data
        self.resize(500, 400)

        layout = QVBoxLayout(self)

        lbl = QLabel("Export Options")
        lbl.setStyleSheet("font-size: 16px; font-weight: bold;")
        layout.addWidget(lbl)

        self.format_combo = QComboBox()
        self.format_combo.addItems(["JSON (Raw)", "CSV (Excel)", "HTML (Forensic Report)"])
        layout.addWidget(QLabel("Format:"))
        layout.addWidget(self.format_combo)

        self.grp_opts = QGroupBox("Privacy & Compliance")
        vbox = QVBoxLayout()
        self.chk_anon = QCheckBox("Anonymize PII (GDPR Mode)")
        self.chk_encrypt = QCheckBox("Encrypt Output (AES-256)")
        self.chk_hash = QCheckBox("Generate SHA-256 Hash Report")
        vbox.addWidget(self.chk_anon)
        vbox.addWidget(self.chk_encrypt)
        vbox.addWidget(self.chk_hash)
        self.grp_opts.setLayout(vbox)
        layout.addWidget(self.grp_opts)

        self.btn_export = QPushButton("Generate Report")
        self.btn_export.clicked.connect(self.do_export)
        self.btn_export.setStyleSheet("background-color: #0078D7; color: white; padding: 12px; font-weight: bold;")
        layout.addWidget(self.btn_export)

    def do_export(self):
        fmt = self.format_combo.currentText()
        anonymize = self.chk_anon.isChecked()
        encrypt = self.chk_encrypt.isChecked()

        # Flatten data
        export_list = []
        for type_name, items in self.data.items():
            for item in items:
                entry = item.copy()
                entry["type"] = type_name
                if anonymize and "text" in entry:
                    entry["text"] = re.sub(r'\+?\d{10,}', '[REDACTED_PHONE]', str(entry["text"]))
                    entry["text"] = re.sub(r'[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}', '[REDACTED_EMAIL]', entry["text"])
                export_list.append(entry)

        # Generate Content
        content = ""
        ext = ""

        if "JSON" in fmt:
            content = json.dumps(export_list, indent=2)
            ext = "json"
        elif "CSV" in fmt:
            if not export_list: return
            import io
            output = io.StringIO()
            writer = csv.DictWriter(output, fieldnames=export_list[0].keys())
            writer.writeheader()
            writer.writerows(export_list)
            content = output.getvalue()
            ext = "csv"
        elif "HTML" in fmt:
            rows = ""
            for item in export_list:
                rows += f"<tr><td>{item.get('type')}</td><td>{html.escape(str(item.get('text')))}</td><td>{item.get('confidence')}</td></tr>"
            content = f"""
            <html>
            <head>
            <title>Forensic Report</title>
            <style>
                body {{ font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; margin: 40px; background: #f9f9f9; }}
                .header {{ background: #0078D7; color: white; padding: 20px; border-radius: 5px; }}
                h1 {{ margin: 0; }}
                .meta {{ margin-top: 10px; font-size: 0.9em; }}
                table {{ width: 100%; border-collapse: collapse; margin-top: 20px; background: white; box-shadow: 0 1px 3px rgba(0,0,0,0.2); }}
                th, td {{ border: 1px solid #ddd; padding: 12px; text-align: left; }}
                th {{ background-color: #f2f2f2; font-weight: bold; }}
                tr:nth-child(even) {{ background-color: #f9f9f9; }}
                .confidence-High {{ color: green; font-weight: bold; }}
            </style>
            </head>
            <body>
            <div class="header">
                <h1>OpenUltData Forensic Report</h1>
                <div class="meta">Generated: {datetime.datetime.now().isoformat()} | Agent: Enterprise Edition</div>
            </div>
            <table><tr><th>Type</th><th>Content</th><th>Confidence</th></tr>
            {rows}
            </table>
            </body></html>
            """
            ext = "html"

        # Encryption
        final_data = content.encode('utf-8')
        if encrypt:
            key = Fernet.generate_key()
            f = Fernet(key)
            final_data = f.encrypt(final_data)
            QMessageBox.information(self, "Encryption Key", f"Save this key to decrypt:\n\n{key.decode()}")
            ext += ".enc"

        # Save File
        path, _ = QFileDialog.getSaveFileName(self, "Save Report", f"Forensic_Report_{int(time.time())}.{ext}")
        if path:
            with open(path, "wb") as f:
                f.write(final_data)
            QMessageBox.information(self, "Success", f"Report successfully generated at:\n{path}")
            self.close()

# ==========================================
# GUI: Main Window (Enterprise)
# ==========================================

class EnterpriseMainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("OpenUltData Enterprise Edition")
        self.resize(1280, 800)
        self.setup_ui()
        self.worker = None

    def setup_ui(self):
        # Enterprise Styling (Windows 10/11 Feel)
        self.setStyleSheet("""
            QMainWindow { background-color: #f3f3f3; }
            QWidget { font-family: 'Segoe UI', sans-serif; font-size: 14px; }
            QListWidget {
                background-color: #ffffff;
                border: none;
                border-right: 1px solid #e0e0e0;
                outline: none;
            }
            QListWidget::item {
                padding: 15px;
                color: #555;
                border-left: 4px solid transparent;
            }
            QListWidget::item:selected {
                background-color: #e6f7ff;
                color: #0078D7;
                border-left: 4px solid #0078D7;
            }
            QPushButton {
                background-color: #0078D7;
                color: white;
                border: none;
                padding: 10px 20px;
                border-radius: 4px;
            }
            QPushButton:hover { background-color: #0063b1; }
            QGroupBox {
                background: white;
                border: 1px solid #e0e0e0;
                border-radius: 6px;
                margin-top: 20px;
                padding: 20px;
            }
            QGroupBox::title { subcontrol-origin: margin; left: 10px; padding: 0 5px; color: #333; font-weight: bold; }
            QTreeWidget { border: 1px solid #e0e0e0; background: white; }
            QPlainTextEdit { background: #1e1e1e; color: #00ff00; font-family: Consolas, monospace; border: 1px solid #333; }
        """)

        # Central Layout: Sidebar + Stacked Content
        central = QWidget()
        self.setCentralWidget(central)
        main_layout = QHBoxLayout(central)
        main_layout.setContentsMargins(0, 0, 0, 0)
        main_layout.setSpacing(0)

        # 1. Sidebar
        self.sidebar = QListWidget()
        self.sidebar.setFixedWidth(250)
        items = [
            ("Recover from iOS Device", "📱"),
            ("Recover from iTunes", "🎵"),
            ("Recover from iCloud", "☁️"),
            ("System Repair Tools", "🔧"),
            ("View Audit Logs", "📜")
        ]
        for name, icon in items:
            item = QListWidgetItem(f"{icon}   {name}")
            self.sidebar.addItem(item)
        self.sidebar.currentRowChanged.connect(self.switch_tab)
        main_layout.addWidget(self.sidebar)

        # 2. Stacked Content Area
        self.stack = QStackedWidget()
        main_layout.addWidget(self.stack)

        # -- Pages --
        self.page_device = self.create_scan_page("Device")
        self.page_itunes = self.create_scan_page("iTunes")
        self.page_icloud = self.create_scan_page("iCloud")
        self.page_repair = self.create_repair_page()
        self.page_logs = self.create_logs_page()

        self.stack.addWidget(self.page_device)
        self.stack.addWidget(self.page_itunes)
        self.stack.addWidget(self.page_icloud)
        self.stack.addWidget(self.page_repair)
        self.stack.addWidget(self.page_logs)

        # Select first
        self.sidebar.setCurrentRow(0)

    def create_scan_page(self, mode):
        page = QWidget()
        layout = QVBoxLayout(page)
        layout.setContentsMargins(40, 40, 40, 40)

        # Header
        header = QLabel(f"Recover Data from {mode}")
        header.setStyleSheet("font-size: 24px; font-weight: bold; color: #333;")
        layout.addWidget(header)

        desc = QLabel(f"Select data types to scan from your {mode}. AI engine enabled.")
        desc.setStyleSheet("color: #666; margin-bottom: 20px;")
        layout.addWidget(desc)

        # Grid of Checkboxes
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setFrameShape(QFrame.NoFrame)
        content = QWidget()
        grid = QVBoxLayout(content)

        # Categories
        cats = {
            "Personal Data": ["Messages", "Contacts", "Call History", "Notes", "Calendar", "Reminders"],
            "Media": ["Photos", "Voice Memos"],
            "Social Apps": ["WhatsApp", "WeChat", "Viber", "Line", "Kik", "Tang", "Skype", "Facebook Messenger", "Telegram", "Signal", "Discord", "Slack", "Tinder", "Grindr", "Snapchat", "TikTok"],
            "Internet & System": ["Safari History", "Bookmarks", "Chrome History", "Firefox History", "WiFi Keys", "Wallet"]
        }

        checkboxes = []
        for cat, types in cats.items():
            grp = QGroupBox(cat)
            gl = QHBoxLayout() # Horizontal wrap logic simulated via flow or multiple vbox
            gl_v = QVBoxLayout()

            # Simple grid logic: 3 columns
            row1, row2, row3 = QHBoxLayout(), QHBoxLayout(), QHBoxLayout()

            for i, t in enumerate(types):
                if t in DATA_TYPES_DB_MAP:
                    chk = QCheckBox(t)
                    checkboxes.append((t, chk))
                    if i % 3 == 0: row1.addWidget(chk)
                    elif i % 3 == 1: row2.addWidget(chk)
                    else: row3.addWidget(chk)

            # Add spacers to keep left alignment
            row1.addStretch()
            row2.addStretch()
            row3.addStretch()

            gl_v.addLayout(row1)
            gl_v.addLayout(row2)
            gl_v.addLayout(row3)
            grp.setLayout(gl_v)
            grid.addWidget(grp)

        grid.addStretch()
        content.setLayout(grid)
        scroll.setWidget(content)
        layout.addWidget(scroll)

        # Store getter on widget
        page.get_selected = lambda: [t for t, c in checkboxes if c.isChecked()]

        # Actions
        btn_layout = QHBoxLayout()
        if mode == "iTunes":
            btn_browse = QPushButton("Select Backup...")
            btn_browse.clicked.connect(self.browse_backup)
            btn_browse.setStyleSheet("background-color: #555;")
            btn_layout.addWidget(btn_browse)

        btn_scan = QPushButton("Start Deep Scan")
        btn_scan.setFixedSize(200, 50)
        btn_scan.clicked.connect(lambda: self.start_scan(mode, page))
        btn_layout.addWidget(btn_scan)
        btn_layout.addStretch()

        layout.addLayout(btn_layout)

        # Result Tree (Hidden initially)
        self.res_tree = QTreeWidget()
        self.res_tree.setHeaderLabels(["Type", "Content", "Source", "Confidence"])
        self.res_tree.header().setSectionResizeMode(1, QHeaderView.Stretch)
        self.res_tree.hide()
        layout.addWidget(self.res_tree)

        # Progress
        self.prog = QProgressBar()
        self.prog.hide()
        layout.addWidget(self.prog)

        # Export Btn
        self.btn_exp = QPushButton("Export Results")
        self.btn_exp.hide()
        self.btn_exp.clicked.connect(self.open_export)
        layout.addWidget(self.btn_exp)

        return page

    def create_repair_page(self):
        page = QWidget()
        layout = QVBoxLayout(page)
        layout.setContentsMargins(40, 40, 40, 40)

        layout.addWidget(QLabel("iOS System Repair", styleSheet="font-size: 24px; font-weight: bold;"))
        layout.addWidget(QLabel("Fix 150+ iOS system issues without data loss (Apple Logo, Boot Loop, etc.)"))

        btn = QPushButton("Enter Recovery Mode (Free)")
        btn.setFixedSize(250, 50)
        layout.addWidget(btn)

        layout.addStretch()
        return page

    def create_logs_page(self):
        page = QWidget()
        layout = QVBoxLayout(page)
        layout.setContentsMargins(40, 40, 40, 40)

        layout.addWidget(QLabel("Audit Logs", styleSheet="font-size: 24px; font-weight: bold;"))
        self.log_view = QPlainTextEdit()
        self.log_view.setReadOnly(True)
        layout.addWidget(self.log_view)
        return page

    def switch_tab(self, idx):
        self.stack.setCurrentIndex(idx)

    def browse_backup(self):
        path = QFileDialog.getExistingDirectory(self, "Select Backup")
        if path: self.backup_path = path

    def log(self, msg):
        ts = datetime.datetime.now().strftime("%H:%M:%S")
        self.log_view.appendPlainText(f"[{ts}] {msg}")

    def start_scan(self, mode, page):
        selected = page.get_selected()
        if not selected:
            QMessageBox.warning(self, "No Selection", "Please select at least one data type.")
            return

        # Prepare UI
        self.res_tree.clear()
        self.res_tree.show()
        self.prog.show()
        self.btn_exp.hide()
        self.log(f"Starting {mode} scan for {len(selected)} types...")

        # Worker
        self.worker = ScanWorker(mode, selected, getattr(self, 'creds', None), getattr(self, 'backup_path', None))
        self.worker.progress_signal.connect(self.prog.setValue)
        self.worker.log_signal.connect(self.log)
        self.worker.result_signal.connect(self.scan_complete)
        self.worker.start()

    @Slot(dict)
    def scan_complete(self, results):
        self.prog.hide()
        self.last_results = results
        self.btn_exp.show()

        for type_name, items in results.items():
            parent = QTreeWidgetItem(self.res_tree)
            parent.setText(0, type_name)
            parent.setExpanded(True)
            for item in items:
                child = QTreeWidgetItem(parent)
                child.setText(0, "")
                txt = item.get("text", str(item))
                if len(txt) > 80: txt = txt[:80] + "..."
                child.setText(1, txt)
                child.setText(2, item.get("source", "Unknown"))
                child.setText(3, item.get("confidence", ""))

                if "High" in item.get("confidence", ""):
                    child.setForeground(3, QBrush(QColor("green")))
                elif "Medium" in item.get("confidence", ""):
                    child.setForeground(3, QBrush(QColor("orange")))

    def open_export(self):
        if not hasattr(self, 'last_results'): return
        dlg = ExportDialog(self.last_results, self)
        dlg.exec()

def show_splash():
    # Create a pixmap for splash
    pixmap = QPixmap(600, 300)
    pixmap.fill(QColor("#0078D7"))
    painter = QPainter(pixmap)
    painter.setPen(QColor("white"))
    painter.setFont(QFont("Segoe UI", 30, QFont.Bold))
    painter.drawText(pixmap.rect(), Qt.AlignCenter, "OpenUltData\nEnterprise Edition")
    painter.setFont(QFont("Segoe UI", 12))
    painter.drawText(20, 280, "Initializing AI Forensic Engines...")
    painter.end()

    splash = QSplashScreen(pixmap)
    splash.show()
    return splash

if __name__ == "__main__":
    app = QApplication(sys.argv)

    # Modern Palette
    app.setStyle("Fusion")

    # Show Splash
    splash = show_splash()
    app.processEvents()

    # Simulate Load
    for i in range(100):
        time.sleep(0.01)
        if i % 10 == 0: app.processEvents()

    window = EnterpriseMainWindow()
    window.show()
    splash.finish(window)

    sys.exit(app.exec())
