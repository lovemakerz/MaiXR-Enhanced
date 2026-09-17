using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

[BepInPlugin("maimaivr.integrated.baseline", "MaiMaiVR Integrated Baseline", "0.6.6")]
public partial class MaiMaiVRIntegratedBaseline : BaseUnityPlugin
{
    [DllImport("user32.dll", SetLastError = false)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const float MoveSpeed = 0.30f;
    private const float Deadzone = 0.18f;
    private const float TurnDeadzone = 0.15f;
    private const float TurnSpeed = 55.0f;

    // The display mesh sits behind the bezel. A controlled physical overscan is
    // safer than UV stretching: the bezel masks the extra geometry.
    private const float DisplayGeometryOverscan = 1.02f;

    // Exact CiRCLE PLUS source split measured from the user's PC capture.
    // Full source: 2048 wide in the uploaded reference.
    // Active pair: x=376..1672, centered exactly at x=1024.
    // P1 therefore occupies x=376..1024 = 648 px.
    // Normalized: offset=376/2048=0.18359375, width=648/2048=0.31640625.
    private const float P1SourceOffsetX = 0.18359375f;
    private const float P1SourceScaleX = 0.31640625f;

    // P2 is exactly symmetrical around the center boundary:
    // x=1024..1672 of the same 2048-wide reference.
    private const float P2SourceOffsetX = 0.50000000f;
    private const float P2SourceScaleX = 0.31640625f;

    private const float DisplayLiftMeters = 0.004f;

    // The maimai touch controller expects a continuous state stream.
    // 11 ms ~= 90.9 Hz, matching the robust touch-fix architecture while
    // staying comfortably below the practical 9600-baud packet ceiling.
    private const int ContinuousTouchIntervalMs = 11;

    // V0.2.5 touch haptics. Values intentionally conservative for Quest Touch:
    // a short contact pulse, then a light 30 Hz texture while the hand remains
    // inside one or more touch-panel zones.
    private const float TouchHapticOnsetAmplitude = 0.28f;
    private const float TouchHapticOnsetDuration = 0.035f;
    private const float TouchHapticSustainAmplitude = 0.12f;
    private const float TouchHapticSustainDuration = 0.025f;
    private const float TouchHapticFrequency = 30.0f;
    private const float TouchHapticReleaseGraceSeconds = 0.040f;

    // V0.3.0 Arcade Night: darken the room without dimming local cabinet LEDs.
    private const float ArcadeNightAmbientMultiplier = 0.10f;
    private const float ArcadeNightDirectionalMultiplier = 0.15f;
    private const float ArcadeNightReflectionMultiplier = 0.20f;
    private const float ArcadeNightSkyboxExposureMultiplier = 0.18f;
    private const float ArcadeNightMaterialMultiplier = 1.00f;
    private const float ArcadeNightMaterialRescanSeconds = 1.00f;

    // V0.3.0.4 - physical button glow.
    // A local Point light gives the wanted outward halo without lighting the
    // whole room like a Directional light would. Shadows are explicitly OFF.
    private const float ButtonGlowEmissionMultiplier = 0.95f;
    private const float ButtonHaloIntensity = 0.30f;
    private const float ButtonHaloRangeMeters = 0.20f;
    private const float ButtonHaloOutwardOffsetMeters = 0.070f;
    private const float ButtonRendererSearchRadiusMeters = 0.16f;

    // V0.3.0.8 - refined real-cabinet lighting pass.
    // Keep the dark 20% ring from V0.3.0.3/4, then rebuild the cabinet glow
    // around it like the real hardware: subtle white body emission, white
    // perimeter halo, cyan top accent and warm red/orange side accents.
    // No HDR emission on the large white body: it caused the huge white/pink wash.
    // Instead the already-darkened neutral shell is raised from ~20% to ~36%.
    private const float CabinetWhiteEmissionMultiplier = 0.0f;
    private const float CabinetWhitePlasticBoost = 1.55f;
    private const float CabinetWhitePlasticMax = 0.36f;
    private const float ButtonEmissionMax = 0.26f;
    private const float AccentEmissionMax = 0.040f;
    private const float CabinetAccentEmissionMultiplier = 0.05f;
    private const float RingContourHaloIntensity = 0.065f;
    private const float RingContourHaloRangeMeters = 0.24f;
    private const float RingContourRadialOffsetMeters = 0.050f;
    private const float TopGlowIntensity = 0.028f;
    private const float TopGlowRangeMeters = 0.30f;
    private const float SideGlowIntensity = 0.022f;
    private const float SideGlowRangeMeters = 0.22f;

    // V0.6.1 - performance-optimized global-light stability.
    // CMD57 is intercepted inside LightManager.UpdateLED before MaiDXR can
    // animate BodyLed / DisplayLed. This avoids the V0.3.1.12 per-frame
    // read/write fight in LateUpdate while RingLeds stay fully game-driven.
    private const float StableBodyLedIntensity = 0.050f;
    private const float StableDisplayLedIntensity = 0.0375f;

    // Left stick click -> one coin pulse. Default segatools coin VK is '3'.
    private const float CoinPulseSeconds = 0.080f;
    private const uint KeyEventKeyUp = 0x0002;

    private InputDevice leftDevice;
    private InputDevice rightDevice;
    private Vector2Control leftStick;
    private Vector2Control rightStick;
    private ButtonControl leftStickClick;
    private ButtonControl rightTriggerPressed;
    private AxisControl rightTrigger;

    private Transform xrRoot;
    private Camera hmdCamera;
    private CharacterController characterController;
    private Component xrOriginComponent;
    private MethodInfo moveCameraToWorldLocation;

    private Behaviour rightRayInteractor;
    private MethodInfo tryGetUIRaycast;
    private bool previousTrigger;
    private GameObject pressedUiHandler;
    private PointerEventData pressedPointerData;
    private GameObject draggedUiHandler;
    private bool uiDragging;
    private int uiBeginDrags;
    private int uiDragEvents;
    private int uiEndDrags;

    private float discoverTimer;
    private float statusTimer;

    private int movementFrames;
    private int forwardFrames;
    private float originalFixedDeltaTime;
    private bool rhythmFixedDeltaConfigured;

    private int backwardFrames;
    private int leftFrames;
    private int rightFrames;
    private int turnFrames;
    private int triggerPresses;
    private int uiHits;
    private int uiClicks;
    private int uiMisses;
    private int disabledMoveProviders;
    private int disabledTurnProviders;
    private int scaleRestores;
    private int cameraScaleRestores;

    private string movementMethod = "NONE";

    // Arcade Night state.
    private bool arcadeNightApplied;
    private float arcadeNightOriginalAmbientIntensity;
    private float arcadeNightOriginalReflectionIntensity;
    private float arcadeNightOriginalSkyboxExposure = float.NaN;
    private int arcadeNightDirectionalLightsDimmed;
    private readonly Dictionary<int, float> arcadeNightOriginalDirectionalIntensities = new Dictionary<int, float>();
    private readonly HashSet<int> arcadeNightProcessedRenderers = new HashSet<int>();
    private int arcadeNightRenderersScanned;
    private int arcadeNightRenderersDimmed;
    private int arcadeNightMaterialsDimmed;
    private int arcadeNightRenderersSkippedProtected;
    private int arcadeNightRenderersSkippedGlow;
    private float arcadeNightMaterialScanTimer;
    private bool arcadeNightGlobalSettingsApplied;

    // Left-stick coin state.
    private int coinVirtualKey = 0x72;
    private bool previousLeftStickClick;
    private bool coinKeyDown;
    private float coinReleaseAt = -1f;
    private int coinStickPresses;
    private int coinKeyDownCount;
    private int coinKeyUpCount;
    private int coinSendErrors;
    private string coinLastError = "<none>";
    private string coinRequestPath = "<none>";
    private int coinRequestWriteCount;
    private int coinRequestWriteErrors;

    // Escape closes the whole session through a marker watched by the launcher.
    private bool escapeCloseAllRequested;
    private int escapeCloseAllCount;
    private string escapeCloseAllMarkerPath = "<none>";

    private readonly List<Transform> guardedTransforms = new List<Transform>();
    private readonly List<Vector3> guardedScales = new List<Vector3>();

    private readonly HashSet<int> fixedDisplayRenderers = new HashSet<int>();
    private int displayGeometryFixedCount;

    private readonly List<Material> p1DisplayMaterials = new List<Material>();
    private readonly HashSet<int> p1MaterialIds = new HashSet<int>();
    private int p1CropApplyCount;
    private int p1DisplayLiftCount;

    private readonly List<Material> p2DisplayMaterials = new List<Material>();
    private readonly HashSet<int> p2MaterialIds = new HashSet<int>();
    private int p2CropApplyCount;
    private int p2DisplayLiftCount;

    private Texture sharedLiveGameTexture;
    private int p2TextureShareApplyCount;
    private int p2TextureShareChangeCount;
    private string sharedLiveGameTextureInfo = "<none>";

    // V0.2.0 IO P1 telemetry. Reflection only: no change to upstream SerialManager.
    private Type serialManagerType;
    private FieldInfo serialP1Field;
    private FieldInfo serialP2Field;
    private FieldInfo serialStartUpField;
    private FieldInfo serialTouchDataField;
    private FieldInfo serialTouchData2Field;
    private bool serialReflectionReady;
    private bool serialP1OpenCurrent;
    private bool serialP2OpenCurrent;
    private bool serialStartUpCurrent;
    private bool serialP1OpenSeen;
    private bool serialP2OpenSeen;
    private bool serialStartUpSeen;
    private bool serialStateInitialized;
    private byte[] lastTouchDataP1;
    private string touchDataP1Hex = "<unavailable>";
    private int touchDataP1ChangeCount;

    private byte[] lastTouchDataP2;
    private string touchDataP2Hex = "<unavailable>";
    private int touchDataP2ChangeCount;

    private float serialTelemetryTimer;

    // V0.2.3: continuous P1 touch transport. Upstream MaiDXR only sends on
    // change + once per second; CiRCLE PLUS/game polling can miss those packets.
    private Thread continuousTouchThread;
    private volatile bool continuousTouchThreadRunning;
    private bool continuousTouchThreadStarted;
    private bool continuousTouchEverActive;
    private bool continuousTouchActiveLogged;
    private int continuousTouchWrites;
    private int continuousTouchWriteErrors;
    private int continuousTouchWritesPerSecond;
    private int continuousTouchLastRateCount;

    private int continuousTouchWritesP2;
    private int continuousTouchWriteErrorsP2;
    private int continuousTouchWritesPerSecondP2;
    private int continuousTouchLastRateCountP2;
    private bool continuousTouchP2EverActive;

    private string continuousTouchLastError = "<none>";
    private string continuousTouchLastErrorP2 = "<none>";

    private string requestedP1SerialPort = "COM5";
    private string actualP1SerialPort = "<unknown>";
    private bool dynamicP1SerialPortApplied;
    private string dynamicP1SerialPortResult = "<not attempted>";

    // Touch haptics - independent from serial/touch logic.
    private MaiMaiVRTouchHapticHandObserver leftTouchHapticObserver;
    private MaiMaiVRTouchHapticHandObserver rightTouchHapticObserver;
    private Collider leftTouchHapticCollider;
    private Collider rightTouchHapticCollider;

    private UnityEngine.XR.InputDevice leftXRHapticDevice;
    private UnityEngine.XR.InputDevice rightXRHapticDevice;

    private bool leftHapticCapabilityKnown;
    private bool rightHapticCapabilityKnown;
    private bool leftHapticImpulseSupported;
    private bool rightHapticImpulseSupported;

    private int leftHapticContacts;
    private int rightHapticContacts;
    private int leftHapticEnters;
    private int rightHapticEnters;
    private int leftHapticExits;
    private int rightHapticExits;
    private int leftHapticImpulses;
    private int rightHapticImpulses;
    private int leftHapticSendFailures;
    private int rightHapticSendFailures;
    private int hapticObserversInstalled;
    private int legacyHapticManagersDisabled;

    private bool leftHapticOnsetPending;
    private bool rightHapticOnsetPending;
    private bool leftHapticWasRunning;
    private bool rightHapticWasRunning;
    private float leftHapticPulseTimer;
    private float rightHapticPulseTimer;
    private float leftHapticReleaseDeadline = -1f;
    private float rightHapticReleaseDeadline = -1f;
    private string leftHapticLastZone = "<none>";
    private string rightHapticLastZone = "<none>";

    // V0.2.6 LED transport/telemetry. Upstream LightManager hardcodes COM51.
    private string requestedLedSerialPort = "COM51";
    private string actualLedSerialPort = "<unknown>";
    private bool dynamicLedSerialPortApplied;
    private string dynamicLedSerialPortResult = "<not attempted>";

    private Type lightManagerType;
    private object lightManagerInstance;
    private FieldInfo lightP1SerialField;
    private FieldInfo lightRingLedsField;
    private FieldInfo lightBodyLedField;
    private FieldInfo lightDisplayLedField;
    private FieldInfo lightStreamListField;
    private FieldInfo lightInstantListField;

    private bool ledReflectionReady;
    private bool ledSerialOpen;
    private int ledRingCount;
    private int ledRingColorChangeSamples;
    private int ledBodyIntensityChangeSamples;
    private int ledDisplayIntensityChangeSamples;
    private static int globalLightCmd57Suppressed;
    private static int globalLightBodyWrites;
    private static int globalLightDisplayWrites;
    private string ledRingColorSnapshot = "<unavailable>";
    private float ledBodyCurrentIntensity = -1f;
    private float ledDisplayCurrentIntensity = -1f;
    private float lastObservedBodyIntensity = float.NaN;
    private float lastObservedDisplayIntensity = float.NaN;
    private Color32[] lastObservedRingColors;
    private int ledStreamQueueCount;
    private int ledInstantQueueCount;
    private float ledTelemetryTimer;

    // V0.3.0.4 button emission + local outward halo.
    private readonly List<Light> buttonHaloLights = new List<Light>();
    private readonly List<Renderer> buttonGlowRenderers = new List<Renderer>();
    private readonly List<Material[]> buttonGlowMaterials = new List<Material[]>();
    private bool buttonGlowSetupDone;
    private int buttonHaloLightsCreated;
    private int buttonGlowRenderersBound;
    private int buttonGlowMaterialsBound;
    private int buttonGlowEmissionUpdates;
    private int buttonGlowSetupAttempts;
    private string buttonGlowLastBinding = "<none>";

    // V0.3.0.5 real-cabinet visual pass.
    private bool realCabinetSetupDone;
    private int realCabinetSetupAttempts;
    private Transform realCabinetRoot;
    private Vector3 realCabinetRingCenter;
    private float realCabinetRingRadius;
    private int cabinetWhiteEmissionMaterials;
    private int cabinetAccentEmissionMaterials;
    private readonly List<Light> ringContourHaloLights = new List<Light>();
    private readonly List<Light> cabinetAccentLights = new List<Light>();
    private int ringContourHaloLightsCreated;
    private int cabinetTopGlowLightsCreated;
    private int cabinetSideGlowLightsCreated;
    private string realCabinetRootPath = "<none>";

    // V0.2.6.1: protect upstream LED reader from malformed / zero-length frames.
    private Harmony ledSafetyHarmony;
    private static bool ledSafetyPatchInstalled;
    private static string ledSafetyPatchError = "<none>";
    private static int ledMalformedFramesSkipped;
    private static int ledUnsafeUpdatesSkipped;

    private string reportDir;
    private string eventLog;
    private string statusFile;

    private void Awake()
    {
        reportDir = Path.Combine(Paths.BepInExRootPath, "MaiMaiVR_IntegratedBaseline");
        Directory.CreateDirectory(reportDir);

        eventLog = Path.Combine(reportDir, "INTEGRATED_BASELINE_EVENTS.log");
        statusFile = Path.Combine(reportDir, "INTEGRATED_BASELINE_STATUS.txt");
        coinRequestPath = Path.Combine(reportDir, "COIN_REQUEST.txt");
        try { File.Delete(coinRequestPath); } catch { }
        File.WriteAllText(eventLog, "", Encoding.UTF8);

        Log("BOOT V0.6.6 | VISUAL_BASELINE=V0.1.9_FROZEN");
        Log("MODE=clean native P1 crop + physical display geometry fix");
        Log("MoveSpeed=" + MoveSpeed.ToString("F2", CultureInfo.InvariantCulture));
        Log("TurnSpeed=" + TurnSpeed.ToString("F2", CultureInfo.InvariantCulture));
        Log("DisplayGeometryOverscan=" + DisplayGeometryOverscan.ToString("F2", CultureInfo.InvariantCulture));
        Log("DISPLAY_OVERSCAN_POLICY=1.02 unchanged; P1 crop unchanged; V0.1.7.1 world-up lift=0.004m");
        Log("P1_SOURCE_REFERENCE fullWidth=2048 activePairX=376..1672 splitX=1024");
        Log("P1_SOURCE_CROP offsetX=" + P1SourceOffsetX.ToString("F8", CultureInfo.InvariantCulture) +
            " scaleX=" + P1SourceScaleX.ToString("F8", CultureInfo.InvariantCulture));
        Log("P2_SOURCE_REFERENCE x=1024..1672 splitX=1024");
        Log("P2_SOURCE_CROP offsetX=" + P2SourceOffsetX.ToString("F8", CultureInfo.InvariantCulture) +
            " scaleX=" + P2SourceScaleX.ToString("F8", CultureInfo.InvariantCulture));
        Log("P2_TEXTURE_POLICY=share exact live P1 capture texture; no second window capture");
        Log("DISPLAY_LIFT_METERS=" + DisplayLiftMeters.ToString("F3", CultureInfo.InvariantCulture));
        Log("IO_P1_POLICY=COM3 game side fixed | MaiDXR P1 partner auto-selected | visual baseline unchanged");
        Log("TOUCH_TRANSPORT=continuous P1+P2 state streams ~90Hz when each serial port is open; P1 validated, P2 armed/unvalidated");
        Log("TOUCH_HAPTICS=hand-collider observer | onset 0.28/35ms | sustain 0.12 @30Hz | release grace 40ms");
        Log("LED_TRANSPORT=COM21 game side / dynamic MaiDXR partner; upstream LightManager visuals preserved");
        Log("LED_GLOBAL_STABILITY=CMD57 source guard; BodyLed/DisplayLed fixed without per-frame rewrite; RingLeds remain dynamic");
        Log("LED_SAFETY=malformed-frame guards enabled for LightManager");
        Log("ARCADE_NIGHT_V3=ambient10 reflection20 + ALL non-emissive cabinet/room materials x1.00; emissive/display/UI protected");
        Log("BUTTON_GLOW=dynamic emission + local Point halo | shadows OFF | range 0.28m | intensity 0.55");
        Log("REAL_CABINET_REFINED=less body brightness | visible white halo | whole-button glow + stronger tip glow | P1/P2 shared");
        Log("COIN_INPUT=left stick click -> elevated session bridge; no F2 mapping added");
        Log("ESCAPE_CLOSE_ALL=V0.6.6 elevated bridge closes the complete game/MaiDXR session");
        Log("BACKGROUND_TOUCH=Sinmai logical activation keepalive; Windows foreground remains available to spectator/MaiDXR");
        Log("RHYTHM_TOUCH=60Hz physics + native triggers + non-alloc swept-sphere recovery; transport remains 11ms");

        try
        {
            originalFixedDeltaTime = Time.fixedDeltaTime;
            Time.fixedDeltaTime = 1.0f / 60.0f;
            rhythmFixedDeltaConfigured = true;
            Log("RHYTHM_PHYSICS_FIXED_HZ=60 fixedDeltaTime=" + Time.fixedDeltaTime.ToString("F6", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            Log("RHYTHM_PHYSICS_CONFIG_FAIL " + ex.GetType().Name + ":" + ex.Message);
        }

        try
        {
            Application.runInBackground = true;
            Log("MAIDXR_RUN_IN_BACKGROUND=True");
        }
        catch (Exception ex)
        {
            Log("MAIDXR_RUN_IN_BACKGROUND_FAIL " + ex.GetType().Name + ":" + ex.Message);
        }

        ApplyCoinConfigFromEnvironment();
        ApplyDynamicP1SerialPort();
        ApplyDynamicLedSerialPort();
        InstallLedSafetyPatches();
        AmbientLabAwake();

        Discover(true);
        WriteStatus();
    }

    private void Update()
    {
        discoverTimer -= Time.unscaledDeltaTime;
        statusTimer -= Time.unscaledDeltaTime;

        if (discoverTimer <= 0f)
        {
            discoverTimer = 1.0f;
            Discover(false);
        }

        RestoreScales();
        ApplyEscapeCloseAll();
        ApplyArcadeNight();
        ApplyLocomotion();
        ApplyImmediateTurn();
        ApplyCoinClick();
        ApplyTriggerClick();
        UpdateTouchHaptics();
        AmbientLabUpdate();

        serialTelemetryTimer -= Time.unscaledDeltaTime;
        ledTelemetryTimer -= Time.unscaledDeltaTime;

        if (serialTelemetryTimer <= 0f)
        {
            serialTelemetryTimer = 0.10f;
            PollSerialTelemetry();
            EnsureContinuousTouchThread();
        }

        if (ledTelemetryTimer <= 0f)
        {
            ledTelemetryTimer = 0.10f;
            PollLedTelemetry();
        }

        if (continuousTouchEverActive && !continuousTouchActiveLogged)
        {
            continuousTouchActiveLogged = true;
            Log("CONTINUOUS_TOUCH_ACTIVE intervalMs=" + ContinuousTouchIntervalMs +
                " targetHz=" + (1000.0 / ContinuousTouchIntervalMs).ToString("F1", CultureInfo.InvariantCulture));
        }

        if (statusTimer <= 0f)
        {
            statusTimer = 1.0f;
            UpdateContinuousTouchRate();
            WriteStatus();
        }
    }

    private void LateUpdate()
    {
        // LightManager changes the 8 RingLeds during Update. Mirror those
        // colors here after all Updates so button emission/halo stays in sync.
        UpdatePhysicalButtonGlow();
        UpdateRealCabinetLighting();
        AmbientLabLateUpdate();

        // CiRCLE PLUS already contains P1 and P2 in ONE live PC texture.
        // Upstream MaiDXR leaves DisplayP2 on MaiDXR_Idle, so share the live
        // P1 texture with P2 first, then apply the exact independent crops.
        ShareLiveP1TextureWithP2();
        ApplyExactP1SourceCrop();
        ApplyExactP2SourceCrop();
    }

    private void OnDestroy()
    {
        SurfaceContactDestroy();
        if (rhythmFixedDeltaConfigured)
        {
            try { Time.fixedDeltaTime = originalFixedDeltaTime; } catch { }
            rhythmFixedDeltaConfigured = false;
        }
        AmbientLabOnDestroy();
        ReleaseCoinKeyIfNeeded();
        StopTouchHaptics(true);
        StopTouchHaptics(false);
        StopContinuousTouchThread();
        UpdateContinuousTouchRate();
        WriteStatus();
    }

    private void Log(string message)
    {
        string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message;
        Logger.LogInfo(line);
        try { File.AppendAllText(eventLog, line + Environment.NewLine, Encoding.UTF8); } catch { }
    }

    private static string GetPath(Transform t)
    {
        if (t == null) return "<null>";
        string p = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            p = t.name + "/" + p;
        }
        return p;
    }

    private static bool HasUsage(InputDevice d, string usage)
    {
        if (d == null) return false;
        for (int i = 0; i < d.usages.Count; i++)
            if (string.Equals(d.usages[i].ToString(), usage, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void Discover(bool forceLog)
    {
        DiscoverDevices();
        DiscoverXRRootAndCamera();
        DiscoverXROrigin();
        DiscoverCharacterController();

        if (xrRoot != null && guardedTransforms.Count == 0)
            InitializeScaleGuard();

        DisableLegacyLocomotion();
        FixDisplayGeometry();
        RefreshP1Materials();
        RefreshP2Materials();
        DiscoverTouchHapticHands(forceLog);
        DiscoverSurfaceContact();

        if (rightRayInteractor == null)
            FindRightRayInteractor();

        if (forceLog)
            Log("DISCOVER initial complete");
    }

    private void DiscoverTouchHapticHands(bool forceLog)
    {
        if (xrRoot == null)
            return;

        Collider[] colliders = xrRoot.GetComponentsInChildren<Collider>(true);

        Collider foundLeft = null;
        Collider foundRight = null;

        for (int i = 0; i < colliders.Length; i++)
        {
            Collider c = colliders[i];
            if (c == null || c.gameObject == null)
                continue;

            string n = c.gameObject.name;

            if (foundLeft == null &&
                string.Equals(n, "LHand", StringComparison.OrdinalIgnoreCase))
            {
                foundLeft = c;
            }

            if (foundRight == null &&
                string.Equals(n, "RHand", StringComparison.OrdinalIgnoreCase))
            {
                foundRight = c;
            }
        }

        if (foundLeft != null && leftTouchHapticCollider != foundLeft)
        {
            leftTouchHapticCollider = foundLeft;
            leftTouchHapticObserver = InstallTouchHapticObserver(foundLeft, true);
            if (forceLog || leftTouchHapticObserver != null)
                Log("HAPTIC_LEFT_HAND=" + GetPath(foundLeft.transform));
        }

        if (foundRight != null && rightTouchHapticCollider != foundRight)
        {
            rightTouchHapticCollider = foundRight;
            rightTouchHapticObserver = InstallTouchHapticObserver(foundRight, false);
            if (forceLog || rightTouchHapticObserver != null)
                Log("HAPTIC_RIGHT_HAND=" + GetPath(foundRight.transform));
        }
    }

    private MaiMaiVRTouchHapticHandObserver InstallTouchHapticObserver(
        Collider handCollider,
        bool isLeft)
    {
        if (handCollider == null)
            return null;

        GameObject go = handCollider.gameObject;

        // Disable only the legacy haptic script on this exact hand object.
        // It does not participate in touch serial/physics; leaving it enabled
        // would call StopHaptics on every trigger exit and fight our sustained
        // touch texture during slides.
        MonoBehaviour[] behaviours = go.GetComponents<MonoBehaviour>();
        for (int i = 0; i < behaviours.Length; i++)
        {
            MonoBehaviour mb = behaviours[i];
            if (mb == null)
                continue;

            if (string.Equals(
                mb.GetType().Name,
                "ControllerHapticManager",
                StringComparison.Ordinal))
            {
                if (mb.enabled)
                {
                    mb.enabled = false;
                    legacyHapticManagersDisabled++;
                    Log("LEGACY_HAPTIC_DISABLED path=" + GetPath(go.transform) +
                        " type=" + mb.GetType().FullName);
                }
            }
        }

        MaiMaiVRTouchHapticHandObserver observer =
            go.GetComponent<MaiMaiVRTouchHapticHandObserver>();

        if (observer == null)
        {
            observer = go.AddComponent<MaiMaiVRTouchHapticHandObserver>();
            hapticObserversInstalled++;
        }

        observer.Owner = this;
        observer.IsLeft = isLeft;

        return observer;
    }

    public void OnTouchHapticContactState(
        bool isLeft,
        int contactCount,
        bool entering,
        Collider zoneCollider)
    {
        contactCount = Mathf.Max(0, contactCount);

        string zonePath = zoneCollider != null
            ? GetPath(zoneCollider.transform)
            : "<none>";

        if (isLeft)
        {
            int previous = leftHapticContacts;
            leftHapticContacts = contactCount;

            if (entering)
            {
                leftHapticEnters++;
                leftHapticLastZone = zonePath;
            }
            else
            {
                leftHapticExits++;
            }

            if (previous == 0 && contactCount > 0)
            {
                leftHapticOnsetPending = true;
                leftHapticReleaseDeadline = -1f;
                Log("HAPTIC_TOUCH_START hand=LEFT zone=" + zonePath);
            }
            else if (contactCount == 0)
            {
                leftHapticReleaseDeadline =
                    Time.unscaledTime + TouchHapticReleaseGraceSeconds;
            }
            else
            {
                leftHapticReleaseDeadline = -1f;
            }
        }
        else
        {
            int previous = rightHapticContacts;
            rightHapticContacts = contactCount;

            if (entering)
            {
                rightHapticEnters++;
                rightHapticLastZone = zonePath;
            }
            else
            {
                rightHapticExits++;
            }

            if (previous == 0 && contactCount > 0)
            {
                rightHapticOnsetPending = true;
                rightHapticReleaseDeadline = -1f;
                Log("HAPTIC_TOUCH_START hand=RIGHT zone=" + zonePath);
            }
            else if (contactCount == 0)
            {
                rightHapticReleaseDeadline =
                    Time.unscaledTime + TouchHapticReleaseGraceSeconds;
            }
            else
            {
                rightHapticReleaseDeadline = -1f;
            }
        }
    }

    public void ResetTouchHapticHand(bool isLeft)
    {
        if (isLeft)
        {
            leftHapticContacts = 0;
            leftHapticOnsetPending = false;
            leftHapticReleaseDeadline = Time.unscaledTime;
        }
        else
        {
            rightHapticContacts = 0;
            rightHapticOnsetPending = false;
            rightHapticReleaseDeadline = Time.unscaledTime;
        }
    }

    private void UpdateTouchHaptics()
    {
        UpdateTouchHapticHand(true);
        UpdateTouchHapticHand(false);
    }

    private void UpdateTouchHapticHand(bool isLeft)
    {
        int contacts = isLeft ? leftHapticContacts : rightHapticContacts;
        float deadline = isLeft ? leftHapticReleaseDeadline : rightHapticReleaseDeadline;

        bool inGrace =
            contacts == 0 &&
            deadline >= 0f &&
            Time.unscaledTime < deadline;

        bool shouldRun = contacts > 0 || inGrace;

        if (isLeft)
        {
            if (leftHapticOnsetPending)
            {
                leftHapticOnsetPending = false;
                TrySendTouchHaptic(
                    true,
                    TouchHapticOnsetAmplitude,
                    TouchHapticOnsetDuration);
                leftHapticPulseTimer = 0f;
            }

            if (shouldRun)
            {
                leftHapticPulseTimer -= Time.unscaledDeltaTime;
                if (leftHapticPulseTimer <= 0f)
                {
                    TrySendTouchHaptic(
                        true,
                        TouchHapticSustainAmplitude,
                        TouchHapticSustainDuration);

                    leftHapticPulseTimer = 1f / TouchHapticFrequency;
                }

                leftHapticWasRunning = true;
            }
            else if (leftHapticWasRunning)
            {
                StopTouchHaptics(true);
                leftHapticWasRunning = false;
                leftHapticReleaseDeadline = -1f;
                Log("HAPTIC_TOUCH_STOP hand=LEFT");
            }
        }
        else
        {
            if (rightHapticOnsetPending)
            {
                rightHapticOnsetPending = false;
                TrySendTouchHaptic(
                    false,
                    TouchHapticOnsetAmplitude,
                    TouchHapticOnsetDuration);
                rightHapticPulseTimer = 0f;
            }

            if (shouldRun)
            {
                rightHapticPulseTimer -= Time.unscaledDeltaTime;
                if (rightHapticPulseTimer <= 0f)
                {
                    TrySendTouchHaptic(
                        false,
                        TouchHapticSustainAmplitude,
                        TouchHapticSustainDuration);

                    rightHapticPulseTimer = 1f / TouchHapticFrequency;
                }

                rightHapticWasRunning = true;
            }
            else if (rightHapticWasRunning)
            {
                StopTouchHaptics(false);
                rightHapticWasRunning = false;
                rightHapticReleaseDeadline = -1f;
                Log("HAPTIC_TOUCH_STOP hand=RIGHT");
            }
        }
    }

    private bool TrySendTouchHaptic(
        bool isLeft,
        float amplitude,
        float duration)
    {
        UnityEngine.XR.InputDevice device =
            isLeft ? leftXRHapticDevice : rightXRHapticDevice;

        if (!device.isValid)
        {
            device = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(
                isLeft
                    ? UnityEngine.XR.XRNode.LeftHand
                    : UnityEngine.XR.XRNode.RightHand);

            if (isLeft)
            {
                leftXRHapticDevice = device;
                leftHapticCapabilityKnown = false;
            }
            else
            {
                rightXRHapticDevice = device;
                rightHapticCapabilityKnown = false;
            }
        }

        if (!device.isValid)
        {
            RegisterHapticFailure(isLeft, "XR device invalid");
            return false;
        }

        bool capabilityKnown =
            isLeft ? leftHapticCapabilityKnown : rightHapticCapabilityKnown;

        bool supportsImpulse =
            isLeft ? leftHapticImpulseSupported : rightHapticImpulseSupported;

        if (!capabilityKnown)
        {
            UnityEngine.XR.HapticCapabilities capabilities;
            bool gotCapabilities =
                device.TryGetHapticCapabilities(out capabilities);

            supportsImpulse =
                gotCapabilities &&
                capabilities.supportsImpulse &&
                capabilities.numChannels > 0;

            if (isLeft)
            {
                leftHapticCapabilityKnown = true;
                leftHapticImpulseSupported = supportsImpulse;
            }
            else
            {
                rightHapticCapabilityKnown = true;
                rightHapticImpulseSupported = supportsImpulse;
            }

            Log("HAPTIC_CAPS hand=" + (isLeft ? "LEFT" : "RIGHT") +
                " device=" + device.name +
                " gotCaps=" + gotCapabilities +
                " supportsImpulse=" + supportsImpulse +
                (gotCapabilities
                    ? " channels=" + capabilities.numChannels
                    : ""));
        }

        if (!supportsImpulse)
        {
            RegisterHapticFailure(isLeft, "Impulse not supported");
            return false;
        }

        amplitude = Mathf.Clamp01(amplitude);
        duration = Mathf.Max(0.001f, duration);

        bool ok = false;

        try
        {
            ok = device.SendHapticImpulse(0u, amplitude, duration);
        }
        catch (Exception ex)
        {
            RegisterHapticFailure(
                isLeft,
                ex.GetType().Name + ":" + ex.Message);
            return false;
        }

        if (ok)
        {
            if (isLeft)
                leftHapticImpulses++;
            else
                rightHapticImpulses++;
        }
        else
        {
            RegisterHapticFailure(isLeft, "SendHapticImpulse returned false");
        }

        return ok;
    }

    private void RegisterHapticFailure(bool isLeft, string reason)
    {
        if (isLeft)
            leftHapticSendFailures++;
        else
            rightHapticSendFailures++;

        int failures = isLeft
            ? leftHapticSendFailures
            : rightHapticSendFailures;

        if (failures <= 5 || (failures % 100) == 0)
        {
            Log("HAPTIC_SEND_FAIL hand=" +
                (isLeft ? "LEFT" : "RIGHT") +
                " count=" + failures +
                " reason=" + reason);
        }
    }

    private void StopTouchHaptics(bool isLeft)
    {
        UnityEngine.XR.InputDevice device =
            isLeft ? leftXRHapticDevice : rightXRHapticDevice;

        if (!device.isValid)
        {
            device = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(
                isLeft
                    ? UnityEngine.XR.XRNode.LeftHand
                    : UnityEngine.XR.XRNode.RightHand);

            if (isLeft)
                leftXRHapticDevice = device;
            else
                rightXRHapticDevice = device;
        }

        if (!device.isValid)
            return;

        try
        {
            device.StopHaptics();
        }
        catch { }
    }

    private void ApplyEscapeCloseAll()
    {
        if (escapeCloseAllRequested)
            return;

        Keyboard keyboard = Keyboard.current;

        if (keyboard == null ||
            keyboard.escapeKey == null ||
            !keyboard.escapeKey.wasPressedThisFrame)
        {
            return;
        }

        escapeCloseAllRequested = true;
        escapeCloseAllCount++;

        try
        {
            escapeCloseAllMarkerPath =
                Path.Combine(reportDir, "ESCAPE_CLOSE_ALL.request");

            File.WriteAllText(
                escapeCloseAllMarkerPath,
                DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
                Encoding.UTF8);

            Log("ESCAPE_CLOSE_ALL_REQUESTED marker=" + escapeCloseAllMarkerPath);
        }
        catch (Exception ex)
        {
            Log("ESCAPE_CLOSE_ALL_MARKER_FAIL " +
                ex.GetType().Name + ":" + ex.Message);
        }

        ReleaseCoinKeyIfNeeded();
        WriteStatus();

        // The PowerShell launcher sees the marker and closes Sinmai/amdaemon.
        // Quit MaiDXR as well so no VR window is left behind.
        Application.Quit(0);
    }

    private void ApplyCoinConfigFromEnvironment()
    {
        string raw = Environment.GetEnvironmentVariable("MAIMAIVR_COIN_VK");
        if (string.IsNullOrWhiteSpace(raw))
        {
            Log("COIN_VK=0x72 source=segatools_default_F3");
            return;
        }

        try
        {
            raw = raw.Trim();
            int parsed = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToInt32(raw.Substring(2), 16)
                : Convert.ToInt32(raw, CultureInfo.InvariantCulture);

            if (parsed < 1 || parsed > 255)
                throw new ArgumentOutOfRangeException("coinVirtualKey");

            coinVirtualKey = parsed;
            Log("COIN_VK=0x" + coinVirtualKey.ToString("X2", CultureInfo.InvariantCulture) + " source=launcher_segtools");
        }
        catch (Exception ex)
        {
            coinVirtualKey = 0x72;
            coinLastError = "Coin VK parse: " + ex.GetType().Name + ":" + ex.Message;
            Log("COIN_VK_PARSE_FAIL fallback=0x72 error=" + coinLastError);
        }
    }

    private void ApplyCoinClick()
    {
        bool pressed = leftStickClick != null && leftStickClick.isPressed;

        if (pressed && !previousLeftStickClick)
        {
            coinStickPresses++;
            BeginCoinPulse();
        }

        previousLeftStickClick = pressed;

        if (coinKeyDown && coinReleaseAt >= 0f && Time.unscaledTime >= coinReleaseAt)
            ReleaseCoinKeyIfNeeded();
    }

    private void BeginCoinPulse()
    {
        try
        {
            coinRequestWriteCount++;
            File.WriteAllText(
                coinRequestPath,
                coinRequestWriteCount.ToString(CultureInfo.InvariantCulture),
                Encoding.ASCII);

            Log("COIN_STICK_CLICK press=" + coinStickPresses +
                " request=" + coinRequestWriteCount +
                " bridge=ELEVATED_SESSION");
        }
        catch (Exception ex)
        {
            coinRequestWriteErrors++;
            coinSendErrors++;
            coinLastError = ex.GetType().Name + ":" + ex.Message;
            Log("COIN_REQUEST_FAIL " + coinLastError);
        }
    }

    private void ReleaseCoinKeyIfNeeded()
    {
        coinKeyDown = false;
        coinReleaseAt = -1f;
    }

    private static bool ArcadeNightNameLooksProtected(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        string s = text.ToLowerInvariant();

        return
            s.Contains("display p1") ||
            s.Contains("display p2") ||
            s.Contains("screen") ||
            s.Contains("monitor") ||
            s.Contains("canvas") ||
            s.Contains("ui") ||
            s.Contains("ray") ||
            s.Contains("controller") ||
            s.Contains("hand") ||
            s.Contains("camera") ||
            s.Contains("spectator") ||
            s.Contains("liv") ||
            s.Contains("nvr") ||
            s.Contains("fpsblock") ||
            s.Contains("tpsblock") ||
            s.Contains("maimaivr_");
    }

    private static bool ArcadeNightMaterialLooksEmissive(Material material)
    {
        if (material == null)
            return false;

        try
        {
            if (material.IsKeywordEnabled("_EMISSION"))
                return true;

            if (material.HasProperty("_EmissionColor"))
            {
                Color e = material.GetColor("_EmissionColor");
                if (Mathf.Max(e.r, Mathf.Max(e.g, e.b)) > 0.02f)
                    return true;
            }
        }
        catch { }

        return false;
    }

    private static bool ArcadeNightTryDimMaterial(Material material)
    {
        if (material == null)
            return false;

        try
        {
            if (material.HasProperty("_BaseColor"))
            {
                Color c = material.GetColor("_BaseColor");
                material.SetColor(
                    "_BaseColor",
                    new Color(
                        c.r * ArcadeNightMaterialMultiplier,
                        c.g * ArcadeNightMaterialMultiplier,
                        c.b * ArcadeNightMaterialMultiplier,
                        c.a));

                return true;
            }

            if (material.HasProperty("_Color"))
            {
                Color c = material.GetColor("_Color");
                material.SetColor(
                    "_Color",
                    new Color(
                        c.r * ArcadeNightMaterialMultiplier,
                        c.g * ArcadeNightMaterialMultiplier,
                        c.b * ArcadeNightMaterialMultiplier,
                        c.a));

                return true;
            }
        }
        catch { }

        return false;
    }

    private void ApplyArcadeNight()
    {
        if (!arcadeNightGlobalSettingsApplied)
        {
            try
            {
                arcadeNightOriginalAmbientIntensity = RenderSettings.ambientIntensity;
                arcadeNightOriginalReflectionIntensity = RenderSettings.reflectionIntensity;

                RenderSettings.ambientIntensity =
                    Mathf.Max(0f,
                        arcadeNightOriginalAmbientIntensity *
                        ArcadeNightAmbientMultiplier);

                RenderSettings.reflectionIntensity =
                    Mathf.Max(0f,
                        arcadeNightOriginalReflectionIntensity *
                        ArcadeNightReflectionMultiplier);

                Material skybox = RenderSettings.skybox;

                if (skybox != null && skybox.HasProperty("_Exposure"))
                {
                    arcadeNightOriginalSkyboxExposure = skybox.GetFloat("_Exposure");
                    skybox.SetFloat(
                        "_Exposure",
                        Mathf.Max(
                            0.01f,
                            arcadeNightOriginalSkyboxExposure *
                            ArcadeNightSkyboxExposureMultiplier));
                }

                Light[] allLights = Resources.FindObjectsOfTypeAll<Light>();

                for (int i = 0; i < allLights.Length; i++)
                {
                    Light light = allLights[i];

                    if (light == null ||
                        light.gameObject == null ||
                        !light.gameObject.scene.IsValid() ||
                        light.type != LightType.Directional)
                    {
                        continue;
                    }

                    int id = light.GetInstanceID();

                    if (!arcadeNightOriginalDirectionalIntensities.ContainsKey(id))
                        arcadeNightOriginalDirectionalIntensities[id] = light.intensity;

                    light.intensity =
                        arcadeNightOriginalDirectionalIntensities[id] *
                        ArcadeNightDirectionalMultiplier;

                    arcadeNightDirectionalLightsDimmed++;
                }

                arcadeNightGlobalSettingsApplied = true;

                Log("ARCADE_NIGHT_GLOBAL_APPLIED ambient=" +
                    arcadeNightOriginalAmbientIntensity.ToString("F3", CultureInfo.InvariantCulture) +
                    "->" +
                    RenderSettings.ambientIntensity.ToString("F3", CultureInfo.InvariantCulture) +
                    " reflection=" +
                    arcadeNightOriginalReflectionIntensity.ToString("F3", CultureInfo.InvariantCulture) +
                    "->" +
                    RenderSettings.reflectionIntensity.ToString("F3", CultureInfo.InvariantCulture) +
                    " directionalLights=" +
                    arcadeNightDirectionalLightsDimmed);
            }
            catch (Exception ex)
            {
                Log("ARCADE_NIGHT_GLOBAL_FAIL " +
                    ex.GetType().Name + ":" + ex.Message);
            }
        }

        arcadeNightMaterialScanTimer -= Time.unscaledDeltaTime;

        if (arcadeNightMaterialScanTimer > 0f)
            return;

        arcadeNightMaterialScanTimer = ArcadeNightMaterialRescanSeconds;

        try
        {
            Renderer[] renderers = Resources.FindObjectsOfTypeAll<Renderer>();

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];

                if (renderer == null ||
                    renderer.gameObject == null ||
                    !renderer.gameObject.scene.IsValid())
                {
                    continue;
                }

                int rendererId = renderer.GetInstanceID();

                if (arcadeNightProcessedRenderers.Contains(rendererId))
                    continue;

                arcadeNightProcessedRenderers.Add(rendererId);
                arcadeNightRenderersScanned++;

                string path = GetPath(renderer.transform);

                if (ArcadeNightNameLooksProtected(path))
                {
                    arcadeNightRenderersSkippedProtected++;
                    continue;
                }

                Material[] materials;

                try
                {
                    materials = renderer.materials;
                }
                catch
                {
                    continue;
                }

                bool rendererDimmed = false;

                for (int m = 0; m < materials.Length; m++)
                {
                    Material material = materials[m];

                    if (material == null)
                        continue;

                    if (ArcadeNightNameLooksProtected(path + "/" + material.name))
                    {
                        arcadeNightRenderersSkippedProtected++;
                        continue;
                    }

                    if (ArcadeNightMaterialLooksEmissive(material))
                    {
                        arcadeNightRenderersSkippedGlow++;
                        continue;
                    }

                    if (ArcadeNightTryDimMaterial(material))
                    {
                        arcadeNightMaterialsDimmed++;
                        rendererDimmed = true;
                    }
                }

                if (rendererDimmed)
                {
                    arcadeNightRenderersDimmed++;

                    if (arcadeNightRenderersDimmed <= 20)
                        Log("ARCADE_NIGHT_RENDERER_DIM path=" + path);
                }
            }

            arcadeNightApplied =
                arcadeNightGlobalSettingsApplied &&
                arcadeNightRenderersDimmed > 0;
        }
        catch (Exception ex)
        {
            Log("ARCADE_NIGHT_MATERIAL_FAIL " +
                ex.GetType().Name + ":" + ex.Message);
        }
    }

    private void DiscoverDevices()
    {
        InputDevice newLeft = null;
        InputDevice newRight = null;

        foreach (InputDevice d in InputSystem.devices)
        {
            string info = (d.layout + " " + d.name + " " + d.displayName).ToLowerInvariant();
            bool xrLike = info.Contains("xr") || info.Contains("oculus") ||
                          info.Contains("touch") || info.Contains("controller");
            if (!xrLike) continue;

            if (HasUsage(d, "LeftHand")) newLeft = d;
            if (HasUsage(d, "RightHand")) newRight = d;
        }

        if (newLeft != leftDevice)
        {
            leftDevice = newLeft;
            leftStick = leftDevice != null
                ? (leftDevice.TryGetChildControl<Vector2Control>("thumbstick") ??
                   leftDevice.TryGetChildControl<Vector2Control>("primary2DAxis"))
                : null;

            leftStickClick = leftDevice != null
                ? (leftDevice.TryGetChildControl<ButtonControl>("thumbstickClicked") ??
                   leftDevice.TryGetChildControl<ButtonControl>("thumbstickClick") ??
                   leftDevice.TryGetChildControl<ButtonControl>("primary2DAxisClick") ??
                   leftDevice.TryGetChildControl<ButtonControl>("stickPress"))
                : null;

            Log("LEFT_DEVICE=" +
                (leftDevice != null
                    ? leftDevice.displayName + " layout=" + leftDevice.layout +
                      " stick=" + (leftStick != null) +
                      " stickClick=" + (leftStickClick != null)
                    : "NONE"));
        }

        if (newRight != rightDevice)
        {
            rightDevice = newRight;
            rightStick = rightDevice != null
                ? (rightDevice.TryGetChildControl<Vector2Control>("thumbstick") ??
                   rightDevice.TryGetChildControl<Vector2Control>("primary2DAxis"))
                : null;

            rightTriggerPressed = rightDevice != null
                ? rightDevice.TryGetChildControl<ButtonControl>("triggerpressed")
                : null;
            rightTrigger = rightDevice != null
                ? rightDevice.TryGetChildControl<AxisControl>("trigger")
                : null;

            Log("RIGHT_DEVICE=" +
                (rightDevice != null
                    ? rightDevice.displayName + " layout=" + rightDevice.layout +
                      " stick=" + (rightStick != null) +
                      " triggerPressed=" + (rightTriggerPressed != null) +
                      " trigger=" + (rightTrigger != null)
                    : "NONE"));
        }
    }

    private void DiscoverXRRootAndCamera()
    {
        if (xrRoot == null)
        {
            GameObject root = GameObject.Find("XR Local");
            if (root == null) root = GameObject.Find("XRLocal");

            if (root == null)
            {
                GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < all.Length; i++)
                {
                    GameObject go = all[i];
                    if (go == null || !go.scene.IsValid()) continue;

                    if (string.Equals(go.name, "XR Local", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(go.name, "XRLocal", StringComparison.OrdinalIgnoreCase))
                    {
                        root = go;
                        break;
                    }
                }
            }

            if (root != null)
            {
                xrRoot = root.transform;
                Log("XR_ROOT=" + GetPath(xrRoot));
            }
        }

        if (hmdCamera == null)
        {
            hmdCamera = Camera.main;

            if (hmdCamera == null)
            {
                Camera[] cams = Resources.FindObjectsOfTypeAll<Camera>();
                for (int i = 0; i < cams.Length; i++)
                {
                    Camera c = cams[i];
                    if (c != null && c.gameObject.scene.IsValid() &&
                        c.gameObject.name.IndexOf("Main Camera", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        hmdCamera = c;
                        break;
                    }
                }
            }

            if (hmdCamera != null)
                Log("HMD_CAMERA=" + GetPath(hmdCamera.transform));
        }
    }

    private void DiscoverXROrigin()
    {
        if (xrOriginComponent != null && moveCameraToWorldLocation != null)
            return;

        MonoBehaviour[] all = Resources.FindObjectsOfTypeAll<MonoBehaviour>();

        for (int i = 0; i < all.Length; i++)
        {
            MonoBehaviour mb = all[i];
            if (mb == null || !mb.gameObject.scene.IsValid())
                continue;

            Type t = mb.GetType();
            string full = t.FullName ?? "";

            if (!string.Equals(full, "Unity.XR.CoreUtils.XROrigin", StringComparison.Ordinal))
                continue;

            MethodInfo method = t.GetMethod(
                "MoveCameraToWorldLocation",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new Type[] { typeof(Vector3) },
                null);

            if (method == null)
                continue;

            xrOriginComponent = mb;
            moveCameraToWorldLocation = method;

            if (xrRoot == null)
                xrRoot = mb.transform;

            Log("XRORIGIN=" + full + " on " + GetPath(mb.transform));
            break;
        }
    }

    private void DiscoverCharacterController()
    {
        if (characterController != null || xrRoot == null)
            return;

        characterController = xrRoot.GetComponent<CharacterController>();
        if (characterController == null)
            characterController = xrRoot.GetComponentInChildren<CharacterController>();

        if (characterController != null)
            Log("CHARACTER_CONTROLLER=" + GetPath(characterController.transform));
    }

    private void InitializeScaleGuard()
    {
        guardedTransforms.Clear();
        guardedScales.Clear();

        Transform t = xrRoot;
        while (t != null)
        {
            guardedTransforms.Add(t);
            guardedScales.Add(t.localScale);
            Log("SCALE_GUARD " + GetPath(t) + "=" + t.localScale.ToString("F4"));
            t = t.parent;
        }

        if (hmdCamera != null)
        {
            guardedTransforms.Add(hmdCamera.transform);
            guardedScales.Add(hmdCamera.transform.localScale);
            Log("SCALE_GUARD " + GetPath(hmdCamera.transform) + "=" + hmdCamera.transform.localScale.ToString("F4"));
        }
    }

    private void RestoreScales()
    {
        for (int i = 0; i < guardedTransforms.Count; i++)
        {
            Transform t = guardedTransforms[i];
            if (t == null) continue;

            Vector3 wanted = guardedScales[i];
            Vector3 current = t.localScale;

            if ((current - wanted).sqrMagnitude > 0.000001f)
            {
                t.localScale = wanted;

                if (hmdCamera != null && t == hmdCamera.transform)
                    cameraScaleRestores++;
                else
                    scaleRestores++;

                Log("SCALE_RESTORE " + GetPath(t) + " from " +
                    current.ToString("F4") + " to " + wanted.ToString("F4"));
            }
        }
    }

    private void DisableLegacyLocomotion()
    {
        MonoBehaviour[] all = Resources.FindObjectsOfTypeAll<MonoBehaviour>();

        for (int i = 0; i < all.Length; i++)
        {
            MonoBehaviour mb = all[i];
            if (mb == null || !mb.gameObject.scene.IsValid() || !mb.enabled)
                continue;

            string full = mb.GetType().FullName ?? mb.GetType().Name;

            bool isMove =
                full.IndexOf("ContinuousMoveProvider", StringComparison.OrdinalIgnoreCase) >= 0 ||
                full.IndexOf("ActionBasedContinuousMoveProvider", StringComparison.OrdinalIgnoreCase) >= 0;

            bool isTurn =
                full.IndexOf("ContinuousTurnProvider", StringComparison.OrdinalIgnoreCase) >= 0 ||
                full.IndexOf("ActionBasedContinuousTurnProvider", StringComparison.OrdinalIgnoreCase) >= 0 ||
                full.IndexOf("SnapTurnProvider", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isMove)
            {
                mb.enabled = false;
                disabledMoveProviders++;
                Log("DISABLED_LEGACY_MOVE_PROVIDER=" + full + " on " + GetPath(mb.transform));
            }
            else if (isTurn)
            {
                mb.enabled = false;
                disabledTurnProviders++;
                Log("DISABLED_LEGACY_TURN_PROVIDER=" + full + " on " + GetPath(mb.transform));
            }
        }
    }

    private void RefreshP1Materials()
    {
        Renderer[] renderers = Resources.FindObjectsOfTypeAll<Renderer>();

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null || !r.gameObject.scene.IsValid())
                continue;

            Material[] mats;
            try { mats = r.materials; }
            catch { continue; }

            if (mats == null)
                continue;

            for (int j = 0; j < mats.Length; j++)
            {
                Material m = mats[j];
                if (m == null)
                    continue;

                string n = m.name ?? "";
                if (n.IndexOf("DisplayP1", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                int id = m.GetInstanceID();
                if (p1MaterialIds.Contains(id))
                    continue;

                p1MaterialIds.Add(id);
                p1DisplayMaterials.Add(m);

                Log("P1_MATERIAL_FOUND renderer=" + GetPath(r.transform) +
                    " material=" + n);
            }
        }
    }

    private void ApplyExactP1SourceCrop()
    {
        if (p1DisplayMaterials.Count == 0)
            return;

        for (int i = 0; i < p1DisplayMaterials.Count; i++)
        {
            Material m = p1DisplayMaterials[i];
            if (m == null || !m.HasProperty("_MainTex"))
                continue;

            // This is not a hand-tuned fit. It is the exact normalized interval
            // occupied by P1 in the centered PC source capture.
            m.SetTextureScale("_MainTex", new Vector2(P1SourceScaleX, -1f));
            m.SetTextureOffset("_MainTex", new Vector2(P1SourceOffsetX, 0f));
            p1CropApplyCount++;
        }
    }

    private void RefreshP2Materials()
    {
        Renderer[] renderers = Resources.FindObjectsOfTypeAll<Renderer>();

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null || !r.gameObject.scene.IsValid())
                continue;

            Material[] mats;
            try { mats = r.materials; }
            catch { continue; }

            if (mats == null)
                continue;

            for (int j = 0; j < mats.Length; j++)
            {
                Material m = mats[j];
                if (m == null)
                    continue;

                string n = m.name ?? "";
                if (n.IndexOf("DisplayP2", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                int id = m.GetInstanceID();
                if (p2MaterialIds.Contains(id))
                    continue;

                p2MaterialIds.Add(id);
                p2DisplayMaterials.Add(m);

                Log("P2_MATERIAL_FOUND renderer=" + GetPath(r.transform) +
                    " material=" + n);
            }
        }
    }

    private Texture FindLiveP1GameTexture()
    {
        Texture best = null;

        for (int i = 0; i < p1DisplayMaterials.Count; i++)
        {
            Material m = p1DisplayMaterials[i];
            if (m == null || !m.HasProperty("_MainTex"))
                continue;

            Texture t = null;
            try { t = m.mainTexture; }
            catch { continue; }

            if (t == null)
                continue;

            string n = t.name ?? "";
            bool idle = n.IndexOf("MaiDXR_Idle", StringComparison.OrdinalIgnoreCase) >= 0;

            // The observed live game texture is 2560x1440.
            // Prefer any non-idle texture large enough to be a PC/window capture.
            if (!idle && t.width >= 1280 && t.height >= 720)
                return t;

            if (!idle && best == null)
                best = t;
        }

        return best;
    }

    private void ShareLiveP1TextureWithP2()
    {
        Texture live = FindLiveP1GameTexture();

        if (live == null)
            return;

        if (sharedLiveGameTexture != live)
        {
            sharedLiveGameTexture = live;
            sharedLiveGameTextureInfo =
                (live.name ?? "<unnamed>") + " " + live.width + "x" + live.height;

            p2TextureShareChangeCount++;

            Log("LIVE_GAME_TEXTURE_SELECTED source=P1 texture=" +
                sharedLiveGameTextureInfo);
        }

        for (int i = 0; i < p2DisplayMaterials.Count; i++)
        {
            Material m = p2DisplayMaterials[i];
            if (m == null || !m.HasProperty("_MainTex"))
                continue;

            try
            {
                if (m.mainTexture != live)
                {
                    Texture previous = m.mainTexture;

                    m.mainTexture = live;

                    Log("P2_TEXTURE_SHARED previous=" +
                        (previous != null
                            ? ((previous.name ?? "<unnamed>") + " " +
                               previous.width + "x" + previous.height)
                            : "<null>") +
                        " new=" + sharedLiveGameTextureInfo);
                }

                p2TextureShareApplyCount++;
            }
            catch (Exception ex)
            {
                Log("P2_TEXTURE_SHARE_ERROR " +
                    ex.GetType().Name + ":" + ex.Message);
            }
        }
    }

    private void ApplyExactP2SourceCrop()
    {
        if (p2DisplayMaterials.Count == 0)
            return;

        for (int i = 0; i < p2DisplayMaterials.Count; i++)
        {
            Material m = p2DisplayMaterials[i];
            if (m == null || !m.HasProperty("_MainTex"))
                continue;

            // Exact right-hand active interval in the centered PC source:
            // x=1024..1672 on a 2048-wide reference.
            m.SetTextureScale("_MainTex", new Vector2(P2SourceScaleX, -1f));
            m.SetTextureOffset("_MainTex", new Vector2(P2SourceOffsetX, 0f));
            p2CropApplyCount++;
        }
    }

    private void FixDisplayGeometry()
    {
        Renderer[] renderers = Resources.FindObjectsOfTypeAll<Renderer>();

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null || !r.gameObject.scene.IsValid())
                continue;

            bool isP1Display = false;
            bool isP2Display = false;
            Material[] mats = null;

            try { mats = r.sharedMaterials; }
            catch { continue; }

            if (mats == null) continue;

            for (int j = 0; j < mats.Length; j++)
            {
                Material m2 = mats[j];
                if (m2 == null) continue;

                string n2 = m2.name ?? "";

                if (n2.IndexOf("DisplayP1", StringComparison.OrdinalIgnoreCase) >= 0)
                    isP1Display = true;

                if (n2.IndexOf("DisplayP2", StringComparison.OrdinalIgnoreCase) >= 0)
                    isP2Display = true;
            }

            if (!isP1Display && !isP2Display)
                continue;

            string playerLabel = isP2Display ? "P2" : "P1";

            int id = r.GetInstanceID();
            if (fixedDisplayRenderers.Contains(id))
                continue;

            fixedDisplayRenderers.Add(id);

            MeshFilter mf = r.GetComponent<MeshFilter>();
            Vector3 originalScale = r.transform.localScale;
            Vector3 originalPos = r.transform.localPosition;
            Vector3 newScale = originalScale;
            Vector3 newPos = originalPos;
            string axisInfo = "fallback XY";
            int thicknessAxis = 2;

            if (mf != null && mf.sharedMesh != null)
            {
                Vector3 meshSize = mf.sharedMesh.bounds.size;

                // The smallest mesh dimension is the plane thickness / normal axis.
                if (meshSize.x <= meshSize.y && meshSize.x <= meshSize.z)
                {
                    thicknessAxis = 0;
                    newScale.y *= DisplayGeometryOverscan;
                    newScale.z *= DisplayGeometryOverscan;
                    axisInfo = "YZ";
                }
                else if (meshSize.y <= meshSize.x && meshSize.y <= meshSize.z)
                {
                    thicknessAxis = 1;
                    newScale.x *= DisplayGeometryOverscan;
                    newScale.z *= DisplayGeometryOverscan;
                    axisInfo = "XZ";
                }
                else
                {
                    thicknessAxis = 2;
                    newScale.x *= DisplayGeometryOverscan;
                    newScale.y *= DisplayGeometryOverscan;
                    axisInfo = "XY";
                }

                // V0.1.7: deterministic physical translation.
                // Move the display exactly along WORLD UP instead of approximating
                // an in-plane local axis. This avoids cabinet/model rotation
                // affecting the requested vertical correction.
                Vector3 worldLift = Vector3.up * DisplayLiftMeters;
                Vector3 originalWorldPos = r.transform.position;
                Vector3 targetWorldPos = originalWorldPos + worldLift;

                Log("DISPLAY_WORLD_LIFT player=" + playerLabel + " path=" + GetPath(r.transform) +
                    " originalWorldPosition=" + originalWorldPos.ToString("F4") +
                    " targetWorldPosition=" + targetWorldPos.ToString("F4") +
                    " liftMeters=" + DisplayLiftMeters.ToString("F3", CultureInfo.InvariantCulture));

                Log("DISPLAY_GEOMETRY_AUDIT player=" + playerLabel + " path=" + GetPath(r.transform) +
                    " mesh=" + mf.sharedMesh.name +
                    " meshBounds=" + meshSize.ToString("F4") +
                    " rendererBounds=" + r.bounds.size.ToString("F4") +
                    " originalLocalScale=" + originalScale.ToString("F4") +
                    " originalLocalPosition=" + originalPos.ToString("F4") +
                    " inPlaneAxes=" + axisInfo +
                    " liftMode=WorldUp");
            }
            else
            {
                newScale.x *= DisplayGeometryOverscan;
                newScale.y *= DisplayGeometryOverscan;

                Log("DISPLAY_GEOMETRY_AUDIT player=" + playerLabel + " path=" + GetPath(r.transform) +
                    " mesh=<none> rendererBounds=" + r.bounds.size.ToString("F4") +
                    " originalLocalScale=" + originalScale.ToString("F4") +
                    " originalLocalPosition=" + originalPos.ToString("F4") +
                    " inPlaneAxes=fallbackXY liftMode=WorldUp");
            }

            r.transform.localScale = newScale;
            r.transform.position = r.transform.position + (Vector3.up * DisplayLiftMeters);
            newPos = r.transform.localPosition;
            displayGeometryFixedCount++;

            if (isP2Display)
                p2DisplayLiftCount++;
            else
                p1DisplayLiftCount++;

            Texture tex = null;
            try
            {
                if (mats.Length > 0 && mats[0] != null && mats[0].HasProperty("_MainTex"))
                    tex = mats[0].mainTexture;
            }
            catch { }

            Log("DISPLAY_GEOMETRY_FIXED player=" + playerLabel + " path=" + GetPath(r.transform) +
                " newLocalScale=" + newScale.ToString("F4") +
                " newLocalPosition=" + newPos.ToString("F4") +
                " texture=" +
                (tex != null ? (tex.name + " " + tex.width + "x" + tex.height) : "<none>"));
        }
    }

    private Vector2 ReadStick(Vector2Control control)
    {
        if (control == null) return Vector2.zero;

        Vector2 v;
        try { v = control.ReadValue(); }
        catch { return Vector2.zero; }

        if (v.magnitude < 0.14f)
            return Vector2.zero;

        return v;
    }

    private void ApplyLocomotion()
    {
        if (rightStick == null || hmdCamera == null)
            return;

        Vector2 stick = ReadStick(rightStick);
        if (stick.magnitude < Deadzone)
            return;

        Vector3 forward = hmdCamera.transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f && xrRoot != null)
            forward = xrRoot.forward;
        if (forward.sqrMagnitude < 0.0001f)
            return;
        forward.Normalize();

        Vector3 right = hmdCamera.transform.right;
        right.y = 0f;
        if (right.sqrMagnitude < 0.0001f && xrRoot != null)
            right = xrRoot.right;
        if (right.sqrMagnitude < 0.0001f)
            return;
        right.Normalize();

        Vector3 direction = right * stick.x + forward * stick.y;
        if (direction.sqrMagnitude > 1f)
            direction.Normalize();

        Vector3 delta = direction * MoveSpeed * Time.unscaledDeltaTime;
        bool moved = false;

        if (xrOriginComponent != null && moveCameraToWorldLocation != null && hmdCamera != null)
        {
            try
            {
                Vector3 targetCameraWorld = hmdCamera.transform.position + delta;
                moveCameraToWorldLocation.Invoke(xrOriginComponent, new object[] { targetCameraWorld });
                movementMethod = "XROrigin.MoveCameraToWorldLocation";
                moved = true;
            }
            catch
            {
                xrOriginComponent = null;
                moveCameraToWorldLocation = null;
            }
        }

        if (!moved && characterController != null)
        {
            try
            {
                characterController.Move(delta);
                movementMethod = "CharacterController.Move";
                moved = true;
            }
            catch { characterController = null; }
        }

        if (!moved && xrRoot != null)
        {
            xrRoot.position += delta;
            movementMethod = "XRLocal.TransformFallback";
            moved = true;
        }

        if (!moved) return;

        movementFrames++;
        if (stick.y > Deadzone) forwardFrames++;
        if (stick.y < -Deadzone) backwardFrames++;
        if (stick.x < -Deadzone) leftFrames++;
        if (stick.x > Deadzone) rightFrames++;
    }

    private void ApplyImmediateTurn()
    {
        if (leftStick == null || hmdCamera == null)
            return;

        Vector2 stick = ReadStick(leftStick);
        if (Mathf.Abs(stick.x) < TurnDeadzone)
            return;

        Transform pivot = xrRoot != null ? xrRoot : hmdCamera.transform.parent;
        if (pivot == null)
            return;

        float angle = stick.x * TurnSpeed * Time.unscaledDeltaTime;
        pivot.RotateAround(hmdCamera.transform.position, Vector3.up, angle);
        turnFrames++;
    }

    private bool ReadRightTrigger()
    {
        try
        {
            if (rightTriggerPressed != null)
                return rightTriggerPressed.ReadValue() >= 0.5f;
            if (rightTrigger != null)
                return rightTrigger.ReadValue() >= 0.55f;
        }
        catch { }

        return false;
    }

    private void FindRightRayInteractor()
    {
        MonoBehaviour[] all = Resources.FindObjectsOfTypeAll<MonoBehaviour>();

        for (int i = 0; i < all.Length; i++)
        {
            MonoBehaviour mb = all[i];
            if (mb == null || !mb.gameObject.scene.IsValid())
                continue;

            Type t = mb.GetType();
            string full = t.FullName ?? t.Name;

            if (full.IndexOf("XRRayInteractor", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            string path = GetPath(mb.transform);
            if (path.IndexOf("RightHand", StringComparison.OrdinalIgnoreCase) < 0 &&
                path.IndexOf("Right Hand", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            MethodInfo[] methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            for (int m = 0; m < methods.Length; m++)
            {
                if (methods[m].Name != "TryGetCurrentUIRaycastResult")
                    continue;

                ParameterInfo[] ps = methods[m].GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType.IsByRef &&
                    ps[0].ParameterType.GetElementType() == typeof(RaycastResult))
                {
                    rightRayInteractor = mb;
                    tryGetUIRaycast = methods[m];
                    break;
                }
            }

            if (rightRayInteractor != null)
            {
                Log("RIGHT_RAY=" + full + " on " + path);
                break;
            }
        }
    }

    private bool TryGetCurrentUIHit(out RaycastResult result)
    {
        result = new RaycastResult();

        if (rightRayInteractor == null || tryGetUIRaycast == null)
            return false;

        try
        {
            object[] args = new object[] { result };
            object ret = tryGetUIRaycast.Invoke(rightRayInteractor, args);

            if (ret is bool && (bool)ret && args[0] is RaycastResult)
            {
                result = (RaycastResult)args[0];
                return result.gameObject != null;
            }
        }
        catch
        {
            rightRayInteractor = null;
            tryGetUIRaycast = null;
        }

        return false;
    }

    private PointerEventData BuildPointerData(RaycastResult hit)
    {
        if (EventSystem.current == null)
            return null;

        PointerEventData data = new PointerEventData(EventSystem.current);
        data.pointerId = -1002;
        data.button = PointerEventData.InputButton.Left;
        data.pointerCurrentRaycast = hit;
        data.pointerPressRaycast = hit;
        data.position = hit.screenPosition;
        data.pressPosition = data.position;
        data.eligibleForClick = true;
        data.clickCount = 1;
        data.clickTime = Time.unscaledTime;

        return data;
    }

    private void ApplyTriggerClick()
    {
        bool pressed = ReadRightTrigger();

        if (pressed && !previousTrigger)
        {
            triggerPresses++;
            RaycastResult hit;

            if (TryGetCurrentUIHit(out hit))
            {
                uiHits++;
                pressedPointerData = BuildPointerData(hit);

                if (pressedPointerData != null)
                {
                    pressedUiHandler = ExecuteEvents.ExecuteHierarchy(
                        hit.gameObject,
                        pressedPointerData,
                        ExecuteEvents.pointerDownHandler);

                    if (pressedUiHandler == null)
                    {
                        pressedUiHandler =
                            ExecuteEvents.GetEventHandler<IPointerClickHandler>(
                                hit.gameObject);
                    }

                    draggedUiHandler =
                        ExecuteEvents.GetEventHandler<IDragHandler>(
                            hit.gameObject);

                    if (draggedUiHandler != null)
                    {
                        pressedPointerData.pointerDrag = draggedUiHandler;

                        ExecuteEvents.Execute(
                            draggedUiHandler,
                            pressedPointerData,
                            ExecuteEvents.initializePotentialDrag);
                    }

                    pressedPointerData.pointerPress = pressedUiHandler;
                    pressedPointerData.rawPointerPress = hit.gameObject;
                    uiDragging = false;
                }
            }
            else
            {
                uiMisses++;
            }
        }
        else if (pressed && previousTrigger)
        {
            // Real XR slider dragging. V0.3.1.0-1.2 only sent down/up/click,
            // so sliders could jump on click but could never follow the ray.
            if (draggedUiHandler != null &&
                pressedPointerData != null)
            {
                RaycastResult hit;

                if (TryGetCurrentUIHit(out hit))
                {
                    Vector2 previousPosition =
                        pressedPointerData.position;

                    pressedPointerData.pointerCurrentRaycast = hit;
                    pressedPointerData.position = hit.screenPosition;
                    pressedPointerData.delta =
                        pressedPointerData.position -
                        previousPosition;

                    if (!uiDragging)
                    {
                        pressedPointerData.dragging = true;

                        ExecuteEvents.Execute(
                            draggedUiHandler,
                            pressedPointerData,
                            ExecuteEvents.beginDragHandler);

                        uiDragging = true;
                        uiBeginDrags++;
                    }

                    ExecuteEvents.Execute(
                        draggedUiHandler,
                        pressedPointerData,
                        ExecuteEvents.dragHandler);

                    uiDragEvents++;
                }
            }
        }
        else if (!pressed && previousTrigger)
        {
            if (pressedUiHandler != null &&
                pressedPointerData != null)
            {
                ExecuteEvents.ExecuteHierarchy(
                    pressedUiHandler,
                    pressedPointerData,
                    ExecuteEvents.pointerUpHandler);

                if (uiDragging && draggedUiHandler != null)
                {
                    ExecuteEvents.Execute(
                        draggedUiHandler,
                        pressedPointerData,
                        ExecuteEvents.endDragHandler);

                    uiEndDrags++;
                }
                else
                {
                    GameObject clickHandler =
                        ExecuteEvents.GetEventHandler<IPointerClickHandler>(
                            pressedUiHandler);

                    if (clickHandler == null)
                        clickHandler = pressedUiHandler;

                    ExecuteEvents.ExecuteHierarchy(
                        clickHandler,
                        pressedPointerData,
                        ExecuteEvents.pointerClickHandler);

                    uiClicks++;
                }
            }

            pressedUiHandler = null;
            pressedPointerData = null;
            draggedUiHandler = null;
            uiDragging = false;
        }

        previousTrigger = pressed;
    }

    private void InstallLedSafetyPatches()
    {
        if (ledSafetyPatchInstalled)
            return;

        try
        {
            Type t = FindLightManagerType();
            if (t == null)
            {
                ledSafetyPatchError = "LightManager type not found";
                Log("LED_SAFETY_PATCH_FAIL " + ledSafetyPatchError);
                return;
            }

            ledSafetyHarmony =
                new Harmony("maimaivr.integrated.baseline.ledsafety.v0.6.1");

            MethodInfo separate = AccessTools.Method(t, "SeperatData");
            MethodInfo updateLed = AccessTools.Method(t, "UpdateLED");

            if (separate == null)
                throw new MissingMethodException("LightManager.SeperatData");
            if (updateLed == null)
                throw new MissingMethodException("LightManager.UpdateLED");

            ledSafetyHarmony.Patch(
                separate,
                prefix: new HarmonyMethod(
                    typeof(MaiMaiVRIntegratedBaseline),
                    nameof(LightManagerSeparateDataPrefix)));

            ledSafetyHarmony.Patch(
                updateLed,
                prefix: new HarmonyMethod(
                    typeof(MaiMaiVRIntegratedBaseline),
                    nameof(LightManagerUpdateLedPrefix)));

            ledSafetyPatchInstalled = true;
            ledSafetyPatchError = "<none>";
            Log("LED_SAFETY_PATCH_OK methods=SeperatData+UpdateLED");
        }
        catch (Exception ex)
        {
            ledSafetyPatchError = ex.GetType().Name + ":" + ex.Message;
            Log("LED_SAFETY_PATCH_FAIL " + ledSafetyPatchError);
        }
    }

    private static bool LightManagerSeparateDataPrefix(List<byte> data)
    {
        if (data == null || data.Count < 1)
        {
            Interlocked.Increment(ref ledMalformedFramesSkipped);
            return false;
        }

        return true;
    }

    private static bool LightManagerUpdateLedPrefix(
        List<byte> _data,
        List<Light> ringLeds,
        Light bodyLed,
        Light dispayLed)
    {
        if (_data == null || _data.Count < 1)
        {
            Interlocked.Increment(ref ledUnsafeUpdatesSkipped);
            return false;
        }

        int cmd = _data[0];

        if (cmd == 49)
        {
            if (_data.Count < 5 ||
                ringLeds == null ||
                _data[1] >= ringLeds.Count)
            {
                Interlocked.Increment(ref ledUnsafeUpdatesSkipped);
                return false;
            }
        }
        else if (cmd == 50)
        {
            if (_data.Count < 7 || ringLeds == null)
            {
                Interlocked.Increment(ref ledUnsafeUpdatesSkipped);
                return false;
            }

            int start = _data[1];
            int end = Mathf.Min(_data[2], (byte)8);

            if (start > end || start >= ringLeds.Count)
            {
                Interlocked.Increment(ref ledUnsafeUpdatesSkipped);
                return false;
            }
        }
        else if (cmd == 51)
        {
            if (_data.Count < 8 || ringLeds == null)
            {
                Interlocked.Increment(ref ledUnsafeUpdatesSkipped);
                return false;
            }

            int start = _data[1];
            int end = Mathf.Min(_data[2], (byte)8);

            if (start > end || start >= ringLeds.Count)
            {
                Interlocked.Increment(ref ledUnsafeUpdatesSkipped);
                return false;
            }
        }
        else if (cmd == 57)
        {
            if (_data.Count < 3)
            {
                Interlocked.Increment(ref ledUnsafeUpdatesSkipped);
                return false;
            }

            // V0.6.1: suppress only CMD57's broad-cast Body/Display
            // animation at the source. Ring LED commands 49/50/51 continue
            // through the original MaiDXR code unchanged.
            if (bodyLed != null &&
                !Mathf.Approximately(bodyLed.intensity, StableBodyLedIntensity))
            {
                bodyLed.intensity = StableBodyLedIntensity;
                Interlocked.Increment(ref globalLightBodyWrites);
            }

            if (dispayLed != null &&
                !Mathf.Approximately(dispayLed.intensity, StableDisplayLedIntensity))
            {
                dispayLed.intensity = StableDisplayLedIntensity;
                Interlocked.Increment(ref globalLightDisplayWrites);
            }

            Interlocked.Increment(ref globalLightCmd57Suppressed);
            return false;
        }

        return true;
    }

    private Type FindLightManagerType()
    {
        try
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    Type t = assemblies[i].GetType("LightManager", false);
                    if (t != null)
                        return t;
                }
                catch { }
            }
        }
        catch { }

        return null;
    }

    private void ApplyDynamicLedSerialPort()
    {
        string envPort = Environment.GetEnvironmentVariable("MAIMAIVR_LED_P1_PORT");

        if (!string.IsNullOrWhiteSpace(envPort))
            requestedLedSerialPort = envPort.Trim().ToUpperInvariant();

        if (!requestedLedSerialPort.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
        {
            dynamicLedSerialPortResult = "invalid env port=" + requestedLedSerialPort;
            Log("LED_SERIAL_DYNAMIC_FAIL " + dynamicLedSerialPortResult);
            return;
        }

        Type t = FindLightManagerType();

        if (t == null)
        {
            dynamicLedSerialPortResult = "LightManager type not found in Awake";
            Log("LED_SERIAL_DYNAMIC_FAIL " + dynamicLedSerialPortResult);
            return;
        }

        const BindingFlags flags =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        FieldInfo field = t.GetField("p1Serial", flags);

        if (field == null)
        {
            dynamicLedSerialPortResult = "LightManager.p1Serial field not found";
            Log("LED_SERIAL_DYNAMIC_FAIL " + dynamicLedSerialPortResult);
            return;
        }

        try
        {
            SerialPort serial = field.GetValue(null) as SerialPort;

            if (serial == null)
            {
                dynamicLedSerialPortResult = "LightManager.p1Serial null";
                Log("LED_SERIAL_DYNAMIC_FAIL " + dynamicLedSerialPortResult);
                return;
            }

            string before = serial.PortName;

            if (serial.IsOpen &&
                !string.Equals(before, requestedLedSerialPort, StringComparison.OrdinalIgnoreCase))
            {
                actualLedSerialPort = before;
                dynamicLedSerialPortResult = "too late: already open on " + before;
                Log("LED_SERIAL_DYNAMIC_FAIL " + dynamicLedSerialPortResult);
                return;
            }

            if (!serial.IsOpen &&
                !string.Equals(before, requestedLedSerialPort, StringComparison.OrdinalIgnoreCase))
            {
                serial.PortName = requestedLedSerialPort;
            }

            actualLedSerialPort = serial.PortName;
            dynamicLedSerialPortApplied = string.Equals(
                actualLedSerialPort,
                requestedLedSerialPort,
                StringComparison.OrdinalIgnoreCase);

            dynamicLedSerialPortResult =
                "before=" + before +
                " requested=" + requestedLedSerialPort +
                " after=" + actualLedSerialPort +
                " isOpen=" + serial.IsOpen +
                " applied=" + dynamicLedSerialPortApplied;

            Log("LED_SERIAL_DYNAMIC " + dynamicLedSerialPortResult);
        }
        catch (Exception ex)
        {
            dynamicLedSerialPortResult = ex.GetType().Name + ":" + ex.Message;
            Log("LED_SERIAL_DYNAMIC_FAIL " + dynamicLedSerialPortResult);
        }
    }

    private void DiscoverLedTelemetry()
    {
        if (lightManagerType == null)
            lightManagerType = FindLightManagerType();

        if (lightManagerType == null)
            return;

        const BindingFlags staticFlags =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        const BindingFlags instanceFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        if (lightP1SerialField == null)
            lightP1SerialField = lightManagerType.GetField("p1Serial", staticFlags);

        if (lightRingLedsField == null)
            lightRingLedsField = lightManagerType.GetField("RingLeds", instanceFlags);

        if (lightBodyLedField == null)
            lightBodyLedField = lightManagerType.GetField("BodyLed", instanceFlags);

        if (lightDisplayLedField == null)
            lightDisplayLedField = lightManagerType.GetField("DisplayLed", instanceFlags);

        if (lightStreamListField == null)
            lightStreamListField = lightManagerType.GetField("dataListStreamP1", instanceFlags);

        if (lightInstantListField == null)
            lightInstantListField = lightManagerType.GetField("dataListInstantP1", instanceFlags);

        if (lightManagerInstance == null)
        {
            try
            {
                UnityEngine.Object[] objects =
                    UnityEngine.Resources.FindObjectsOfTypeAll(lightManagerType);

                if (objects != null)
                {
                    for (int i = 0; i < objects.Length; i++)
                    {
                        Component c = objects[i] as Component;
                        if (c == null || c.gameObject == null)
                            continue;

                        if (c.gameObject.scene.IsValid())
                        {
                            lightManagerInstance = c;
                            break;
                        }
                    }

                    if (lightManagerInstance == null && objects.Length > 0)
                        lightManagerInstance = objects[0];
                }
            }
            catch { }
        }

        bool ready =
            lightP1SerialField != null &&
            lightManagerInstance != null &&
            lightRingLedsField != null;

        if (ready && !ledReflectionReady)
        {
            ledReflectionReady = true;
            Log("LED_REFLECTION_READY type=" + lightManagerType.AssemblyQualifiedName);
        }
    }

    private static int ReadCollectionCount(object value)
    {
        if (value == null)
            return -1;

        try
        {
            PropertyInfo p =
                value.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);

            if (p == null)
                return -1;

            object result = p.GetValue(value, null);
            return result is int ? (int)result : -1;
        }
        catch
        {
            return -1;
        }
    }

    private void PollLedTelemetry()
    {
        DiscoverLedTelemetry();

        if (lightP1SerialField != null)
        {
            try
            {
                SerialPort serial = lightP1SerialField.GetValue(null) as SerialPort;

                if (serial != null)
                {
                    actualLedSerialPort = serial.PortName;
                    ledSerialOpen = serial.IsOpen;
                }
            }
            catch { }
        }

        if (!ledReflectionReady || lightManagerInstance == null)
            return;

        try
        {
            System.Collections.IList ringList =
                lightRingLedsField.GetValue(lightManagerInstance)
                as System.Collections.IList;

            if (ringList != null)
            {
                ledRingCount = ringList.Count;

                if (lastObservedRingColors == null ||
                    lastObservedRingColors.Length != ledRingCount)
                {
                    lastObservedRingColors = new Color32[ledRingCount];

                    for (int i = 0; i < ledRingCount; i++)
                    {
                        Light l = ringList[i] as Light;
                        lastObservedRingColors[i] =
                            l != null ? (Color32)l.color : new Color32();
                    }
                }
                else
                {
                    bool changed = false;
                    StringBuilder sbColors = new StringBuilder();

                    for (int i = 0; i < ledRingCount; i++)
                    {
                        Light l = ringList[i] as Light;
                        if (l == null)
                            continue;

                        Color32 c = (Color32)l.color;

                        if (!c.Equals(lastObservedRingColors[i]))
                        {
                            changed = true;
                            lastObservedRingColors[i] = c;
                        }

                        if (i < 8)
                        {
                            if (sbColors.Length > 0)
                                sbColors.Append(" | ");

                            sbColors.Append(i);
                            sbColors.Append(':');
                            sbColors.Append(c.r.ToString("X2", CultureInfo.InvariantCulture));
                            sbColors.Append(c.g.ToString("X2", CultureInfo.InvariantCulture));
                            sbColors.Append(c.b.ToString("X2", CultureInfo.InvariantCulture));
                        }
                    }

                    ledRingColorSnapshot = sbColors.ToString();

                    if (changed)
                    {
                        ledRingColorChangeSamples++;

                        if (ledRingColorChangeSamples <= 10 ||
                            (ledRingColorChangeSamples % 100) == 0)
                        {
                            Log("LED_RING_CHANGE sample=" + ledRingColorChangeSamples +
                                " colors=" + ledRingColorSnapshot);
                        }
                    }
                }
            }

            Light body = lightBodyLedField != null
                ? lightBodyLedField.GetValue(lightManagerInstance) as Light
                : null;

            Light display = lightDisplayLedField != null
                ? lightDisplayLedField.GetValue(lightManagerInstance) as Light
                : null;

            if (body != null)
            {
                ledBodyCurrentIntensity = body.intensity;

                if (!float.IsNaN(lastObservedBodyIntensity) &&
                    !Mathf.Approximately(lastObservedBodyIntensity, ledBodyCurrentIntensity))
                {
                    ledBodyIntensityChangeSamples++;
                }

                lastObservedBodyIntensity = ledBodyCurrentIntensity;
            }

            if (display != null)
            {
                ledDisplayCurrentIntensity = display.intensity;

                if (!float.IsNaN(lastObservedDisplayIntensity) &&
                    !Mathf.Approximately(lastObservedDisplayIntensity, ledDisplayCurrentIntensity))
                {
                    ledDisplayIntensityChangeSamples++;
                }

                lastObservedDisplayIntensity = ledDisplayCurrentIntensity;
            }

            if (lightStreamListField != null)
                ledStreamQueueCount = ReadCollectionCount(
                    lightStreamListField.GetValue(lightManagerInstance));

            if (lightInstantListField != null)
                ledInstantQueueCount = ReadCollectionCount(
                    lightInstantListField.GetValue(lightManagerInstance));
        }
        catch (Exception ex)
        {
            Log("LED_TELEMETRY_ERROR " + ex.GetType().Name + ":" + ex.Message);
        }
    }

    private static float ButtonRendererDistanceToLight(
        Renderer renderer,
        Vector3 position)
    {
        if (renderer == null)
            return float.MaxValue;

        try
        {
            return Vector3.Distance(
                renderer.bounds.ClosestPoint(position),
                position);
        }
        catch
        {
            return float.MaxValue;
        }
    }

    private Renderer FindNearestButtonRenderer(
        Vector3 lightPosition,
        HashSet<int> alreadyUsed)
    {
        Renderer[] all = Resources.FindObjectsOfTypeAll<Renderer>();

        Renderer best = null;
        float bestDistance = ButtonRendererSearchRadiusMeters;

        for (int i = 0; i < all.Length; i++)
        {
            Renderer renderer = all[i];

            if (renderer == null ||
                renderer.gameObject == null ||
                !renderer.gameObject.scene.IsValid())
            {
                continue;
            }

            int id = renderer.GetInstanceID();

            if (alreadyUsed.Contains(id))
                continue;

            string path = GetPath(renderer.transform);
            string lower = path.ToLowerInvariant();

            // Never bind the live game displays, UI, hands or rays.
            if (lower.Contains("display p1") ||
                lower.Contains("display p2") ||
                lower.Contains("screen") ||
                lower.Contains("monitor") ||
                lower.Contains("canvas") ||
                lower.Contains("controller") ||
                lower.Contains("hand") ||
                lower.Contains("ray"))
            {
                continue;
            }

            float distance =
                ButtonRendererDistanceToLight(renderer, lightPosition);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = renderer;
            }
        }

        return best;
    }

