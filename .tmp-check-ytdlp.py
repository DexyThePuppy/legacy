import sqlite3
import os

paths = [
    os.path.expandvars(r"%LOCALAPPDATA%\ersatztv\ersatztv.sqlite3"),
    r"D:\Dexy\Documents\GitHub\legacy\.etv-dev-config\ersatztv.sqlite3",
]

for db in paths:
    print("DB:", db, "exists=", os.path.exists(db))
    if not os.path.exists(db):
        continue
    c = sqlite3.connect(db)
    rows = list(
        c.execute(
            "SELECT Key, Value FROM ConfigElement WHERE Key LIKE '%ytdlp%' OR Key LIKE '%YtDlp%' OR Key LIKE '%youtube%'"
        )
    )
    for k, v in rows:
        print(f"  {k} = {v!r}")
    c.close()

cookie = os.path.expandvars(r"%LOCALAPPDATA%\ersatztv\cache\youtube-cookies.txt")
print("cookie file:", cookie, "exists=", os.path.exists(cookie))
if os.path.exists(cookie):
    yt = 0
    with open(cookie, encoding="utf-8", errors="replace") as f:
        for line in f:
            if "youtube.com" in line or "google.com" in line:
                yt += 1
    print("youtube/google cookie lines:", yt)
