import sqlite3
con = sqlite3.connect(r"C:\Users\Dexy\AppData\Local\ersatztv\ersatztv.sqlite3")
for row in con.execute(
    "SELECT Key, Value FROM ConfigElement WHERE Key LIKE '%ffmpeg%' OR Key LIKE 'ytdlp%' OR Key LIKE '%FFmpeg%'"
):
    print(row)
print("stream", con.execute("SELECT Id, Url FROM RemoteStream WHERE Id=146").fetchone())
con.close()
