<div align="center">

  <h1>📡 LocalStream</h1>
  
  <p>
    <strong>Universal Local Media Server for Windows, Linux, macOS, and Android</strong>
  </p>

  <p>
    <a href="https://dotnet.microsoft.com/">
      <img src="https://img.shields.io/badge/.NET-8.0-512BD4?style=flat&logo=dotnet" alt=".NET 8" />
    </a>
    <a href="https://avaloniaui.net/">
      <img src="https://img.shields.io/badge/UI-Avalonia-AF1859?style=flat" alt="Avalonia UI" />
    </a>
    <img src="https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS%20%7C%20Android-lightgrey" alt="Platforms" />
    <img src="https://img.shields.io/badge/License-MIT-green" alt="License" />
  </p>

  <p>
    <b>LocalStream</b> turns your computer into a powerful local media server.<br>
    Stream movies, music, and photos to any device on your Wi-Fi network<br>
    (Smart TVs, Phones, Tablets) using a modern Web UI or UPnP / DLNA.
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
    <td>⚡ <b>Zero-Lag Streaming</b></td>
    <td>Streams directly over LAN — no internet dependency.</td>
  </tr>
  <tr>
    <td>🌐 <b>Cross-Platform</b></td>
    <td>Runs natively on Windows, Linux, and macOS.</td>
  </tr>
  <tr>
    <td>📱 <b>Android Client</b></td>
    <td>Dedicated Android app for browsing and playback.</td>
  </tr>
  <tr>
    <td>📺 <b>UPnP / DLNA</b></td>
    <td>Auto-discovered by Smart TVs, VLC, and media players.</td>
  </tr>
  <tr>
    <td>🌍 <b>Web Interface</b></td>
    <td>Access your library from any browser.</td>
  </tr>
  <tr>
    <td>🖼️ <b>Smart Previews</b></td>
    <td>Auto-generated thumbnails & album art via FFmpeg.</td>
  </tr>
</table>

<br>
<hr>

## 📥 Installation & Usage

<details>
<summary><strong>🪟 Windows</strong></summary>

1. Download **LocalStream-Win.zip** from Releases  
2. Extract the ZIP  
3. Run `LocalStreamPC.exe`  
4. If SmartScreen appears → **More Info → Run Anyway**

</details>

<details>
<summary><strong>🐧 Linux</strong></summary>

1. Download **LocalStream-Linux.zip**
2. Extract and set permissions:

```bash
unzip LocalStream-Linux.zip
chmod +x LocalStreamPC
chmod +x ffmpeg ffprobe
Run:

bash
Copy code
./LocalStreamPC
</details> <details> <summary><strong>🍎 macOS</strong></summary>
Download LocalStream-Mac.zip

Extract files

Fix “App is Damaged” warning:

bash
Copy code
xattr -cr ~/Downloads/LocalStreamPC/
Double-click LocalStreamPC

</details> <details> <summary><strong>🤖 Android</strong></summary>
Download LocalStream-Android.apk

Install (enable Unknown Sources if prompted)

App automatically discovers your PC server

</details> <br> <hr>
🛠️ Building from Source
<p>You need the <b>.NET 8 SDK</b> installed.</p> <h3>1️⃣ Clone the Repository</h3>
bash
Copy code
git clone https://github.com/yourusername/LocalStream.git
cd LocalStream
<h3>2️⃣ Add FFmpeg Binaries</h3>
bash
Copy code
/LocalStreamPC
  /Binaries
    /win
      ffmpeg.exe
      ffprobe.exe
    /linux
      ffmpeg
      ffprobe
    /mac
      ffmpeg
      ffprobe
<h3>3️⃣ Build Commands</h3>
Windows

bash
Copy code
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
Linux

bash
Copy code
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
macOS (Apple Silicon)

bash
Copy code
dotnet publish -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true
<br> <hr>
🤝 How It Works
<table> <tr> <td>🌐 <b>HTTP Server (Port 8080)</b></td> <td> Hosts the web UI and streams media files.<br> Supports <b>HTTP Range Requests</b> for instant seeking. </td> </tr> <tr> <td>📡 <b>UPnP / SSDP</b></td> <td> Broadcasts server presence on the local network.<br> Automatically detected by Smart TVs, VLC, Roku. </td> </tr> <tr> <td>🎞️ <b>FFmpeg Integration</b></td> <td> Uses <b>Xabe.FFmpeg</b> to generate thumbnails,<br> previews, and album art on demand. </td> </tr> </table> <br> <hr>
📄 License
This project is licensed under the MIT License.
You are free to use, modify, and distribute it.

<br> <p align="center"> <b>LocalStream — Fast • Private • Offline-Friendly</b> </p> ```
