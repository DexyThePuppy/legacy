import sqlite3
from pathlib import Path

cookies = Path(r"C:\Users\Dexy\AppData\Local\ersatztv\cache\youtube-cookies.txt")
assert cookies.exists(), cookies
# no quotes — ArgumentList passes each token separately
extra = f"--cookies {cookies}"

con = sqlite3.connect(r"C:\Users\Dexy\AppData\Local\ersatztv\ersatztv.sqlite3")
cur = con.cursor()
row = cur.execute("SELECT Id, Value FROM ConfigElement WHERE Key='ytdlp.extra_args'").fetchone()
print("before", repr(row[1] if row else None))
if row:
    cur.execute("UPDATE ConfigElement SET Value=? WHERE Key='ytdlp.extra_args'", (extra,))
else:
    cur.execute("INSERT INTO ConfigElement (Key, Value) VALUES ('ytdlp.extra_args', ?)", (extra,))
con.commit()
print("after", repr(cur.execute("SELECT Value FROM ConfigElement WHERE Key='ytdlp.extra_args'").fetchone()[0]))
con.close()
print("cookies_size", cookies.stat().st_size)