    private void EnsurePhysicalButtonGlowSetup(
        System.Collections.IList ringList)
    {
        if (buttonGlowSetupDone ||
            ringList == null ||
            ringList.Count < 1)
        {
            return;
        }

        buttonGlowSetupAttempts++;

        List<Light> sourceLights = new List<Light>();

        for (int i = 0; i < ringList.Count; i++)
        {
            Light source = ringList[i] as Light;
            if (source != null)
                sourceLights.Add(source);
        }

        if (sourceLights.Count < 1)
            return;

        Vector3 center = Vector3.zero;

        for (int i = 0; i < sourceLights.Count; i++)
            center += sourceLights[i].transform.position;

        center /= sourceLights.Count;

        HashSet<int> usedRenderers = new HashSet<int>();

        // The old Button Tip spots were aimed radially PARALLEL to the cabinet
        // face, so increasing range/intensity barely illuminated the plastic.
        // Fit the ring plane from the eight source LEDs, move each tip slightly
        // in front of the cabinet, then aim it back onto the OUTER lip directly
        // beyond its button (the green/magenta zone from the V0.3.1.7 test).
        Vector3 ringNormal = Vector3.zero;
        Vector3 firstRadial = Vector3.zero;

        for (int i = 0; i < sourceLights.Count; i++)
        {
            Vector3 r = sourceLights[i].transform.position - center;
            if (r.sqrMagnitude > 0.000001f)
            {
                firstRadial = r.normalized;
                break;
            }
        }

        float bestCrossSq = 0f;
        for (int i = 0; i < sourceLights.Count; i++)
        {
            Vector3 r = sourceLights[i].transform.position - center;
            if (r.sqrMagnitude < 0.000001f || firstRadial.sqrMagnitude < 0.5f)
                continue;

            Vector3 c = Vector3.Cross(firstRadial, r.normalized);
            float sq = c.sqrMagnitude;
            if (sq > bestCrossSq)
            {
                bestCrossSq = sq;
                ringNormal = c;
            }
        }

        if (ringNormal.sqrMagnitude < 0.0001f)
            ringNormal = sourceLights[0].transform.forward;

        ringNormal.Normalize();

        if (hmdCamera != null)
        {
            Vector3 toViewer = hmdCamera.transform.position - center;
            if (Vector3.Dot(ringNormal, toViewer) < 0f)
                ringNormal = -ringNormal;
        }

        for (int i = 0; i < sourceLights.Count; i++)
        {
            Light source = sourceLights[i];

            Vector3 radial =
                source.transform.position - center;

            if (radial.sqrMagnitude < 0.000001f)
                radial = source.transform.right;
            else
                radial.Normalize();

            GameObject haloObject =
                new GameObject("MaiMaiVR_ButtonHalo_" + i);

            haloObject.transform.SetParent(
                source.transform,
                true);

            Vector3 tipPosition =
                source.transform.position +
                radial * 0.018f +
                ringNormal * 0.050f;

            Vector3 outerCabinetTarget =
                source.transform.position +
                radial * ButtonHaloOutwardOffsetMeters -
                ringNormal * 0.006f;

            Vector3 tipDirection =
                outerCabinetTarget - tipPosition;

            if (tipDirection.sqrMagnitude < 0.000001f)
                tipDirection = radial - ringNormal;

            haloObject.transform.position = tipPosition;
            haloObject.transform.rotation =
                Quaternion.LookRotation(tipDirection.normalized, radial);

            Light halo = haloObject.AddComponent<Light>();
            halo.type = LightType.Spot;
            halo.spotAngle = 72f;
            halo.color = source.color;
            halo.intensity = ButtonHaloIntensity;
            halo.range = ButtonHaloRangeMeters;
            halo.shadows = LightShadows.None;
            halo.renderMode = LightRenderMode.ForcePixel;
            halo.bounceIntensity = 0f;

            buttonHaloLights.Add(halo);
            buttonHaloLightsCreated++;

            // V0.6.1: NEVER bind RingLed colours to arbitrary nearby renderers.
            // The old nearest-renderer heuristic bound LEDs to BG, Body P1,
            // Ring and even hitboxes. That is why the whole cabinet appeared
            // attached to Button Emission. AMBIANT V0.6.1 now binds the visible
            // native "Button Base" material (with non-hitbox Button fallback); these legacy lists are
            // retained only for status compatibility with V0.3.0.8.
            buttonGlowRenderers.Add(null);
            buttonGlowMaterials.Add(null);

            buttonGlowLastBinding =
                "index=" + i +
                " source=" + GetPath(source.transform) +
                " renderer=<MATERIAL_BINDING_DISABLED>";

            Log("BUTTON_GLOW_BIND " + buttonGlowLastBinding);
        }

        buttonGlowSetupDone =
            buttonHaloLightsCreated == sourceLights.Count;

        Log(
            "BUTTON_GLOW_SETUP done=" + buttonGlowSetupDone +
            " sourceLights=" + sourceLights.Count +
            " halos=" + buttonHaloLightsCreated +
            " renderers=" + buttonGlowRenderersBound +
            " emissionMaterials=" + buttonGlowMaterialsBound);
    }

