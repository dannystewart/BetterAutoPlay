# 🎮 Better Auto Play

[![BepInEx](https://img.shields.io/badge/BepInEx-IL2CPP-blue)](https://github.com/BepInEx/BepInEx)
[![.NET](https://img.shields.io/badge/.NET-6.0-purple)](https://dotnet.microsoft.com/download/dotnet/6.0)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

A smart card sorting plugin for **Vampire Crawlers** that optimizes auto-play by prioritizing mana generators and utility cards before attacks.

## ✨ Features

- **Mana-First Strategy**: Mana-generating cards (Empty Tome, etc.) are played first to enable more plays
- **Utility Buffing**: Utility/buff cards are played before attacks to maximize damage output
- **Smart Combo Chain**: Prevents inefficient `0→0→0→1` chains by pairing low-cost plays with optimal follow-ups
- **Crawler Support**: FCC companion cards are intelligently slotted into the play order
- **Auto-Play Optimization**: Automatically enables Combo sorting mode during auto-play

## 📋 How It Works

Cards are classified into five priority tiers:

| Role | Priority | Examples |
|------|----------|----------|
| **Mana Generator** | 0 (highest) | Empty Tome, Mana gain cards |
| **Utility** | 1 | Candles, stat buffs, support |
| **Crawler** | 2 | FCC companion cards |
| **Attack** | 3 | Whip, Runetracer, damage cards |
| **Unknown** | 4 (lowest) | Unclassified cards |

### Combo Algorithm

1. **Starting Card**: Picks the best card that continues an existing combo, or the highest-priority card available
2. **Combo Chain**: Selects next cards based on:
   - Mana generators (always prioritized)
   - Cards that **increase** mana cost (proper combo steps)
   - Cards that **maintain** mana cost (fallback)
   - Cards that **decrease** mana cost (last resort)
3. **Remaining Cards**: Sorted by role → mana gain → mana cost → evolved status

---

## 📦 Installation

### Step 1: Install BepInEx

> **IMPORTANT**: This mod requires BepInEx to be installed first. BepInEx is a framework that allows modding games.

#### 1.1 - Download BepInEx

1. Click the link below:
   **[https://github.com/BepInEx/BepInEx/releases/latest](https://github.com/BepInEx/BepInEx/releases/latest)**

2. You will see an **"Assets"** header on the page with several files below it.

3. If your computer is **64-bit** (most likely):
   - 📥 Download **`BepInEx_x64_6.0.0-be.661.zip`** (or similar version)

4. If your computer is **32-bit** (rare):
   - 📥 Download **`BepInEx_x86_6.0.0-be.661.zip`** (or similar version)

#### 1.2 - Locate Game Folder

Now let's find where Vampire Crawlers is installed:

1. Open **Steam**
2. Go to the **Library** tab
3. Find **Vampire Crawlers** in your game list
4. **Right-click** on the game
5. Select **"Properties..."** from the menu
6. Click on the **"Installed Files"** tab
7. Press the **"Browse..."** button

🎉 You are now in the game folder! **Keep this window open** in the background.

#### 1.3 - Extract BepInEx to Game Folder

1. Find the downloaded **.zip** file (should be in Downloads folder)
2. **Right-click** the zip file → **"Extract All..."** or **"Extract here"**
3. Extract to **the game folder you just opened**
4. When done, the game folder should look like:
   ```
   Vampire Crawlers/
   ├── BepInEx/
   ├── GameAssembly.dll
   ├── UnityPlayer.dll
   └── ... (other files)
   ```

#### 1.4 - Run the Game Once

1. Launch **Vampire Crawlers** from Steam
2. When you reach the main menu, **close the game**
3. This step allows BepInEx to create necessary files

✅ **BepInEx installation complete!**

---

### Step 2: Download the Mod

1. Go to the **"Releases"** section on this GitHub page
2. Download the **`BetterAutoPlay.dll`** file from the latest release
3. The file is small (~15 KB), download will be quick

---

### Step 3: Install the Mod

1. Go to the game folder you found in **Step 1.2**
2. Open the **`BepInEx`** folder
3. Open the **`plugins`** folder
4. Create a new folder here: **`BetterAutoPlay`**
5. Copy the downloaded **`BetterAutoPlay.dll`** file into this new folder

It should look like this:
```
Vampire Crawlers/
└── BepInEx/
    └── plugins/
        └── BetterAutoPlay/
            └── BetterAutoPlay.dll  ← Put it here!
```

---

### Step 4: Test

1. Launch the game
2. The mod should load automatically
3. To verify it's working:
   - Open the **`BepInEx/LogOutput.log`** file in the game folder
   - Search for "Better Auto Play loaded"

🎮 **Enjoy the game!**

---

## 🛠️ Building from Source

### Prerequisites

- [.NET SDK 6.0+](https://dotnet.microsoft.com/download)
- **BepInEx IL2CPP must be installed** (follow Step 1 above)
- Vampire Crawlers must be installed on Steam
- [Il2CppDumper](https://github.com/Perfare/Il2CppDumper/releases) - to generate interop DLLs

### Build Steps

1. **Clone the repository**:
   ```bash
   git clone https://github.com/yourusername/BetterAutoPlay.git
   cd BetterAutoPlay
   ```

2. **Generate Interop DLLs with Il2CppDumper**:

   > **Why?** Vampire Crawlers is built with IL2CPP. We need to dump the game's DLLs to get type definitions like `CardModel`, `PlayerModel`, etc.

   1. Download [Il2CppDumper](https://github.com/Perfare/Il2CppDumper/releases) (e.g., `Il2CppDumper-win-v6.7.46.zip`)
   2. Extract it somewhere (e.g., `C:\Il2CppDumper`)
   3. Run `Il2CppDumper.exe`
   4. Select file: Navigate to your game folder and select `GameAssembly.dll`
   5. Select folder: Choose `C:\Program Files (x86)\Steam\steamapps\common\Vampire Crawlers\BepInEx\`
   6. A new `DummyDLL` folder will be created - rename it to `interop`
   7. Move `interop` folder into `BepInEx\` (if not already there)

   Result should look like:
   ```
   Vampire Crawlers/
   └── BepInEx/
       ├── core/
       └── interop/
           ├── Assembly-CSharp.dll
           ├── Pancake.dll
           ├── UnityEngine.CoreModule.dll
           └── Sirenix.Serialization.dll
   ```

3. **Check your Steam path**:

   The project expects the game at:
   ```
   C:\Program Files (x86)\Steam\steamapps\common\Vampire Crawlers
   ```

   If your game is installed elsewhere, open the `.csproj` file and update all the `HintPath` entries to match your path.

4. **Build the project**:
   ```bash
   dotnet build -c Release
   ```

5. **Copy to game**:
   ```bash
   # Windows (PowerShell)
   Copy-Item bin\Release\BetterAutoPlay.dll "C:\Program Files (x86)\Steam\steamapps\common\Vampire Crawlers\BepInEx\plugins\BetterAutoPlay\"
   
   # Or manually copy the DLL to:
   # <Game Folder>\BepInEx\plugins\BetterAutoPlay\
   ```

6. **Output**: `bin/Release/BetterAutoPlay.dll` is now ready to use!

---

## 🐛 Troubleshooting

**Mod not loading?**
- Make sure you installed BepInEx IL2CPP (not Mono)
- Check the `BepInEx/LogOutput.log` file
- Ensure the DLL is in `BepInEx/plugins/BetterAutoPlay/` folder

**Game won't start?**
- Make sure you're using the IL2CPP version
- Update BepInEx to the latest version
- Verify game and BepInEx version compatibility

---

## 📄 License

MIT License - Copyright (c) 2025 CriticalRange

## 🤝 Contributing

Contributions are welcome! Feel free to open issues or pull requests.

## 📜 Changelog

### v0.1.0
- Initial release
- Mana-first card sorting
- Combo chain optimization
- Auto-play integration

---

**Made with ❤️ for the Vampire Crawlers community**
