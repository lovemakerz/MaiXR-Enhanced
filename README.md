# MaiMaiVR - Enhanced V0.7.5.4

Enhanced VR arcade experience for **maimai DX**, based on [MaiDXR](https://github.com/xiaopeng12138/MaiDXR).

> **MaiMaiVR - Enhanced is built on top of MaiDXR and keeps full credit to the original project and its contributors.**
>
> Original MaiDXR project:  
> [https://github.com/xiaopeng12138/MaiDXR](https://github.com/xiaopeng12138/MaiDXR)

---

## About

MaiMaiVR - Enhanced extends MaiDXR with a portable launcher, improved VR interaction and direct communication with the game.

Version **0.7.5.4** is the stable multi-region release. It supports the audited international setup and the audited Japanese DX 1.66 setup.

Its native I/O transport sends VR touch contacts, cabinet buttons and RGB LED data through shared memory. It does not require virtual COM ports, com0com or an additional Windows driver.

The project focuses on:

- simple installation and launch;
- responsive VR touch interaction;
- reliable rhythm-game contact detection;
- native touch, button and LED transport without COM emulation;
- automatic selection and preservation of game installations;
- Player 1 and Player 2 support;
- spectator and third-person use;
- portable and offline distribution;
- automatic diagnostic reports.

---

## Main Features

### MaiDXR foundation

MaiMaiVR - Enhanced preserves the main MaiDXR experience:

- maimai DX cabinet environment;
- VR controller interaction;
- customizable controller haptics;
- Player 1 and Player 2 displays;
- 90 Hz and 120 Hz capture modes;
- third-person and spectator camera;
- smooth camera movement;
- customizable buttons;
- in-game configuration panel;
- BepInEx plugin support.

### Native game I/O

MaiMaiVR V0.7.5.4 uses the included `mai2io-rave` bridge and shared memory for direct game communication:

- 34 touch zones transmitted independently;
- simultaneous multi-contact support;
- automatic contact release if the VR stream stops;
- cabinet test, service, coin and side-button input;
- dynamic RGB data for the eight cabinet buttons;
- no COM3, COM5, COM21 or COM51 dependency;
- no com0com installation;
- no virtual serial-port configuration.

MaiDXR is started and validated before the game. The launcher waits for the VR plugin, shared memory and LightManager before continuing.

### Improved VR interaction

- native collider-based touch observation;
- 60 Hz rhythm-touch physics;
- short recovery sweep for fast hand movement;
- touch keepalive and release protection;
- controller haptics on contact;
- background input support while the game has focus;
- session input bridge for test, service, coin and cabinet buttons.

### Cabinet lighting

- dynamic RGB transport from the game;
- GS output used as the primary source;
- DC and Billboard fallback handling;
- eight illuminated cabinet buttons;
- stable cabinet contour lighting;
- protected handling of malformed LED frames.

The stable white cabinet halo is a visual effect. The eight cabinet buttons are the reference used to verify dynamic RGB transport.

### International and Japanese game profiles

The launcher supports:

- the audited international installation;
- the audited Japanese DX 1.66 installation.

For Japanese DX 1.66, the compatible SegaTools hook is copied under a temporary session filename. The hook already installed with the game is never overwritten.

The V0.7.5.4 launcher uses absolute paths for `inject.exe`, the session hook, Sinmai and amdaemon data. This prevents a `start.bat` working-directory change from breaking the launch.

Unknown SegaTools builds are rejected before any game file is changed.

### Automatic AquaMai session configuration

When a supported AquaMai 2.5 `AquaMai.toml` is detected next to `Sinmai.exe`, MaiMaiVR temporarily:

- enables the normal two-player path;
- disables the SinglePlayer module;
- restores normal TIME SKIP and menu countdowns;
- disables the DisableTimeout module.

The original TOML is backed up before the session and restored byte-for-byte afterward. Window size, network settings, shortcuts and unrelated AquaMai options are left unchanged.

If AquaMai is absent, the game starts normally. An unknown TOML format is preserved and reported instead of being modified.

### Safe temporary configuration

During a VR session, MaiMaiVR can temporarily apply the required game settings:

```ini
[Debug]
SinglePlayer=0

[AM]
DummyTouchPanel=0
DummyLED=1
```

The original `mai2.ini`, `AquaMai.toml`, `start.bat` and SegaTools configuration are preserved. Session changes are restored when the game closes or when startup fails.

An interrupted restoration is detected and completed automatically on the next launch.

---

## Portable Full Build

MaiMaiVR - Enhanced is distributed as a complete portable package.

No separate MaiDXR, BepInEx, Roslyn or native-I/O download is required.

```text
MaiMaiVR_V0.7.5.4_NATIVE_IO_MULTI_REGION\
├─ MaiMaiVR.exe
├─ 00_START_HERE.txt
├─ README.md
├─ README.txt
├─ VERSION.txt
├─ Config\
├─ Documentation\
├─ Runtime\
│  ├─ MaiDXR\
│  │  ├─ BepInEx\
│  │  └─ MaiDXR.exe
│  └─ NativeIO\
│     ├─ mai2io.dll
│     └─ mai2hook_jp166_compatible.dll
├─ Scripts\
├─ Source\
├─ Tools\
│  └─ Roslyn\
├─ THIRD_PARTY_LICENSES\
└─ THIRD_PARTY_SOURCE\
```

User settings, temporary files and reports remain inside the portable MaiMaiVR folder.

---

## Requirements

- Windows 10 or Windows 11, 64-bit;
- SteamVR installed;
- a VR headset and controllers available through SteamVR/OpenXR;
- an audited maimai DX international installation or Japanese DX 1.66 installation;
- the game closed before starting MaiMaiVR.

com0com is neither required nor distributed.

---

## Installation

1. Extract the complete ZIP into a new folder.
2. Keep the original directory structure intact.
3. Start `MaiMaiVR.exe`.
4. Select the game folder when requested:
   - **International:** select the folder containing `start.bat`;
   - **Japanese DX 1.66:** select the `Package` folder containing `start.bat`, `Sinmai.exe` and `inject.exe`.
5. Select the installation in the launcher.
6. Click **LAUNCH IN VR**.

Do not copy MaiMaiVR files into the game directory. Do not rename or replace the game's original `mai2hook.dll`.

---

## Normal Session Flow

MaiMaiVR automatically:

1. checks the selected game installation;
2. verifies the compatible SegaTools profile;
3. prepares temporary native-I/O and AquaMai settings;
4. starts SteamVR;
5. compiles and loads the included MaiDXR plugin;
6. starts MaiDXR before the game;
7. starts amdaemon and Sinmai with absolute paths;
8. validates the native shared-memory connection;
9. runs the VR input bridge;
10. restores the original game files after the session;
11. creates a diagnostic report.

Wait for the launcher to finish restoration before moving or deleting the MaiMaiVR folder.

---

## Diagnostics

After every session, MaiMaiVR creates:

```text
Reports\MaiMaiVR_V0.7.5.4_LAST_SESSION.zip
```

If startup, touch input or lighting fails, send this ZIP with a short description of what happened. It contains the generated launch command, compatibility information, native-I/O counters and restoration result.

The report does not contain the complete game installation.

---

## Validation Status

The native transport has been validated with:

- all 34 touch zones sent individually;
- 34 simultaneous contacts;
- automatic release after the input stream stops;
- red, green and blue output on all eight LED channels;
- game-side native-I/O connection;
- exact restoration of temporary configuration files.

Actual note judgement, headset rendering and gameplay feel must still be verified on each game and VR configuration.

---

## Source and Licenses

MaiMaiVR - Enhanced source files are included under `Source`.

Third-party notices, licenses and redistributed source archives are included under:

```text
THIRD_PARTY_NOTICES.txt
THIRD_PARTY_LICENSES\
THIRD_PARTY_SOURCE\
```

Included third-party components remain governed by their respective licenses:

- MaiDXR;
- BepInEx;
- Microsoft Roslyn;
- mai2io-rave;
- SegaTools;
- capnhook;
- cwinwebsocket.

MaiMaiVR - Enhanced itself is distributed under the MIT License. See `LICENSE.txt`.