    private void UpdatePhysicalButtonGlow()
    {
        if (!ledReflectionReady ||
            lightManagerInstance == null ||
            lightRingLedsField == null)
        {
            return;
        }

        System.Collections.IList ringList = null;

        try
        {
            ringList =
                lightRingLedsField.GetValue(lightManagerInstance)
                as System.Collections.IList;
        }
        catch
        {
            return;
        }

        if (ringList == null || ringList.Count < 1)
            return;

        EnsurePhysicalButtonGlowSetup(ringList);

        int count = Mathf.Min(
            ringList.Count,
            buttonHaloLights.Count);

        for (int i = 0; i < count; i++)
        {
            Light source = ringList[i] as Light;
            Light halo = buttonHaloLights[i];

            if (source == null || halo == null)
                continue;

            // The physical tip spot and the visible button cap share this exact source LED hue.
            // Button face emission is handled by AMBIANT's exact native
            // "Button" material binding so Ring/Button Base stay untouched.
            halo.color = source.color;
            halo.enabled =
                source.enabled &&
                source.color.maxColorComponent > 0.005f;
        }
    }

    private static Transform FindCommonAncestor(List<Light> lights)
    {
        if (lights == null || lights.Count < 1 || lights[0] == null)
            return null;

        Transform candidate = lights[0].transform;

        while (candidate != null)
        {
            bool allInside = true;

            for (int i = 1; i < lights.Count; i++)
            {
                Light light = lights[i];

                if (light == null || !light.transform.IsChildOf(candidate))
                {
                    allInside = false;
                    break;
                }
            }

            if (allInside)
                return candidate;

            candidate = candidate.parent;
        }

        return null;
    }

