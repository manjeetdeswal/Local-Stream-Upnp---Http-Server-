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

<p align="center">
  <img src="https://github.com/manjeetdeswal/Local-Stream-Upnp---Http-Server-/blob/main/LocalStreamPC/ss/Screenshot%202026-02-26%20104009.png" width="45%" alt="Home Screen" />
  <img src="https://github.com/manjeetdeswal/Local-Stream-Upnp---Http-Server-/blob/main/LocalStreamPC/ss/Screenshot%202026-02-26%20104108.png" width="45%" alt="Smart Touchpad" />
</p>
<p align="center">
  <img src="https://github.com/manjeetdeswal/Local-Stream-Upnp---Http-Server-/blob/main/LocalStreamPC/ss/Screenshot%202026-02-26%20104219.png" width="45%" alt="Keyboard" />
  <img src="https://github.com/manjeetdeswal/Local-Stream-Upnp---Http-Server-/blob/main/LocalStreamPC/ss/Screenshot%202026-02-26%20104316.png" width="45%" alt="Display Extension" />
</p>
<p align="center">
  <img src="https://github.com/manjeetdeswal/Local-Stream-Upnp---Http-Server-/blob/main/LocalStreamPC/ss/Screenshot%202026-02-26%20104341.png" width="45%" alt="Mic Streaming" />
  <img src="https://github.com/manjeetdeswal/Local-Stream-Upnp---Http-Server-/blob/main/LocalStreamPC/ss/Screenshot%202026-02-26%20104516.png" width="45%" alt="Mic Streaming" />
</p>


</div>

---

## ✨ Features

| Feature | Description |
| :--- | :--- |
| ⚡ **Zero Lag** | Streams directly over LAN. No internet speed caps or buffering. |
| 🌐 **Cross-Platform** | Runs natively on **Windows, Linux, macOS** servers. |
| 📱 **Android App** | Dedicated client app for mobile browsing. |
| 📺 **UPnP / DLNA** | Automatically discovered by Smart TVs, Roku, and VLC. |
| 🌍 **Web Interface** | Browse and play files from any web browser (Chrome, Safari). |
| 🖼️ **Smart Previews** | Auto-generates video thumbnails (FFmpeg) and album art. |

---

## 📥 Downloads

| Platform | Download Link | Notes |
| :--- | :--- | :--- |
| **Windows** | `LocalStreamWin.zip` | Windows 10 & 11 (x64) |
| **Linux** | `LocalStreamLinux.zip` | Ubuntu, Debian, Fedora |
| **macOS** | `LocalStreamMac.zip` | Apple Silicon (M1/M2) |
| **Android** | https://play.google.com/store/apps/details?id=com.jeet_studio.localstream4k | Android 10+ (Client App) |

---

## 🚀 Installation Guide

Click the arrow next to your platform to see instructions.

<details>
<summary><strong>🪟 Windows (Click to Expand)</strong></summary>

1. Download **LocalStreamWin.zip**
2. Extract the ZIP file
3. Run `LocalStreamPC.exe`
4. If Windows SmartScreen appears → Click **More Info → Run Anyway**

</details>

<details>
<summary><strong>🐧 Linux (Click to Expand)</strong></summary>

1. Download **LocalStreamLinux.zip**
2. Open Terminal:

```bash
unzip LocalStreamLinux.zip
cd LocalStreamLinux
chmod +x LocalStreamPC
chmod +x ffmpeg
./LocalStreamPC


   or


# Move the app to a permanent location
   unzip LocalStreamLinux.zip -d LocalStream
   
   # Move that folder to /opt/ (the standard location for manual installs)
   sudo mv LocalStream /opt/
   
   # Make the core app and FFmpeg binaries executable
   sudo chmod +x /opt/LocalStream/LocalStreamPC
   sudo chmod +x /opt/LocalStream/ffmpeg
   sudo chmod +x /opt/LocalStream/ffprobe
   
   # Copy the .desktop file to register the app and its icon in your launcher
   sudo cp /opt/LocalStream/LocalStream.desktop /usr/share/applications/
```

</details>

<details>
<summary><strong>🍎 macOS (Click to Expand)</strong></summary>

1. Download **LocalStreamMac.zip**
2. Extract the files
3. Open Terminal and navigate:

```bash
cd ~/Downloads/LocalStreamMac
```

4. Remove macOS quarantine:

```bash
sudo codesign --force --deep --sign - LocalStream.app.
```

5. Self-sign (required on newer macOS):

```bash
sudo codesign --force --deep --sign - LocalStream.app
```

6. Run:

```bash
open LocalStream.app
```

### ⚠️ If you see `zsh: killed`

On Apple Silicon (M1/M2/M3):

```bash
softwareupdate --install-rosetta --agree-to-license
arch -x86_64 ./LocalStreamPC
```

</details>

<details>
<summary><strong>🤖 Android (Click to Expand)</strong></summary>

1. Download the app from the Play Store  
2. Open the app  
3. Enter the IP address shown in your PC server  
4. Connect and start streaming  

</details>

---

## 📖 How to Use

### Add Folders
Open the app on your PC and click **Add Folder** to select your movie or music folders.

### Start Server
Click the **Start Server** button to begin streaming.

### Connect
- **On Phone:** Open the Android app or any browser and enter the IP address shown.
- **On TV:** Open VLC or built-in Media Player → Look under **Local Network**.

---

## 🧠 Under the Hood

**HTTP Server (Port 8080)**  
Streams media with range-request support for instant seeking.

**UPnP (SSDP)**  
Broadcasts server so TVs, Roku, and VLC auto-discover it.

**FFmpeg Integration**  
Generates video thumbnails and previews dynamically.

---

## 🛠️ Developers: Build from Source

Requires **.NET 8 SDK**

### Clone

```bash
git clone https://github.com/manjeetdeswal/Local-Stream-Upnp---Http-Server-
cd Local-Stream-Upnp---Http-Server-
```

### Add Binaries Structure

```
/LocalStreamPC
  /Binaries
    /win    (ffmpeg.exe, ffprobe.exe)
    /linux  (ffmpeg, ffprobe)
    /mac    (ffmpeg, ffprobe)
```

### Build Commands

**Windows**
```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

**Linux**
```bash
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
```

**macOS (Apple Silicon)**
```bash
dotnet publish -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true
```

---

<div align="center">
  <p>Distributed under the <strong>MIT License</strong>.</p>
  <img src="https://img.shields.io/badge/Made%20with-%E2%9D%A4-red" alt="Made with Love">
  <p><b>Manjeet Deswal</b></p>
</div>
