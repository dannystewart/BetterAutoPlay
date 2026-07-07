# 🎮 Better Auto Play

[![BepInEx](https://img.shields.io/badge/BepInEx-IL2CPP-blue)](https://github.com/BepInEx/BepInEx)
[![.NET](https://img.shields.io/badge/.NET-6.0-purple)](https://dotnet.microsoft.com/download/dotnet/6.0)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

A smart card sorting plugin for **Vampire Crawlers** that optimizes auto-play by prioritizing mana generators and utility cards before attacks.

## ✨ Features

- **Mana-First Strategy**: Mana-generating cards (Empty Tome, etc.) are played first to enable more plays
- **Utility Buffing**: Utility/buff cards are played before attacks to maximize damage output
- **Smart Combo Chain**: Keeps same-cost mana-generator chains alive before stepping up to the next mana cost
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
   - Mana generators that **maintain** mana cost
   - Cards that **increase** mana cost, with mana generators prioritized inside that step
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
- The game must have been launched once through BepInEx so `BepInEx/interop` has been generated

### Build Steps

1. **Clone the repository**:
   ```bash
   git clone https://github.com/yourusername/BetterAutoPlay.git
   cd BetterAutoPlay
   ```

2. **Generate BepInEx Interop DLLs**:

   > **Why?** Vampire Crawlers is built with IL2CPP. We need to dump the game's DLLs to get type definitions like `CardModel`, `PlayerModel`, etc.

   Launch Vampire Crawlers once through BepInEx. BepInEx will generate the interop assemblies automatically in `BepInEx/interop`.

   On macOS, install BepInEx using the IL2CPP macOS instructions and launch through `run_bepinex.sh` as described in the BepInEx documentation.

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

   The project has default game paths for Windows and macOS:
   ```
   C:\Program Files (x86)\Steam\steamapps\common\Vampire Crawlers
   /Users/danny/Library/Application Support/Steam/steamapps/common/Vampire Crawlers
   ```

   If your game is installed elsewhere, pass `GameDir` when building:
   ```bash
   dotnet build -c Release -p:GameDir="/path/to/Vampire Crawlers"
   ```

4. **Build the project**:
   ```bash
   dotnet build -c Release
   ```

5. **Copy to game**:
   ```bash
   # Windows (PowerShell)
   Copy-Item bin\Release\BetterAutoPlay.dll "C:\Program Files (x86)\Steam\steamapps\common\Vampire Crawlers\BepInEx\plugins\BetterAutoPlay\"

   # macOS
   mkdir -p "/Users/danny/Library/Application Support/Steam/steamapps/common/Vampire Crawlers/BepInEx/plugins/BetterAutoPlay"
   cp bin/Release/BetterAutoPlay.dll "/Users/danny/Library/Application Support/Steam/steamapps/common/Vampire Crawlers/BepInEx/plugins/BetterAutoPlay/"
   
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