    private static bool TryGetMaterialBaseColor(
        Material material,
        out Color color,
        out string property)
    {
        color = Color.white;
        property = null;

        if (material == null)
            return false;

        try
        {
            if (material.HasProperty("_BaseColor"))
            {
                color = material.GetColor("_BaseColor");
                property = "_BaseColor";
                return true;
            }

            if (material.HasProperty("_Color"))
            {
                color = material.GetColor("_Color");
                property = "_Color";
                return true;
            }
        }
        catch { }

        return false;
    }

    private static bool RealCabinetSetEmission(
        Material material,
        Color emission)
    {
        if (material == null || !material.HasProperty("_EmissionColor"))
            return false;

        try
        {
            float maxComponent =
                Mathf.Max(emission.r, Mathf.Max(emission.g, emission.b));

            if (maxComponent > 0.10f && maxComponent > 0.0001f)
            {
                float scale = 0.10f / maxComponent;
                emission *= scale;
            }

            emission.a = 1.0f;
            material.SetColor("_EmissionColor", emission);
            material.EnableKeyword("_EMISSION");
            material.globalIlluminationFlags =
                MaterialGlobalIlluminationFlags.RealtimeEmissive;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyCabinetMaterialEmission(
        Transform cabinetRoot,
        Vector3 ringCenter,
        float ringRadius)
    {
        if (cabinetRoot == null)
            return;

        Renderer[] renderers =
            cabinetRoot.GetComponentsInChildren<Renderer>(true);

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];

            if (renderer == null || renderer.gameObject == null)
                continue;

            string path = GetPath(renderer.transform);
            string lower = path.ToLowerInvariant();

            if (lower.Contains("display p1") ||
                lower.Contains("display p2") ||
                lower.Contains("screen") ||
                lower.Contains("monitor") ||
                lower.Contains("canvas") ||
                lower.Contains("controller") ||
                lower.Contains("hand") ||
                lower.Contains("ray"))
            {
                continue;
            }

            float distanceToRing =
                ButtonRendererDistanceToLight(renderer, ringCenter);

            bool inDarkRingZone =
                distanceToRing < ringRadius + 0.24f;

            Material[] materials;

            try
            {
                materials = renderer.materials;
            }
            catch
            {
                continue;
            }

            for (int m = 0; m < materials.Length; m++)
            {
                Material material = materials[m];
                Color baseColor;
                string colorProperty;

                if (!TryGetMaterialBaseColor(
                        material,
                        out baseColor,
                        out colorProperty))
                {
                    continue;
                }

                float h;
                float s;
                float v;
                Color.RGBToHSV(baseColor, out h, out s, out v);

                // The button/ring plastics stay dark at the requested 20%.
                // We only add a tiny body self-light to bright neutral shell
                // pieces outside the circular ring itself.
                if (!inDarkRingZone &&
                    v >= 0.45f &&
                    s <= 0.20f)
                {
                    try
                    {
                        Color whitePlastic = baseColor * CabinetWhitePlasticBoost;

                        whitePlastic.r = Mathf.Min(
                            whitePlastic.r,
                            CabinetWhitePlasticMax);

                        whitePlastic.g = Mathf.Min(
                            whitePlastic.g,
                            CabinetWhitePlasticMax);

                        whitePlastic.b = Mathf.Min(
                            whitePlastic.b,
                            CabinetWhitePlasticMax);

                        whitePlastic.a = baseColor.a;

                        if (!string.IsNullOrEmpty(colorProperty))
                            material.SetColor(colorProperty, whitePlastic);

                        // Explicitly kill the extra shell emission used by
                        // V0.3.0.5/6 so bloom cannot turn the cabinet white/pink.
                        if (material.HasProperty("_EmissionColor"))
                        {
                            material.SetColor("_EmissionColor", Color.black);
                            material.DisableKeyword("_EMISSION");
                        }

                        cabinetWhiteEmissionMaterials++;
                    }
                    catch { }

                    continue;
                }

                // Real cabinets have colored luminous side/top accents.
                // Make existing saturated cyan/blue or red/orange parts glow
                // if the shader supports emission. This is intentionally based
                // on the actual material color, not on object names.
                bool cyanBlue =
                    s >= 0.40f &&
                    v >= 0.30f &&
                    h >= 0.45f &&
                    h <= 0.62f;

                bool redOrange =
                    s >= 0.45f &&
                    v >= 0.30f &&
                    (h <= 0.10f || h >= 0.95f);

                if (cyanBlue || redOrange)
                {
                    Color accentEmission =
                        baseColor * CabinetAccentEmissionMultiplier;

                    float accentMax = Mathf.Max(
                        accentEmission.r,
                        Mathf.Max(accentEmission.g, accentEmission.b));

                    if (accentMax > AccentEmissionMax && accentMax > 0.0001f)
                        accentEmission *= AccentEmissionMax / accentMax;

                    if (RealCabinetSetEmission(
                            material,
                            accentEmission))
                    {
                        cabinetAccentEmissionMaterials++;
                    }
                }
            }
        }
    }

