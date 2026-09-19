// MaiMaiVR - Enhanced V0.7.4 Native IO bridge
// MIT License - MaiMaiVR project code
// Bridges MaiDXR directly to mai2io-rave v0.4.2 shared memory.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using UnityEngine;

public sealed class MaiMaiVRNativeIO : MonoBehaviour
{
    private const string MappingName = "Local\\MAI2IO_RAVE_V2";
    private const string DiagMappingName = "Local\\MAIMAIVR_NATIVE_DIAG_V074";
    private const uint DiagMagic = 0x3437444Du;
    private const int DiagStructSize = 96;
    private const uint Magic = 0x5652324Du;
    private const ushort Major = 2;
    private const ushort Minor = 0;
    private const int StructSize = 348;

    private const uint FileMapAllAccess = 0x000F001Fu;
    private const uint PageReadWrite = 0x00000004u;
    private const int ErrorAlreadyExists = 183;
    private const uint CapInput = 0x00000001u;
    private const uint CapLedOutput = 0x00000002u;
    private const uint CapTouch = 0x00000004u;
    private const uint CapServerStatus = 0x00000008u;

    private const int OffMagic = 0;
    private const int OffMajor = 4;
    private const int OffMinor = 6;
    private const int OffStructSize = 8;
    private const int OffCapabilities = 12;
    private const int OffInputSequence = 16;
    private const int OffOutputSequence = 20;
    private const int OffInput = 24;
    private const int OffOutput = 118;
    private const int OffServer = 284;

    private const int InOpBtn = OffInput + 0;
    private const int InP1Buttons = OffInput + 2;
    private const int InP2Buttons = OffInput + 4;
    private const int InP1Touch = OffInput + 6;
    private const int InP2Touch = OffInput + 40;
    private const int InSourceId = OffInput + 74;
    private const int InTimestampUs = OffInput + 78;
    private const int InLeaseMs = OffInput + 86;

    private const int OutFet = OffOutput + 0;
    private const int OutGs = OffOutput + 6;
    private const int OutDc = OffOutput + 70;
    private const int OutBillboard = OffOutput + 150;
    private const int OutTimestampUs = OffOutput + 158;

    private const int ServerProcessId = OffServer + 0;
    private const int ServerMajor = OffServer + 4;
    private const int ServerMinor = OffServer + 6;
    private const int ServerStartedMs = OffServer + 8;
    private const int ServerHeartbeatMs = OffServer + 16;
    private const int ServerInputSequence = OffServer + 24;
    private const int OffIo = OffServer + 32;
    private const int IoProcessId = OffIo + 0;
    private const int IoMajor = OffIo + 4;
    private const int IoMinor = OffIo + 6;
    private const int IoStartedMs = OffIo + 8;
    private const int IoHeartbeatMs = OffIo + 16;
    private const int IoInputSequence = OffIo + 24;

    private const int DiagProcessId = 12;
    private const int DiagPollCalls = 16;
    private const int DiagTouchInitCalls = 20;
    private const int DiagTouchUpdateCalls = 24;
    private const int DiagTouchCallbackFrames = 28;
    private const int DiagTouchNonzeroFrames = 32;
    private const int DiagArmRequests = 36;
    private const int DiagArmOpenAttempts = 40;
    private const int DiagArmOpenSuccesses = 44;
    private const int DiagArmWriteAttempts = 48;
    private const int DiagArmWriteSuccesses = 52;
    private const int DiagLedInitCalls = 56;
    private const int DiagLedFetCalls = 60;
    private const int DiagLedDcCalls = 64;
    private const int DiagLedGsCalls = 68;
    private const int DiagLedBillboardCalls = 72;
    private const int DiagLastArmOpenError = 76;
    private const int DiagLastArmWriteError = 80;
    private const int DiagP1Enabled = 84;
    private const int DiagHeartbeatMs = 88;

