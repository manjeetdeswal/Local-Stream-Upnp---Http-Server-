<div align="center">

  <h1>📡 LocalStream</h1>
<h2>
  <a href="https://manjeetdeswal.github.io/Local-Stream-Upnp---Http-Server-/" target="_blank">🌐 Website</a>
</h2>
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

  <a href="https://github.com/manjeetdeswal/Local-Stream-Upnp---Http-Server-/releases/tag/1.0">
    <img src="https://img.shields.io/badge/⬇_Download_App-3B82F6?style=for-the-badge&logo=windows" height="40">
  </a>
  <a href="#-how-to-use">
    <img src="https://img.shields.io/badge/📖_Read_Guide-1F2937?style=for-the-badge&logo=readthedocs" height="40">
  </a>

</div>

<br>
<hr>

## ✨ Features

| Feature | Description |
| :--- | :--- |
| ⚡ **Zero Lag** | Streams directly over LAN. No internet speed caps or buffering. |
| 🌐 **Cross-Platform** | Runs natively on **Windows, Linux, macOS** servers. |
| 📱 **Android App** | Dedicated client app for mobile browsing. |
| 📺 **UPnP / DLNA** | Automatically discovered by **Smart TVs, Roku, and VLC**. |
| 🌍 **Web Interface** | Browse and play files from any web browser (Chrome, Safari). |
| 🖼️ **Smart Previews** | Auto-generates **video thumbnails** (FFmpeg) and album art. |

<br>

## 📥 Downloads

| Platform | Download Link | Notes |
| :--- | :--- | :--- |
| **Windows** | [**`LocalStream-Win.zip`**](#) | Windows 10 & 11 (x64) |
| **Linux** | [**`LocalStream-Linux.zip`**](#) | Ubuntu, Debian, Fedora |
| **macOS** | [**`LocalStream-Mac.zip`**](#) | Apple Silicon (M1/M2) |
| **Android** | [**`Download from play store`**](https://play.google.com/store/apps/details?id=com.jeet_studio.localstream4k) | Android 10+ (Client App) |

<br>

## 🚀 Installation Guide

Click the arrow next to your platform to see instructions.

<details>
<summary><strong>🪟 Windows (Click to Expand)</strong></summary>

1. Download **`LocalStream-Win.zip`**.
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

Fix "App is Damaged" Error: Apple blocks unsigned apps. Run this in Terminal:

Bash

xattr -cr ~/Downloads/LocalStreamPC/
Double-click LocalStreamPC to launch.

</details>

<details> <summary><strong>🤖 Android (Click to Expand)</strong></summary>

Download LocalStream-Android.apk from play store.



Use the app to connect to your PC server or Vice Versa.

</details>
## 📖 How to Use

**Add Folders:**  
Open the app on your PC and click **"Add Folder"** to select your movie or music folders.

**Start Server:**  
Click the big **"Start Server"** button to begin streaming.

**Connect:**  
- **On Phone:** Open the Android app or any web browser and type the IP address shown in the PC app.  
- **On TV:** Open VLC or the built-in Media Player and look for **"LocalStream"** under the **Local Network** section.

---

## 🧠 Under the Hood

**HTTP Server (Port 8080):**  
Hosts the web interface and streams media with range-request support for instant seeking and skipping.

**UPnP (SSDP):**  
Broadcasts the server on your local network so devices like Samsung TVs, Roku, and VLC can automatically discover it.

**FFmpeg Integration:**  
Uses **Xabe.FFmpeg** to generate video thumbnails and previews on the fly.

---

## 🛠️ Developers: Build from Source

To build this project yourself, you need the **.NET 8 SDK**.

### 1. Clone & Setup

git clone https://github.com/manjeetdeswal/Local-Stream-Upnp---Http-Server-  
cd LocalStream

### 2. Add Binaries

Create a **Binaries** folder in the project root and add **ffmpeg / ffprobe** executables for each OS in the following structure:

/LocalStreamPC  
  /Binaries  
    /win    (ffmpeg.exe, ffprobe.exe)  
    /linux  (ffmpeg, ffprobe)  
    /mac    (ffmpeg, ffprobe)

### 3. Build Commands

**Windows (PowerShell):**  
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

**Linux (Bash):**  
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true

**macOS (Apple Silicon):**  
dotnet publish -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true

---

<div align="center">
  <p>Distributed under the <strong>MIT License</strong>.</p>
  <img src="https://img.shields.io/badge/Made%20with-%E2%9D%A4-red" alt="Made with Love"><br>
  <p><b>Manjeet Deswal</b></p>
</div>

