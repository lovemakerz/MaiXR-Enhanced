MaiMaiVR - Enhanced source included with this portable distribution:

  MaiMaiVRIntegratedBaseline.cs   validated VR/runtime core
  MaiMaiVRAmbientLab.cs          AMBIANT UI / visual controls
  MaiMaiVRSurfaceMath.cs         surface-contact geometry helpers
  MaiMaiVRSurfaceData.cs         surface-contact runtime data
  MaiMaiVRSurfaceContact.cs      constrained physical hand contact
  MaiMaiVRRhythmTouch.cs         60 Hz hybrid/swept rhythm touch recovery
  MaiMaiVRNativeIO.cs            V0.7.4 mai2io-rave shared-memory bridge + native diagnostics
  Launcher\MaiMaiVRBootstrap.c   native portable launcher bootstrap

The PowerShell backend source is kept directly in the Scripts folder.
MaiMaiVR - Enhanced source is licensed under the MIT License in ..\LICENSE.txt.

V0.7.4 handshake delta
- Session policy: SinglePlayer=0 / DummyTouchPanel=0 / DummyLED=1, then exact mai2.ini restore.
- MaiDXR is launched and validated before Sinmai.
- SegaTools custom IO path is supplied by INI and SEGATOOLS_MAI2IO_PATH.
- Bundled x64 mai2io.dll rearms the SegaTools touch STAT gate internally.
- Separate native diagnostic mapping reports touch init/update/callback/STAT and LED callback counters.
- LED output consumer still prefers GS, with DC/Billboard fallbacks.