    private void CreateRingContourHalo(
        List<Light> sourceLights,
        Vector3 center)
    {
        if (sourceLights == null || sourceLights.Count < 2)
            return;

        Color whiteHalo = new Color(0.95f, 0.97f, 1.0f, 1.0f);

        for (int i = 0; i < sourceLights.Count; i++)
        {
            Light a = sourceLights[i];
            Light b = sourceLights[(i + 1) % sourceLights.Count];

            if (a == null || b == null)
                continue;

            Vector3 midpoint =
                (a.transform.position + b.transform.position) * 0.5f;

            Vector3 radial = midpoint - center;

            if (radial.sqrMagnitude > 0.000001f)
                radial.Normalize();
            else
                radial = Vector3.up;

            GameObject go =
                new GameObject("MaiMaiVR_RingContourHalo_" + i);

            go.transform.position =
                midpoint +
                radial * RingContourRadialOffsetMeters;

            go.transform.rotation =
                Quaternion.LookRotation(radial, Vector3.up);

            Light halo = go.AddComponent<Light>();
            halo.type = LightType.Spot;
            halo.spotAngle = 88f;
            halo.color = whiteHalo;
            halo.intensity = RingContourHaloIntensity;
            halo.range = RingContourHaloRangeMeters;
            halo.shadows = LightShadows.None;
            halo.bounceIntensity = 0f;
            halo.renderMode = LightRenderMode.ForcePixel;

            ringContourHaloLights.Add(halo);
            ringContourHaloLightsCreated++;
        }
    }

