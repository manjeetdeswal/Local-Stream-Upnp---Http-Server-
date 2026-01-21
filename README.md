📡 LocalStream
Universal Local Media Server for Windows, Linux, macOS, and Android.

LocalStream turns your computer into a powerful local media server. Stream movies, music, and photos to any device on your Wi-Fi network (Smart TVs, Phones, Tablets) via a modern Web Interface or UPnP/DLNA.

✨ Features
⚡ Zero Lag Streaming: Streams directly over LAN. No internet speed caps.

🌐 Cross-Platform Server: Runs natively on Windows, Linux, and macOS.

📱 Android Client: Dedicated app for mobile browsing and playback.

📺 UPnP / DLNA Support: Automatically discovered by Smart TVs and VLC.

🌍 Modern Web Interface: Browse and play files from any web browser (Chrome, Safari, Edge).

🖼️ Smart Previews: Auto-generates thumbnails for videos (FFmpeg) and album art for music.

📂 File Management: Upload files to your PC or download folders as ZIPs.

🌗 Dark Mode: Beautiful, adaptive UI.

📥 Installation & Usage
🪟 Windows
Download LocalStream-Win.zip from the Releases page.

Extract the ZIP file.

Run LocalStreamPC.exe.

Note: If prompted by Windows SmartScreen, click More Info -> Run Anyway.

🐧 Linux
Download LocalStream-Linux.zip.

Open Terminal and unzip/install:

Bash

unzip LocalStream-Linux.zip
chmod +x LocalStreamPC
chmod +x ffmpeg
Run the app:

Bash

./LocalStreamPC
🍎 macOS
Download LocalStream-Mac.zip.

Extract the files.

Fix "App is Damaged" Error: Apple blocks unsigned apps by default. Run this in Terminal:

Bash

xattr -cr ~/Downloads/LocalStreamPC/
Double-click LocalStreamPC to launch.

🤖 Android
Download LocalStream-Android.apk to your phone.

Tap to install.

Note: You may need to enable "Install from Unknown Sources" in your browser settings.

Use the app to connect to your PC server automatically.

🛠️ Building from Source
If you want to modify the code or build it yourself, you need the .NET 8 SDK.

Prerequisites
.NET 8.0 SDK

FFmpeg binaries for your OS

1. Clone the Repo
Bash

git clone https://github.com/yourusername/LocalStream.git
cd LocalStream
2. Add FFmpeg Binaries
To support cross-platform builds, create a Binaries folder in the project root and add the ffmpeg / ffprobe executables for each OS:

Plaintext

/LocalStreamPC
  /Binaries
    /win   (ffmpeg.exe, ffprobe.exe)
    /linux (ffmpeg, ffprobe)     <-- No extension
    /mac   (ffmpeg, ffprobe)     <-- No extension
3. Build & Publish
Windows:

PowerShell

dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
Linux:

Bash

dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
macOS (Apple Silicon):

Bash

dotnet publish -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true
🤝 How It Works
HTTP Server (Port 8080): Hosts a web interface and handles file streaming. It supports Range-Requests, allowing you to seek/skip in videos instantly.

UPnP (SSDP): Broadcasts presence to the network so devices like Samsung TVs, Roku, and VLC can find it without typing IP addresses.

FFmpeg Integration: Uses Xabe.FFmpeg to generate snapshots of videos on the fly for the web interface.

📄 License
Distributed under the MIT License. See LICENSE for more information.

❤️ Contributing
Pull requests are welcome! For major changes, please open an issue first to discuss what you would like to change.
