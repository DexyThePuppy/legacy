import sqlite3
import os
import subprocess

db = r"C:\Users\Dexy\AppData\Local\ersatztv\ersatztv.sqlite3"
con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
cur = con.cursor()

print("--- RemoteStream 146 ---")
for row in cur.execute("SELECT Id, Url FROM RemoteStream WHERE Id=146"):
    print(row)

print("--- Media files ---")
for row in cur.execute(
    """
    SELECT mv.Id, mf.Path
    FROM MediaVersion mv
    JOIN MediaFile mf ON mf.MediaVersionId = mv.Id
    WHERE mv.RemoteStreamId = 146
    """
):
    print(row)

print("--- Config ---")
cfg = {}
for key, value in cur.execute(
    "SELECT Key, Value FROM ConfigElement WHERE Key LIKE 'ytdlp%' OR Key LIKE '%ffmpeg%' OR Key LIKE '%deno%'"
):
    cfg[key] = value
    print(key, "=", value)

con.close()

cache = r"C:\Users\Dexy\AppData\Local\ersatztv\cache\youtube"
print("--- cache ---")
if os.path.isdir(cache):
    files = os.listdir(cache)
    print("count", len(files))
    for e in files[:20]:
        print(os.path.getsize(os.path.join(cache, e)), e)
    print("hit", [f for f in files if f.startswith("PRKqvZKbY2k")])
else:
    print("missing")

logs = r"C:\Users\Dexy\AppData\Local\ersatztv\logs"
print("--- logs ---")
if os.path.isdir(logs):
    for e in sorted(os.listdir(logs), key=lambda n: os.path.getmtime(os.path.join(logs, n)), reverse=True)[:8]:
        full = os.path.join(logs, e)
        print(os.path.getsize(full), e)

# find yt-dlp
candidates = []
for k in ["ytdlp.path", "ffmpeg.path"]:
    if k in cfg and cfg[k]:
        candidates.append(cfg[k])
for p in [
    r"C:\Users\Dexy\AppData\Local\ersatztv\youtube",
    r"d:\Dexy\Documents\GitHub\legacy\.etv-publish",
    r"C:\Users\Dexy\AppData\Local\Microsoft\WinGet\Links",
]:
    if os.path.isdir(p):
        for root, dirs, files in os.walk(p):
            for f in files:
                if f.lower() in ("yt-dlp.exe", "yt-dlp", "deno.exe", "ffmpeg.exe"):
                    candidates.append(os.path.join(root, f))
            if root.count(os.sep) - p.count(os.sep) > 2:
                dirs.clear()

print("--- tool candidates ---")
for c in candidates:
    print(c, os.path.exists(c))