    private Light CreateCabinetAccentLight(
        string name,
        Vector3 position,
        Vector3 direction,
        Color color,
        float intensity,
        float range)
    {
        GameObject go = new GameObject(name);
        go.transform.position = position;

        if (direction.sqrMagnitude < 0.000001f)
            direction = Vector3.forward;

        go.transform.rotation =
            Quaternion.LookRotation(direction.normalized, Vector3.up);

        Light light = go.AddComponent<Light>();
        light.type = LightType.Spot;
        light.spotAngle = 92f;
        light.color = color;
        light.intensity = intensity;
        light.range = range;
        light.shadows = LightShadows.None;
        light.bounceIntensity = 0f;
        light.renderMode = LightRenderMode.ForcePixel;

        cabinetAccentLights.Add(light);
        return light;
    }

    private void CreateCabinetTopAndSideGlow(
        Vector3 center,
        float radius)
    {
        // The physical cabinet is almost vertical in the current MaiDXR room,
        // so world-up/right are a stable visual approximation and deliberately
        // less fragile than trying to infer a mesh normal from imported data.
        Vector3 top =
            center + Vector3.up * (radius + 0.62f);

        Color cyan = new Color(0.08f, 0.55f, 0.62f, 1.0f);
        Color red = new Color(0.62f, 0.08f, 0.06f, 1.0f);
        Color orange = new Color(0.70f, 0.28f, 0.05f, 1.0f);

        CreateCabinetAccentLight(
            "MaiMaiVR_TopGlow_Center",
            top,
            (center - top).normalized,
            cyan,
            TopGlowIntensity,
            TopGlowRangeMeters);
        cabinetTopGlowLightsCreated++;

        CreateCabinetAccentLight(
            "MaiMaiVR_TopGlow_Left",
            top - Vector3.right * 0.34f,
            (center - (top - Vector3.right * 0.34f)).normalized,
            cyan,
            TopGlowIntensity * 0.82f,
            TopGlowRangeMeters * 0.85f);
        cabinetTopGlowLightsCreated++;

        CreateCabinetAccentLight(
            "MaiMaiVR_TopGlow_Right",
            top + Vector3.right * 0.34f,
            (center - (top + Vector3.right * 0.34f)).normalized,
            cyan,
            TopGlowIntensity * 0.82f,
            TopGlowRangeMeters * 0.85f);
        cabinetTopGlowLightsCreated++;

        Vector3 leftSide =
            center - Vector3.right * (radius + 0.48f) +
            Vector3.up * 0.34f;

        Vector3 rightSide =
            center + Vector3.right * (radius + 0.48f) +
            Vector3.up * 0.34f;

        CreateCabinetAccentLight(
            "MaiMaiVR_SideGlow_LeftRed",
            leftSide,
            (leftSide - center).normalized,
            red,
            SideGlowIntensity,
            SideGlowRangeMeters);
        cabinetSideGlowLightsCreated++;

        CreateCabinetAccentLight(
            "MaiMaiVR_SideGlow_RightOrange",
            rightSide,
            (rightSide - center).normalized,
            orange,
            SideGlowIntensity,
            SideGlowRangeMeters);
        cabinetSideGlowLightsCreated++;
    }

    private void EnsureRealCabinetLightingSetup(
        System.Collections.IList ringList)
    {
        if (realCabinetSetupDone || ringList == null || ringList.Count < 4)
            return;

        realCabinetSetupAttempts++;

        List<Light> sourceLights = new List<Light>();

        for (int i = 0; i < ringList.Count; i++)
        {
            Light source = ringList[i] as Light;
            if (source != null)
                sourceLights.Add(source);
        }

        if (sourceLights.Count < 4)
            return;

        Vector3 center = Vector3.zero;

        for (int i = 0; i < sourceLights.Count; i++)
            center += sourceLights[i].transform.position;

        center /= sourceLights.Count;

        float radius = 0f;

        for (int i = 0; i < sourceLights.Count; i++)
        {
            radius += Vector3.Distance(
                sourceLights[i].transform.position,
                center);
        }

        radius /= sourceLights.Count;

        Transform common = FindCommonAncestor(sourceLights);
        Transform cabinet = common;

        // Climb a little until we have enough geometry to include the body,
        // but never walk all the way to an entire scene root by brute force.
        for (int step = 0; step < 4 && cabinet != null; step++)
        {
            Renderer[] rr =
                cabinet.GetComponentsInChildren<Renderer>(true);

            if (rr != null && rr.Length >= 12)
                break;

            cabinet = cabinet.parent;
        }

        realCabinetRoot = cabinet != null ? cabinet : common;
        realCabinetRootPath =
            realCabinetRoot != null
                ? GetPath(realCabinetRoot)
                : "<none>";

        realCabinetRingCenter = center;
        realCabinetRingRadius = radius;

        ApplyCabinetMaterialEmission(
            realCabinetRoot,
            center,
            radius);

        CreateRingContourHalo(
            sourceLights,
            center);

        CreateCabinetTopAndSideGlow(
            center,
            radius);

        realCabinetSetupDone =
            ringContourHaloLightsCreated >= 4 &&
            cabinetTopGlowLightsCreated >= 1;

        Log(
            "REAL_CABINET_SETUP done=" + realCabinetSetupDone +
            " root=" + realCabinetRootPath +
            " radius=" + radius.ToString("F3", CultureInfo.InvariantCulture) +
            " whiteEmissionMaterials=" + cabinetWhiteEmissionMaterials +
            " accentEmissionMaterials=" + cabinetAccentEmissionMaterials +
            " contourHalos=" + ringContourHaloLightsCreated +
            " topLights=" + cabinetTopGlowLightsCreated +
            " sideLights=" + cabinetSideGlowLightsCreated);
    }