    private const int TouchCells = 34;
    private const int PublishIntervalMs = 11;
    private const ulong LeaseMs = 500;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileMapping(IntPtr hFile, IntPtr lpAttributes, uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenFileMapping(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static MaiMaiVRNativeIO instance;
    private Action<string> logInfo;
    private bool initialized;

    private readonly object inputLock = new object();
    private readonly byte[] p1Touch = new byte[TouchCells];
    private readonly byte[] p2Touch = new byte[TouchCells];
    private readonly byte[] publishP1Touch = new byte[TouchCells];
    private readonly byte[] publishP2Touch = new byte[TouchCells];
    private readonly byte[] gsSnapshot = new byte[64];
    private readonly byte[] dcSnapshot = new byte[80];
    private readonly byte[] billboardSnapshot = new byte[6];
    private readonly byte[] fetSnapshot = new byte[6];
    private readonly byte[] previousGsSnapshot = new byte[64];
    private readonly byte[] previousDcSnapshot = new byte[80];
    private readonly byte[] previousBillboardSnapshot = new byte[6];
    private readonly byte[] previousFetSnapshot = new byte[6];

    private IntPtr mapHandle = IntPtr.Zero;
    private IntPtr mapView = IntPtr.Zero;
    private IntPtr diagHandle = IntPtr.Zero;
    private IntPtr diagView = IntPtr.Zero;
    private Thread publisherThread;
    private volatile bool publisherRunning;
    private ulong serverStartedMs;
    private int reconnectCountdown;
    private int publishCount;
    private int mappingConnects;
    private int mappingFailures;
    private int touchChanges;
    private int outputSnapshots;
    private int lastAppliedOutputSequence;
    private static long actualRingChanges;
    private long actualRingFrames;
    private int gsChangeCount;
    private int dcChangeCount;
    private int billboardChangeCount;
    private int fetChangeCount;
    private bool gsChannelSeen;
    private bool dcChannelSeen;
    private string lastLedSource = "NONE";
    private bool mappingOwnedByMaiMaiVR;
    private bool ioEndpointSeen;
    private int lastIoProcessId;
    private ulong lastIoHeartbeatMs;
    private float ioStatusTimer;

    private int diagProcessId;
    private uint diagPollCalls;
    private uint diagTouchInitCalls;
    private uint diagTouchUpdateCalls;
    private uint diagTouchCallbackFrames;
    private uint diagTouchNonzeroFrames;
    private uint diagArmRequests;
    private uint diagArmOpenAttempts;
    private uint diagArmOpenSuccesses;
    private uint diagArmWriteAttempts;
    private uint diagArmWriteSuccesses;
    private uint diagLedInitCalls;
    private uint diagLedFetCalls;
    private uint diagLedDcCalls;
    private uint diagLedGsCalls;
    private uint diagLedBillboardCalls;
    private uint diagLastArmOpenError;
    private uint diagLastArmWriteError;
    private uint diagP1Enabled;
    private ulong diagHeartbeatMs;
    private bool diagConnectedLogged;

    private readonly int[] p1ButtonVk = new int[9];
    private readonly int[] p2ButtonVk = new int[9];
    private ushort directP1Buttons;
    private ushort directP2Buttons;
    private byte directOpButtons;
    private int testVk;
    private int serviceVk;
    private int coinVk;

    private Harmony harmony;
    private Type serialManagerType;
    private Type lightManagerType;
    private Type buttonToKeyType;
    private FieldInfo buttonKeyField;
    private FieldInfo buttonInsideCountField;
    private FieldInfo serialTouchP1Field;
    private FieldInfo serialTouchP2Field;
    private FieldInfo serialStartUpField;
    private object lightManagerInstance;
    private FieldInfo ringLedsField;
    private FieldInfo whitePointField;
    private FieldInfo bodyLedField;
    private FieldInfo displayLedField;
    private bool stableCabinetLightsApplied;
    private float lightResolveTimer;

    public bool IsInitialized { get { return initialized; } }
    public bool IsMappingConnected { get { return mapView != IntPtr.Zero; } }
    public bool IsGameEndpointSeen { get { return ioEndpointSeen; } }
    public int PublishCount { get { return publishCount; } }
    public int TouchChangeCount { get { return touchChanges; } }
    public int OutputSnapshotCount { get { return outputSnapshots; } }
    public int LastAppliedOutputSequence { get { return lastAppliedOutputSequence; } }
    public int GsChangeCount { get { return gsChangeCount; } }
    public int DcChangeCount { get { return dcChangeCount; } }
    public int BillboardChangeCount { get { return billboardChangeCount; } }
    public int FetChangeCount { get { return fetChangeCount; } }
    public string LastLedSource { get { return lastLedSource; } }
    public int MappingFailureCount { get { return mappingFailures; } }
    public bool IsNativeDiagConnected { get { return diagView != IntPtr.Zero; } }
    public int NativeDiagProcessId { get { return diagProcessId; } }
    public uint NativeDiagPollCalls { get { return diagPollCalls; } }
    public uint NativeDiagTouchInitCalls { get { return diagTouchInitCalls; } }
    public uint NativeDiagTouchUpdateCalls { get { return diagTouchUpdateCalls; } }
    public uint NativeDiagTouchCallbackFrames { get { return diagTouchCallbackFrames; } }
    public uint NativeDiagTouchNonzeroFrames { get { return diagTouchNonzeroFrames; } }
    public uint NativeDiagArmRequests { get { return diagArmRequests; } }
    public uint NativeDiagArmOpenAttempts { get { return diagArmOpenAttempts; } }
    public uint NativeDiagArmOpenSuccesses { get { return diagArmOpenSuccesses; } }
    public uint NativeDiagArmWriteAttempts { get { return diagArmWriteAttempts; } }
    public uint NativeDiagArmWriteSuccesses { get { return diagArmWriteSuccesses; } }
    public uint NativeDiagLedInitCalls { get { return diagLedInitCalls; } }
    public uint NativeDiagLedFetCalls { get { return diagLedFetCalls; } }
    public uint NativeDiagLedDcCalls { get { return diagLedDcCalls; } }
    public uint NativeDiagLedGsCalls { get { return diagLedGsCalls; } }
    public uint NativeDiagLedBillboardCalls { get { return diagLedBillboardCalls; } }
    public uint NativeDiagLastArmOpenError { get { return diagLastArmOpenError; } }
    public uint NativeDiagLastArmWriteError { get { return diagLastArmWriteError; } }
    public uint NativeDiagP1Enabled { get { return diagP1Enabled; } }
    public ulong NativeDiagHeartbeatMs { get { return diagHeartbeatMs; } }

    // Exact sparse area layout used by MaiDXR SerialManager.touchData.
    // Each entry corresponds to contiguous mai2io-rave cells A1..E8.
    private static readonly int[] CellToSparseArea = new int[]
    {
        0, 1, 2, 3, 4, 8, 9, 10,
        11, 12, 16, 17, 18, 19, 20, 24,
        25, 26, 27, 28, 32, 33, 34, 35,
        36, 40, 41, 42, 43, 44, 48, 49, 50, 51
    };

    public void Initialize(Action<string> logger)
    {
        if (initialized) return;

        logInfo = logger;
        instance = this;
        serverStartedMs = GetTickCount64();
        LoadKeyMapFromEnvironment();
        InstallNativeIoPatches();
        StartPublisher();
        initialized = true;

        LogInfo("NATIVE_IO_BRIDGE=EMBEDDED_COMPONENT_V0.7.4");
        LogInfo("NATIVE_IO mapping=" + MappingName + " protocol=2.0 size=348 publishMs=" + PublishIntervalMs);
        LogInfo("NATIVE_IO COM transport disabled: SerialManager + LightManager serial threads suppressed");
    }

    private void LogInfo(string message)
    {
        try
        {
            if (logInfo != null) logInfo(message);
        }
        catch { }
    }

    private void Update()
    {
        if (!initialized) return;

        // V0.7.4: raw SerialManager buffers are the authoritative fallback.
        // This makes touch publication independent from Harmony postfix timing.
        SyncTouchFromSerialBuffers();

        lightResolveTimer -= Time.unscaledDeltaTime;
        if (lightResolveTimer <= 0f)
        {
            lightResolveTimer = 1.0f;
            ResolveLightManager();
            ResolveSerialManagerBuffers();
        }

        ApplyLedOutput();

        ioStatusTimer -= Time.unscaledDeltaTime;
        if (ioStatusTimer <= 0f)
        {
            ioStatusTimer = 1.0f;
            PollIoEndpointStatus();
            PollNativeDiag();
            try {
                string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "../BepInEx/MaiMaiVR_IntegratedBaseline"));
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "NATIVE_IO_RING_V075.txt"),
                    "Version=0.7.5\nProcess=MaiDXR.exe\nActualRingFrames=" + actualRingFrames +
                    "\nActualRingChanges=" + actualRingChanges + "\nGSChanges=" + gsChangeCount + "\n");
            } catch { }

        }
    }

    private void OnDestroy()
    {
        if (!initialized) return;

        ClearInputState();
        PublishInputOnce();
        StopPublisher();
        DisconnectNativeDiag();

        if (harmony != null)
        {
            try { harmony.UnpatchSelf(); } catch { }
        }

        instance = null;
        initialized = false;
    }

    private void LoadKeyMapFromEnvironment()
    {
        testVk = ReadEnvVk("MAIMAIVR_NATIVE_TEST_VK", 0x31);
        serviceVk = ReadEnvVk("MAIMAIVR_NATIVE_SERVICE_VK", 0x32);
        coinVk = ReadEnvVk("MAIMAIVR_NATIVE_COIN_VK", 0x72);

        int[] p1Defaults = new int[] { 0x57, 0x45, 0x44, 0x43, 0x58, 0x5A, 0x41, 0x51, 0x33 };
        int[] p2Defaults = new int[] { 0x68, 0x69, 0x66, 0x63, 0x62, 0x61, 0x64, 0x67, 0x6A };

        for (int i = 0; i < 9; i++)
        {
            p1ButtonVk[i] = ReadEnvVk("MAIMAIVR_NATIVE_P1_BTN" + (i + 1).ToString() + "_VK", p1Defaults[i]);
            p2ButtonVk[i] = ReadEnvVk("MAIMAIVR_NATIVE_P2_BTN" + (i + 1).ToString() + "_VK", p2Defaults[i]);
        }
    }

    private static int ReadEnvVk(string name, int fallback)
    {
        try
        {
            string raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            raw = raw.Trim();
            int value;
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(raw.Substring(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value))
                    return value & 0xFF;
            }
            else if (int.TryParse(raw, out value))
            {
                return value & 0xFF;
            }
        }
        catch { }
        return fallback;
    }

    private static bool KeyDown(int vk)
    {
        if (vk <= 0) return false;
        return (GetAsyncKeyState(vk) & unchecked((short)0x8000)) != 0;
    }

    private void StartPublisher()
    {
        if (publisherRunning) return;
        publisherRunning = true;
        publisherThread = new Thread(PublisherProc);
        publisherThread.IsBackground = true;
        publisherThread.Name = "MaiMaiVR Native IO Shared Memory";
        publisherThread.Start();
    }

    private void StopPublisher()
    {
        publisherRunning = false;
        try
        {
            if (publisherThread != null && publisherThread.IsAlive)
                publisherThread.Join(500);
        }
        catch { }
        publisherThread = null;
        DisconnectMapping();
    }

    private void PublisherProc()
    {
        while (publisherRunning)
        {
            if (mapView == IntPtr.Zero)
            {
                if (reconnectCountdown <= 0)
                {
                    if (!ConnectMapping()) mappingFailures++;
                    reconnectCountdown = 23;
                }
                else reconnectCountdown--;
            }

            if (mapView != IntPtr.Zero)
                PublishInputOnce();

            Thread.Sleep(PublishIntervalMs);
        }
    }

    private bool ConnectMapping()
    {
        DisconnectMapping();

        // V0.7.4 server-first behaviour: create the mapping ourselves when the
        // game DLL has not created it yet. mai2io-rave uses the exact same
        // CreateFileMapping ABI and will attach to this mapping later.
        IntPtr handle = CreateFileMapping(
            new IntPtr(-1),
            IntPtr.Zero,
            PageReadWrite,
            0,
            StructSize,
            MappingName);

        if (handle == IntPtr.Zero) return false;

        int createError = Marshal.GetLastWin32Error();
        bool existed = createError == ErrorAlreadyExists;

        IntPtr view = MapViewOfFile(handle, FileMapAllAccess, 0, 0, (UIntPtr)StructSize);
        if (view == IntPtr.Zero)
        {
            CloseHandle(handle);
            return false;
        }

        try
        {
            if (!existed)
            {
                // Newly created shared memory must match shared_memory.h exactly.
                byte[] zero = new byte[StructSize];
                Marshal.Copy(zero, 0, view, zero.Length);
                Marshal.WriteInt32(view, OffMagic, unchecked((int)Magic));
                Marshal.WriteInt16(view, OffMajor, unchecked((short)Major));
                Marshal.WriteInt16(view, OffMinor, unchecked((short)Minor));
                Marshal.WriteInt32(view, OffStructSize, StructSize);
                Marshal.WriteInt32(view, OffCapabilities,
                    unchecked((int)(CapInput | CapTouch | CapLedOutput | CapServerStatus)));
                mappingOwnedByMaiMaiVR = true;
            }
            else
            {
                uint magic = unchecked((uint)Marshal.ReadInt32(view, OffMagic));
                ushort major = unchecked((ushort)Marshal.ReadInt16(view, OffMajor));
                ushort minor = unchecked((ushort)Marshal.ReadInt16(view, OffMinor));
                int size = Marshal.ReadInt32(view, OffStructSize);

                if (magic != Magic || major != Major || minor != Minor || size != StructSize)
                {
                    UnmapViewOfFile(view);
                    CloseHandle(handle);
                    return false;
                }
                mappingOwnedByMaiMaiVR = false;
            }

            uint caps = unchecked((uint)Marshal.ReadInt32(view, OffCapabilities));
            Marshal.WriteInt32(view, OffCapabilities,
                unchecked((int)(caps | CapInput | CapTouch | CapLedOutput | CapServerStatus)));
        }
        catch
        {
            UnmapViewOfFile(view);
            CloseHandle(handle);
            return false;
        }

        mapHandle = handle;
        mapView = view;
        mappingConnects++;
        return true;
    }

    private void DisconnectMapping()
    {
        IntPtr view = mapView;
        IntPtr handle = mapHandle;
        mapView = IntPtr.Zero;
        mapHandle = IntPtr.Zero;

        if (view != IntPtr.Zero)
        {
            try { UnmapViewOfFile(view); } catch { }
        }
        if (handle != IntPtr.Zero)
        {
            try { CloseHandle(handle); } catch { }
        }
    }

    private void PublishInputOnce()
    {
        IntPtr view = mapView;
        if (view == IntPtr.Zero) return;

        byte op;
        ushort p1Buttons;
        ushort p2Buttons;

        lock (inputLock)
        {
            op = directOpButtons;
            p1Buttons = directP1Buttons;
            p2Buttons = directP2Buttons;
            Buffer.BlockCopy(p1Touch, 0, publishP1Touch, 0, TouchCells);
            Buffer.BlockCopy(p2Touch, 0, publishP2Touch, 0, TouchCells);
        }

        // Global key sampling remains as a compatibility fallback, but direct
        // ButtonToKey collision hooks below mean VR cabinet buttons no longer
        // depend on foreground focus or on the duration of a synthesized key.
        if (KeyDown(testVk)) op |= 0x01;
        if (KeyDown(serviceVk)) op |= 0x02;
        if (KeyDown(coinVk)) op |= 0x04;

        for (int i = 0; i < 9; i++)
        {
            if (KeyDown(p1ButtonVk[i])) p1Buttons |= (ushort)(1 << i);
            if (KeyDown(p2ButtonVk[i])) p2Buttons |= (ushort)(1 << i);
        }

        try
        {
            int seq = Marshal.ReadInt32(view, OffInputSequence);
            if ((seq & 1) != 0) seq++;
            int odd = seq + 1;
            int even = seq + 2;

            Marshal.WriteInt32(view, OffInputSequence, odd);
            Thread.MemoryBarrier();

            Marshal.WriteByte(view, InOpBtn, op);
            Marshal.WriteByte(view, OffInput + 1, 0);
            Marshal.WriteInt16(view, InP1Buttons, unchecked((short)p1Buttons));
            Marshal.WriteInt16(view, InP2Buttons, unchecked((short)p2Buttons));
            Marshal.Copy(publishP1Touch, 0, IntPtr.Add(view, InP1Touch), TouchCells);
            Marshal.Copy(publishP2Touch, 0, IntPtr.Add(view, InP2Touch), TouchCells);
            Marshal.WriteInt32(view, InSourceId, unchecked((int)GetCurrentProcessId()));
            Marshal.WriteInt64(view, InTimestampUs, unchecked((long)UnixTimeMicroseconds()));
            Marshal.WriteInt64(view, InLeaseMs, unchecked((long)LeaseMs));

            ulong nowMs = GetTickCount64();
            Marshal.WriteInt32(view, ServerProcessId, unchecked((int)GetCurrentProcessId()));
            Marshal.WriteInt16(view, ServerMajor, unchecked((short)Major));
            Marshal.WriteInt16(view, ServerMinor, unchecked((short)Minor));
            Marshal.WriteInt64(view, ServerStartedMs, unchecked((long)serverStartedMs));
            Marshal.WriteInt64(view, ServerHeartbeatMs, unchecked((long)nowMs));
            Marshal.WriteInt64(view, ServerInputSequence, unchecked((long)(uint)even));

            Thread.MemoryBarrier();
            Marshal.WriteInt32(view, OffInputSequence, even);
            publishCount++;
        }
        catch
        {
            DisconnectMapping();
        }
    }

    private static ulong UnixTimeMicroseconds()
    {
        long ticks = DateTime.UtcNow.Ticks - 621355968000000000L;
        if (ticks < 0) ticks = 0;
        return unchecked((ulong)(ticks / 10L));
    }

    private void ClearInputState()
    {
        lock (inputLock)
        {
            Array.Clear(p1Touch, 0, p1Touch.Length);
            Array.Clear(p2Touch, 0, p2Touch.Length);
            directP1Buttons = 0;
            directP2Buttons = 0;
            directOpButtons = 0;
        }
    }

    private static int SparseAreaToCell(int area)
    {
        switch (area)
        {
            case 0: return 0;   case 1: return 1;   case 2: return 2;   case 3: return 3;   case 4: return 4;
            case 8: return 5;   case 9: return 6;   case 10: return 7; case 11: return 8;  case 12: return 9;
            case 16: return 10; case 17: return 11; case 18: return 12; case 19: return 13; case 20: return 14;
            case 24: return 15; case 25: return 16; case 26: return 17; case 27: return 18; case 28: return 19;
            case 32: return 20; case 33: return 21; case 34: return 22; case 35: return 23; case 36: return 24;
            case 40: return 25; case 41: return 26; case 42: return 27; case 43: return 28; case 44: return 29;
            case 48: return 30; case 49: return 31; case 50: return 32; case 51: return 33;
            default: return -1;
        }
    }

    private void SetTouch(bool isP1, int sparseArea, bool state)
    {
        int cell = SparseAreaToCell(sparseArea);
        if (cell < 0 || cell >= TouchCells) return;

        lock (inputLock)
        {
            byte[] target = isP1 ? p1Touch : p2Touch;
            byte value = state ? (byte)1 : (byte)0;
            if (target[cell] == value) return;
            target[cell] = value;
            touchChanges++;

            if (touchChanges <= 12)
            {
                LogInfo("NATIVE_IO_TOUCH_CHANGE player=" + (isP1 ? "P1" : "P2") +
                    " cell=" + cell + " sparse=" + sparseArea + " state=" + state);
            }
        }
    }

    private void ResolveSerialManagerBuffers()
    {
        try
        {
            if (serialManagerType == null) serialManagerType = FindType("SerialManager");
            if (serialManagerType == null) return;

            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            if (serialTouchP1Field == null) serialTouchP1Field = serialManagerType.GetField("touchData", flags);
            if (serialTouchP2Field == null) serialTouchP2Field = serialManagerType.GetField("touchData2", flags);
            if (serialStartUpField == null) serialStartUpField = serialManagerType.GetField("startUp", flags);

            // Mimic the old serial handshake's ready state. ChangeTouch updates
            // the buffers regardless, but some upstream code uses startUp as a
            // general IO-ready flag.
            if (serialStartUpField != null)
            {
                try { serialStartUpField.SetValue(null, true); } catch { }
            }
        }
        catch { }
    }

    private static bool ReadSparseTouchBit(byte[] raw, int sparseArea)
    {
        if (raw == null) return false;
        int bitIndex = sparseArea + 8;
        int byteIndex = bitIndex >> 3;
        int bit = bitIndex & 7;
        if (byteIndex < 0 || byteIndex >= raw.Length) return false;
        return (raw[byteIndex] & (1 << bit)) != 0;
    }

    private void SyncTouchFromSerialBuffers()
    {
        ResolveSerialManagerBuffers();
        if (serialTouchP1Field == null && serialTouchP2Field == null) return;

        byte[] rawP1 = null;
        byte[] rawP2 = null;
        try
        {
            if (serialTouchP1Field != null) rawP1 = serialTouchP1Field.GetValue(null) as byte[];
            if (serialTouchP2Field != null) rawP2 = serialTouchP2Field.GetValue(null) as byte[];
        }
        catch { return; }

        lock (inputLock)
        {
            for (int cell = 0; cell < TouchCells; cell++)
            {
                int area = CellToSparseArea[cell];
                if (rawP1 != null) p1Touch[cell] = ReadSparseTouchBit(rawP1, area) ? (byte)1 : (byte)0;
                if (rawP2 != null) p2Touch[cell] = ReadSparseTouchBit(rawP2, area) ? (byte)1 : (byte)0;
            }
        }
    }

    private void DisconnectNativeDiag()
    {
        IntPtr view = diagView;
        IntPtr handle = diagHandle;
        diagView = IntPtr.Zero;
        diagHandle = IntPtr.Zero;
        diagProcessId = 0;

        if (view != IntPtr.Zero)
        {
            try { UnmapViewOfFile(view); } catch { }
        }
        if (handle != IntPtr.Zero)
        {
            try { CloseHandle(handle); } catch { }
        }
    }

    private bool TryConnectNativeDiag()
    {
        if (diagView != IntPtr.Zero) return true;

        IntPtr handle = OpenFileMapping(FileMapAllAccess, false, DiagMappingName);
        if (handle == IntPtr.Zero) return false;

        IntPtr view = MapViewOfFile(handle, FileMapAllAccess, 0, 0, (UIntPtr)DiagStructSize);
        if (view == IntPtr.Zero)
        {
            CloseHandle(handle);
            return false;
        }

        try
        {
            uint magic = unchecked((uint)Marshal.ReadInt32(view, 0));
            int size = Marshal.ReadInt32(view, 8);
            if (magic != DiagMagic || size != DiagStructSize)
            {
                UnmapViewOfFile(view);
                CloseHandle(handle);
                return false;
            }
        }
        catch
        {
            UnmapViewOfFile(view);
            CloseHandle(handle);
            return false;
        }

        diagHandle = handle;
        diagView = view;
        return true;
    }

    private static uint ReadDiagUInt32(IntPtr view, int offset)
    {
        return unchecked((uint)Marshal.ReadInt32(view, offset));
    }

    private void PollNativeDiag()
    {
        if (!TryConnectNativeDiag()) return;

        IntPtr view = diagView;
        if (view == IntPtr.Zero) return;

        try
        {
            uint magic = ReadDiagUInt32(view, 0);
            int size = Marshal.ReadInt32(view, 8);
            if (magic != DiagMagic || size != DiagStructSize)
            {
                DisconnectNativeDiag();
                return;
            }

            diagProcessId = Marshal.ReadInt32(view, DiagProcessId);
            diagPollCalls = ReadDiagUInt32(view, DiagPollCalls);
            diagTouchInitCalls = ReadDiagUInt32(view, DiagTouchInitCalls);
            diagTouchUpdateCalls = ReadDiagUInt32(view, DiagTouchUpdateCalls);
            diagTouchCallbackFrames = ReadDiagUInt32(view, DiagTouchCallbackFrames);
            diagTouchNonzeroFrames = ReadDiagUInt32(view, DiagTouchNonzeroFrames);
            diagArmRequests = ReadDiagUInt32(view, DiagArmRequests);
            diagArmOpenAttempts = ReadDiagUInt32(view, DiagArmOpenAttempts);
            diagArmOpenSuccesses = ReadDiagUInt32(view, DiagArmOpenSuccesses);
            diagArmWriteAttempts = ReadDiagUInt32(view, DiagArmWriteAttempts);
            diagArmWriteSuccesses = ReadDiagUInt32(view, DiagArmWriteSuccesses);
            diagLedInitCalls = ReadDiagUInt32(view, DiagLedInitCalls);
            diagLedFetCalls = ReadDiagUInt32(view, DiagLedFetCalls);
            diagLedDcCalls = ReadDiagUInt32(view, DiagLedDcCalls);
            diagLedGsCalls = ReadDiagUInt32(view, DiagLedGsCalls);
            diagLedBillboardCalls = ReadDiagUInt32(view, DiagLedBillboardCalls);
            diagLastArmOpenError = ReadDiagUInt32(view, DiagLastArmOpenError);
            diagLastArmWriteError = ReadDiagUInt32(view, DiagLastArmWriteError);
            diagP1Enabled = ReadDiagUInt32(view, DiagP1Enabled);
            diagHeartbeatMs = unchecked((ulong)Marshal.ReadInt64(view, DiagHeartbeatMs));

            if (!diagConnectedLogged)
            {
                diagConnectedLogged = true;
                LogInfo("NATIVE_IO_DIAG_CONNECTED pid=" + diagProcessId +
                    " touchInit=" + diagTouchInitCalls +
                    " armWriteSuccess=" + diagArmWriteSuccesses +
                    " ledGs=" + diagLedGsCalls);
            }
        }
        catch
        {
            DisconnectNativeDiag();
        }
    }

    private void PollIoEndpointStatus()
    {
        IntPtr view = mapView;
        if (view == IntPtr.Zero) return;
        try
        {
            int pid = Marshal.ReadInt32(view, IoProcessId);
            ulong heartbeat = unchecked((ulong)Marshal.ReadInt64(view, IoHeartbeatMs));
            if (pid > 0 && heartbeat > 0)
            {
                bool first = !ioEndpointSeen;
                ioEndpointSeen = true;
                lastIoProcessId = pid;
                lastIoHeartbeatMs = heartbeat;
                if (first)
                {
                    ushort maj = unchecked((ushort)Marshal.ReadInt16(view, IoMajor));
                    ushort min = unchecked((ushort)Marshal.ReadInt16(view, IoMinor));
                    LogInfo("NATIVE_IO_GAME_ENDPOINT_CONNECTED pid=" + pid +
                        " protocol=" + maj + "." + min +
                        " mappingCreatedByMaiMaiVR=" + mappingOwnedByMaiMaiVR);
                }
            }
        }
        catch { }
    }

    private void InstallNativeIoPatches()
    {
        harmony = new Harmony("maimaivr.enhanced.nativeio.patches");
        serialManagerType = FindType("SerialManager");
        ResolveSerialManagerBuffers();
        lightManagerType = FindType("LightManager");
        buttonToKeyType = FindType("ButtonToKey");

        HarmonyMethod skip = new HarmonyMethod(typeof(MaiMaiVRNativeIO).GetMethod(nameof(SkipSerialMethod), BindingFlags.Static | BindingFlags.NonPublic));

        if (serialManagerType != null)
        {
            PatchPrefix(serialManagerType, "Start", skip);
            PatchPrefix(serialManagerType, "OnDestroy", skip);
            PatchPrefix(serialManagerType, "UpdateTouch", skip);

            MethodInfo changeTouch = AccessTools.Method(serialManagerType, "ChangeTouch");
            MethodInfo postMethod = typeof(MaiMaiVRNativeIO).GetMethod(nameof(ChangeTouchPostfix), BindingFlags.Static | BindingFlags.NonPublic);
            if (changeTouch != null && postMethod != null)
                harmony.Patch(changeTouch, postfix: new HarmonyMethod(postMethod));
        }

        if (lightManagerType != null)
        {
            PatchPrefix(lightManagerType, "Start", skip);
            PatchPrefix(lightManagerType, "OnDestroy", skip);
        }

        if (buttonToKeyType != null)
        {
            buttonKeyField = buttonToKeyType.GetField("keyToPress", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            buttonInsideCountField = buttonToKeyType.GetField("_insideColliderCount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            MethodInfo enter = AccessTools.Method(buttonToKeyType, "OnTriggerEnter");
            MethodInfo exit = AccessTools.Method(buttonToKeyType, "OnTriggerExit");
            MethodInfo enterPost = typeof(MaiMaiVRNativeIO).GetMethod(nameof(ButtonEnterPostfix), BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo exitPost = typeof(MaiMaiVRNativeIO).GetMethod(nameof(ButtonExitPostfix), BindingFlags.Static | BindingFlags.NonPublic);
            if (enter != null && enterPost != null) harmony.Patch(enter, postfix: new HarmonyMethod(enterPost));
            if (exit != null && exitPost != null) harmony.Patch(exit, postfix: new HarmonyMethod(exitPost));
        }
    }

    private void PatchPrefix(Type type, string methodName, HarmonyMethod prefix)
    {
        MethodInfo target = AccessTools.Method(type, methodName);
        if (target != null) harmony.Patch(target, prefix: prefix);
    }

    private static bool SkipSerialMethod()
    {
        return false;
    }

    private static void ChangeTouchPostfix(bool __0, int __1, bool __2)
    {
        MaiMaiVRNativeIO current = instance;
        if (current != null) current.SetTouch(__0, __1, __2);
    }


    private static void ButtonEnterPostfix(object __instance)
    {
        MaiMaiVRNativeIO current = instance;
        if (current != null) current.SetButtonFromInstance(__instance, true);
    }

    private static void ButtonExitPostfix(object __instance)
    {
        MaiMaiVRNativeIO current = instance;
        if (current == null) return;

        // ButtonToKey only sends key-up when its own collider count reaches zero.
        // Mirror that exact rule so multiple hand colliders cannot release early.
        try
        {
            if (current.buttonInsideCountField != null)
            {
                object raw = current.buttonInsideCountField.GetValue(__instance);
                if (raw is int && (int)raw > 0) return;
            }
        }
        catch { }

        current.SetButtonFromInstance(__instance, false);
    }

    private void SetButtonFromInstance(object buttonInstance, bool state)
    {
        if (buttonInstance == null || buttonKeyField == null) return;

        int vk;
        try
        {
            object raw = buttonKeyField.GetValue(buttonInstance);
            if (raw == null) return;
            vk = Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture) & 0xFF;
        }
        catch { return; }

        if (vk <= 0) return;

        lock (inputLock)
        {
            ushort p1Mask = directP1Buttons;
            ushort p2Mask = directP2Buttons;
            byte opMask = directOpButtons;

            for (int i = 0; i < 9; i++)
            {
                ushort bit = (ushort)(1 << i);
                if (p1ButtonVk[i] == vk)
                {
                    if (state) p1Mask |= bit; else p1Mask &= (ushort)~bit;
                }
                if (p2ButtonVk[i] == vk)
                {
                    if (state) p2Mask |= bit; else p2Mask &= (ushort)~bit;
                }
            }

            if (testVk == vk) { if (state) opMask |= 0x01; else opMask &= 0xFE; }
            if (serviceVk == vk) { if (state) opMask |= 0x02; else opMask &= 0xFD; }
            if (coinVk == vk) { if (state) opMask |= 0x04; else opMask &= 0xFB; }

            directP1Buttons = p1Mask;
            directP2Buttons = p2Mask;
            directOpButtons = opMask;
        }
    }

    private static Type FindType(string name)
    {
        try
        {
            Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < all.Length; i++)
            {
                Type t = all[i].GetType(name, false);
                if (t != null) return t;
            }
        }
        catch { }
        return null;
    }

    private void ResolveLightManager()
    {
        try
        {
            if (lightManagerType == null) lightManagerType = FindType("LightManager");
            if (lightManagerType == null) return;

            if (ringLedsField == null)
                ringLedsField = lightManagerType.GetField("RingLeds", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (whitePointField == null)
                whitePointField = lightManagerType.GetField("RingLedsWhitePointSubtractor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (bodyLedField == null)
                bodyLedField = lightManagerType.GetField("BodyLed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (displayLedField == null)
                displayLedField = lightManagerType.GetField("DisplayLed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (lightManagerInstance == null || (lightManagerInstance is UnityEngine.Object && ((UnityEngine.Object)lightManagerInstance) == null))
            {
                UnityEngine.Object[] objects = UnityEngine.Object.FindObjectsOfType(lightManagerType);
                if (objects != null && objects.Length > 0) lightManagerInstance = objects[0];
            }

            // V0.6.6 froze CMD57-driven broad cabinet lights to these validated
            // intensities. Native IO suppresses the serial LightManager thread,
            // so apply the same validated baseline once when the manager appears.
            if (!stableCabinetLightsApplied && lightManagerInstance != null)
            {
                Light body = bodyLedField != null ? bodyLedField.GetValue(lightManagerInstance) as Light : null;
                Light display = displayLedField != null ? displayLedField.GetValue(lightManagerInstance) as Light : null;
                if (body != null) body.intensity = 0.050f;
                if (display != null) display.intensity = 0.0375f;
                stableCabinetLightsApplied = true;
            }
        }
        catch { }
    }

    private bool TryReadOutputSnapshot(out int outputSequence)
    {
        outputSequence = 0;
        IntPtr view = mapView;
        if (view == IntPtr.Zero) return false;

        try
        {
            for (int attempt = 0; attempt < 32; attempt++)
            {
                int before = Marshal.ReadInt32(view, OffOutputSequence);
                if (before == 0 || (before & 1) != 0) continue;

                Thread.MemoryBarrier();
                Marshal.Copy(IntPtr.Add(view, OutFet), fetSnapshot, 0, fetSnapshot.Length);
                Marshal.Copy(IntPtr.Add(view, OutGs), gsSnapshot, 0, gsSnapshot.Length);
                Marshal.Copy(IntPtr.Add(view, OutDc), dcSnapshot, 0, dcSnapshot.Length);
                Marshal.Copy(IntPtr.Add(view, OutBillboard), billboardSnapshot, 0, billboardSnapshot.Length);
                long ts = Marshal.ReadInt64(view, OutTimestampUs);
                Thread.MemoryBarrier();

                int after = Marshal.ReadInt32(view, OffOutputSequence);
                if (before == after && (after & 1) == 0 && ts > 0)
                {
                    outputSequence = after;
                    outputSnapshots++;
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static bool BufferDifferent(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return true;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return true;
        return false;
    }

    private static void CopyBuffer(byte[] src, byte[] dst)
    {
        if (src == null || dst == null) return;
        Array.Copy(src, dst, Math.Min(src.Length, dst.Length));
    }

    private static void SetRingLight(IList ringList, int index, byte r, byte g, byte b, byte whitePoint)
    {
        if (ringList == null || index < 0 || index >= ringList.Count) return;
        Light light = ringList[index] as Light;
        if (light == null) return;

        int sum = r + g + b;
        int mp = whitePoint * (sum / 765);
        light.enabled = true;
        Color nextColor = new Color32(
            (byte)Math.Max(0, r - mp),
            (byte)Math.Max(0, g - mp),
            (byte)Math.Max(0, b - mp),
            255);
        if (light.color != nextColor) actualRingChanges++;
        light.color = nextColor;
    }

    private static void ApplyPerLedBuffer(IList ringList, byte[] buffer, int boardStride, byte whitePoint)
    {
        if (ringList == null || buffer == null) return;
        int p1Count = Math.Min(8, ringList.Count);
        for (int i = 0; i < p1Count; i++)
        {
            int p = i * 4;
            if (p + 2 >= buffer.Length) break;
            SetRingLight(ringList, i, buffer[p], buffer[p + 1], buffer[p + 2], whitePoint);
        }

        if (ringList.Count >= 16 && boardStride + 31 < buffer.Length)
        {
            for (int i = 0; i < 8; i++)
            {
                int p = boardStride + i * 4;
                SetRingLight(ringList, 8 + i, buffer[p], buffer[p + 1], buffer[p + 2], whitePoint);
            }
        }
    }

    private static void ApplyBillboardFallback(IList ringList, byte[] buffer, byte whitePoint)
    {
        if (ringList == null || buffer == null || buffer.Length < 3) return;
        int p1Count = Math.Min(8, ringList.Count);
        for (int i = 0; i < p1Count; i++)
            SetRingLight(ringList, i, buffer[0], buffer[1], buffer[2], whitePoint);

        if (ringList.Count >= 16 && buffer.Length >= 6)
        {
            for (int i = 0; i < 8; i++)
                SetRingLight(ringList, 8 + i, buffer[3], buffer[4], buffer[5], whitePoint);
        }
    }

    private void ApplyLedOutput()
    {
        if (lightManagerInstance == null || ringLedsField == null) return;

        int seq;
        if (!TryReadOutputSnapshot(out seq)) return;
        if (seq == lastAppliedOutputSequence) return;
        lastAppliedOutputSequence = seq;

        bool gsChanged = BufferDifferent(gsSnapshot, previousGsSnapshot);
        bool dcChanged = BufferDifferent(dcSnapshot, previousDcSnapshot);
        bool billboardChanged = BufferDifferent(billboardSnapshot, previousBillboardSnapshot);
        bool fetChanged = BufferDifferent(fetSnapshot, previousFetSnapshot);

        if (gsChanged) { gsChangeCount++; gsChannelSeen = true; CopyBuffer(gsSnapshot, previousGsSnapshot); }
        if (dcChanged) { dcChangeCount++; dcChannelSeen = true; CopyBuffer(dcSnapshot, previousDcSnapshot); }
        if (billboardChanged) { billboardChangeCount++; CopyBuffer(billboardSnapshot, previousBillboardSnapshot); }
        if (fetChanged) { fetChangeCount++; CopyBuffer(fetSnapshot, previousFetSnapshot); }

        IList ringList = null;
        try { ringList = ringLedsField.GetValue(lightManagerInstance) as IList; }
        catch { return; }
        if (ringList == null || ringList.Count == 0) return;

        byte whitePoint = 130;
        try
        {
            object wp = whitePointField != null ? whitePointField.GetValue(lightManagerInstance) : null;
            if (wp is byte) whitePoint = (byte)wp;
        }
        catch { }

        long previousActual = actualRingChanges;
        // GS is the canonical 8-button RGB channel. If this SegaTools build
        // never emits GS, fall back to DC (10 RGB-speed slots per board), then
        // billboard colour. This keeps older/newer hook variants usable.
        if (gsChanged || (gsChannelSeen && !dcChanged && !billboardChanged))
        {
            lastLedSource = "GS";
            ApplyPerLedBuffer(ringList, gsSnapshot, 32, whitePoint);
        }
        else if (!gsChannelSeen && dcChanged)
        {
            lastLedSource = "DC_FALLBACK";
            ApplyPerLedBuffer(ringList, dcSnapshot, 40, whitePoint);
        }
        else if (!gsChannelSeen && !dcChannelSeen && billboardChanged)
        {
            lastLedSource = "BILLBOARD_FALLBACK";
            ApplyBillboardFallback(ringList, billboardSnapshot, whitePoint);
        }
        if (actualRingChanges != previousActual && lastLedSource == "GS") actualRingFrames++;
    }

}
