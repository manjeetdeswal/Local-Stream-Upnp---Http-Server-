<div align="center">

  <h1>📡 LocalStream</h1>
  
  <p>
    <strong>Universal Local Media Server for Windows, Linux, macOS, and Android.</strong>
  </p>

  <p>
    <a href="https://dotnet.microsoft.com/">
      <img src="https://img.shields.io/badge/.NET-8.0-512BD4?style=flat&logo=dotnet" alt=".NET 8" />
    </a>
    <a href="https://avaloniaui.net/">
      <img src="https://img.shields.io/badge/UI-Avalonia-AF1859?style=flat" alt="Avalonia UI" />
    </a>
    <img src="https://img.shields.io/badge/Platform-Win%20%7C%20Linux%20%7C%20Mac%20%7C%20Android-lightgrey" alt="Platforms" />
    <img src="https://img.shields.io/badge/License-MIT-green" alt="License" />
  </p>

  <p>
    <b>LocalStream</b> turns your computer into a powerful local media server.<br>
    Stream movies, music, and photos to any device on your Wi-Fi network<br>
    (Smart TVs, Phones, Tablets) via a modern Web Interface or UPnP/DLNA.
  </p>

  <br>

  <a href="#">
    <img src="https://img.shields.io/badge/⬇_Download_Latest_Release-3B82F6?style=for-the-badge&logo=github" height="40">
  </a>

</div>

<br>
<hr>

## ✨ Features

<table>
  <tr>
    <td>⚡ <b>Zero Lag Streaming</b></td>
    <td>Streams directly over LAN. No internet speed caps.</td>
  </tr>
  <tr>
    <td>🌐 <b>Cross-Platform</b></td>
    <td>Runs natively on Windows, Linux, and macOS.</td>
  </tr>
  <tr>
    <td>📱 <b>Android Client</b></td>
    <td>Dedicated app for mobile browsing and playback.</td>
  </tr>
  <tr>
    <td>📺 <b>UPnP / DLNA</b></td>
    <td>Automatically discovered by Smart TVs and VLC.</td>
  </tr>
  <tr>
    <td>🌍 <b>Web Interface</b></td>
    <td>Browse and play files from any web browser.</td>
  </tr>
  <tr>
    <td>🖼️ <b>Smart Previews</b></td>
    <td>Auto-generates thumbnails (FFmpeg) and album art.</td>
  </tr>
</table>

<br>

## 📥 Installation & Usage

<details>
<summary><strong>🪟 Windows (Click to Expand)</strong></summary>

1. Download **`LocalStream-Win.zip`** from Releases.
2. Extract the ZIP file.
3. Run `LocalStreamPC.exe`.
4. *Note:* If prompted by Windows SmartScreen, click **More Info -> Run Anyway**.

</details>

<details>
<summary><strong>🐧 Linux (Click to Expand)</strong></summary>

1. Download **`LocalStream-Linux.zip`**.
2. Open Terminal and unzip/install:
   ```bash
   unzip LocalStream-Linux.zip
   chmod +x LocalStreamPC
   chmod +x ffmpeg
Run the app:

Bash

./LocalStreamPC
</details>

<details> <summary><strong>🍎 macOS (Click to Expand)</strong></summary>

Download LocalStream-Mac.zip.

Extract the files.

Fix "App is Damaged" Error:

Bash

xattr -cr ~/Downloads/LocalStreamPC/
Double-click LocalStreamPC to launch.

</details>

<details> <summary><strong>🤖 Android (Click to Expand)</strong></summary>

Download LocalStream-Android.apk to your phone.

Tap to install.

Note: Enable "Install from Unknown Sources" if prompted.

Use the app to connect to your PC server automatically.

</details>

🛠️ Building from Source
To build this project yourself, you need the .NET 8 SDK.

1. Clone the Repo
Bash

git clone [https://github.com/yourusername/LocalStream.git](https://github.com/yourusername/LocalStream.git)
cd LocalStream
2. Add Binaries
Create a Binaries folder in the project root and add ffmpeg / ffprobe executables:

Plaintext

/LocalStreamPC
  /Binaries
    /win   (ffmpeg.exe, ffprobe.exe)
    /linux (ffmpeg, ffprobe)  <-- No extension
    /mac   (ffmpeg, ffprobe)  <-- No extension
3. Build Command
Bash

# Windows
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# Linux
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true

# macOS (Apple Silicon)
dotnet publish -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true
🤝 How It Works
HTTP Server (Port 8080): Hosts a web interface and handles file streaming. Supports Range-Requests for instant seeking.

UPnP (SSDP): Broadcasts presence to the network so devices like Samsung TVs, Roku, and VLC can auto-discover the server.

FFmpeg Integration: Uses Xabe.FFmpeg to generate video thumbnails on the fly.