    private void UpdateRealCabinetLighting()
    {
        if (!ledReflectionReady ||
            lightManagerInstance == null ||
            lightRingLedsField == null)
        {
            return;
        }

        System.Collections.IList ringList = null;

        try
        {
            ringList =
                lightRingLedsField.GetValue(lightManagerInstance)
                as System.Collections.IList;
        }
        catch
        {
            return;
        }

        if (ringList == null || ringList.Count < 1)
            return;

        EnsureRealCabinetLightingSetup(ringList);

        // White perimeter is intentionally stable like the real cabinet;
        // button colors remain fully dynamic and are handled separately by
        // UpdatePhysicalButtonGlow().
        for (int i = 0; i < ringContourHaloLights.Count; i++)
        {
            Light halo = ringContourHaloLights[i];
            if (halo == null)
                continue;

            halo.enabled = true;
            halo.color = new Color(0.72f, 0.78f, 0.86f, 1.0f);
        }
    }

    private Type FindSerialManagerType()
    {
        try
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    Type t = assemblies[i].GetType("SerialManager", false);
                    if (t != null)
                        return t;
                }
                catch { }
            }
        }
        catch { }

        return null;
    }

    private void ApplyDynamicP1SerialPort()
    {
        string envPort = Environment.GetEnvironmentVariable("MAIMAIVR_TOUCH_P1_PORT");

        if (!string.IsNullOrWhiteSpace(envPort))
            requestedP1SerialPort = envPort.Trim().ToUpperInvariant();

        if (!requestedP1SerialPort.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
        {
            dynamicP1SerialPortResult = "invalid env port=" + requestedP1SerialPort;
            Log("P1_SERIAL_DYNAMIC_FAIL " + dynamicP1SerialPortResult);
            return;
        }

        Type t = FindSerialManagerType();

        if (t == null)
        {
            dynamicP1SerialPortResult = "SerialManager type not found in Awake";
            Log("P1_SERIAL_DYNAMIC_FAIL " + dynamicP1SerialPortResult);
            return;
        }

        const BindingFlags flags =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        FieldInfo p1Field = t.GetField("p1Serial", flags);

        if (p1Field == null)
        {
            dynamicP1SerialPortResult = "p1Serial field not found";
            Log("P1_SERIAL_DYNAMIC_FAIL " + dynamicP1SerialPortResult);
            return;
        }

        object serial = null;

        try
        {
            serial = p1Field.GetValue(null);
        }
        catch (Exception ex)
        {
            dynamicP1SerialPortResult =
                "p1Serial get failed " + ex.GetType().Name + ":" + ex.Message;
            Log("P1_SERIAL_DYNAMIC_FAIL " + dynamicP1SerialPortResult);
            return;
        }

        if (serial == null)
        {
            dynamicP1SerialPortResult = "p1Serial object null";
            Log("P1_SERIAL_DYNAMIC_FAIL " + dynamicP1SerialPortResult);
            return;
        }

        try
        {
            Type serialType = serial.GetType();
            PropertyInfo isOpenProperty = serialType.GetProperty("IsOpen");
            PropertyInfo portNameProperty = serialType.GetProperty("PortName");

            if (portNameProperty == null || !portNameProperty.CanWrite)
            {
                dynamicP1SerialPortResult = "SerialPort.PortName unavailable";
                Log("P1_SERIAL_DYNAMIC_FAIL " + dynamicP1SerialPortResult);
                return;
            }

            bool isOpen = false;

            if (isOpenProperty != null)
            {
                object openValue = isOpenProperty.GetValue(serial, null);
                if (openValue is bool)
                    isOpen = (bool)openValue;
            }

            object beforeObj = portNameProperty.GetValue(serial, null);
            string before = beforeObj != null ? beforeObj.ToString() : "<null>";

            if (isOpen && !string.Equals(
                before,
                requestedP1SerialPort,
                StringComparison.OrdinalIgnoreCase))
            {
                dynamicP1SerialPortResult =
                    "too late: p1Serial already open on " + before;
                actualP1SerialPort = before;
                Log("P1_SERIAL_DYNAMIC_FAIL " + dynamicP1SerialPortResult);
                return;
            }

            if (!string.Equals(
                before,
                requestedP1SerialPort,
                StringComparison.OrdinalIgnoreCase))
            {
                portNameProperty.SetValue(serial, requestedP1SerialPort, null);
            }

            object afterObj = portNameProperty.GetValue(serial, null);
            actualP1SerialPort = afterObj != null ? afterObj.ToString() : "<null>";

            dynamicP1SerialPortApplied = string.Equals(
                actualP1SerialPort,
                requestedP1SerialPort,
                StringComparison.OrdinalIgnoreCase);

            dynamicP1SerialPortResult =
                "before=" + before +
                " requested=" + requestedP1SerialPort +
                " after=" + actualP1SerialPort +
                " isOpen=" + isOpen +
                " applied=" + dynamicP1SerialPortApplied;

            Log("P1_SERIAL_DYNAMIC " + dynamicP1SerialPortResult);
        }
        catch (Exception ex)
        {
            dynamicP1SerialPortResult =
                ex.GetType().Name + ":" + ex.Message;

            Log("P1_SERIAL_DYNAMIC_FAIL " + dynamicP1SerialPortResult);
        }
    }

    private void EnsureContinuousTouchThread()
    {
        if (continuousTouchThreadStarted)
            return;

        if (!serialReflectionReady ||
            serialP1Field == null ||
            serialStartUpField == null ||
            serialTouchDataField == null)
            return;

        continuousTouchThreadRunning = true;
        continuousTouchThread = new Thread(ContinuousTouchThreadProc);
        continuousTouchThread.IsBackground = true;
        continuousTouchThread.Name = "MaiMaiVR Continuous P1+P2 Touch";
        continuousTouchThread.Start();
        continuousTouchThreadStarted = true;

        Log("CONTINUOUS_TOUCH_THREAD_STARTED players=P1+P2 intervalMs=" + ContinuousTouchIntervalMs);
    }

    private void ContinuousTouchThreadProc()
    {
        byte[] packetP1 = new byte[9];
        byte[] packetP2 = new byte[9];

        while (continuousTouchThreadRunning)
        {
            bool startup = false;

            try
            {
                object startupObj = serialStartUpField != null
                    ? serialStartUpField.GetValue(null)
                    : null;

                startup = startupObj is bool && (bool)startupObj;
            }
            catch { }

            if (startup)
            {
                // P1 -- validated path from V0.2.3. Keep behavior unchanged.
                try
                {
                    SerialPort serialP1 = serialP1Field != null
                        ? serialP1Field.GetValue(null) as SerialPort
                        : null;

                    if (serialP1 != null && serialP1.IsOpen)
                    {
                        byte[] liveDataP1 = serialTouchDataField != null
                            ? serialTouchDataField.GetValue(null) as byte[]
                            : null;

                        if (liveDataP1 != null && liveDataP1.Length >= 9)
                        {
                            Buffer.BlockCopy(liveDataP1, 0, packetP1, 0, 9);
                            serialP1.Write(packetP1, 0, 9);

                            Interlocked.Increment(ref continuousTouchWrites);
                            continuousTouchEverActive = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref continuousTouchWriteErrors);
                    continuousTouchLastError =
                        ex.GetType().Name + ":" + ex.Message;
                }

                // P2 -- armed for future use. It only writes when upstream p2Serial
                // is actually open, so current validated P1-only IO setup is untouched.
                try
                {
                    SerialPort serialP2 = serialP2Field != null
                        ? serialP2Field.GetValue(null) as SerialPort
                        : null;

                    if (serialP2 != null && serialP2.IsOpen)
                    {
                        byte[] liveDataP2 = serialTouchData2Field != null
                            ? serialTouchData2Field.GetValue(null) as byte[]
                            : null;

                        if (liveDataP2 != null && liveDataP2.Length >= 9)
                        {
                            Buffer.BlockCopy(liveDataP2, 0, packetP2, 0, 9);
                            serialP2.Write(packetP2, 0, 9);

                            Interlocked.Increment(ref continuousTouchWritesP2);
                            continuousTouchP2EverActive = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref continuousTouchWriteErrorsP2);
                    continuousTouchLastErrorP2 =
                        ex.GetType().Name + ":" + ex.Message;
                }
            }

            Thread.Sleep(ContinuousTouchIntervalMs);
        }
    }

    private void StopContinuousTouchThread()
    {
        continuousTouchThreadRunning = false;

        try
        {
            if (continuousTouchThread != null && continuousTouchThread.IsAlive)
                continuousTouchThread.Join(500);
        }
        catch { }
    }

    private void UpdateContinuousTouchRate()
    {
        int nowP1 = Interlocked.CompareExchange(ref continuousTouchWrites, 0, 0);
        continuousTouchWritesPerSecond = nowP1 - continuousTouchLastRateCount;
        continuousTouchLastRateCount = nowP1;

        int nowP2 = Interlocked.CompareExchange(ref continuousTouchWritesP2, 0, 0);
        continuousTouchWritesPerSecondP2 = nowP2 - continuousTouchLastRateCountP2;
        continuousTouchLastRateCountP2 = nowP2;
    }

    private void DiscoverSerialTelemetry()
    {
        if (serialReflectionReady)
            return;

        Type found = FindSerialManagerType();

        if (found == null)
            return;

        const BindingFlags flags =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        serialP1Field = found.GetField("p1Serial", flags);
        serialP2Field = found.GetField("p2Serial", flags);
        serialStartUpField = found.GetField("startUp", flags);
        serialTouchDataField = found.GetField("touchData", flags);
        serialTouchData2Field = found.GetField("touchData2", flags);

        serialManagerType = found;
        serialReflectionReady =
            serialP1Field != null &&
            serialStartUpField != null &&
            serialTouchDataField != null;

        if (serialReflectionReady)
        {
            Log("SERIAL_REFLECTION_READY type=" + found.AssemblyQualifiedName +
                " p1Field=" + (serialP1Field != null) +
                " p2Field=" + (serialP2Field != null) +
                " startUpField=" + (serialStartUpField != null) +
                " touchDataField=" + (serialTouchDataField != null) +
                " touchData2Field=" + (serialTouchData2Field != null));
        }
    }

    private static bool ReadIsOpen(object serialObject)
    {
        if (serialObject == null)
            return false;

        try
        {
            PropertyInfo p = serialObject.GetType().GetProperty(
                "IsOpen",
                BindingFlags.Instance | BindingFlags.Public);

            if (p == null)
                return false;

            object value = p.GetValue(serialObject, null);
            return value is bool && (bool)value;
        }
        catch
        {
            return false;
        }
    }

    private static string BytesToHex(byte[] data)
    {
        if (data == null)
            return "<null>";

        StringBuilder sb = new StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static bool ByteArraysEqual(byte[] a, byte[] b)
    {
        if (ReferenceEquals(a, b))
            return true;

        if (a == null || b == null || a.Length != b.Length)
            return false;

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        return true;
    }

    private void PollSerialTelemetry()
    {
        DiscoverSerialTelemetry();

        if (!serialReflectionReady)
            return;

        try
        {
            object p1 = serialP1Field.GetValue(null);
            object p2 = serialP2Field != null ? serialP2Field.GetValue(null) : null;

            if (p1 != null)
            {
                try
                {
                    PropertyInfo portProp = p1.GetType().GetProperty("PortName");
                    if (portProp != null)
                    {
                        object value = portProp.GetValue(p1, null);
                        if (value != null)
                            actualP1SerialPort = value.ToString();
                    }
                }
                catch { }
            }

            bool p1Open = ReadIsOpen(p1);
            bool p2Open = ReadIsOpen(p2);

            object startupObj = serialStartUpField.GetValue(null);
            bool startup = startupObj is bool && (bool)startupObj;

            byte[] liveData = serialTouchDataField.GetValue(null) as byte[];
            byte[] snapshot = null;

            if (liveData != null)
            {
                snapshot = new byte[liveData.Length];
                Buffer.BlockCopy(liveData, 0, snapshot, 0, liveData.Length);
                touchDataP1Hex = BytesToHex(snapshot);
            }

            byte[] liveDataP2 = serialTouchData2Field != null
                ? serialTouchData2Field.GetValue(null) as byte[]
                : null;

            byte[] snapshotP2 = null;

            if (liveDataP2 != null)
            {
                snapshotP2 = new byte[liveDataP2.Length];
                Buffer.BlockCopy(liveDataP2, 0, snapshotP2, 0, liveDataP2.Length);
                touchDataP2Hex = BytesToHex(snapshotP2);
            }

            if (!serialStateInitialized)
            {
                serialStateInitialized = true;
                serialP1OpenCurrent = p1Open;
                serialP2OpenCurrent = p2Open;
                serialStartUpCurrent = startup;
                lastTouchDataP1 = snapshot;
                lastTouchDataP2 = snapshotP2;

                Log("SERIAL_INITIAL p1Open=" + p1Open +
                    " p2Open=" + p2Open +
                    " startUp=" + startup +
                    " touchDataP1=" + touchDataP1Hex +
                    " touchDataP2=" + touchDataP2Hex);
            }
            else
            {
                if (p1Open != serialP1OpenCurrent)
                {
                    serialP1OpenCurrent = p1Open;
                    Log("SERIAL_P1_OPEN_CHANGE=" + p1Open);
                }

                if (p2Open != serialP2OpenCurrent)
                {
                    serialP2OpenCurrent = p2Open;
                    Log("SERIAL_P2_OPEN_CHANGE=" + p2Open);
                }

                if (startup != serialStartUpCurrent)
                {
                    serialStartUpCurrent = startup;
                    Log("SERIAL_STARTUP_CHANGE=" + startup);
                }

                if (!ByteArraysEqual(lastTouchDataP1, snapshot))
                {
                    touchDataP1ChangeCount++;
                    lastTouchDataP1 = snapshot;
                    Log("TOUCH_DATA_P1_CHANGE count=" + touchDataP1ChangeCount +
                        " hex=" + touchDataP1Hex);
                }

                if (!ByteArraysEqual(lastTouchDataP2, snapshotP2))
                {
                    touchDataP2ChangeCount++;
                    lastTouchDataP2 = snapshotP2;
                    Log("TOUCH_DATA_P2_CHANGE count=" + touchDataP2ChangeCount +
                        " hex=" + touchDataP2Hex);
                }
            }

            if (p1Open) serialP1OpenSeen = true;
            if (p2Open) serialP2OpenSeen = true;
            if (startup) serialStartUpSeen = true;
        }
        catch (Exception ex)
        {
            Log("SERIAL_TELEMETRY_ERROR " +
                ex.GetType().Name + ":" + ex.Message);
        }
    }

    private void WriteStatus()
    {
        try
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("MaiMaiVR Integrated Baseline V0.6.6");
            sb.AppendLine("Time=" + DateTime.Now.ToString("O"));
            sb.AppendLine("LeftStick=" + (leftStick != null));
            sb.AppendLine("LeftStickClick=" + (leftStickClick != null));
            sb.AppendLine("CoinInput=LEFT_STICK_CLICK_ONLY");
            sb.AppendLine("CoinVirtualKey=0x" + coinVirtualKey.ToString("X2", CultureInfo.InvariantCulture));
            sb.AppendLine("CoinStickPresses=" + coinStickPresses);
            sb.AppendLine("CoinKeyDownCount=" + coinKeyDownCount);
            sb.AppendLine("CoinKeyUpCount=" + coinKeyUpCount);
            sb.AppendLine("CoinSendErrors=" + coinSendErrors);
            sb.AppendLine("CoinLastError=" + coinLastError);
            sb.AppendLine("CoinBridge=ELEVATED_SESSION_FILE_BRIDGE");
            sb.AppendLine("CoinRequestPath=" + coinRequestPath);
            sb.AppendLine("CoinRequestWriteCount=" + coinRequestWriteCount);
            sb.AppendLine("CoinRequestWriteErrors=" + coinRequestWriteErrors);
            sb.AppendLine("EscapeCloseAllRequested=" + escapeCloseAllRequested);
            sb.AppendLine("EscapeCloseAllCount=" + escapeCloseAllCount);
            sb.AppendLine("EscapeCloseAllMarkerPath=" + escapeCloseAllMarkerPath);
            sb.AppendLine("ArcadeNightApplied=" + arcadeNightApplied);
            sb.AppendLine("ArcadeNightAmbientMultiplier=" + ArcadeNightAmbientMultiplier.ToString("F2", CultureInfo.InvariantCulture));
            sb.AppendLine("ArcadeNightDirectionalMultiplier=" + ArcadeNightDirectionalMultiplier.ToString("F2", CultureInfo.InvariantCulture));
            sb.AppendLine("ArcadeNightReflectionMultiplier=" + ArcadeNightReflectionMultiplier.ToString("F2", CultureInfo.InvariantCulture));
            sb.AppendLine("ArcadeNightDirectionalLightsDimmed=" + arcadeNightDirectionalLightsDimmed);
            sb.AppendLine("ArcadeNightMaterialMultiplier=" + ArcadeNightMaterialMultiplier.ToString("F2", CultureInfo.InvariantCulture));
            sb.AppendLine("ArcadeNightRenderersScanned=" + arcadeNightRenderersScanned);
            sb.AppendLine("ArcadeNightRenderersDimmed=" + arcadeNightRenderersDimmed);
            sb.AppendLine("ArcadeNightMaterialsDimmed=" + arcadeNightMaterialsDimmed);
            sb.AppendLine("ArcadeNightRenderersSkippedProtected=" + arcadeNightRenderersSkippedProtected);
            sb.AppendLine("ArcadeNightRenderersSkippedGlow=" + arcadeNightRenderersSkippedGlow);
            sb.AppendLine("RightStick=" + (rightStick != null));
            sb.AppendLine("RightTrigger=" + (rightTriggerPressed != null || rightTrigger != null));
            sb.AppendLine("XRRoot=" + (xrRoot != null ? GetPath(xrRoot) : "<none>"));
            sb.AppendLine("HMDCamera=" + (hmdCamera != null ? GetPath(hmdCamera.transform) : "<none>"));
            sb.AppendLine("MovementMethod=" + movementMethod);
            sb.AppendLine("DisabledLegacyMoveProviders=" + disabledMoveProviders);
            sb.AppendLine("DisabledLegacyTurnProviders=" + disabledTurnProviders);
            sb.AppendLine("ScaleGuardEntries=" + guardedTransforms.Count);
            sb.AppendLine("ScaleRestores=" + scaleRestores);
            sb.AppendLine("CameraScaleRestores=" + cameraScaleRestores);
            sb.AppendLine("DisplayGeometryOverscan=" + DisplayGeometryOverscan.ToString("F2", CultureInfo.InvariantCulture));
            sb.AppendLine("DisplayGeometryFixedCount=" + displayGeometryFixedCount);
            sb.AppendLine("P1DisplayMaterials=" + p1DisplayMaterials.Count);
            sb.AppendLine("P1SourceOffsetX=" + P1SourceOffsetX.ToString("F8", CultureInfo.InvariantCulture));
            sb.AppendLine("P1SourceScaleX=" + P1SourceScaleX.ToString("F8", CultureInfo.InvariantCulture));
            sb.AppendLine("P2DisplayMaterials=" + p2DisplayMaterials.Count);
            sb.AppendLine("P2SourceOffsetX=" + P2SourceOffsetX.ToString("F8", CultureInfo.InvariantCulture));
            sb.AppendLine("P2SourceScaleX=" + P2SourceScaleX.ToString("F8", CultureInfo.InvariantCulture));
            sb.AppendLine("DisplayLiftMeters=" + DisplayLiftMeters.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("P1CropApplyCount=" + p1CropApplyCount);
            sb.AppendLine("P1DisplayLiftCount=" + p1DisplayLiftCount);
            sb.AppendLine("P2CropApplyCount=" + p2CropApplyCount);
            sb.AppendLine("P2DisplayLiftCount=" + p2DisplayLiftCount);
            sb.AppendLine("SharedLiveGameTexture=" + sharedLiveGameTextureInfo);
            sb.AppendLine("P2TextureShareApplyCount=" + p2TextureShareApplyCount);
            sb.AppendLine("P2TextureShareChangeCount=" + p2TextureShareChangeCount);
            sb.AppendLine("VisualBaseline=V0.1.9_FROZEN");
            SurfaceContactStatus(sb);
            sb.AppendLine("P1TouchBaseline=V0.2.3_VALIDATED");
            sb.AppendLine("TouchHaptics=V0.2.5_VALIDATED");
            sb.AppendLine("HapticOnsetAmplitude=" + TouchHapticOnsetAmplitude.ToString("F2", CultureInfo.InvariantCulture));
            sb.AppendLine("HapticOnsetDuration=" + TouchHapticOnsetDuration.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("HapticSustainAmplitude=" + TouchHapticSustainAmplitude.ToString("F2", CultureInfo.InvariantCulture));
            sb.AppendLine("HapticSustainDuration=" + TouchHapticSustainDuration.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("HapticFrequency=" + TouchHapticFrequency.ToString("F1", CultureInfo.InvariantCulture));
            sb.AppendLine("HapticReleaseGraceSeconds=" + TouchHapticReleaseGraceSeconds.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("HapticObserversInstalled=" + hapticObserversInstalled);
            sb.AppendLine("LegacyHapticManagersDisabled=" + legacyHapticManagersDisabled);
            sb.AppendLine("LeftHapticHand=" + (leftTouchHapticCollider != null ? GetPath(leftTouchHapticCollider.transform) : "<none>"));
            sb.AppendLine("RightHapticHand=" + (rightTouchHapticCollider != null ? GetPath(rightTouchHapticCollider.transform) : "<none>"));
            sb.AppendLine("LeftHapticXRDeviceValid=" + leftXRHapticDevice.isValid);
            sb.AppendLine("RightHapticXRDeviceValid=" + rightXRHapticDevice.isValid);
            sb.AppendLine("LeftHapticImpulseSupported=" + leftHapticImpulseSupported);
            sb.AppendLine("RightHapticImpulseSupported=" + rightHapticImpulseSupported);
            sb.AppendLine("LeftHapticContacts=" + leftHapticContacts);
            sb.AppendLine("RightHapticContacts=" + rightHapticContacts);
            sb.AppendLine("LeftHapticEnters=" + leftHapticEnters);
            sb.AppendLine("RightHapticEnters=" + rightHapticEnters);
            sb.AppendLine("LeftHapticExits=" + leftHapticExits);
            sb.AppendLine("RightHapticExits=" + rightHapticExits);
            sb.AppendLine("LeftHapticImpulses=" + leftHapticImpulses);
            sb.AppendLine("RightHapticImpulses=" + rightHapticImpulses);
            sb.AppendLine("LeftHapticSendFailures=" + leftHapticSendFailures);
            sb.AppendLine("RightHapticSendFailures=" + rightHapticSendFailures);
            sb.AppendLine("LeftHapticLastZone=" + leftHapticLastZone);
            sb.AppendLine("RightHapticLastZone=" + rightHapticLastZone);
            sb.AppendLine("LEDPhase=V0.2.6_ACTIVE");
            sb.AppendLine("RequestedLEDSerialPort=" + requestedLedSerialPort);
            sb.AppendLine("ActualLEDSerialPort=" + actualLedSerialPort);
            sb.AppendLine("DynamicLEDSerialPortApplied=" + dynamicLedSerialPortApplied);
            sb.AppendLine("DynamicLEDSerialPortResult=" + dynamicLedSerialPortResult);
            sb.AppendLine("LEDReflectionReady=" + ledReflectionReady);
            sb.AppendLine("LEDSerialOpen=" + ledSerialOpen);
            sb.AppendLine("LEDRingCount=" + ledRingCount);
            sb.AppendLine("LEDRingColorChangeSamples=" + ledRingColorChangeSamples);
            sb.AppendLine("LEDRingColorSnapshot=" + ledRingColorSnapshot);
            sb.AppendLine("LEDBodyIntensity=" + ledBodyCurrentIntensity.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("LEDDisplayIntensity=" + ledDisplayCurrentIntensity.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("LEDBodyIntensityChangeSamples=" + ledBodyIntensityChangeSamples);
            sb.AppendLine("LEDDisplayIntensityChangeSamples=" + ledDisplayIntensityChangeSamples);
            sb.AppendLine("GlobalLightStability=CMD57_PREFIX_SUPPRESSION_NO_PER_FRAME_WRITE");
            sb.AppendLine("StableBodyLedIntensity=" + StableBodyLedIntensity.ToString("F4", CultureInfo.InvariantCulture));
            sb.AppendLine("StableDisplayLedIntensity=" + StableDisplayLedIntensity.ToString("F4", CultureInfo.InvariantCulture));
            sb.AppendLine("GlobalLightCmd57Suppressed=" + globalLightCmd57Suppressed);
            sb.AppendLine("GlobalLightBodyWrites=" + globalLightBodyWrites);
            sb.AppendLine("GlobalLightDisplayWrites=" + globalLightDisplayWrites);
            sb.AppendLine("LEDStreamQueueCount=" + ledStreamQueueCount);
            sb.AppendLine("LEDInstantQueueCount=" + ledInstantQueueCount);
            sb.AppendLine("ButtonGlowMode=MESH_FREE_LOCAL_EMISSION_PLUS_LOCAL_TIP_LIGHT");
            sb.AppendLine("ButtonGlowSetupDone=" + buttonGlowSetupDone);
            sb.AppendLine("ButtonGlowSetupAttempts=" + buttonGlowSetupAttempts);
            sb.AppendLine("ButtonHaloLightsCreated=" + buttonHaloLightsCreated);
            sb.AppendLine("ButtonHaloIntensity=" + ButtonHaloIntensity.ToString("F2", CultureInfo.InvariantCulture));
            sb.AppendLine("ButtonHaloRangeMeters=" + ButtonHaloRangeMeters.ToString("F2", CultureInfo.InvariantCulture));
            sb.AppendLine("ButtonGlowRenderersBound=" + buttonGlowRenderersBound);
            sb.AppendLine("ButtonGlowMaterialsBound=" + buttonGlowMaterialsBound);
            sb.AppendLine("ButtonGlowEmissionUpdates=" + buttonGlowEmissionUpdates);
            sb.AppendLine("ButtonGlowLastBinding=" + buttonGlowLastBinding);
            sb.AppendLine("RealCabinetLighting=REFERENCE_PHOTO_REFINED_V4");
            sb.AppendLine("RealCabinetSetupDone=" + realCabinetSetupDone);
            sb.AppendLine("RealCabinetSetupAttempts=" + realCabinetSetupAttempts);
            sb.AppendLine("RealCabinetRoot=" + realCabinetRootPath);
            sb.AppendLine("RealCabinetRingRadius=" + realCabinetRingRadius.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("CabinetWhiteEmissionMaterials=" + cabinetWhiteEmissionMaterials);
            sb.AppendLine("CabinetAccentEmissionMaterials=" + cabinetAccentEmissionMaterials);
            sb.AppendLine("RingContourHaloLightsCreated=" + ringContourHaloLightsCreated);
            sb.AppendLine("CabinetTopGlowLightsCreated=" + cabinetTopGlowLightsCreated);
            sb.AppendLine("CabinetSideGlowLightsCreated=" + cabinetSideGlowLightsCreated);
            sb.AppendLine("RingContourHaloIntensity=" + RingContourHaloIntensity.ToString("F2", CultureInfo.InvariantCulture));
            sb.AppendLine("CabinetWhiteEmissionMultiplier=" + CabinetWhiteEmissionMultiplier.ToString("F2", CultureInfo.InvariantCulture));
            sb.AppendLine("CabinetWhitePlasticBoost=" + CabinetWhitePlasticBoost.ToString("F2", CultureInfo.InvariantCulture));
            sb.AppendLine("ButtonHaloLightType=SPOT_RADIAL_OUTWARD");
            sb.AppendLine("ButtonGoal=WHOLE_BUTTON_GLOW_PLUS_OUTER_CABINET_TIP_PATCH_WITH_RING_OFFSET_CONTROL");
            sb.AppendLine("ButtonTipAimMode=FRONT_OF_RING_AIMED_BACK_TO_OUTER_LIP + DIAMETER_RUNTIME_ADJUSTABLE");
            sb.AppendLine("ButtonColorLink=CAP_AND_TIP_SHARE_RINGLED_HUE");
            sb.AppendLine("CabinetGoal=LOWER_BODY_BRIGHTNESS_PLUS_VISIBLE_WHITE_CONTOUR");
            sb.AppendLine("RingContourLightType=SPOT_RADIAL_OUTWARD");
            sb.AppendLine("LEDSafetyPatchInstalled=" + ledSafetyPatchInstalled);
            sb.AppendLine("LEDSafetyPatchError=" + ledSafetyPatchError);
            sb.AppendLine("LEDMalformedFramesSkipped=" +
                Interlocked.CompareExchange(ref ledMalformedFramesSkipped, 0, 0));
            sb.AppendLine("LEDUnsafeUpdatesSkipped=" +
                Interlocked.CompareExchange(ref ledUnsafeUpdatesSkipped, 0, 0));
            sb.AppendLine("SerialReflectionReady=" + serialReflectionReady);
            sb.AppendLine("RequestedP1SerialPort=" + requestedP1SerialPort);
            sb.AppendLine("ActualP1SerialPort=" + actualP1SerialPort);
            sb.AppendLine("DynamicP1SerialPortApplied=" + dynamicP1SerialPortApplied);
            sb.AppendLine("DynamicP1SerialPortResult=" + dynamicP1SerialPortResult);
            sb.AppendLine("P1SerialOpenCurrent=" + serialP1OpenCurrent);
            sb.AppendLine("P1SerialOpenSeen=" + serialP1OpenSeen);
            sb.AppendLine("P2SerialOpenCurrent=" + serialP2OpenCurrent);
            sb.AppendLine("P2SerialOpenSeen=" + serialP2OpenSeen);
            sb.AppendLine("SerialStartUpCurrent=" + serialStartUpCurrent);
            sb.AppendLine("SerialStartUpSeen=" + serialStartUpSeen);
            sb.AppendLine("TouchDataP1ChangeCount=" + touchDataP1ChangeCount);
            sb.AppendLine("TouchDataP1Hex=" + touchDataP1Hex);
            sb.AppendLine("TouchDataP2ChangeCount=" + touchDataP2ChangeCount);
            sb.AppendLine("TouchDataP2Hex=" + touchDataP2Hex);
            sb.AppendLine("P2TouchContinuousTransportArmed=True");
            sb.AppendLine("P2TouchValidation=UNVALIDATED_NO_P2_TEST_AVAILABLE");
            sb.AppendLine("ContinuousTouchStream=True");
            sb.AppendLine("ContinuousTouchIntervalMs=" + ContinuousTouchIntervalMs);
            sb.AppendLine("ContinuousTouchThreadStarted=" + continuousTouchThreadStarted);
            sb.AppendLine("ContinuousTouchThreadAlive=" +
                (continuousTouchThread != null && continuousTouchThread.IsAlive));
            sb.AppendLine("ContinuousTouchEverActive=" + continuousTouchEverActive);
            sb.AppendLine("ContinuousTouchWrites=" +
                Interlocked.CompareExchange(ref continuousTouchWrites, 0, 0));
            sb.AppendLine("ContinuousTouchWritesPerSecond=" + continuousTouchWritesPerSecond);
            sb.AppendLine("ContinuousTouchWriteErrors=" +
                Interlocked.CompareExchange(ref continuousTouchWriteErrors, 0, 0));
            sb.AppendLine("ContinuousTouchLastError=" + continuousTouchLastError);
            sb.AppendLine("ContinuousTouchP2EverActive=" + continuousTouchP2EverActive);
            sb.AppendLine("ContinuousTouchWritesP2=" +
                Interlocked.CompareExchange(ref continuousTouchWritesP2, 0, 0));
            sb.AppendLine("ContinuousTouchWritesPerSecondP2=" + continuousTouchWritesPerSecondP2);
            sb.AppendLine("ContinuousTouchWriteErrorsP2=" +
                Interlocked.CompareExchange(ref continuousTouchWriteErrorsP2, 0, 0));
            sb.AppendLine("ContinuousTouchLastErrorP2=" + continuousTouchLastErrorP2);
            sb.AppendLine();
            sb.AppendLine("MovementFrames=" + movementFrames);
            sb.AppendLine("ForwardFrames=" + forwardFrames);
            sb.AppendLine("BackwardFrames=" + backwardFrames);
            sb.AppendLine("LeftFrames=" + leftFrames);
            sb.AppendLine("RightFrames=" + rightFrames);
            sb.AppendLine("TurnFrames=" + turnFrames);
            sb.AppendLine("TriggerPresses=" + triggerPresses);
            sb.AppendLine("UIHits=" + uiHits);
            sb.AppendLine("UIClicksSent=" + uiClicks);
            sb.AppendLine("UIBeginDrags=" + uiBeginDrags);
            sb.AppendLine("UIDragEvents=" + uiDragEvents);
            sb.AppendLine("UIEndDrags=" + uiEndDrags);
            sb.AppendLine("UIMisses=" + uiMisses);
            AmbientLabAppendStatus(sb);

            File.WriteAllText(statusFile, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }
}

public class MaiMaiVRTouchHapticHandObserver : MonoBehaviour
{
    public MaiMaiVRIntegratedBaseline Owner;
    public bool IsLeft;

    private readonly HashSet<int> activeTouchColliderIds =
        new HashSet<int>();

    private bool IsTouchPanelCollider(Collider other)
    {
        if (other == null)
            return false;

        Transform t = other.transform;
        int guard = 0;

        while (t != null && guard++ < 16)
        {
            MonoBehaviour[] behaviours = t.GetComponents<MonoBehaviour>();

            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour mb = behaviours[i];
                if (mb == null)
                    continue;

                if (string.Equals(
                    mb.GetType().Name,
                    "TouchPanelManager",
                    StringComparison.Ordinal))
                {
                    return true;
                }
            }

            t = t.parent;
        }

        return false;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (Owner == null || !IsTouchPanelCollider(other))
            return;

        int id = other.GetInstanceID();

        if (activeTouchColliderIds.Add(id))
        {
            Owner.OnTouchHapticContactState(
                IsLeft,
                activeTouchColliderIds.Count,
                true,
                other);
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (Owner == null || other == null)
            return;

        int id = other.GetInstanceID();

        if (activeTouchColliderIds.Remove(id))
        {
            Owner.OnTouchHapticContactState(
                IsLeft,
                activeTouchColliderIds.Count,
                false,
                other);
        }
    }

    private void OnDisable()
    {
        if (activeTouchColliderIds.Count > 0 && Owner != null)
            Owner.ResetTouchHapticHand(IsLeft);

        activeTouchColliderIds.Clear();
    }

    private void OnDestroy()
    {
        if (activeTouchColliderIds.Count > 0 && Owner != null)
            Owner.ResetTouchHapticHand(IsLeft);

        activeTouchColliderIds.Clear();
    }
}


