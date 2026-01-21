<!DOCTYPE html>
<html lang="en" class="scroll-smooth">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>LocalStream - Universal Media Server</title>
    <script src="https://cdn.tailwindcss.com"></script>
    <script>
        tailwind.config = {
            theme: {
                extend: {
                    colors: {
                        primary: '#3B82F6',
                        dark: '#030712',
                        card: '#1F2937',
                        accent: '#10B981'
                    }
                }
            }
        }
    </script>
    <style>
        body { font-family: 'Inter', sans-serif; background-color: #030712; color: white; }
        .glow { box-shadow: 0 0 20px rgba(59, 130, 246, 0.5); }
        .tab-active { border-bottom: 2px solid #3B82F6; color: white; }
        .tab-inactive { color: #9CA3AF; }
        .step-circle { width: 32px; height: 32px; display: flex; align-items: center; justify-content: center; border-radius: 50%; background: #3B82F6; font-weight: bold; }
        /* Custom Scrollbar */
        ::-webkit-scrollbar { width: 8px; }
        ::-webkit-scrollbar-track { background: #111827; }
        ::-webkit-scrollbar-thumb { background: #374151; border-radius: 4px; }
        ::-webkit-scrollbar-thumb:hover { background: #4B5563; }
    </style>
</head>
<body class="antialiased text-gray-100">

    <nav class="fixed w-full z-50 bg-dark/80 backdrop-blur-md border-b border-gray-800">
        <div class="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
            <div class="flex items-center justify-between h-16">
                <span class="text-xl font-bold text-white flex items-center gap-2">
                    📡 LocalStream
                </span>
                <div class="hidden md:block">
                    <div class="ml-10 flex items-baseline space-x-4">
                        <a href="#features" class="hover:text-white text-gray-300 px-3 py-2 rounded-md text-sm font-medium">Features</a>
                        <a href="#how-to-use" class="hover:text-white text-gray-300 px-3 py-2 rounded-md text-sm font-medium">Guide</a>
                        <a href="#download" class="hover:text-white text-gray-300 px-3 py-2 rounded-md text-sm font-medium">Download</a>
                        <a href="#developers" class="bg-gray-800 hover:bg-gray-700 text-white px-3 py-2 rounded-md text-sm font-medium transition">Developers</a>
                    </div>
                </div>
            </div>
        </div>
    </nav>

    <header class="relative pt-32 pb-20 overflow-hidden text-center">
        <div class="max-w-7xl mx-auto px-4">
            <div class="flex justify-center gap-2 mb-6 opacity-90 flex-wrap">
                <img src="https://img.shields.io/badge/.NET-8.0-512BD4?style=flat&logo=dotnet" alt=".NET 8">
                <img src="https://img.shields.io/badge/UI-Avalonia-AF1859?style=flat" alt="Avalonia UI">
                <img src="https://img.shields.io/badge/Platform-Win%20%7C%20Linux%20%7C%20Mac%20%7C%20Android-lightgrey" alt="Platforms">
                <img src="https://img.shields.io/badge/License-MIT-green" alt="License">
            </div>

            <h1 class="text-5xl md:text-7xl font-bold tracking-tight mb-6">
                Your Media. <br>
                <span class="text-transparent bg-clip-text bg-gradient-to-r from-blue-400 to-emerald-400">On Every Screen.</span>
            </h1>
            <p class="text-xl text-gray-400 max-w-2xl mx-auto mb-10">
                Turn your PC into a powerful local streaming server. 
                Watch movies, listen to music, and browse photos on any device in your house.
            </p>
            <div class="flex justify-center gap-4">
                <a href="#download" class="bg-primary hover:bg-blue-600 text-white font-bold py-3 px-8 rounded-full transition transform hover:scale-105 glow">
                    Get Started
                </a>
                <a href="#how-to-use" class="bg-card hover:bg-gray-700 text-white font-semibold py-3 px-8 rounded-full transition border border-gray-700">
                    How it Works
                </a>
            </div>
        </div>
    </header>

    <section id="features" class="py-20 bg-gray-900/50">
        <div class="max-w-7xl mx-auto px-4">
            <div class="grid md:grid-cols-3 gap-8">
                <div class="bg-card p-8 rounded-2xl border border-gray-800 hover:border-blue-500 transition">
                    <div class="text-4xl mb-4">⚡</div>
                    <h3 class="text-xl font-bold mb-2">Zero Lag</h3>
                    <p class="text-gray-400">Streams directly over your Wi-Fi. No internet usage, no buffering, just pure speed.</p>
                </div>
                <div class="bg-card p-8 rounded-2xl border border-gray-800 hover:border-blue-500 transition">
                    <div class="text-4xl mb-4">📱</div>
                    <h3 class="text-xl font-bold mb-2">Any Device</h3>
                    <p class="text-gray-400">Works with Smart TVs, Phones, Tablets, VLC, and Web Browsers.</p>
                </div>
                <div class="bg-card p-8 rounded-2xl border border-gray-800 hover:border-blue-500 transition">
                    <div class="text-4xl mb-4">🖼️</div>
                    <h3 class="text-xl font-bold mb-2">Smart Previews</h3>
                    <p class="text-gray-400">Automatically generates thumbnails for your videos and album art for your music.</p>
                </div>
            </div>
        </div>
    </section>

    <section id="how-to-use" class="py-20">
        <div class="max-w-5xl mx-auto px-4">
            <h2 class="text-3xl font-bold mb-12 text-center">How to Use LocalStream</h2>
            
            <div class="grid md:grid-cols-2 gap-12 items-center">
                <div class="space-y-8">
                    <div class="flex gap-4">
                        <div class="step-circle shrink-0">1</div>
                        <div>
                            <h3 class="text-xl font-bold">Add Your Folders</h3>
                            <p class="text-gray-400 mt-2">Open the app and click <strong>"Add Folder"</strong>. Select the folders containing your movies, music, or photos.</p>
                        </div>
                    </div>
                    <div class="flex gap-4">
                        <div class="step-circle shrink-0">2</div>
                        <div>
                            <h3 class="text-xl font-bold">Start the Server</h3>
                            <p class="text-gray-400 mt-2">Click the big <strong>"Start Server"</strong> button. The status bar will turn green, indicating you are live.</p>
                        </div>
                    </div>
                    <div class="flex gap-4">
                        <div class="step-circle shrink-0">3</div>
                        <div>
                            <h3 class="text-xl font-bold">Connect & Play</h3>
                            <p class="text-gray-400 mt-2">On your phone or TV, open a browser and type the IP address shown in the app (e.g., <code>192.168.1.5:8080</code>) OR use a DLNA app like VLC.</p>
                        </div>
                    </div>
                </div>

                <div class="bg-card p-8 rounded-2xl border border-gray-700">
                    <h3 class="text-lg font-bold mb-4 text-blue-400">💡 Pro Tip: Dark Mode</h3>
                    <p class="text-gray-400 mb-6">You can toggle between Light and Dark themes in the app settings to match your system preference.</p>
                    
                    <h3 class="text-lg font-bold mb-4 text-emerald-400">📂 Supported Formats</h3>
                    <div class="flex flex-wrap gap-2">
                        <span class="px-3 py-1 bg-gray-800 rounded-md text-sm">MP4</span>
                        <span class="px-3 py-1 bg-gray-800 rounded-md text-sm">MKV</span>
                        <span class="px-3 py-1 bg-gray-800 rounded-md text-sm">MP3</span>
                        <span class="px-3 py-1 bg-gray-800 rounded-md text-sm">JPG</span>
                        <span class="px-3 py-1 bg-gray-800 rounded-md text-sm">PDF</span>
                        <span class="px-3 py-1 bg-gray-800 rounded-md text-sm">ZIP</span>
                    </div>
                </div>
            </div>
        </div>
    </section>

    <section id="tech" class="py-20 bg-gray-900">
        <div class="max-w-6xl mx-auto px-4">
            <h2 class="text-3xl font-bold mb-12 text-center">Under the Hood</h2>
            
            <div class="grid md:grid-cols-2 gap-8">
                <div class="bg-dark p-8 rounded-2xl border border-gray-800 relative overflow-hidden">
                    <div class="absolute top-0 right-0 p-4 opacity-10 text-9xl font-bold select-none">HTTP</div>
                    <h3 class="text-2xl font-bold mb-4 flex items-center gap-3">
                        <span class="text-blue-500">🌐</span> HTTP Web Server
                    </h3>
                    <p class="text-gray-400 leading-relaxed mb-4">
                        LocalStream acts like a mini-website hosted directly on your PC. When you start the server, it opens a "port" (8080) that allows other devices to request files.
                    </p>
                    <ul class="space-y-2 text-gray-500">
                        <li>• Generates a beautiful web interface for browsing.</li>
                        <li>• Handles file downloads and video streaming chunks.</li>
                        <li>• Works with any modern web browser (Chrome, Safari, Edge).</li>
                    </ul>
                </div>

                <div class="bg-dark p-8 rounded-2xl border border-gray-800 relative overflow-hidden">
                    <div class="absolute top-0 right-0 p-4 opacity-10 text-9xl font-bold select-none">UPnP</div>
                    <h3 class="text-2xl font-bold mb-4 flex items-center gap-3">
                        <span class="text-emerald-500">📡</span> UPnP & DLNA
                    </h3>
                    <p class="text-gray-400 leading-relaxed mb-4">
                        <strong>Universal Plug and Play (UPnP)</strong> is a discovery protocol. It acts like a digital beacon, shouting "I am here!" to your network.
                    </p>
                    <ul class="space-y-2 text-gray-500">
                        <li>• Allows Smart TVs and VLC to find your server automatically.</li>
                        <li>• No need to manually type IP addresses.</li>
                        <li>• Compatible with thousands of DLNA-certified devices.</li>
                    </ul>
                </div>
            </div>
        </div>
    </section>

    <section id="download" class="py-20">
        <div class="max-w-7xl mx-auto px-4 text-center">
            <h2 class="text-3xl font-bold mb-4">Download LocalStream</h2>
            <p class="text-gray-400 mb-12">Choose your platform to get started.</p>
            
            <div class="grid md:grid-cols-2 lg:grid-cols-4 gap-6 max-w-7xl mx-auto">
                <div class="bg-card border border-gray-700 rounded-2xl p-8 hover:border-blue-500 transition relative group">
                    <div class="absolute top-0 right-0 bg-blue-600 text-xs font-bold px-3 py-1 rounded-bl-lg">RECOMMENDED</div>
                    <h3 class="text-2xl font-bold mb-2">Windows</h3>
                    <p class="text-gray-400 mb-6">Windows 10 & 11 (x64)</p>
                    <a href="downloads/LocalStream-Win.zip" class="block w-full bg-white text-black font-bold py-3 rounded-lg hover:bg-gray-200 transition">
                        Download .zip
                    </a>
                </div>

                <div class="bg-card border border-gray-700 rounded-2xl p-8 hover:border-orange-500 transition">
                    <h3 class="text-2xl font-bold mb-2">Linux</h3>
                    <p class="text-gray-400 mb-6">Ubuntu, Debian, Fedora</p>
                    <a href="downloads/LocalStream-Linux.zip" class="block w-full bg-gray-700 text-white font-bold py-3 rounded-lg hover:bg-gray-600 transition">
                        Download .zip
                    </a>
                </div>

                <div class="bg-card border border-gray-700 rounded-2xl p-8 hover:border-gray-400 transition">
                    <h3 class="text-2xl font-bold mb-2">macOS</h3>
                    <p class="text-gray-400 mb-6">Apple Silicon (M1/M2/M3)</p>
                    <a href="downloads/LocalStream-Mac.zip" class="block w-full bg-gray-700 text-white font-bold py-3 rounded-lg hover:bg-gray-600 transition">
                        Download .zip
                    </a>
                </div>

                <div class="bg-card border border-gray-700 rounded-2xl p-8 hover:border-green-500 transition relative">
                    <div class="absolute top-0 right-0 bg-green-600 text-xs font-bold px-3 py-1 rounded-bl-lg">MOBILE</div>
                    <h3 class="text-2xl font-bold mb-2">Android</h3>
                    <p class="text-gray-400 mb-6">Android 10+</p>
                    <a href="downloads/LocalStream-Android.apk" class="block w-full bg-green-600 text-white font-bold py-3 rounded-lg hover:bg-green-500 transition">
                        Download .apk
                    </a>
                </div>
            </div>
        </div>
    </section>

    <section class="py-20 bg-dark">
        <div class="max-w-4xl mx-auto px-4">
            <h2 class="text-2xl font-bold text-center mb-8">Installation Instructions</h2>

            <div class="flex flex-wrap justify-center mb-8 border-b border-gray-700">
                <button onclick="showTab('win')" id="tab-win" class="px-6 py-4 font-medium tab-active transition">Windows</button>
                <button onclick="showTab('linux')" id="tab-linux" class="px-6 py-4 font-medium tab-inactive transition">Linux</button>
                <button onclick="showTab('mac')" id="tab-mac" class="px-6 py-4 font-medium tab-inactive transition">macOS</button>
                <button onclick="showTab('android')" id="tab-android" class="px-6 py-4 font-medium tab-inactive transition text-green-400">Android</button>
            </div>

            <div id="content-win" class="bg-card p-8 rounded-xl border border-gray-700">
                <ol class="list-decimal list-inside space-y-4 text-gray-300">
                    <li>Extract the downloaded <strong>.zip</strong> file.</li>
                    <li>Open the extracted folder.</li>
                    <li>Double-click <code class="bg-black px-2 py-1 rounded text-blue-400">LocalStreamPC.exe</code>.</li>
                    <li>Click <strong>"Run Anyway"</strong> if prompted by Windows SmartScreen.</li>
                </ol>
            </div>

            <div id="content-linux" class="hidden bg-card p-8 rounded-xl border border-gray-700">
                <div class="bg-black p-4 rounded-lg font-mono text-sm text-green-400 mb-4">
                    unzip LocalStream-Linux.zip<br>
                    chmod +x LocalStreamPC<br>
                    chmod +x ffmpeg<br>
                    ./LocalStreamPC
                </div>
            </div>

            <div id="content-mac" class="hidden bg-card p-8 rounded-xl border border-gray-700">
                <p class="mb-4 text-gray-400">Run this command to fix the "App is damaged" error:</p>
                <div class="bg-black p-4 rounded-lg font-mono text-sm text-green-400 mb-4">
                    xattr -cr ~/Downloads/LocalStreamPC/
                </div>
            </div>

            <div id="content-android" class="hidden bg-card p-8 rounded-xl border border-gray-700">
                <ol class="list-decimal list-inside space-y-4 text-gray-300">
                    <li>Download the <strong>.apk</strong> file to your phone.</li>
                    <li>Tap the file to open it.</li>
                    <li>If prompted, enable <strong>"Install from Unknown Sources"</strong> in your settings.</li>
                    <li>Tap <strong>Install</strong>.</li>
                </ol>
            </div>
        </div>
    </section>

    <section id="developers" class="py-20 bg-gray-900">
        <div class="max-w-5xl mx-auto px-4">
            <h2 class="text-3xl font-bold mb-8 text-center">🛠️ Building from Source</h2>
            <p class="text-gray-400 text-center mb-10 max-w-2xl mx-auto">
                Want to modify the code? You can build LocalStream yourself using the .NET 8 SDK.
            </p>

            <div class="bg-card p-8 rounded-xl border border-gray-700">
                <h3 class="text-xl font-bold mb-4">1. Clone & Setup</h3>
                <div class="bg-black p-4 rounded-lg font-mono text-sm text-green-400 mb-6">
                    git clone https://github.com/yourusername/LocalStream.git<br>
                    cd LocalStream
                </div>

                <h3 class="text-xl font-bold mb-4">2. Add FFmpeg Binaries</h3>
                <p class="text-gray-400 mb-4 text-sm">Create a <code class="bg-gray-800 px-1 rounded">Binaries</code> folder in the project root with subfolders for each OS:</p>
                <div class="grid md:grid-cols-3 gap-4 mb-6 text-sm font-mono text-gray-300">
                    <div class="bg-gray-800 p-3 rounded">/Binaries/win/ffmpeg.exe</div>
                    <div class="bg-gray-800 p-3 rounded">/Binaries/linux/ffmpeg</div>
                    <div class="bg-gray-800 p-3 rounded">/Binaries/mac/ffmpeg</div>
                </div>

                <h3 class="text-xl font-bold mb-4">3. Build Commands</h3>
                <div class="bg-black p-4 rounded-lg font-mono text-sm text-green-400 overflow-x-auto">
                    # Windows<br>
                    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true<br><br>
                    # Linux<br>
                    dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true<br><br>
                    # macOS (Apple Silicon)<br>
                    dotnet publish -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true
                </div>
            </div>
        </div>
    </section>

    <footer class="py-10 text-center text-gray-600 border-t border-gray-800 mt-10">
        <p>&copy; 2026 LocalStream. Distributed under the MIT License.</p>
        <p class="text-sm mt-2">Built with .NET 8 & Avalonia UI.</p>
    </footer>

    <script>
        function showTab(os) {
            ['win', 'linux', 'mac', 'android'].forEach(id => {
                document.getElementById('content-' + id).classList.add('hidden');
                document.getElementById('tab-' + id).className = 'px-6 py-4 font-medium tab-inactive transition';
            });
            
            document.getElementById('content-' + os).classList.remove('hidden');
            document.getElementById('tab-' + os).className = 'px-6 py-4 font-medium tab-active transition';
            
            if(os === 'android') {
                document.getElementById('tab-android').classList.add('text-green-400', 'border-green-500');
            }
        }
    </script>
</body>
</html>
