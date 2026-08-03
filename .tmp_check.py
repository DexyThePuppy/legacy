import sqlite3
from pathlib import Path

con = sqlite3.connect(r"C:\Users\Dexy\AppData\Local\ersatztv\ersatztv.sqlite3")
print("extra_args:", con.execute("SELECT Value FROM ConfigElement WHERE Key='ytdlp.extra_args'").fetchone())
print("stream 146:", con.execute("SELECT Id, Url FROM RemoteStream WHERE Id=146").fetchone())
con.close()

cookies = Path(r"C:\Users\Dexy\AppData\Local\ersatztv\cache\youtube-cookies.txt")
print("cookies", cookies.exists(), cookies.stat().st_size if cookies.exists() else 0)

# how SplitExtraArgs would parse current value
extra = "--cookies C:\\Users\\Dexy\\AppData\\Local\\ersatztv\\cache\\youtube-cookies.txt"
print("split:", extra.split())
