using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx;
using UnityEngine;
using UnityEngine.UI;

public partial class MaiMaiVRIntegratedBaseline
{
    [Serializable]
    private class AmbientLabConfig
    {
        public int ConfigVersion = 10;
        public bool MasterEnabled = true;

        // Surface multipliers are relative to the known-working V0.3.0.8 look.
        public float RoomLevel = 1.53499997f;
        public float CabinetBody = 0.86161965f;
        public float CabinetEmission = 0.00f;
        public float RingPlastic = 0.66916478f;
        // Keep the ring plastic non-emissive by default. The visible
        // "Button Base" cap material is detected separately and receives the
        // game-driven RingLed colour.
        public bool RingEmissionEnabled = false;

        public bool WhiteHaloEnabled = true;

        // Legacy V0.3.1.0-1.5 field retained for JSON migration only.
        public float WhiteHaloIntensity = 0.08897242f;

        // V0.6.1 halo controls.
        public float WhiteHaloPower = 0.47956973f;
        public float WhiteHaloRange = 0.11859817f;
        public float WhiteHaloRadiusOffset = 0.02469482f;
        public int WhiteHaloPointCount = 48;

        public bool ButtonEmissionEnabled = true;
        public float ButtonEmission = 0.35739219f;
        public float ButtonActiveOpacity = 0.14981486f;
        public bool ButtonTipLightEnabled = true;
        public float ButtonTipIntensity = 2.93876648f;
        public float ButtonTipRange = 0.25066698f;
        public float ButtonTipDiameter = 0.00472562f;
        public float ButtonTipRingOffset = -0.12f;

        // Top cyan + side red/orange are one user control and are independent
        // from Room/Cabinet MASTER multipliers.
        public bool AccentGlowEnabled = true;
        public float AccentGlowIntensity = 0.96539450f;

        // Legacy V0.3.1.0-1.2 fields retained only for JSON migration.
        public bool TopGlowEnabled = true;
        public float TopGlowIntensity = 0.96539450f;
        public bool SideGlowEnabled = true;
        public float SideGlowIntensity = 0.75300765f;
    }

    private enum AmbientSurfaceCategory
    {
        Ignore,
        Room,
        CabinetBody,
        Ring,
        Accent,
        Button
    }

    private sealed class AmbientMaterialState
    {
        public Renderer Renderer;
        public Material Material;
        public string ColorProperty;
        public Color BaseColor;
        public bool HasEmission;
        public Color BaseEmission;
        public bool BaseEmissionKeyword;
        public AmbientSurfaceCategory Category;
        public int MaterialIndex;
    }

    private sealed class AmbientMaterialPair
    {
        public AmbientMaterialState P1;
        public AmbientMaterialState P2;
    }

    private AmbientLabConfig ambientLabConfig = new AmbientLabConfig();
    private string ambientLabConfigPath = "<not set>";
    private bool ambientLabDirty;
    private float ambientLabSaveTimer = -1f;
    private int ambientLabSaveCount;
    private int ambientLabLoadCount;
    private string ambientLabLastError = "<none>";

    private readonly Dictionary<int, AmbientMaterialState> ambientLabMaterials =
        new Dictionary<int, AmbientMaterialState>();
    private float ambientLabMaterialScanTimer;
    private bool ambientLabRenderBaseCaptured;
    private float ambientLabBaseAmbientIntensity;
    private float ambientLabBaseReflectionIntensity;
    private int ambientLabMaterialScanCount;
    private int ambientLabMaterialApplyCount;

    private bool ambientLabUiBuilt;
    private float ambientLabUiRetryTimer;
    private GameObject ambientLabPanel;
    private GameObject ambientLabTab1;
    private GameObject ambientLabTab2;
    private Button ambientLabTabButton;
    private Button ambientLabMainButton;
    private Button ambientLabTweaksButton;
    private Font ambientLabFont;
    private int ambientLabUiBuildAttempts;
    private string ambientLabUiStatus = "NOT_BUILT";
    private bool ambientLabReopenAfterReset;
    private bool ambientLabNativeTabPositionsCaptured;
    private Vector2 ambientLabNativeMainOriginalPos;
    private Vector2 ambientLabNativeTweaksOriginalPos;

    // P2 mirrored light layer. V0.3.0.8's upstream LightManager is P1-only;
    // this mirrors its visual effect around DisplayP2 without touching capture.
    private readonly List<Light> ambientLabP2ButtonLights = new List<Light>();
    private readonly List<Light> ambientLabP2ContourLights = new List<Light>();
    private readonly List<Light> ambientLabP2AccentLights = new List<Light>();
    private readonly List<Light> ambientLabP1SourceRingLights = new List<Light>();
    private readonly List<Light> ambientLabP2SourceRingLights = new List<Light>();
    private Light ambientLabP1BodyLed;
    private Light ambientLabP2BodyLed;
    private Light ambientLabP1DisplayLed;
    private Light ambientLabP2DisplayLed;
    private bool ambientLabP2MirrorBuilt;
    private Vector3 ambientLabP1DisplayCenter;
    private Vector3 ambientLabP2DisplayCenter;
    private bool ambientLabDisplayCentersReady;
    private int ambientLabP2MirrorBuildAttempts;

    // Continuous visual halo: the old contour Spot lights produced visible
    // dots. V0.3.1.5 keeps those lights disabled and draws a soft 3-layer
    // loop around the complete ring instead.
    private readonly List<LineRenderer> ambientLabP1DiffuseRing = new List<LineRenderer>();
    private readonly List<LineRenderer> ambientLabP2DiffuseRing = new List<LineRenderer>();
    private readonly List<Material> ambientLabDiffuseRingMaterials = new List<Material>();
    private bool ambientLabDiffuseRingBuilt;
    private int ambientLabDiffuseRingBuildAttempts;

    // P2 cabinet materials are present upstream but can stay almost black.
    // Pair P1/P2-tagged non-button materials and mirror the final P1 surface
    // state to P2 without touching either live display material.
    private readonly List<AmbientMaterialPair> ambientLabP2MaterialPairs = new List<AmbientMaterialPair>();
    private int ambientLabP2MaterialPairCount;
    private int ambientLabP2MaterialParityUpdates;

    // V0.6.1 visible native-button cap material separation.
    // Never classify the ring as a button merely because its bounds touch a RingLed.
    private readonly List<Renderer> ambientLabStrictP1Buttons = new List<Renderer>();
    private readonly List<Renderer> ambientLabStrictP2Buttons = new List<Renderer>();
    private bool ambientLabStrictButtonBindingsReady;
    private int ambientLabStrictButtonBindingAttempts;
    private int ambientLabStrictButtonEmissionUpdates;
    private int ambientLabVisibleButtonFallbackUpdates;
    private int ambientLabVisibleButtonFallbackRenderers;
    private readonly HashSet<int> ambientLabVisibleButtonVisited = new HashSet<int>();
    private bool ambientLabStrictButtonHotPathRetired = true;

    // Native UI reuse. Existing MaiDXR sliders already work with its XR pointer,
    // so AMBIANT clones them instead of constructing foreign Slider hierarchies.
    private Slider ambientLabNativeSliderTemplate;
    private int ambientLabSliderForeignBehavioursDisabled;
    private string ambientLabSliderLastForeignBehaviour = "<none>";
    private readonly List<GameObject> ambientLabTweaksNativeUi = new List<GameObject>();
    private readonly List<bool> ambientLabTweaksNativeUiWasActive = new List<bool>();

    // V0.3.1.5: visible overlay button planes are RETIRED from the runtime path.
    // They were easy to see as magenta/translucent quads from oblique angles.
    // The old fields stay only for backward-safe cleanup/status compatibility.
    private readonly List<MeshRenderer> ambientLabP1ButtonFaces = new List<MeshRenderer>();
    private readonly List<MeshRenderer> ambientLabP2ButtonFaces = new List<MeshRenderer>();
    private readonly List<Mesh> ambientLabButtonFaceMeshes = new List<Mesh>();
    private readonly List<GameObject> ambientLabButtonFaceObjects = new List<GameObject>();
    private Material ambientLabButtonFaceMaterial;
    private bool ambientLabButtonFacesBuilt;
    private int ambientLabButtonFaceBuildAttempts;
    private int ambientLabButtonFaceUpdates;
    private string ambientLabButtonFaceShader = "<none>";

    // Legacy V0.3.1.5 local button-emission lights retained only for cleanup/status.
    // V0.6.1 does not call this path: the real visible native Button Base material is authoritative.
    private readonly List<Light> ambientLabP1ButtonEmissionLights = new List<Light>();
    private readonly List<Light> ambientLabP2ButtonEmissionLights = new List<Light>();
    private bool ambientLabButtonEmissionLightsBuilt;
    private int ambientLabButtonEmissionLightBuildAttempts;
    private int ambientLabButtonEmissionLightUpdates;

    // White ring halo: pure real-time lights, no visible LineRenderer mesh.
    // V0.6.1 makes density, radius and thickness runtime-adjustable.
    private readonly List<Light> ambientLabP1WhiteHaloLights = new List<Light>();
    private readonly List<Light> ambientLabP2WhiteHaloLights = new List<Light>();
    private bool ambientLabWhiteHaloBuilt;
    private int ambientLabWhiteHaloBuildAttempts;
    private int ambientLabWhiteHaloUpdates;
    private int ambientLabWhiteHaloRebuilds;

    // Explicit BodyP1/BodyP2 + HeadP1/HeadP2 material parity.
    private readonly List<AmbientMaterialPair> ambientLabExplicitP2Pairs = new List<AmbientMaterialPair>();
    private int ambientLabExplicitP2PairBuilds;
    private int ambientLabExplicitP2PairUpdates;
    private float ambientLabExplicitP2PairRetryTimer;

    private float AmbientLabButtonEmissionValue
    {
        get
        {
            if (ambientLabConfig == null ||
                !ambientLabConfig.ButtonEmissionEnabled)
            {
                return 0f;
            }

            return Mathf.Clamp(ambientLabConfig.ButtonEmission, 0f, 4.0f);
        }
    }

    private void AmbientLabAwake()
    {
        try
        {
            string env = Environment.GetEnvironmentVariable("MAIMAIVR_AMBIENT_CONFIG");

            ambientLabConfigPath = !string.IsNullOrWhiteSpace(env)
                ? env.Trim()
                : Path.Combine(Paths.BepInExRootPath, "MaiMaiVR_AmbientLab.json");

            AmbientLabLoadConfig();
            AmbientLabSanitizeConfig();

            Log("AMBIANT_LAB=enabled | tab=AMBIANT | autosave=0.35s | config=" +
                ambientLabConfigPath);
            Log("AMBIANT_LAB_BASE=V0.3.0.8 | P1/P2 capture/crop/controller constants untouched");
        }
        catch (Exception ex)
        {
            ambientLabLastError = ex.GetType().Name + ":" + ex.Message;
            Log("AMBIANT_LAB_AWAKE_FAIL " + ambientLabLastError);
        }
    }

    private void AmbientLabUpdate()
    {
        if (ambientLabSaveTimer >= 0f)
        {
            ambientLabSaveTimer -= Time.unscaledDeltaTime;

            if (ambientLabSaveTimer <= 0f && ambientLabDirty)
                AmbientLabSaveConfig(false);
        }

        if (!ambientLabUiBuilt)
        {
            ambientLabUiRetryTimer -= Time.unscaledDeltaTime;

            if (ambientLabUiRetryTimer <= 0f)
            {
                ambientLabUiRetryTimer = 1.0f;
                AmbientLabTryBuildUi();
            }
        }
    }

    private void AmbientLabLateUpdate()
    {
        try
        {
            AmbientLabApplyLiveLightValues();
            AmbientLabApplySurfaceOverrides();
            AmbientLabEnsureP2Mirror();

            // V0.6.1 PERF HOTPATH FIX:
            // The strict binding path could fail to resolve all 8+8 buttons and
            // then retry Resources.FindObjectsOfTypeAll<Renderer>() up to 16 times
            // EVERY FRAME while logging every candidate. Runtime V0.3.1.13 proved
            // 46,613 retries / >416k log lines in one session. The visible-material
            // fallback is the path that actually drives the validated button look,
            // so keep only that lightweight path at runtime.
            AmbientLabApplyVisibleButtonMaterialFallback();

            AmbientLabEnsureWhiteHaloLights();
            AmbientLabUpdateWhiteHaloLights();

            AmbientLabUpdateP2Mirror();

            AmbientLabEnsureExplicitP2Pairs();
            AmbientLabApplyExplicitP2Parity();
        }
        catch (Exception ex)
        {
            ambientLabLastError = ex.GetType().Name + ":" + ex.Message;

            if (ambientLabMaterialApplyCount < 10)
                Log("AMBIANT_LAB_LATE_FAIL " + ambientLabLastError);
        }
    }

    private void AmbientLabOnDestroy()
    {
        try
        {
            AmbientLabSaveConfig(true);

            for (int i = 0; i < ambientLabButtonFaceObjects.Count; i++)
                if (ambientLabButtonFaceObjects[i] != null) UnityEngine.Object.Destroy(ambientLabButtonFaceObjects[i]);

            for (int i = 0; i < ambientLabButtonFaceMeshes.Count; i++)
                if (ambientLabButtonFaceMeshes[i] != null) UnityEngine.Object.Destroy(ambientLabButtonFaceMeshes[i]);

            if (ambientLabButtonFaceMaterial != null)
                UnityEngine.Object.Destroy(ambientLabButtonFaceMaterial);

            for (int i = 0; i < ambientLabP1ButtonEmissionLights.Count; i++)
                if (ambientLabP1ButtonEmissionLights[i] != null) UnityEngine.Object.Destroy(ambientLabP1ButtonEmissionLights[i].gameObject);

            for (int i = 0; i < ambientLabP2ButtonEmissionLights.Count; i++)
                if (ambientLabP2ButtonEmissionLights[i] != null) UnityEngine.Object.Destroy(ambientLabP2ButtonEmissionLights[i].gameObject);

            for (int i = 0; i < ambientLabP1WhiteHaloLights.Count; i++)
                if (ambientLabP1WhiteHaloLights[i] != null) UnityEngine.Object.Destroy(ambientLabP1WhiteHaloLights[i].gameObject);

            for (int i = 0; i < ambientLabP2WhiteHaloLights.Count; i++)
                if (ambientLabP2WhiteHaloLights[i] != null) UnityEngine.Object.Destroy(ambientLabP2WhiteHaloLights[i].gameObject);

            for (int i = 0; i < ambientLabP1DiffuseRing.Count; i++)
                if (ambientLabP1DiffuseRing[i] != null) UnityEngine.Object.Destroy(ambientLabP1DiffuseRing[i].gameObject);

            for (int i = 0; i < ambientLabP2DiffuseRing.Count; i++)
                if (ambientLabP2DiffuseRing[i] != null) UnityEngine.Object.Destroy(ambientLabP2DiffuseRing[i].gameObject);

            for (int i = 0; i < ambientLabDiffuseRingMaterials.Count; i++)
                if (ambientLabDiffuseRingMaterials[i] != null) UnityEngine.Object.Destroy(ambientLabDiffuseRingMaterials[i]);
        }
        catch { }
    }

    private void AmbientLabSanitizeConfig()
    {
        if (ambientLabConfig == null)
            ambientLabConfig = new AmbientLabConfig();

        // Migrate V0.3.1.0-1.2 separate top/side controls into one setting.
        if (ambientLabConfig.ConfigVersion < 4)
        {
            ambientLabConfig.AccentGlowEnabled =
                ambientLabConfig.TopGlowEnabled ||
                ambientLabConfig.SideGlowEnabled;

            ambientLabConfig.AccentGlowIntensity =
                Mathf.Max(
                    ambientLabConfig.TopGlowIntensity,
                    ambientLabConfig.SideGlowIntensity);
        }

        if (ambientLabConfig.ConfigVersion < 6)
        {
            // Preserve approximately the V0.3.1.5 visible halo level while
            // converting to the new normalized quadratic Power control.
            float oldPerLight =
                Mathf.Max(0f, ambientLabConfig.WhiteHaloIntensity) * 0.50f;

            ambientLabConfig.WhiteHaloPower =
                Mathf.Clamp01(
                    Mathf.Sqrt(
                        oldPerLight / 0.025f));

            if (ambientLabConfig.WhiteHaloPointCount < 8)
                ambientLabConfig.WhiteHaloPointCount = 48;
        }

        ambientLabConfig.ConfigVersion = 10;

        // Ring plastic is natively light grey/white and no longer receives the old
        // Arcade Night x0.20 material darkening. Keep emission as an optional
        // reserve only when the user pushes Ring Power well above its native look.
        ambientLabConfig.RingEmissionEnabled = ambientLabConfig.RingPlastic > 5.0f;

        ambientLabConfig.RoomLevel =
            Mathf.Clamp(ambientLabConfig.RoomLevel, 0.05f, 5.00f);

        ambientLabConfig.CabinetBody =
            Mathf.Clamp(ambientLabConfig.CabinetBody, 0.05f, 5.00f);

        ambientLabConfig.CabinetEmission =
            Mathf.Clamp(ambientLabConfig.CabinetEmission, 0f, 2.00f);

        ambientLabConfig.RingPlastic =
            Mathf.Clamp(ambientLabConfig.RingPlastic, 0.05f, 10.00f);

        ambientLabConfig.WhiteHaloPower =
            Mathf.Clamp01(ambientLabConfig.WhiteHaloPower);

        ambientLabConfig.WhiteHaloRange =
            Mathf.Clamp(ambientLabConfig.WhiteHaloRange, 0.02f, 0.35f);

        ambientLabConfig.WhiteHaloRadiusOffset =
            Mathf.Clamp(ambientLabConfig.WhiteHaloRadiusOffset, -0.05f, 0.20f);

        ambientLabConfig.WhiteHaloPointCount =
            Mathf.Clamp(ambientLabConfig.WhiteHaloPointCount, 8, 96);

        if (ambientLabConfig.ConfigVersion < 9)
        {
            ambientLabConfig.ButtonActiveOpacity = 0.60f;
            ambientLabConfig.ButtonTipRingOffset = 0.000f;
        }

        ambientLabConfig.ButtonEmission =
            Mathf.Clamp(ambientLabConfig.ButtonEmission, 0f, 4.00f);

        ambientLabConfig.ButtonActiveOpacity =
            Mathf.Clamp01(ambientLabConfig.ButtonActiveOpacity);

        ambientLabConfig.ButtonTipIntensity =
            Mathf.Clamp(ambientLabConfig.ButtonTipIntensity, 0f, 3.00f);

        ambientLabConfig.ButtonTipRange =
            Mathf.Clamp(ambientLabConfig.ButtonTipRange, 0.02f, 1.00f);

        ambientLabConfig.ButtonTipDiameter =
            Mathf.Clamp(ambientLabConfig.ButtonTipDiameter, -0.02f, 0.08f);

        ambientLabConfig.ButtonTipRingOffset =
            Mathf.Clamp(ambientLabConfig.ButtonTipRingOffset, -0.12f, 0.12f);

        ambientLabConfig.AccentGlowIntensity =
            Mathf.Clamp(ambientLabConfig.AccentGlowIntensity, 0f, 2.00f);

        // Keep legacy fields synchronized in saved JSON for backwards clarity.
        ambientLabConfig.TopGlowEnabled = ambientLabConfig.AccentGlowEnabled;
        ambientLabConfig.SideGlowEnabled = ambientLabConfig.AccentGlowEnabled;
        ambientLabConfig.TopGlowIntensity = ambientLabConfig.AccentGlowIntensity;
        ambientLabConfig.SideGlowIntensity =
            ambientLabConfig.AccentGlowIntensity * 0.78f;
    }

    private void AmbientLabLoadConfig()
    {
        ambientLabConfig = new AmbientLabConfig();

        if (string.IsNullOrWhiteSpace(ambientLabConfigPath) ||
            !File.Exists(ambientLabConfigPath))
        {
            AmbientLabSaveConfig(true);
            return;
        }

        try
        {
            string json = File.ReadAllText(ambientLabConfigPath, System.Text.Encoding.UTF8);

            if (!string.IsNullOrWhiteSpace(json))
                JsonUtility.FromJsonOverwrite(json, ambientLabConfig);

            ambientLabLoadCount++;
            ambientLabLastError = "<none>";
        }
        catch (Exception ex)
        {
            ambientLabLastError = ex.GetType().Name + ":" + ex.Message;
            Log("AMBIANT_CONFIG_LOAD_FAIL " + ambientLabLastError);
        }
    }

    private void AmbientLabSaveConfig(bool immediate)
    {
        if (!immediate && !ambientLabDirty)
            return;

        try
        {
            AmbientLabSanitizeConfig();

            string dir = Path.GetDirectoryName(ambientLabConfigPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            string json = JsonUtility.ToJson(ambientLabConfig, true);
            File.WriteAllText(ambientLabConfigPath, json, System.Text.Encoding.UTF8);

            ambientLabDirty = false;
            ambientLabSaveTimer = -1f;
            ambientLabSaveCount++;
            ambientLabLastError = "<none>";
        }
        catch (Exception ex)
        {
            ambientLabLastError = ex.GetType().Name + ":" + ex.Message;
            Log("AMBIANT_CONFIG_SAVE_FAIL " + ambientLabLastError);
        }
    }

    private void AmbientLabMarkDirty()
    {
        ambientLabDirty = true;
        ambientLabSaveTimer = 0.35f;
    }

    private void AmbientLabResetConfig()
    {
        ambientLabConfig = new AmbientLabConfig();
        AmbientLabSanitizeConfig();
        ambientLabDirty = true;
        AmbientLabSaveConfig(true);
        ambientLabReopenAfterReset = true;
        Log("AMBIANT_DEFAULT_PROFILE_RESTORED profile=V0.3.1.10_USER_VALIDATED");

        if (ambientLabMainButton == null)
            ambientLabMainButton = AmbientLabFindButton("Main");

        if (ambientLabTweaksButton == null)
            ambientLabTweaksButton = AmbientLabFindButton("Tweaks");

        AmbientLabRestoreNativeTabPositions();

        if (ambientLabPanel != null)
        {
            UnityEngine.Object.Destroy(ambientLabPanel);
            ambientLabPanel = null;
        }

        if (ambientLabTabButton != null)
        {
            UnityEngine.Object.Destroy(ambientLabTabButton.gameObject);
            ambientLabTabButton = null;
        }

        ambientLabUiBuilt = false;
        ambientLabUiRetryTimer = 0.1f;
        ambientLabUiStatus = "RESET_REBUILD";
    }

    private static Color AmbientLabScaleColor(Color c, float factor)
    {
        factor = Mathf.Max(0f, factor);
        return new Color(c.r * factor, c.g * factor, c.b * factor, c.a);
    }

    private static bool AmbientLabRendererListContains(
        List<Renderer> renderers,
        Renderer renderer)
    {
        if (renderer == null || renderers == null)
            return false;

        int id = renderer.GetInstanceID();

        for (int i = 0; i < renderers.Count; i++)
        {
            Renderer known = renderers[i];

            if (known != null && known.GetInstanceID() == id)
                return true;
        }

        return false;
    }

    private bool AmbientLabIsKnownButtonRenderer(Renderer renderer)
    {
        if (renderer == null)
            return false;

        if (AmbientLabRendererListContains(ambientLabStrictP1Buttons, renderer) ||
            AmbientLabRendererListContains(ambientLabStrictP2Buttons, renderer))
        {
            return true;
        }

        // Before the strict binding is ready, accept ONLY the exact renderer
        // selected by the validated V0.3.0.8 button binding. No proximity test:
        // bounds-based proximity was the reason Ring Plastic became a button.
        for (int i = 0; i < buttonGlowRenderers.Count; i++)
        {
            Renderer known = buttonGlowRenderers[i];

            if (known != null &&
                known.GetInstanceID() == renderer.GetInstanceID())
            {
                return true;
            }
        }

        return false;
    }

    private void AmbientLabEnsureP1SourceRingLights()
    {
        if (ambientLabP1SourceRingLights.Count >= 4)
            return;

        try
        {
            System.Collections.IList ringList =
                lightRingLedsField != null && lightManagerInstance != null
                    ? lightRingLedsField.GetValue(lightManagerInstance)
                        as System.Collections.IList
                    : null;

            if (ringList == null)
                return;

            ambientLabP1SourceRingLights.Clear();

            for (int i = 0; i < ringList.Count; i++)
            {
                Light source = ringList[i] as Light;

                if (source != null)
                    ambientLabP1SourceRingLights.Add(source);
            }
        }
        catch { }
    }

    private static bool AmbientLabRendererHasVisibleButtonMaterial(
        Renderer renderer)
    {
        if (renderer == null)
            return false;

        Material[] materials;

        try { materials = renderer.sharedMaterials; }
        catch { return false; }

        if (materials == null)
            return false;

        for (int i = 0; i < materials.Length; i++)
        {
            Material material = materials[i];

            if (material == null)
                continue;

            string clean = AmbientLabCleanMaterialName(material.name);

            // Runtime report V0.3.1.6 proved that the material named exactly
            // "Button" is carried by DX Unity/Button Hitbox objects.  The
            // visible eight dark button caps use "Button Base".  Prefer the
            // visible cap material while keeping "Button" as a safe fallback
            // only for non-hitbox renderers.
            if (clean == "buttonbase" || clean == "button")
                return true;
        }

        return false;
    }

    private Renderer AmbientLabFindStrictButtonRenderer(
        Vector3 target,
        HashSet<int> used)
    {
        Renderer[] all = Resources.FindObjectsOfTypeAll<Renderer>();
        Renderer best = null;
        float bestScore = float.MaxValue;

        for (int i = 0; i < all.Length; i++)
        {
            Renderer renderer = all[i];

            if (renderer == null ||
                renderer.gameObject == null ||
                !renderer.gameObject.scene.IsValid() ||
                !renderer.enabled ||
                !renderer.gameObject.activeInHierarchy)
            {
                continue;
            }

            int id = renderer.GetInstanceID();

            if (used.Contains(id))
                continue;

            // V0.3.1.6 runtime evidence: exact "Button" belonged to the
            // interaction hitboxes, not the visible caps.  Bind only active,
            // rendered geometry carrying Button Base (preferred) or a
            // non-hitbox Button fallback. Ring remains excluded.
            if (!AmbientLabRendererHasVisibleButtonMaterial(renderer))
                continue;

            string text =
                (GetPath(renderer.transform) + "|" + renderer.gameObject.name)
                .ToLowerInvariant();

            if (text.Contains("button hitbox") ||
                text.Contains("buttonhitbox") ||
                text.Contains("touch hitbox") ||
                text.Contains("touchhitbox") ||
                text.Contains("display p1") ||
                text.Contains("display p2") ||
                text.Contains("screen") ||
                text.Contains("monitor") ||
                text.Contains("canvas") ||
                text.Contains("controller") ||
                text.Contains("hand") ||
                text.Contains("ray") ||
                text.Contains("maimaivr_diffuse") ||
                text.Contains("maimaivr_p1_diffuse") ||
                text.Contains("maimaivr_p2_diffuse"))
            {
                continue;
            }

            Bounds bounds;

            try { bounds = renderer.bounds; }
            catch { continue; }

            float centerDistance =
                Vector3.Distance(bounds.center, target);

            // The physical button is compact and centred close to its RingLed.
            // A giant ring/cabinet renderer may contain the RingLed in its bounds,
            // but its centre/diagonal makes it lose this score.
            if (centerDistance > 0.42f)
                continue;

            float diagonal = bounds.size.magnitude;

            if (diagonal > 0.90f)
                continue;

            float score =
                centerDistance +
                diagonal * 0.20f;

            // Visible cap material wins over any generic Button fallback.
            Material[] scoreMaterials = null;
            try { scoreMaterials = renderer.sharedMaterials; } catch { }
            if (scoreMaterials != null)
            {
                for (int sm = 0; sm < scoreMaterials.Length; sm++)
                {
                    Material scoreMaterial = scoreMaterials[sm];
                    if (scoreMaterial != null &&
                        AmbientLabCleanMaterialName(scoreMaterial.name) == "buttonbase")
                    {
                        score -= 0.18f;
                        break;
                    }
                }
            }

            if (text.Contains("button"))
                score -= 0.08f;

            if (text.Contains("ring") ||
                text.Contains("body") ||
                text.Contains("cabinet"))
            {
                score += 0.12f;
            }

            if (score < bestScore)
            {
                bestScore = score;
                best = renderer;
            }
        }

        return best;
    }

    private void AmbientLabEnsureStrictButtonBindings()
    {
        if (ambientLabStrictButtonBindingsReady)
            return;

        ambientLabStrictButtonBindingAttempts++;
        AmbientLabEnsureP1SourceRingLights();

        if (ambientLabP1SourceRingLights.Count < 4)
            return;

        if (!ambientLabDisplayCentersReady)
        {
            Vector3 p1;
            Vector3 p2;

            if (!AmbientLabTryFindDisplayCenter(p1MaterialIds, out p1) ||
                !AmbientLabTryFindDisplayCenter(p2MaterialIds, out p2))
            {
                return;
            }

            ambientLabP1DisplayCenter = p1;
            ambientLabP2DisplayCenter = p2;
            ambientLabDisplayCentersReady = true;
        }

        Vector3 translation =
            ambientLabP2DisplayCenter -
            ambientLabP1DisplayCenter;

        ambientLabStrictP1Buttons.Clear();
        ambientLabStrictP2Buttons.Clear();

        HashSet<int> usedP1 = new HashSet<int>();
        HashSet<int> usedP2 = new HashSet<int>();

        for (int i = 0; i < ambientLabP1SourceRingLights.Count; i++)
        {
            Light source = ambientLabP1SourceRingLights[i];

            if (source == null)
                continue;

            Renderer p1 =
                AmbientLabFindStrictButtonRenderer(
                    source.transform.position,
                    usedP1);

            Renderer p2 =
                AmbientLabFindStrictButtonRenderer(
                    source.transform.position + translation,
                    usedP2);

            ambientLabStrictP1Buttons.Add(p1);
            ambientLabStrictP2Buttons.Add(p2);

            if (p1 != null) usedP1.Add(p1.GetInstanceID());
            if (p2 != null) usedP2.Add(p2.GetInstanceID());

            Log(
                "AMBIANT_STRICT_BUTTON index=" + i +
                " p1=" + (p1 != null ? GetPath(p1.transform) : "<none>") +
                " p2=" + (p2 != null ? GetPath(p2.transform) : "<none>"));
        }

        int p1Count = 0;
        int p2Count = 0;

        for (int i = 0; i < ambientLabStrictP1Buttons.Count; i++)
            if (ambientLabStrictP1Buttons[i] != null) p1Count++;

        for (int i = 0; i < ambientLabStrictP2Buttons.Count; i++)
            if (ambientLabStrictP2Buttons[i] != null) p2Count++;

        ambientLabStrictButtonBindingsReady =
            p1Count >= 8 &&
            p2Count >= 8;

        Log(
            "AMBIANT_STRICT_BUTTONS ready=" +
            ambientLabStrictButtonBindingsReady +
            " p1=" + p1Count +
            " p2=" + p2Count);
    }

    private bool AmbientLabIsCabinetAccentMaterial(
        Renderer renderer,
        Material material)
    {
        if (renderer == null || material == null)
            return false;

        string path = GetPath(renderer.transform).ToLowerInvariant();

        // Only cabinet geometry. Displays, generated geometry and interaction
        // objects are excluded by AmbientLabClassifySurface before this call.
        if (!path.Contains("dx unity"))
            return false;

        Color baseColor;
        string colorProperty;

        if (!TryGetMaterialBaseColor(material, out baseColor, out colorProperty))
            return false;

        float h;
        float sat;
        float val;
        Color.RGBToHSV(baseColor, out h, out sat, out val);

        bool cyanBlue =
            sat >= 0.40f &&
            val >= 0.30f &&
            h >= 0.45f &&
            h <= 0.62f;

        bool redOrange =
            sat >= 0.45f &&
            val >= 0.30f &&
            (h <= 0.10f || h >= 0.95f);

        return cyanBlue || redOrange;
    }

    private AmbientSurfaceCategory AmbientLabClassifySurface(
        Renderer renderer,
        Material material)
    {
        if (renderer == null || material == null)
            return AmbientSurfaceCategory.Ignore;

        string path = GetPath(renderer.transform).ToLowerInvariant();
        string name = material.name.ToLowerInvariant();

        // Never touch generated AMBIANT geometry or any camera/spectator path.
        if (path.Contains("maimaivr_") ||
            path.Contains("display p1") ||
            path.Contains("display p2") ||
            path.Contains("screen") ||
            path.Contains("monitor") ||
            path.Contains("canvas") ||
            path.Contains("controller") ||
            path.Contains("hand") ||
            path.Contains("ray") ||
            path.Contains("camera") ||
            path.Contains("spectator") ||
            path.Contains("liv") ||
            path.Contains("nvr") ||
            path.Contains("fpsblock") ||
            path.Contains("tpsblock"))
        {
            return AmbientSurfaceCategory.Ignore;
        }

        // V0.3.1.6 report identified exact Button materials on interaction
        // hitboxes. Never treat those invisible/interaction renderers as the
        // visible physical button surface.
        if (path.Contains("button hitbox") ||
            path.Contains("buttonhitbox") ||
            path.Contains("touch hitbox") ||
            path.Contains("touchhitbox"))
        {
            return AmbientSurfaceCategory.Ignore;
        }

        // The visible eight physical caps use Button Base in MaiDXR.  Keep
        // them separate from Ring Plastic and drive their emission directly
        // from the live RingLed colour. A non-hitbox Button remains accepted
        // as a compatibility fallback for upstream prefab variants.
        string cleanMaterialName = AmbientLabCleanMaterialName(material.name);
        if (cleanMaterialName == "buttonbase" ||
            cleanMaterialName == "button")
        {
            return AmbientSurfaceCategory.Button;
        }

        // Ring remains mechanical non-emissive plastic.
        if (name.Contains("ring") ||
            path.Contains("/ring") ||
            path.Contains("/ring+button"))
        {
            return AmbientSurfaceCategory.Ring;
        }

        // The real cyan top and red/orange side plastics must not be driven by
        // Room/Cabinet MASTER any more. They are a dedicated Accent surface.
        if (AmbientLabIsCabinetAccentMaterial(renderer, material))
            return AmbientSurfaceCategory.Accent;

        if (name.Contains("bodyp1") ||
            name.Contains("bodyp2") ||
            name.Contains("headp1") ||
            name.Contains("headp2") ||
            name.StartsWith("body") ||
            name.StartsWith("head") ||
            path.Contains("body p1") ||
            path.Contains("body p2") ||
            path.Contains("bodyp1") ||
            path.Contains("bodyp2") ||
            path.Contains("headp1") ||
            path.Contains("headp2"))
        {
            return AmbientSurfaceCategory.CabinetBody;
        }

        return AmbientSurfaceCategory.Room;
    }

    private void AmbientLabScanMaterials()
    {
        Renderer[] renderers = Resources.FindObjectsOfTypeAll<Renderer>();

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];

            if (renderer == null || renderer.gameObject == null ||
                !renderer.gameObject.scene.IsValid())
            {
                continue;
            }

            Material[] materials;

            try { materials = renderer.materials; }
            catch { continue; }

            if (materials == null)
                continue;

            for (int m = 0; m < materials.Length; m++)
            {
                Material material = materials[m];
                if (material == null)
                    continue;

                int id = material.GetInstanceID();
                AmbientMaterialState existing;
                if (ambientLabMaterials.TryGetValue(id, out existing))
                {
                    if (existing != null)
                        existing.Category = AmbientLabClassifySurface(renderer, material);
                    continue;
                }

                AmbientSurfaceCategory category = AmbientLabClassifySurface(renderer, material);
                if (category == AmbientSurfaceCategory.Ignore)
                    continue;

                string colorProperty = null;
                Color baseColor = Color.white;

                try
                {
                    if (material.HasProperty("_BaseColor"))
                    {
                        colorProperty = "_BaseColor";
                        baseColor = material.GetColor("_BaseColor");
                    }
                    else if (material.HasProperty("_Color"))
                    {
                        colorProperty = "_Color";
                        baseColor = material.GetColor("_Color");
                    }
                }
                catch { }

                if (string.IsNullOrEmpty(colorProperty) && category != AmbientSurfaceCategory.Button)
                    continue;

                AmbientMaterialState state = new AmbientMaterialState();
                state.Renderer = renderer;
                state.Material = material;
                state.ColorProperty = colorProperty;
                state.BaseColor = baseColor;
                state.Category = category;
                state.MaterialIndex = m;

                try
                {
                    state.HasEmission = material.HasProperty("_EmissionColor");
                    state.BaseEmission = state.HasEmission
                        ? material.GetColor("_EmissionColor")
                        : Color.black;
                    state.BaseEmissionKeyword = material.IsKeywordEnabled("_EMISSION");
                }
                catch
                {
                    state.HasEmission = false;
                    state.BaseEmission = Color.black;
                    state.BaseEmissionKeyword = false;
                }

                ambientLabMaterials[id] = state;

                if (state.Category == AmbientSurfaceCategory.Accent)
                {
                    Log(
                        "AMBIANT_ACCENT_MATERIAL renderer=" +
                        GetPath(renderer.transform) +
                        " material=" + material.name);
                }
            }
        }

        ambientLabMaterialScanCount++;
    }

    private void AmbientLabRestoreEmission(AmbientMaterialState state)
    {
        if (state == null || state.Material == null || !state.HasEmission)
            return;

        try
        {
            state.Material.SetColor("_EmissionColor", state.BaseEmission);

            if (state.BaseEmissionKeyword)
                state.Material.EnableKeyword("_EMISSION");
            else
                state.Material.DisableKeyword("_EMISSION");
        }
        catch { }
    }

    private void AmbientLabApplySurfaceOverrides()
    {
        if (!ambientLabRenderBaseCaptured)
        {
            ambientLabBaseAmbientIntensity = RenderSettings.ambientIntensity;
            ambientLabBaseReflectionIntensity = RenderSettings.reflectionIntensity;
            ambientLabRenderBaseCaptured = true;
        }

        ambientLabMaterialScanTimer -= Time.unscaledDeltaTime;

        if (ambientLabMaterialScanTimer <= 0f)
        {
            ambientLabMaterialScanTimer = 1.0f;
            AmbientLabScanMaterials();
        }

        bool master =
            ambientLabConfig != null &&
            ambientLabConfig.MasterEnabled;

        float room =
            master
                ? ambientLabConfig.RoomLevel
                : 1f;

        RenderSettings.ambientIntensity =
            ambientLabBaseAmbientIntensity * room;

        RenderSettings.reflectionIntensity =
            ambientLabBaseReflectionIntensity * room;

        foreach (AmbientMaterialState state in ambientLabMaterials.Values)
        {
            if (state == null || state.Material == null)
                continue;

            try
            {
                float factor = 1f;

                switch (state.Category)
                {
                    case AmbientSurfaceCategory.Room:
                        factor =
                            master
                                ? ambientLabConfig.RoomLevel
                                : 1f;
                        break;

                    case AmbientSurfaceCategory.CabinetBody:
                        factor =
                            master
                                ? ambientLabConfig.CabinetBody
                                : 1f;
                        break;

                    case AmbientSurfaceCategory.Ring:
                        factor =
                            master
                                ? ambientLabConfig.RingPlastic
                                : 1f;
                        break;

                    case AmbientSurfaceCategory.Accent:
                        // Dedicated Top + Side control; never Scene Master.
                        // Also brighten the real coloured plastic slightly so
                        // the control remains visible on materials with weak GI.
                        factor =
                            ambientLabConfig.AccentGlowEnabled
                                ? 1f + ambientLabConfig.AccentGlowIntensity * 0.35f
                                : 1f;
                        break;

                    case AmbientSurfaceCategory.Button:
                        continue;
                }

                if (!string.IsNullOrEmpty(state.ColorProperty))
                {
                    state.Material.SetColor(
                        state.ColorProperty,
                        AmbientLabScaleColor(
                            state.BaseColor,
                            factor));
                }

                if (state.Category ==
                        AmbientSurfaceCategory.Accent &&
                    state.HasEmission)
                {
                    bool accentEnabled =
                        ambientLabConfig.AccentGlowEnabled &&
                        ambientLabConfig.AccentGlowIntensity > 0.0001f;

                    if (accentEnabled)
                    {
                        // V0.6.1 intentionally opens the useful range.
                        // A value of 2.0 reaches a strong HDR emission of 0.8.
                        float emissionPower =
                            ambientLabConfig.AccentGlowIntensity * 0.40f;

                        float baseMax = Mathf.Max(
                            state.BaseColor.r,
                            Mathf.Max(state.BaseColor.g, state.BaseColor.b));

                        Color hue =
                            baseMax > 0.0001f
                                ? state.BaseColor / baseMax
                                : Color.white;

                        Color emission = hue * emissionPower;
                        emission.a = 1f;

                        state.Material.SetColor("_EmissionColor", emission);
                        state.Material.EnableKeyword("_EMISSION");
                        state.Material.globalIlluminationFlags =
                            MaterialGlobalIlluminationFlags.RealtimeEmissive;
                    }
                    else
                    {
                        state.Material.SetColor("_EmissionColor", Color.black);
                        state.Material.DisableKeyword("_EMISSION");
                    }
                }

                if (state.Category ==
                        AmbientSurfaceCategory.CabinetBody &&
                    state.HasEmission)
                {
                    if (master &&
                        ambientLabConfig.CabinetEmission > 0.0001f)
                    {
                        Color emission =
                            AmbientLabScaleColor(
                                state.BaseColor,
                                ambientLabConfig.CabinetEmission);

                        state.Material.SetColor(
                            "_EmissionColor",
                            emission);

                        state.Material.EnableKeyword("_EMISSION");
                    }
                    else
                    {
                        state.Material.SetColor(
                            "_EmissionColor",
                            Color.black);

                        state.Material.DisableKeyword("_EMISSION");
                    }
                }

                if (state.Category ==
                        AmbientSurfaceCategory.Ring &&
                    state.HasEmission)
                {
                    // Ring Power now operates from the native light-grey/white Ring
                    // baseline directly. Values above 5 still add a modest HDR
                    // reserve, but 1.0 already corresponds to the undimmed stock look.
                    float ringPower =
                        master
                            ? Mathf.Clamp(ambientLabConfig.RingPlastic, 0.05f, 10.0f)
                            : 1f;

                    float extraEmission =
                        master
                            ? Mathf.Max(0f, ringPower - 5.0f) * 0.22f
                            : 0f;

                    if (extraEmission > 0.0001f)
                    {
                        Color emission = Color.white * extraEmission;
                        emission.a = 1f;
                        state.Material.SetColor("_EmissionColor", emission);
                        state.Material.EnableKeyword("_EMISSION");
                        state.Material.globalIlluminationFlags =
                            MaterialGlobalIlluminationFlags.RealtimeEmissive;
                    }
                    else
                    {
                        state.Material.SetColor("_EmissionColor", Color.black);
                        state.Material.DisableKeyword("_EMISSION");
                    }
                }
            }
            catch { }
        }

        ambientLabMaterialApplyCount++;
    }

    private void AmbientLabApplyButtonRendererEmission(
        Renderer renderer,
        Color ledColor,
        bool ledEnabled)
    {
        if (renderer == null || ambientLabConfig == null)
            return;

        float desired = AmbientLabButtonEmissionValue;
        bool enabled =
            ambientLabConfig.MasterEnabled &&
            ambientLabConfig.ButtonEmissionEnabled &&
            ledEnabled &&
            ledColor.maxColorComponent > 0.005f &&
            desired > 0.0001f;

        Material[] materials;

        try { materials = renderer.materials; }
        catch { return; }

        for (int i = 0; i < materials.Length; i++)
        {
            Material material = materials[i];

            string cleanMaterialName =
                material != null
                    ? AmbientLabCleanMaterialName(material.name)
                    : string.Empty;

            if (material == null ||
                (cleanMaterialName != "buttonbase" && cleanMaterialName != "button") ||
                !material.HasProperty("_EmissionColor"))
            {
                continue;
            }

            try
            {
                Color emission = Color.black;

                float ledMax =
                    Mathf.Max(
                        ledColor.r,
                        Mathf.Max(ledColor.g, ledColor.b));

                Color hue =
                    ledMax > 0.0001f
                        ? ledColor * (1f / ledMax)
                        : Color.white;
                hue.a = 1f;

                // MaiDXR's original assets are neutral light plastics
                // (Button Base ~= 0.726, Button ~= 0.689). Arcade Night dims
                // them to 20%, which is why emission over the untouched albedo
                // still looked black/purple. While the LED is active, restore
                // the visible cap to the SAME RingLed hue before adding HDR.
                float nativeButtonBase =
                    cleanMaterialName == "buttonbase"
                        ? 0.7264151f
                        : 0.6886792f;

                Color neutralSurface = Color.white * nativeButtonBase;
                neutralSurface.a = 1f;

                Color tintedSurface = hue * nativeButtonBase;
                tintedSurface.a = 1f;

                float activeOpacity =
                    ambientLabConfig != null
                        ? Mathf.Clamp01(ambientLabConfig.ButtonActiveOpacity)
                        : 1f;

                Color surface =
                    enabled
                        ? Color.Lerp(neutralSurface, tintedSurface, activeOpacity)
                        : neutralSurface;

                if (material.HasProperty("_BaseColor"))
                    material.SetColor("_BaseColor", surface);

                if (material.HasProperty("_Color"))
                    material.SetColor("_Color", surface);

                if (enabled && ledMax > 0.0001f)
                {
                    // Button and Button Tip deliberately share the exact same
                    // source RingLed hue. Only their brightness/range controls
                    // are independent.
                    emission = hue * desired;
                    emission.a = 1f;
                }

                material.SetColor("_EmissionColor", emission);

                if (enabled)
                {
                    material.EnableKeyword("_EMISSION");
                    material.globalIlluminationFlags =
                        MaterialGlobalIlluminationFlags.RealtimeEmissive;
                }
                else
                {
                    material.DisableKeyword("_EMISSION");
                }

                ambientLabStrictButtonEmissionUpdates++;
            }
            catch { }
        }
    }

    private void AmbientLabApplyStrictButtonEmission()
    {
        if (!ambientLabStrictButtonBindingsReady)
            return;

        AmbientLabEnsureP1SourceRingLights();

        int count = Mathf.Min(
            ambientLabP1SourceRingLights.Count,
            ambientLabStrictP1Buttons.Count);

        for (int i = 0; i < count; i++)
        {
            Light source = ambientLabP1SourceRingLights[i];

            if (source == null)
                continue;

            Color color = source.color;
            bool active =
                source.enabled &&
                color.maxColorComponent > 0.005f;

            Renderer p1 = ambientLabStrictP1Buttons[i];
            Renderer p2 =
                i < ambientLabStrictP2Buttons.Count
                    ? ambientLabStrictP2Buttons[i]
                    : null;

            AmbientLabApplyButtonRendererEmission(
                p1,
                color,
                active);

            // P2 has no independent LED serial stream in the current validated
            // baseline. Mirror only the BUTTON emission colour, never source
            // RingLed/BodyLed lights onto the entire P2 cabinet.
            AmbientLabApplyButtonRendererEmission(
                p2,
                color,
                active);
        }
    }

    private void AmbientLabApplyVisibleButtonMaterialFallback()
    {
        if (ambientLabConfig == null ||
            ambientLabMaterials.Count < 1)
        {
            return;
        }

        AmbientLabEnsureP1SourceRingLights();

        if (ambientLabP1SourceRingLights.Count < 1)
            return;

        if (!ambientLabDisplayCentersReady)
        {
            Vector3 p1;
            Vector3 p2;

            if (!AmbientLabTryFindDisplayCenter(p1MaterialIds, out p1) ||
                !AmbientLabTryFindDisplayCenter(p2MaterialIds, out p2))
            {
                return;
            }

            ambientLabP1DisplayCenter = p1;
            ambientLabP2DisplayCenter = p2;
            ambientLabDisplayCentersReady = true;
        }

        Vector3 translation =
            ambientLabP2DisplayCenter - ambientLabP1DisplayCenter;

        HashSet<int> visited = ambientLabVisibleButtonVisited;
        visited.Clear();
        int touched = 0;

        foreach (AmbientMaterialState state in ambientLabMaterials.Values)
        {
            if (state == null ||
                state.Category != AmbientSurfaceCategory.Button ||
                state.Renderer == null ||
                state.Material == null ||
                !state.HasEmission ||
                !state.Renderer.enabled ||
                state.Renderer.gameObject == null ||
                !state.Renderer.gameObject.activeInHierarchy)
            {
                continue;
            }

            int rendererId = state.Renderer.GetInstanceID();
            if (visited.Contains(rendererId))
                continue;

            visited.Add(rendererId);

            Vector3 pos;
            try { pos = state.Renderer.bounds.center; }
            catch { continue; }

            bool p2Side =
                Vector3.Distance(pos, ambientLabP2DisplayCenter) <
                Vector3.Distance(pos, ambientLabP1DisplayCenter);

            Light nearest = null;
            float best = float.MaxValue;

            for (int i = 0; i < ambientLabP1SourceRingLights.Count; i++)
            {
                Light source = ambientLabP1SourceRingLights[i];
                if (source == null)
                    continue;

                Vector3 sourcePos =
                    source.transform.position +
                    (p2Side ? translation : Vector3.zero);

                float distance = Vector3.Distance(pos, sourcePos);
                if (distance < best)
                {
                    best = distance;
                    nearest = source;
                }
            }

            if (nearest == null)
                continue;

            AmbientLabApplyButtonRendererEmission(
                state.Renderer,
                nearest.color,
                nearest.enabled &&
                    nearest.color.maxColorComponent > 0.005f);

            touched++;
        }

        ambientLabVisibleButtonFallbackRenderers = touched;
        ambientLabVisibleButtonFallbackUpdates++;
    }

    private Material AmbientLabCreateButtonFaceMaterial()
    {
        if (ambientLabButtonFaceMaterial != null)
            return ambientLabButtonFaceMaterial;

        Shader shader = Shader.Find("Sprites/Default");

        if (shader == null)
            shader = Shader.Find("Unlit/Transparent");

        if (shader == null)
            shader = Shader.Find("Legacy Shaders/Transparent/Diffuse");

        if (shader == null)
            shader = Shader.Find("Unlit/Color");

        if (shader == null)
            return null;

        Material material = new Material(shader);
        material.hideFlags = HideFlags.HideAndDontSave;
        material.renderQueue = 3200;
        material.color = Color.white;

        if (material.HasProperty("_ZWrite"))
            material.SetFloat("_ZWrite", 0f);

        if (material.HasProperty("_Cull"))
            material.SetFloat("_Cull", 0f);

        ambientLabButtonFaceMaterial = material;
        ambientLabButtonFaceShader = shader.name;
        return material;
    }

    private Mesh AmbientLabCreateButtonFaceMesh(
        Vector3 radial,
        Vector3 tangent,
        Vector3 normal)
    {
        // Approximate the translucent face of a real maimai button:
        // narrower on the screen side, wider at the outside edge.
        const float halfDepth = 0.034f;
        const float innerHalfWidth = 0.032f;
        const float outerHalfWidth = 0.050f;

        Vector3[] vertices = new Vector3[]
        {
            -tangent * innerHalfWidth - radial * halfDepth,
             tangent * innerHalfWidth - radial * halfDepth,
             tangent * outerHalfWidth + radial * halfDepth,
            -tangent * outerHalfWidth + radial * halfDepth
        };

        Mesh mesh = new Mesh();
        mesh.name = "MaiMaiVR_Ambient_ButtonFace";
        mesh.vertices = vertices;
        mesh.triangles = new int[]
        {
            0, 2, 1,
            0, 3, 2
        };
        mesh.uv = new Vector2[]
        {
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(1f, 1f),
            new Vector2(0f, 1f)
        };
        mesh.normals = new Vector3[]
        {
            normal,
            normal,
            normal,
            normal
        };
        mesh.RecalculateBounds();
        ambientLabButtonFaceMeshes.Add(mesh);
        return mesh;
    }

    private MeshRenderer AmbientLabCreateButtonFaceObject(
        string name,
        Mesh mesh,
        Vector3 position)
    {
        if (mesh == null ||
            AmbientLabCreateButtonFaceMaterial() == null)
        {
            return null;
        }

        GameObject go =
            new GameObject(
                name,
                typeof(MeshFilter),
                typeof(MeshRenderer));

        go.transform.position = position;

        MeshFilter filter = go.GetComponent<MeshFilter>();
        filter.sharedMesh = mesh;

        MeshRenderer renderer = go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = ambientLabButtonFaceMaterial;
        renderer.shadowCastingMode =
            UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage =
            UnityEngine.Rendering.LightProbeUsage.Off;
        renderer.reflectionProbeUsage =
            UnityEngine.Rendering.ReflectionProbeUsage.Off;

        ambientLabButtonFaceObjects.Add(go);
        return renderer;
    }

    private void AmbientLabEnsureButtonFaces()
    {
        if (ambientLabButtonFacesBuilt)
            return;

        ambientLabButtonFaceBuildAttempts++;
        AmbientLabEnsureP1SourceRingLights();

        if (ambientLabP1SourceRingLights.Count < 8)
            return;

        if (!ambientLabDisplayCentersReady)
        {
            Vector3 p1;
            Vector3 p2;

            if (!AmbientLabTryFindDisplayCenter(
                    p1MaterialIds,
                    out p1) ||
                !AmbientLabTryFindDisplayCenter(
                    p2MaterialIds,
                    out p2))
            {
                return;
            }

            ambientLabP1DisplayCenter = p1;
            ambientLabP2DisplayCenter = p2;
            ambientLabDisplayCentersReady = true;
        }

        Vector3 center;
        Vector3 axisU;
        Vector3 axisV;
        float radius;

        if (!AmbientLabTryGetDiffuseRingGeometry(
                out center,
                out axisU,
                out axisV,
                out radius))
        {
            return;
        }

        Vector3 normal =
            Vector3.Cross(axisU, axisV).normalized;

        if (hmdCamera != null)
        {
            Vector3 toViewer =
                hmdCamera.transform.position - center;

            if (Vector3.Dot(normal, toViewer) < 0f)
                normal = -normal;
        }

        Vector3 translation =
            ambientLabP2DisplayCenter -
            ambientLabP1DisplayCenter;

        ambientLabP1ButtonFaces.Clear();
        ambientLabP2ButtonFaces.Clear();

        for (int i = 0;
             i < ambientLabP1SourceRingLights.Count;
             i++)
        {
            Light source =
                ambientLabP1SourceRingLights[i];

            if (source == null)
                continue;

            Vector3 radial =
                source.transform.position - center;

            radial -=
                normal *
                Vector3.Dot(radial, normal);

            if (radial.sqrMagnitude < 0.000001f)
                continue;

            radial.Normalize();

            Vector3 tangent =
                Vector3.Cross(normal, radial).normalized;

            Mesh mesh =
                AmbientLabCreateButtonFaceMesh(
                    radial,
                    tangent,
                    normal);

            // A few millimetres toward the viewer prevents z-fighting with
            // the native button plastic without adding any collider.
            Vector3 p1Position =
                source.transform.position +
                normal * 0.0045f;

            MeshRenderer p1 =
                AmbientLabCreateButtonFaceObject(
                    "MaiMaiVR_P1_ButtonFace_" + i,
                    mesh,
                    p1Position);

            MeshRenderer p2 =
                AmbientLabCreateButtonFaceObject(
                    "MaiMaiVR_P2_ButtonFace_" + i,
                    mesh,
                    p1Position + translation);

            ambientLabP1ButtonFaces.Add(p1);
            ambientLabP2ButtonFaces.Add(p2);
        }

        ambientLabButtonFacesBuilt =
            ambientLabP1ButtonFaces.Count == 8 &&
            ambientLabP2ButtonFaces.Count == 8;

        Log(
            "AMBIANT_BUTTON_FACES built=" +
            ambientLabButtonFacesBuilt +
            " p1=" + ambientLabP1ButtonFaces.Count +
            " p2=" + ambientLabP2ButtonFaces.Count +
            " shader=" + ambientLabButtonFaceShader);
    }

    private Light AmbientLabCreateButtonEmissionLight(
        string name,
        Vector3 position,
        Vector3 direction)
    {
        GameObject go = new GameObject(name);
        go.transform.position = position;

        if (direction.sqrMagnitude < 0.000001f)
            direction = Vector3.forward;

        go.transform.rotation =
            Quaternion.LookRotation(direction.normalized, Vector3.up);

        Light light = go.AddComponent<Light>();
        light.type = LightType.Spot;
        light.spotAngle = 72f;
        light.color = Color.black;
        light.intensity = 0f;
        light.range = 0.095f;
        light.shadows = LightShadows.None;
        light.bounceIntensity = 0f;
        light.renderMode = LightRenderMode.Auto;
        light.enabled = false;
        return light;
    }

    private void AmbientLabEnsureButtonEmissionLights()
    {
        if (ambientLabButtonEmissionLightsBuilt)
            return;

        ambientLabButtonEmissionLightBuildAttempts++;
        AmbientLabEnsureP1SourceRingLights();

        if (ambientLabP1SourceRingLights.Count < 8)
            return;

        if (!ambientLabDisplayCentersReady)
        {
            Vector3 p1;
            Vector3 p2;

            if (!AmbientLabTryFindDisplayCenter(p1MaterialIds, out p1) ||
                !AmbientLabTryFindDisplayCenter(p2MaterialIds, out p2))
            {
                return;
            }

            ambientLabP1DisplayCenter = p1;
            ambientLabP2DisplayCenter = p2;
            ambientLabDisplayCentersReady = true;
        }

        Vector3 center;
        Vector3 axisU;
        Vector3 axisV;
        float radius;

        if (!AmbientLabTryGetDiffuseRingGeometry(
                out center,
                out axisU,
                out axisV,
                out radius))
        {
            return;
        }

        Vector3 normal = Vector3.Cross(axisU, axisV).normalized;

        if (hmdCamera != null)
        {
            Vector3 toViewer = hmdCamera.transform.position - center;
            if (Vector3.Dot(normal, toViewer) < 0f)
                normal = -normal;
        }

        Vector3 translation =
            ambientLabP2DisplayCenter - ambientLabP1DisplayCenter;

        ambientLabP1ButtonEmissionLights.Clear();
        ambientLabP2ButtonEmissionLights.Clear();

        for (int i = 0; i < 8; i++)
        {
            Light source = ambientLabP1SourceRingLights[i];
            if (source == null)
                continue;

            // Place the light just in front of the physical button and aim it
            // back at the cabinet. Tiny range prevents the old whole-ring wash.
            Vector3 p1Position =
                source.transform.position + normal * 0.024f;

            Light p1 = AmbientLabCreateButtonEmissionLight(
                "MaiMaiVR_P1_ButtonEmission_" + i,
                p1Position,
                -normal);

            Light p2 = AmbientLabCreateButtonEmissionLight(
                "MaiMaiVR_P2_ButtonEmission_" + i,
                p1Position + translation,
                -normal);

            ambientLabP1ButtonEmissionLights.Add(p1);
            ambientLabP2ButtonEmissionLights.Add(p2);
        }

        ambientLabButtonEmissionLightsBuilt =
            ambientLabP1ButtonEmissionLights.Count == 8 &&
            ambientLabP2ButtonEmissionLights.Count == 8;

        Log(
            "AMBIANT_BUTTON_EMISSION_LIGHTS built=" +
            ambientLabButtonEmissionLightsBuilt +
            " p1=" + ambientLabP1ButtonEmissionLights.Count +
            " p2=" + ambientLabP2ButtonEmissionLights.Count +
            " mode=LOCAL_SPOT_NO_MESH");
    }

    private void AmbientLabUpdateButtonEmissionLights()
    {
        if (!ambientLabButtonEmissionLightsBuilt || ambientLabConfig == null)
            return;

        AmbientLabEnsureP1SourceRingLights();

        int count = Mathf.Min(
            ambientLabP1SourceRingLights.Count,
            ambientLabP1ButtonEmissionLights.Count);

        float strength = Mathf.Clamp(ambientLabConfig.ButtonEmission, 0f, 1.5f);
        float intensity = strength * 0.22f;
        bool userEnabled =
            ambientLabConfig.ButtonEmissionEnabled &&
            strength > 0.0001f;

        for (int i = 0; i < count; i++)
        {
            Light source = ambientLabP1SourceRingLights[i];
            Light p1 = ambientLabP1ButtonEmissionLights[i];
            Light p2 =
                i < ambientLabP2ButtonEmissionLights.Count
                    ? ambientLabP2ButtonEmissionLights[i]
                    : null;

            if (source == null)
                continue;

            bool sourceActive =
                source.enabled &&
                source.color.maxColorComponent > 0.005f;

            bool enabled = userEnabled && sourceActive;

            if (p1 != null)
            {
                p1.color = source.color;
                p1.intensity = enabled ? intensity : 0f;
                p1.enabled = enabled;
            }

            if (p2 != null)
            {
                p2.color = source.color;
                p2.intensity = enabled ? intensity : 0f;
                p2.enabled = enabled;
            }
        }

        ambientLabButtonEmissionLightUpdates++;
    }

    private void AmbientLabSetButtonFaceColor(
        MeshRenderer renderer,
        Color sourceColor,
        bool sourceActive)
    {
        if (renderer == null ||
            ambientLabConfig == null)
        {
            return;
        }

        float strength =
            Mathf.Clamp(
                ambientLabConfig.ButtonEmission,
                0f,
                1.5f);

        bool enabled =
            ambientLabConfig.ButtonEmissionEnabled &&
            sourceActive &&
            sourceColor.maxColorComponent > 0.005f &&
            strength > 0.0001f;

        renderer.enabled = enabled;

        if (!enabled)
            return;

        float max =
            Mathf.Max(
                sourceColor.r,
                Mathf.Max(
                    sourceColor.g,
                    sourceColor.b));

        Color hue =
            max > 0.0001f
                ? sourceColor / max
                : Color.white;

        float normalized =
            Mathf.Clamp01(strength / 1.5f);

        Color visible =
            new Color(
                hue.r * (0.22f + normalized * 1.30f),
                hue.g * (0.22f + normalized * 1.30f),
                hue.b * (0.22f + normalized * 1.30f),
                0.10f + normalized * 0.62f);

        MaterialPropertyBlock block =
            new MaterialPropertyBlock();

        renderer.GetPropertyBlock(block);
        block.SetColor("_Color", visible);
        block.SetColor("_TintColor", visible);
        renderer.SetPropertyBlock(block);
    }

    private void AmbientLabUpdateButtonFaces()
    {
        if (!ambientLabButtonFacesBuilt)
            return;

        AmbientLabEnsureP1SourceRingLights();

        int count =
            Mathf.Min(
                ambientLabP1SourceRingLights.Count,
                ambientLabP1ButtonFaces.Count);

        for (int i = 0; i < count; i++)
        {
            Light source =
                ambientLabP1SourceRingLights[i];

            if (source == null)
                continue;

            bool active =
                source.enabled &&
                source.color.maxColorComponent > 0.005f;

            AmbientLabSetButtonFaceColor(
                ambientLabP1ButtonFaces[i],
                source.color,
                active);

            if (i < ambientLabP2ButtonFaces.Count)
            {
                AmbientLabSetButtonFaceColor(
                    ambientLabP2ButtonFaces[i],
                    source.color,
                    active);
            }
        }

        ambientLabButtonFaceUpdates++;
    }

    private void AmbientLabUpdateButtonTipPlacement()
    {
        if (ambientLabConfig == null)
            return;

        AmbientLabEnsureP1SourceRingLights();

        int count = Mathf.Min(buttonHaloLights.Count, ambientLabP1SourceRingLights.Count);
        if (count < 1)
            return;

        Vector3 center = Vector3.zero;
        int validCenter = 0;

        for (int i = 0; i < count; i++)
        {
            Light source = ambientLabP1SourceRingLights[i];
            if (source == null)
                continue;

            center += source.transform.position;
            validCenter++;
        }

        if (validCenter < 1)
            return;

        center /= validCenter;

        Vector3 firstRadial = Vector3.zero;
        for (int i = 0; i < count; i++)
        {
            Light source = ambientLabP1SourceRingLights[i];
            if (source == null)
                continue;

            Vector3 r = source.transform.position - center;
            if (r.sqrMagnitude > 0.000001f)
            {
                firstRadial = r.normalized;
                break;
            }
        }

        Vector3 ringNormal = Vector3.zero;
        float bestCrossSq = 0f;

        for (int i = 0; i < count; i++)
        {
            Light source = ambientLabP1SourceRingLights[i];
            if (source == null)
                continue;

            Vector3 r = source.transform.position - center;
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
            ringNormal = ambientLabP1SourceRingLights[0] != null
                ? ambientLabP1SourceRingLights[0].transform.forward
                : Vector3.forward;

        ringNormal.Normalize();

        if (hmdCamera != null)
        {
            Vector3 toViewer = hmdCamera.transform.position - center;
            if (Vector3.Dot(ringNormal, toViewer) < 0f)
                ringNormal = -ringNormal;
        }

        if (!ambientLabDisplayCentersReady)
        {
            Vector3 p1;
            Vector3 p2;

            if (AmbientLabTryFindDisplayCenter(p1MaterialIds, out p1) &&
                AmbientLabTryFindDisplayCenter(p2MaterialIds, out p2))
            {
                ambientLabP1DisplayCenter = p1;
                ambientLabP2DisplayCenter = p2;
                ambientLabDisplayCentersReady = true;
            }
        }

        Vector3 translation = ambientLabDisplayCentersReady
            ? ambientLabP2DisplayCenter - ambientLabP1DisplayCenter
            : Vector3.zero;

        float localDiameterOffset = ambientLabConfig.ButtonTipDiameter;
        float ringOffset = ambientLabConfig.ButtonTipRingOffset;

        float averageRadius = 0f;
        int radiusSamples = 0;

        for (int i = 0; i < count; i++)
        {
            Light source = ambientLabP1SourceRingLights[i];
            if (source == null)
                continue;

            Vector3 rr = source.transform.position - center;
            float mag = rr.magnitude;
            if (mag > 0.0001f)
            {
                averageRadius += mag;
                radiusSamples++;
            }
        }

        if (radiusSamples > 0)
            averageRadius /= radiusSamples;

        for (int i = 0; i < count; i++)
        {
            Light source = ambientLabP1SourceRingLights[i];
            Light halo = i < buttonHaloLights.Count ? buttonHaloLights[i] : null;
            if (source == null || halo == null)
                continue;

            Vector3 radial = source.transform.position - center;
            if (radial.sqrMagnitude < 0.000001f)
                radial = source.transform.right;
            else
                radial.Normalize();

            float targetRadius = averageRadius + ringOffset;

            Vector3 tipPosition =
                center +
                radial * targetRadius +
                radial * localDiameterOffset +
                ringNormal * 0.050f;

            Vector3 outerCabinetTarget =
                source.transform.position +
                radial * ButtonHaloOutwardOffsetMeters -
                ringNormal * 0.006f;

            Vector3 tipDirection = outerCabinetTarget - tipPosition;
            if (tipDirection.sqrMagnitude < 0.000001f)
                tipDirection = radial - ringNormal;

            halo.transform.position = tipPosition;
            halo.transform.rotation =
                Quaternion.LookRotation(tipDirection.normalized, radial);
            halo.spotAngle = Mathf.Clamp(72f + localDiameterOffset * 260f, 38f, 98f);

            if (i < ambientLabP2ButtonLights.Count)
            {
                Light clone = ambientLabP2ButtonLights[i];
                if (clone != null)
                {
                    clone.transform.position = tipPosition + translation;
                    clone.transform.rotation = halo.transform.rotation;
                    clone.spotAngle = halo.spotAngle;
                }
            }
        }
    }

    private void AmbientLabApplyLiveLightValues()
    {
        if (ambientLabConfig == null)
            return;

        AmbientLabUpdateButtonTipPlacement();

        // Button tip lights are independent from MASTER.
        for (int i = 0; i < buttonHaloLights.Count; i++)
        {
            Light halo = buttonHaloLights[i];

            if (halo == null)
                continue;

            bool enabled =
                ambientLabConfig.ButtonTipLightEnabled;

            halo.intensity =
                enabled
                    ? ambientLabConfig.ButtonTipIntensity
                    : 0f;

            halo.range =
                ambientLabConfig.ButtonTipRange;

            halo.enabled =
                enabled &&
                halo.color.maxColorComponent > 0.005f;
        }

        // Legacy eight contour spots remain geometry reference only.
        for (int i = 0; i < ringContourHaloLights.Count; i++)
        {
            Light halo = ringContourHaloLights[i];

            if (halo == null)
                continue;

            halo.intensity = 0f;
            halo.enabled = false;
        }

        // V0.3.1.5: synthetic Top/Side Spot lights are disabled. The real
        // cyan/red-orange cabinet materials are now the single authoritative
        // Top + Side Glow target inside AmbientLabApplySurfaceOverrides().
        for (int i = 0; i < cabinetAccentLights.Count; i++)
        {
            Light light = cabinetAccentLights[i];
            if (light == null)
                continue;

            light.intensity = 0f;
            light.enabled = false;
        }
    }

    private bool AmbientLabTryFindDisplayCenter(HashSet<int> materialIds, out Vector3 center)
    {
        center = Vector3.zero;

        if (materialIds == null || materialIds.Count < 1)
            return false;

        Renderer[] renderers = Resources.FindObjectsOfTypeAll<Renderer>();
        int count = 0;

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null || renderer.gameObject == null || !renderer.gameObject.scene.IsValid())
                continue;

            Material[] materials;
            try { materials = renderer.materials; }
            catch { continue; }

            bool match = false;
            for (int m = 0; m < materials.Length; m++)
            {
                Material mat = materials[m];
                if (mat != null && materialIds.Contains(mat.GetInstanceID()))
                {
                    match = true;
                    break;
                }
            }

            if (!match)
                continue;

            center += renderer.bounds.center;
            count++;
        }

        if (count < 1)
            return false;

        center /= count;
        return true;
    }

    private Light AmbientLabCloneLightTranslated(Light source, string name, Vector3 translation)
    {
        if (source == null)
            return null;

        GameObject go = new GameObject(name);
        go.transform.position = source.transform.position + translation;
        go.transform.rotation = source.transform.rotation;

        Light clone = go.AddComponent<Light>();
        clone.type = source.type;
        clone.spotAngle = source.spotAngle;
        clone.color = source.color;
        clone.intensity = source.intensity;
        clone.range = source.range;
        clone.shadows = LightShadows.None;
        clone.bounceIntensity = 0f;
        clone.renderMode = LightRenderMode.Auto;
        return clone;
    }

    private void AmbientLabEnsureP2SourceLightMirror()
    {
        // V0.3.1.5: intentionally DO NOT clone RingLeds / BodyLed / DisplayLed.
        // Those real P1 lights illuminated the complete P2 cabinet and made it
        // blink/change colour with the buttons. We only retain P1 RingLeds as
        // colour telemetry for strict P1/P2 button emission.
        AmbientLabEnsureP1SourceRingLights();
    }

    private void AmbientLabEnsureP2Mirror()
    {
        if (ambientLabP2MirrorBuilt)
            return;

        ambientLabP2MirrorBuildAttempts++;

        if (buttonHaloLights.Count < 4 || ringContourHaloLights.Count < 4)
            return;

        if (!ambientLabDisplayCentersReady)
        {
            Vector3 p1;
            Vector3 p2;

            if (!AmbientLabTryFindDisplayCenter(p1MaterialIds, out p1) ||
                !AmbientLabTryFindDisplayCenter(p2MaterialIds, out p2))
            {
                return;
            }

            ambientLabP1DisplayCenter = p1;
            ambientLabP2DisplayCenter = p2;
            ambientLabDisplayCentersReady = true;
        }

        Vector3 translation = ambientLabP2DisplayCenter - ambientLabP1DisplayCenter;

        AmbientLabEnsureP2SourceLightMirror();

        for (int i = 0; i < buttonHaloLights.Count; i++)
        {
            Light c = AmbientLabCloneLightTranslated(
                buttonHaloLights[i],
                "MaiMaiVR_P2_ButtonHalo_" + i,
                translation);

            if (c != null)
                ambientLabP2ButtonLights.Add(c);
        }

        for (int i = 0; i < cabinetAccentLights.Count; i++)
        {
            Light source = cabinetAccentLights[i];
            Light c = AmbientLabCloneLightTranslated(
                source,
                "MaiMaiVR_P2_" + source.gameObject.name,
                translation);

            if (c != null)
                ambientLabP2AccentLights.Add(c);
        }

        ambientLabP2MirrorBuilt = ambientLabP2ButtonLights.Count >= 4;

        Log("AMBIANT_P2_MIRROR built=" + ambientLabP2MirrorBuilt +
            " button=" + ambientLabP2ButtonLights.Count +
            " contour=" + ambientLabP2ContourLights.Count +
            " accent=" + ambientLabP2AccentLights.Count +
            " sourceRing=" + ambientLabP2SourceRingLights.Count +
            " body=" + (ambientLabP2BodyLed != null) +
            " display=" + (ambientLabP2DisplayLed != null) +
            " translation=" + translation.ToString("F3"));
    }

    private void AmbientLabUpdateP2ButtonMaterials()
    {
        if (!ambientLabP2MirrorBuilt || ambientLabP2ButtonLights.Count < 1)
            return;

        float desiredEmission = AmbientLabButtonEmissionValue;

        foreach (AmbientMaterialState state in ambientLabMaterials.Values)
        {
            if (state == null || state.Renderer == null || state.Material == null ||
                state.Category != AmbientSurfaceCategory.Button || !state.HasEmission)
            {
                continue;
            }

            Vector3 pos = state.Renderer.bounds.center;
            float d1 = Vector3.Distance(pos, ambientLabP1DisplayCenter);
            float d2 = Vector3.Distance(pos, ambientLabP2DisplayCenter);

            if (d2 >= d1)
                continue;

            Light nearest = null;
            float best = float.MaxValue;

            for (int i = 0; i < ambientLabP2ButtonLights.Count; i++)
            {
                Light light = ambientLabP2ButtonLights[i];
                if (light == null)
                    continue;

                float d = Vector3.Distance(pos, light.transform.position);
                if (d < best)
                {
                    best = d;
                    nearest = light;
                }
            }

            if (nearest == null)
                continue;

            try
            {
                Color emission = nearest.color;
                float max = Mathf.Max(emission.r, Mathf.Max(emission.g, emission.b));

                if (!nearest.enabled || nearest.color.maxColorComponent <= 0.005f ||
                    desiredEmission <= 0.0001f)
                    emission = Color.black;
                else if (max > desiredEmission && max > 0.0001f)
                    emission *= desiredEmission / max;

                state.Material.SetColor("_EmissionColor", emission);

                if (desiredEmission > 0.0001f)
                    state.Material.EnableKeyword("_EMISSION");
                else
                    state.Material.DisableKeyword("_EMISSION");
            }
            catch { }
        }
    }

    private void AmbientLabUpdateP2Mirror()
    {
        if (!ambientLabP2MirrorBuilt ||
            ambientLabConfig == null)
        {
            return;
        }

        AmbientLabUpdateButtonTipPlacement();

        // P2 gets only local button-tip and cabinet-accent lights.
        // No P1 RingLed/BodyLed/DisplayLed source light is cloned.
        for (int i = 0; i < ambientLabP2ButtonLights.Count; i++)
        {
            Light clone = ambientLabP2ButtonLights[i];
            Light source =
                i < buttonHaloLights.Count
                    ? buttonHaloLights[i]
                    : null;

            if (clone == null || source == null)
                continue;

            clone.color = source.color;

            bool enabled =
                ambientLabConfig.ButtonTipLightEnabled;

            clone.intensity =
                enabled
                    ? ambientLabConfig.ButtonTipIntensity
                    : 0f;

            clone.range =
                ambientLabConfig.ButtonTipRange;

            clone.enabled =
                enabled &&
                clone.color.maxColorComponent > 0.005f;
        }

        for (int i = 0; i < ambientLabP2AccentLights.Count; i++)
        {
            Light clone = ambientLabP2AccentLights[i];
            if (clone == null)
                continue;

            clone.intensity = 0f;
            clone.enabled = false;
        }
    }

    private static string AmbientLabCleanMaterialName(
        string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        string s =
            value.ToLowerInvariant()
                .Replace("(instance)", string.Empty)
                .Replace(" ", string.Empty);

        return s;
    }

    private static int AmbientLabExplicitMaterialSide(
        string cleanName)
    {
        if (string.IsNullOrEmpty(cleanName))
            return 0;

        bool p1 =
            cleanName.Contains("bodyp1") ||
            cleanName.Contains("headp1");

        bool p2 =
            cleanName.Contains("bodyp2") ||
            cleanName.Contains("headp2");

        if (p1 == p2)
            return 0;

        return p1 ? 1 : 2;
    }

    private static string AmbientLabExplicitMaterialKey(
        string cleanName)
    {
        if (string.IsNullOrEmpty(cleanName))
            return string.Empty;

        return cleanName
            .Replace("bodyp1", "bodyp#")
            .Replace("bodyp2", "bodyp#")
            .Replace("headp1", "headp#")
            .Replace("headp2", "headp#");
    }

    private AmbientMaterialState AmbientLabCreateDirectMaterialState(
        Renderer renderer,
        Material material,
        int materialIndex)
    {
        if (renderer == null || material == null)
            return null;

        AmbientMaterialState state =
            new AmbientMaterialState();

        state.Renderer = renderer;
        state.Material = material;
        state.MaterialIndex = materialIndex;
        state.Category = AmbientSurfaceCategory.CabinetBody;

        try
        {
            if (material.HasProperty("_BaseColor"))
            {
                state.ColorProperty = "_BaseColor";
                state.BaseColor =
                    material.GetColor("_BaseColor");
            }
            else if (material.HasProperty("_Color"))
            {
                state.ColorProperty = "_Color";
                state.BaseColor =
                    material.GetColor("_Color");
            }
            else
            {
                state.ColorProperty = null;
                state.BaseColor = Color.white;
            }

            state.HasEmission =
                material.HasProperty("_EmissionColor");

            state.BaseEmission =
                state.HasEmission
                    ? material.GetColor("_EmissionColor")
                    : Color.black;

            state.BaseEmissionKeyword =
                material.IsKeywordEnabled("_EMISSION");
        }
        catch
        {
            state.HasEmission = false;
            state.BaseEmission = Color.black;
        }

        return state;
    }

    private void AmbientLabEnsureExplicitP2Pairs()
    {
        if (ambientLabExplicitP2Pairs.Count > 0)
            return;

        ambientLabExplicitP2PairRetryTimer -=
            Time.unscaledDeltaTime;

        if (ambientLabExplicitP2PairRetryTimer > 0f)
            return;

        ambientLabExplicitP2PairRetryTimer = 2.0f;
        ambientLabExplicitP2PairBuilds++;

        Dictionary<string, AmbientMaterialState> p1 =
            new Dictionary<string, AmbientMaterialState>();

        Dictionary<string, AmbientMaterialState> p2 =
            new Dictionary<string, AmbientMaterialState>();

        Renderer[] renderers =
            Resources.FindObjectsOfTypeAll<Renderer>();

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];

            if (renderer == null ||
                renderer.gameObject == null ||
                !renderer.gameObject.scene.IsValid())
            {
                continue;
            }

            string path =
                GetPath(renderer.transform)
                    .ToLowerInvariant();

            if (path.Contains("display p1") ||
                path.Contains("display p2") ||
                path.Contains("camera") ||
                path.Contains("controller") ||
                path.Contains("hand") ||
                path.Contains("canvas") ||
                path.Contains("maimaivr_"))
            {
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

            for (int m = 0; m < materials.Length; m++)
            {
                Material material = materials[m];

                if (material == null)
                    continue;

                string clean =
                    AmbientLabCleanMaterialName(
                        material.name);

                int side =
                    AmbientLabExplicitMaterialSide(clean);

                if (side == 0)
                    continue;

                string key =
                    AmbientLabExplicitMaterialKey(clean) +
                    "|" +
                    m.ToString(
                        CultureInfo.InvariantCulture);

                AmbientMaterialState state =
                    AmbientLabCreateDirectMaterialState(
                        renderer,
                        material,
                        m);

                if (state == null)
                    continue;

                if (side == 1)
                {
                    if (!p1.ContainsKey(key))
                        p1[key] = state;
                }
                else
                {
                    if (!p2.ContainsKey(key))
                        p2[key] = state;
                }
            }
        }

        foreach (
            KeyValuePair<string, AmbientMaterialState>
            item in p1)
        {
            AmbientMaterialState mate;

            if (!p2.TryGetValue(
                    item.Key,
                    out mate))
            {
                continue;
            }

            AmbientMaterialPair pair =
                new AmbientMaterialPair();

            pair.P1 = item.Value;
            pair.P2 = mate;
            ambientLabExplicitP2Pairs.Add(pair);

            Log(
                "AMBIANT_P2_EXPLICIT_PAIR key=" +
                item.Key +
                " p1=" +
                GetPath(item.Value.Renderer.transform) +
                "/" +
                item.Value.Material.name +
                " p2=" +
                GetPath(mate.Renderer.transform) +
                "/" +
                mate.Material.name);
        }

        Log(
            "AMBIANT_P2_EXPLICIT_PAIRS count=" +
            ambientLabExplicitP2Pairs.Count +
            " p1Candidates=" + p1.Count +
            " p2Candidates=" + p2.Count);
    }

    private void AmbientLabApplyExplicitP2Parity()
    {
        if (ambientLabExplicitP2Pairs.Count < 1)
            return;

        int updates = 0;

        for (int i = 0;
             i < ambientLabExplicitP2Pairs.Count;
             i++)
        {
            AmbientMaterialPair pair =
                ambientLabExplicitP2Pairs[i];

            if (pair == null ||
                pair.P1 == null ||
                pair.P2 == null ||
                pair.P1.Material == null ||
                pair.P2.Material == null)
            {
                continue;
            }

            try
            {
                string p1Color = pair.P1.ColorProperty;
                string p2Color = pair.P2.ColorProperty;

                if (!string.IsNullOrEmpty(p1Color) &&
                    !string.IsNullOrEmpty(p2Color) &&
                    pair.P1.Material.HasProperty(p1Color) &&
                    pair.P2.Material.HasProperty(p2Color))
                {
                    Color c =
                        pair.P1.Material.GetColor(p1Color);

                    pair.P2.Material.SetColor(
                        p2Color,
                        c);
                }

                if (pair.P1.HasEmission &&
                    pair.P2.HasEmission &&
                    pair.P1.Material.HasProperty("_EmissionColor") &&
                    pair.P2.Material.HasProperty("_EmissionColor"))
                {
                    Color source =
                        pair.P1.Material.GetColor(
                            "_EmissionColor");

                    // Neutralize hue before copying. P2 body can match P1
                    // brightness but can never inherit button RGB flashes.
                    float gray =
                        (source.r +
                         source.g +
                         source.b) /
                        3f;

                    Color neutral =
                        new Color(
                            gray,
                            gray,
                            gray,
                            1f);

                    pair.P2.Material.SetColor(
                        "_EmissionColor",
                        neutral);

                    if (gray > 0.0001f)
                        pair.P2.Material.EnableKeyword("_EMISSION");
                    else
                        pair.P2.Material.DisableKeyword("_EMISSION");
                }

                updates++;
            }
            catch { }
        }

        ambientLabExplicitP2PairUpdates += updates;
    }

    private static string AmbientLabNormalizePlayerKey(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        string s = value.ToLowerInvariant().Replace('\\', '/');
        s = s.Replace("player 1", "player#");
        s = s.Replace("player 2", "player#");
        s = s.Replace("player1", "player#");
        s = s.Replace("player2", "player#");
        s = s.Replace("p1", "p#");
        s = s.Replace("p2", "p#");
        return s;
    }

    private int AmbientLabGetMaterialPlayerSide(AmbientMaterialState state)
    {
        if (state == null || state.Renderer == null || state.Material == null)
            return 0;

        string text = (GetPath(state.Renderer.transform) + "|" + state.Material.name).ToLowerInvariant();

        bool p1 = text.Contains("p1") || text.Contains("player1") || text.Contains("player 1");
        bool p2 = text.Contains("p2") || text.Contains("player2") || text.Contains("player 2");

        if (p1 != p2)
            return p1 ? 1 : 2;

        // Some imported P1/P2 shell meshes use identical material/object names.
        // When that happens, use their physical proximity to the already-known
        // live display centers, but only for cabinet-looking geometry.
        bool cabinetLike =
            state.Category == AmbientSurfaceCategory.CabinetBody ||
            state.Category == AmbientSurfaceCategory.Ring ||
            text.Contains("cabinet") || text.Contains("body") ||
            text.Contains("head") || text.Contains("ring");

        if (!cabinetLike || !ambientLabDisplayCentersReady)
            return 0;

        try
        {
            Vector3 pos = state.Renderer.bounds.center;
            float d1 = Vector3.Distance(pos, ambientLabP1DisplayCenter);
            float d2 = Vector3.Distance(pos, ambientLabP2DisplayCenter);

            if (Mathf.Abs(d1 - d2) < 0.10f)
                return 0;

            return d1 < d2 ? 1 : 2;
        }
        catch
        {
            return 0;
        }
    }

    private static string AmbientLabGetMaterialPairKey(AmbientMaterialState state, bool pathAware)
    {
        if (state == null || state.Renderer == null || state.Material == null)
            return string.Empty;

        string material = AmbientLabNormalizePlayerKey(state.Material.name);
        string index = state.MaterialIndex.ToString(CultureInfo.InvariantCulture);

        if (!pathAware)
            return material + "|" + index;

        string path = AmbientLabNormalizePlayerKey(GetPath(state.Renderer.transform));
        return path + "|" + material + "|" + index;
    }

    private void AmbientLabRebuildP2MaterialPairs()
    {
        ambientLabP2MaterialPairs.Clear();

        Dictionary<string, AmbientMaterialState> p1Exact = new Dictionary<string, AmbientMaterialState>();
        Dictionary<string, AmbientMaterialState> p1Fallback = new Dictionary<string, AmbientMaterialState>();
        List<AmbientMaterialState> p2States = new List<AmbientMaterialState>();

        foreach (AmbientMaterialState state in ambientLabMaterials.Values)
        {
            if (state == null || state.Material == null || state.Renderer == null ||
                state.Category == AmbientSurfaceCategory.Button ||
                state.Category == AmbientSurfaceCategory.Ignore)
            {
                continue;
            }

            int side = AmbientLabGetMaterialPlayerSide(state);

            if (side == 1)
            {
                string exact = AmbientLabGetMaterialPairKey(state, true);
                string fallback = AmbientLabGetMaterialPairKey(state, false);

                if (!string.IsNullOrEmpty(exact) && !p1Exact.ContainsKey(exact))
                    p1Exact[exact] = state;

                if (!string.IsNullOrEmpty(fallback) && !p1Fallback.ContainsKey(fallback))
                    p1Fallback[fallback] = state;
            }
            else if (side == 2)
            {
                p2States.Add(state);
            }
        }

        for (int i = 0; i < p2States.Count; i++)
        {
            AmbientMaterialState p2 = p2States[i];
            AmbientMaterialState p1 = null;

            string exact = AmbientLabGetMaterialPairKey(p2, true);
            string fallback = AmbientLabGetMaterialPairKey(p2, false);

            if (!string.IsNullOrEmpty(exact))
                p1Exact.TryGetValue(exact, out p1);

            if (p1 == null && !string.IsNullOrEmpty(fallback))
                p1Fallback.TryGetValue(fallback, out p1);

            if (p1 == null || p1.Material == null || p1.Material == p2.Material)
                continue;

            AmbientMaterialPair pair = new AmbientMaterialPair();
            pair.P1 = p1;
            pair.P2 = p2;
            ambientLabP2MaterialPairs.Add(pair);
        }

        ambientLabP2MaterialPairCount = ambientLabP2MaterialPairs.Count;
    }

    private void AmbientLabApplyP2MaterialParity()
    {
        if (ambientLabP2MaterialPairs.Count < 1)
            return;

        int updates = 0;

        for (int i = 0; i < ambientLabP2MaterialPairs.Count; i++)
        {
            AmbientMaterialPair pair = ambientLabP2MaterialPairs[i];

            if (pair == null || pair.P1 == null || pair.P2 == null ||
                pair.P1.Material == null || pair.P2.Material == null)
            {
                continue;
            }

            try
            {
                if (!string.IsNullOrEmpty(pair.P1.ColorProperty) &&
                    !string.IsNullOrEmpty(pair.P2.ColorProperty) &&
                    pair.P1.Material.HasProperty(pair.P1.ColorProperty) &&
                    pair.P2.Material.HasProperty(pair.P2.ColorProperty))
                {
                    Color c = pair.P1.Material.GetColor(pair.P1.ColorProperty);
                    pair.P2.Material.SetColor(pair.P2.ColorProperty, c);
                }

                updates++;
            }
            catch { }
        }

        ambientLabP2MaterialParityUpdates += updates;
    }

    private bool AmbientLabTryGetDiffuseRingGeometry(
        out Vector3 center,
        out Vector3 axisU,
        out Vector3 axisV,
        out float radius)
    {
        center = Vector3.zero;
        axisU = Vector3.right;
        axisV = Vector3.up;
        radius = 0f;

        List<Vector3> positions = new List<Vector3>();

        for (int i = 0; i < ringContourHaloLights.Count; i++)
        {
            Light l = ringContourHaloLights[i];
            if (l != null)
                positions.Add(l.transform.position);
        }

        // Native IO must not make AMBIANT geometry depend on the legacy
        // serial LightManager path. If the legacy contour helpers have not
        // been built yet, derive the exact same circle directly from the
        // eight serialized RingLed transforms.
        if (positions.Count < 4)
        {
            AmbientLabEnsureP1SourceRingLights();
            positions.Clear();

            for (int i = 0; i < ambientLabP1SourceRingLights.Count; i++)
            {
                Light source = ambientLabP1SourceRingLights[i];
                if (source != null)
                    positions.Add(source.transform.position);
            }
        }

        if (positions.Count < 4)
            return false;

        for (int i = 0; i < positions.Count; i++)
            center += positions[i];

        center /= positions.Count;

        Vector3 first = Vector3.zero;
        for (int i = 0; i < positions.Count; i++)
        {
            Vector3 v = positions[i] - center;
            if (v.sqrMagnitude > 0.000001f)
            {
                first = v.normalized;
                break;
            }
        }

        if (first.sqrMagnitude < 0.5f)
            return false;

        Vector3 bestCross = Vector3.zero;
        float bestCrossSq = 0f;

        for (int i = 0; i < positions.Count; i++)
        {
            Vector3 v = positions[i] - center;
            if (v.sqrMagnitude < 0.000001f)
                continue;

            Vector3 cross = Vector3.Cross(first, v.normalized);
            float sq = cross.sqrMagnitude;

            if (sq > bestCrossSq)
            {
                bestCrossSq = sq;
                bestCross = cross;
            }
        }

        if (bestCrossSq < 0.0001f)
        {
            bestCross = Vector3.Cross(first, Vector3.up);
            if (bestCross.sqrMagnitude < 0.0001f)
                bestCross = Vector3.Cross(first, Vector3.right);
        }

        Vector3 normal = bestCross.normalized;
        axisU = first;
        axisV = Vector3.Cross(normal, axisU).normalized;

        for (int i = 0; i < positions.Count; i++)
            radius += Vector3.Distance(positions[i], center);

        radius /= positions.Count;
        return radius > 0.05f;
    }

    private Light AmbientLabCreateWhiteHaloLight(
        string name,
        Vector3 position)
    {
        GameObject go = new GameObject(name);
        go.transform.position = position;

        Light light = go.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = Color.white;
        light.intensity = 0f;
        light.range = 0.18f;
        light.shadows = LightShadows.None;
        light.bounceIntensity = 0f;
        light.renderMode = LightRenderMode.Auto;
        return light;
    }

    private void AmbientLabDestroyWhiteHaloLights()
    {
        for (int i = 0; i < ambientLabP1WhiteHaloLights.Count; i++)
        {
            Light light = ambientLabP1WhiteHaloLights[i];
            if (light != null)
                UnityEngine.Object.Destroy(light.gameObject);
        }

        for (int i = 0; i < ambientLabP2WhiteHaloLights.Count; i++)
        {
            Light light = ambientLabP2WhiteHaloLights[i];
            if (light != null)
                UnityEngine.Object.Destroy(light.gameObject);
        }

        ambientLabP1WhiteHaloLights.Clear();
        ambientLabP2WhiteHaloLights.Clear();
        ambientLabWhiteHaloBuilt = false;
    }

    private void AmbientLabEnsureWhiteHaloLights()
    {
        if (ambientLabConfig == null)
            return;

        int targetCount =
            Mathf.Clamp(
                ambientLabConfig.WhiteHaloPointCount,
                8,
                96);

        if (ambientLabWhiteHaloBuilt &&
            ambientLabP1WhiteHaloLights.Count == targetCount &&
            ambientLabP2WhiteHaloLights.Count == targetCount)
        {
            return;
        }

        ambientLabWhiteHaloBuildAttempts++;

        if (ambientLabP1WhiteHaloLights.Count > 0 ||
            ambientLabP2WhiteHaloLights.Count > 0)
        {
            AmbientLabDestroyWhiteHaloLights();
            ambientLabWhiteHaloRebuilds++;
        }

        if (!ambientLabDisplayCentersReady)
        {
            Vector3 p1;
            Vector3 p2;

            if (!AmbientLabTryFindDisplayCenter(
                    p1MaterialIds,
                    out p1) ||
                !AmbientLabTryFindDisplayCenter(
                    p2MaterialIds,
                    out p2))
            {
                return;
            }

            ambientLabP1DisplayCenter = p1;
            ambientLabP2DisplayCenter = p2;
            ambientLabDisplayCentersReady = true;
        }

        Vector3 center;
        Vector3 axisU;
        Vector3 axisV;
        float radius;

        if (!AmbientLabTryGetDiffuseRingGeometry(
                out center,
                out axisU,
                out axisV,
                out radius))
        {
            return;
        }

        Vector3 normal =
            Vector3.Cross(axisU, axisV).normalized;

        if (hmdCamera != null)
        {
            Vector3 toViewer =
                hmdCamera.transform.position - center;

            if (Vector3.Dot(normal, toViewer) < 0f)
                normal = -normal;
        }

        Vector3 translation =
            ambientLabP2DisplayCenter -
            ambientLabP1DisplayCenter;

        float haloRadius =
            Mathf.Max(
                0.05f,
                radius + ambientLabConfig.WhiteHaloRadiusOffset);

        for (int i = 0; i < targetCount; i++)
        {
            float angle =
                ((float)i / targetCount) *
                Mathf.PI *
                2f;

            Vector3 radial =
                axisU * Mathf.Cos(angle) +
                axisV * Mathf.Sin(angle);

            Vector3 p1Position =
                center +
                radial * haloRadius +
                normal * 0.018f;

            Light p1 =
                AmbientLabCreateWhiteHaloLight(
                    "MaiMaiVR_P1_WhiteHalo_" + i,
                    p1Position);

            Light p2 =
                AmbientLabCreateWhiteHaloLight(
                    "MaiMaiVR_P2_WhiteHalo_" + i,
                    p1Position + translation);

            ambientLabP1WhiteHaloLights.Add(p1);
            ambientLabP2WhiteHaloLights.Add(p2);
        }

        ambientLabWhiteHaloBuilt =
            ambientLabP1WhiteHaloLights.Count == targetCount &&
            ambientLabP2WhiteHaloLights.Count == targetCount;

        Log(
            "AMBIANT_WHITE_HALO built=" +
            ambientLabWhiteHaloBuilt +
            " mode=ADJUSTABLE_POINT_LIGHTS" +
            " p1=" + ambientLabP1WhiteHaloLights.Count +
            " p2=" + ambientLabP2WhiteHaloLights.Count +
            " radius=" + haloRadius.ToString(
                "F3",
                CultureInfo.InvariantCulture) +
            " thickness=" + ambientLabConfig.WhiteHaloRange.ToString(
                "F3",
                CultureInfo.InvariantCulture));
    }

    private void AmbientLabUpdateWhiteHaloLights()
    {
        if (ambientLabConfig == null)
            return;

        int targetCount =
            Mathf.Clamp(
                ambientLabConfig.WhiteHaloPointCount,
                8,
                96);

        if (!ambientLabWhiteHaloBuilt ||
            ambientLabP1WhiteHaloLights.Count != targetCount ||
            ambientLabP2WhiteHaloLights.Count != targetCount)
        {
            AmbientLabEnsureWhiteHaloLights();

            if (!ambientLabWhiteHaloBuilt)
                return;
        }

        bool enabled =
            ambientLabConfig.WhiteHaloEnabled &&
            ambientLabConfig.WhiteHaloPower > 0.0001f;

        // Quadratic power curve: much finer manoeuvring near zero.
        // Density compensation keeps roughly comparable global brightness when
        // the user changes the number of points.
        float densityScale =
            48f / Mathf.Max(8f, (float)targetCount);

        float intensity =
            enabled
                ? ambientLabConfig.WhiteHaloPower *
                  ambientLabConfig.WhiteHaloPower *
                  0.025f *
                  densityScale
                : 0f;

        float range =
            ambientLabConfig.WhiteHaloRange;

        // Radius can be dragged live without rebuilding the lights.
        Vector3 center;
        Vector3 axisU;
        Vector3 axisV;
        float radius;

        bool geometryReady =
            AmbientLabTryGetDiffuseRingGeometry(
                out center,
                out axisU,
                out axisV,
                out radius);

        Vector3 normal = Vector3.forward;
        Vector3 translation = Vector3.zero;
        float haloRadius = 0f;

        if (geometryReady)
        {
            normal =
                Vector3.Cross(axisU, axisV).normalized;

            if (hmdCamera != null)
            {
                Vector3 toViewer =
                    hmdCamera.transform.position - center;

                if (Vector3.Dot(normal, toViewer) < 0f)
                    normal = -normal;
            }

            translation =
                ambientLabP2DisplayCenter -
                ambientLabP1DisplayCenter;

            haloRadius =
                Mathf.Max(
                    0.05f,
                    radius + ambientLabConfig.WhiteHaloRadiusOffset);
        }

        for (int i = 0;
             i < ambientLabP1WhiteHaloLights.Count;
             i++)
        {
            Light light =
                ambientLabP1WhiteHaloLights[i];

            if (light == null)
                continue;

            if (geometryReady)
            {
                float angle =
                    ((float)i / targetCount) *
                    Mathf.PI *
                    2f;

                Vector3 radial =
                    axisU * Mathf.Cos(angle) +
                    axisV * Mathf.Sin(angle);

                light.transform.position =
                    center +
                    radial * haloRadius +
                    normal * 0.018f;
            }

            light.color = Color.white;
            light.intensity = intensity;
            light.range = range;
            light.enabled = enabled;
        }

        for (int i = 0;
             i < ambientLabP2WhiteHaloLights.Count;
             i++)
        {
            Light light =
                ambientLabP2WhiteHaloLights[i];

            if (light == null)
                continue;

            if (geometryReady)
            {
                float angle =
                    ((float)i / targetCount) *
                    Mathf.PI *
                    2f;

                Vector3 radial =
                    axisU * Mathf.Cos(angle) +
                    axisV * Mathf.Sin(angle);

                light.transform.position =
                    center +
                    radial * haloRadius +
                    normal * 0.018f +
                    translation;
            }

            light.color = Color.white;
            light.intensity = intensity;
            light.range = range;
            light.enabled = enabled;
        }

        ambientLabWhiteHaloUpdates++;
    }

    private LineRenderer AmbientLabCreateDiffuseRingLayer(
        string name,
        Vector3 center,
        Vector3 axisU,
        Vector3 axisV,
        float radius,
        Vector3 translation)
    {
        Shader shader = Shader.Find("Particles/Additive");
        if (shader == null) shader = Shader.Find("Legacy Shaders/Particles/Additive");
        if (shader == null) shader = Shader.Find("Mobile/Particles/Additive");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        if (shader == null) return null;

        GameObject go = new GameObject(name);
        LineRenderer line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.loop = true;
        line.alignment = LineAlignment.View;
        line.textureMode = LineTextureMode.Stretch;
        line.numCornerVertices = 16;
        line.numCapVertices = 0;
        line.positionCount = 128;
        line.widthMultiplier = 1f;
        line.startWidth = 0.04f;
        line.endWidth = 0.04f;
        line.startColor = Color.white;
        line.endColor = Color.white;
        line.sortingOrder = 50;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;

        for (int i = 0; i < line.positionCount; i++)
        {
            float a =
                ((float)i / line.positionCount) *
                Mathf.PI *
                2f;

            Vector3 p =
                center +
                axisU * (Mathf.Cos(a) * radius) +
                axisV * (Mathf.Sin(a) * radius);

            line.SetPosition(i, p + translation);
        }

        Material material = new Material(shader);
        material.hideFlags = HideFlags.HideAndDontSave;
        material.mainTexture = Texture2D.whiteTexture;
        material.renderQueue = 3200;
        material.color = Color.white;

        if (material.HasProperty("_TintColor"))
            material.SetColor("_TintColor", Color.white);

        line.material = material;
        ambientLabDiffuseRingMaterials.Add(material);

        return line;
    }

    private void AmbientLabEnsureDiffuseRing()
    {
        if (ambientLabDiffuseRingBuilt)
            return;

        ambientLabDiffuseRingBuildAttempts++;

        if (!ambientLabDisplayCentersReady || ringContourHaloLights.Count < 4)
            return;

        Vector3 center;
        Vector3 axisU;
        Vector3 axisV;
        float radius;

        if (!AmbientLabTryGetDiffuseRingGeometry(out center, out axisU, out axisV, out radius))
            return;

        Vector3 p2Translation = ambientLabP2DisplayCenter - ambientLabP1DisplayCenter;
        string[] layerNames = new string[] { "Mist", "Outer", "Middle", "Inner", "Core" };

        for (int i = 0; i < layerNames.Length; i++)
        {
            LineRenderer p1 = AmbientLabCreateDiffuseRingLayer(
                "MaiMaiVR_P1_DiffuseRing_" + layerNames[i],
                center, axisU, axisV, radius, Vector3.zero);
            if (p1 != null) ambientLabP1DiffuseRing.Add(p1);

            LineRenderer p2 = AmbientLabCreateDiffuseRingLayer(
                "MaiMaiVR_P2_DiffuseRing_" + layerNames[i],
                center, axisU, axisV, radius, p2Translation);
            if (p2 != null) ambientLabP2DiffuseRing.Add(p2);
        }

        ambientLabDiffuseRingBuilt =
            ambientLabP1DiffuseRing.Count == 5 &&
            ambientLabP2DiffuseRing.Count == 5;

        Log("AMBIANT_DIFFUSE_RING built=" + ambientLabDiffuseRingBuilt +
            " p1Layers=" + ambientLabP1DiffuseRing.Count +
            " p2Layers=" + ambientLabP2DiffuseRing.Count +
            " radius=" + radius.ToString("F3", CultureInfo.InvariantCulture));
    }

    private static void AmbientLabApplyDiffuseRingLayer(
        LineRenderer line,
        bool enabled,
        float width,
        Color color)
    {
        if (line == null)
            return;

        line.enabled = enabled;
        line.startWidth = width;
        line.endWidth = width;
        line.startColor = color;
        line.endColor = color;
    }

    private void AmbientLabUpdateDiffuseRing()
    {
        if (!ambientLabDiffuseRingBuilt || ambientLabConfig == null)
            return;

        bool enabled = ambientLabConfig.MasterEnabled &&
            ambientLabConfig.WhiteHaloEnabled &&
            ambientLabConfig.WhiteHaloPower > 0.0001f;

        float power01 = Mathf.Clamp01(ambientLabConfig.WhiteHaloPower);
        float glow = power01;
        float width01 = Mathf.InverseLerp(0.08f, 0.50f, ambientLabConfig.WhiteHaloRange);
        float coreWidth = Mathf.Lerp(0.022f, 0.060f, width01);

        float[] widths = new float[]
        {
            coreWidth * 7.0f,
            coreWidth * 4.8f,
            coreWidth * 3.0f,
            coreWidth * 1.8f,
            coreWidth * 0.85f
        };

        float[] alphas = new float[]
        {
            0.035f * glow,
            0.075f * glow,
            0.16f * glow,
            0.34f * glow,
            0.82f * glow
        };

        for (int i = 0; i < 5; i++)
        {
            Color c = new Color(1f, 1f, 1f, alphas[i]);

            if (i < ambientLabP1DiffuseRing.Count)
                AmbientLabApplyDiffuseRingLayer(ambientLabP1DiffuseRing[i], enabled, widths[i], c);

            if (i < ambientLabP2DiffuseRing.Count)
                AmbientLabApplyDiffuseRingLayer(ambientLabP2DiffuseRing[i], enabled, widths[i], c);
        }
    }

    private Font AmbientLabGetFont()
    {
        if (ambientLabFont != null)
            return ambientLabFont;

        try { ambientLabFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
        catch { }

        if (ambientLabFont == null)
        {
            try { ambientLabFont = Font.CreateDynamicFontFromOSFont("Segoe UI", 20); }
            catch { }
        }

        if (ambientLabFont == null)
        {
            try { ambientLabFont = Font.CreateDynamicFontFromOSFont("Arial", 20); }
            catch { }
        }

        return ambientLabFont;
    }

    private string AmbientLabGetUiText(GameObject go)
    {
        if (go == null)
            return null;

        Component[] components = go.GetComponentsInChildren<Component>(true);

        for (int i = 0; i < components.Length; i++)
        {
            Component c = components[i];
            if (c == null)
                continue;

            try
            {
                PropertyInfo p = c.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (p != null && p.PropertyType == typeof(string) && p.CanRead)
                {
                    string value = p.GetValue(c, null) as string;
                    if (!string.IsNullOrWhiteSpace(value))
                        return value.Trim();
                }
            }
            catch { }
        }

        return null;
    }

    private void AmbientLabSetUiText(GameObject go, string value)
    {
        if (go == null)
            return;

        Component[] components = go.GetComponentsInChildren<Component>(true);

        for (int i = 0; i < components.Length; i++)
        {
            Component c = components[i];
            if (c == null)
                continue;

            try
            {
                PropertyInfo p = c.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (p != null && p.PropertyType == typeof(string) && p.CanWrite)
                {
                    p.SetValue(c, value, null);
                    return;
                }
            }
            catch { }
        }
    }

    private Button AmbientLabFindButton(string text)
    {
        Button[] buttons =
            Resources.FindObjectsOfTypeAll<Button>();

        Button fallback = null;

        for (int i = 0; i < buttons.Length; i++)
        {
            Button b = buttons[i];

            if (b == null ||
                b.gameObject == null ||
                !b.gameObject.scene.IsValid())
            {
                continue;
            }

            string value =
                AmbientLabGetUiText(
                    b.gameObject);

            if (!string.Equals(
                    value,
                    text,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (b.gameObject.activeInHierarchy)
                return b;

            if (fallback == null)
                fallback = b;
        }

        return fallback;
    }

    private Text AmbientLabCreateText(
        Transform parent,
        string name,
        string value,
        Vector2 pos,
        Vector2 size,
        int fontSize,
        TextAnchor anchor)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        go.transform.SetParent(parent, false);

        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;

        Text text = go.GetComponent<Text>();
        text.font = AmbientLabGetFont();
        text.fontSize = fontSize;
        text.color = Color.white;
        text.alignment = anchor;
        text.text = value;
        text.raycastTarget = false;
        return text;
    }

    private Slider AmbientLabFindNativeSliderTemplate()
    {
        if (ambientLabNativeSliderTemplate != null)
            return ambientLabNativeSliderTemplate;

        try
        {
            if (ambientLabTab1 != null)
            {
                Slider[] sliders =
                    ambientLabTab1.GetComponentsInChildren<Slider>(true);

                if (sliders != null && sliders.Length > 0)
                {
                    ambientLabNativeSliderTemplate = sliders[0];
                    return ambientLabNativeSliderTemplate;
                }
            }

            Slider[] all = Resources.FindObjectsOfTypeAll<Slider>();

            for (int i = 0; i < all.Length; i++)
            {
                Slider s = all[i];

                if (s == null ||
                    s.gameObject == null ||
                    !s.gameObject.scene.IsValid())
                {
                    continue;
                }

                if (ambientLabPanel != null &&
                    s.transform.IsChildOf(ambientLabPanel.transform))
                {
                    continue;
                }

                ambientLabNativeSliderTemplate = s;
                return s;
            }
        }
        catch { }

        return null;
    }

    private Slider AmbientLabCreateSlider(
        Transform parent,
        string label,
        float min,
        float max,
        float value,
        Vector2 pos,
        Vector2 size,
        Action<float> setter,
        string format,
        bool wholeNumbers = false)
    {
        GameObject row =
            new GameObject(
                "Ambient_" + label,
                typeof(RectTransform));

        row.transform.SetParent(parent, false);

        RectTransform rowRt = row.GetComponent<RectTransform>();
        rowRt.anchorMin =
            rowRt.anchorMax =
            new Vector2(0.5f, 0.5f);
        rowRt.pivot =
            new Vector2(0.5f, 0.5f);
        rowRt.anchoredPosition = pos;
        rowRt.sizeDelta = size;

        Text labelText = AmbientLabCreateText(
            row.transform,
            "Label",
            label + ": " +
            (wholeNumbers
                ? Mathf.RoundToInt(value).ToString(CultureInfo.InvariantCulture)
                : value.ToString(
                    format,
                    CultureInfo.InvariantCulture)),
            new Vector2(-size.x * 0.25f, 0f),
            new Vector2(size.x * 0.48f, size.y),
            12,
            TextAnchor.MiddleRight);

        Slider native = AmbientLabFindNativeSliderTemplate();

        if (native == null)
        {
            ambientLabUiStatus = "NATIVE_SLIDER_NOT_FOUND";
            return null;
        }

        GameObject sliderGo =
            UnityEngine.Object.Instantiate(
                native.gameObject,
                row.transform);

        // Native MaiDXR sliders can carry persistent Inspector callbacks such
        // as NoneVRSettingManager.SetNVRFOV. Remove the copied behavioural
        // payload before this clone becomes interactive.
        sliderGo.SetActive(false);
        sliderGo.name =
            "NativeSlider_" + label;

        MonoBehaviour[] copiedBehaviours =
            sliderGo.GetComponentsInChildren<MonoBehaviour>(true);

        for (int i = 0; i < copiedBehaviours.Length; i++)
        {
            MonoBehaviour behaviour = copiedBehaviours[i];

            if (behaviour == null ||
                behaviour is Slider)
            {
                continue;
            }

            Type behaviourType = behaviour.GetType();
            string typeName = behaviourType.Name;

            // Target only MaiDXR setting handlers. Do not broadly disable
            // third-party/UI behaviours (for example TMPro components) that
            // a future native slider template may legitimately contain.
            if (string.Equals(
                    typeName,
                    "NoneVRSettingManager",
                    StringComparison.Ordinal) ||
                typeName.EndsWith(
                    "SettingManager",
                    StringComparison.Ordinal))
            {
                behaviour.enabled = false;
                ambientLabSliderForeignBehavioursDisabled++;
                ambientLabSliderLastForeignBehaviour =
                    behaviourType.FullName;
            }
        }

        RectTransform srt =
            sliderGo.GetComponent<RectTransform>();

        srt.anchorMin =
            srt.anchorMax =
            new Vector2(0.5f, 0.5f);
        srt.pivot =
            new Vector2(0.5f, 0.5f);
        srt.anchoredPosition =
            new Vector2(size.x * 0.27f, 0f);
        srt.sizeDelta =
            new Vector2(
                size.x * 0.43f,
                Mathf.Max(
                    24f,
                    Mathf.Abs(srt.sizeDelta.y)));

        Slider slider =
            sliderGo.GetComponent<Slider>();

        // RemoveAllListeners() does not remove persistent Inspector listeners.
        // A fresh SliderEvent fully isolates AMBIANT from NVRFOV/NVRFPS.
        slider.onValueChanged = new Slider.SliderEvent();
        slider.minValue = min;
        slider.maxValue = max;
        slider.wholeNumbers = wholeNumbers;
        slider.direction =
            Slider.Direction.LeftToRight;
        slider.interactable = true;
        slider.SetValueWithoutNotify(value);
        sliderGo.SetActive(true);

        Graphic[] graphics =
            sliderGo.GetComponentsInChildren<Graphic>(true);

        for (int i = 0; i < graphics.Length; i++)
        {
            Graphic graphic = graphics[i];

            if (graphic == null)
                continue;

            // Preserve the native visual hierarchy, but make the handle/track
            // raycastable exactly like MaiDXR's own working sliders.
            if (graphic == slider.targetGraphic ||
                graphic.transform == slider.handleRect)
            {
                graphic.raycastTarget = true;
            }
        }

        slider.onValueChanged.AddListener(
            delegate(float v)
            {
                setter(v);

                labelText.text =
                    label + ": " +
                    (wholeNumbers
                        ? Mathf.RoundToInt(v).ToString(CultureInfo.InvariantCulture)
                        : v.ToString(
                            format,
                            CultureInfo.InvariantCulture));

                AmbientLabMarkDirty();
            });

        return slider;
    }

    private Toggle AmbientLabCreateToggle(
        Transform parent,
        string label,
        bool value,
        Vector2 pos,
        Vector2 size,
        Action<bool> setter)
    {
        GameObject row = new GameObject("Ambient_" + label, typeof(RectTransform));
        row.transform.SetParent(parent, false);
        RectTransform rowRt = row.GetComponent<RectTransform>();
        rowRt.anchorMin = rowRt.anchorMax = new Vector2(0.5f, 0.5f);
        rowRt.pivot = new Vector2(0.5f, 0.5f);
        rowRt.anchoredPosition = pos;
        rowRt.sizeDelta = size;

        AmbientLabCreateText(
            row.transform,
            "Label",
            label,
            new Vector2(-size.x * 0.08f, 0f),
            new Vector2(size.x * 0.72f, size.y),
            14,
            TextAnchor.MiddleRight);

        GameObject toggleGo = new GameObject("Toggle", typeof(RectTransform), typeof(Toggle));
        toggleGo.transform.SetParent(row.transform, false);
        RectTransform trt = toggleGo.GetComponent<RectTransform>();
        trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 0.5f);
        trt.anchoredPosition = new Vector2(size.x * 0.36f, 0f);
        trt.sizeDelta = new Vector2(28f, 28f);

        GameObject bg = new GameObject("Background", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        bg.transform.SetParent(toggleGo.transform, false);
        RectTransform brt = bg.GetComponent<RectTransform>();
        brt.anchorMin = Vector2.zero;
        brt.anchorMax = Vector2.one;
        brt.offsetMin = Vector2.zero;
        brt.offsetMax = Vector2.zero;
        Image bgImage = bg.GetComponent<Image>();
        bgImage.color = new Color(0.12f, 0.20f, 0.22f, 1f);

        GameObject check = new GameObject("Checkmark", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        check.transform.SetParent(bg.transform, false);
        RectTransform crt = check.GetComponent<RectTransform>();
        crt.anchorMin = new Vector2(0.2f, 0.2f);
        crt.anchorMax = new Vector2(0.8f, 0.8f);
        crt.offsetMin = Vector2.zero;
        crt.offsetMax = Vector2.zero;
        Image checkImage = check.GetComponent<Image>();
        checkImage.color = new Color(0.25f, 0.95f, 0.72f, 1f);

        Toggle toggle = toggleGo.GetComponent<Toggle>();
        toggle.targetGraphic = bgImage;
        toggle.graphic = checkImage;
        toggle.isOn = value;
        toggle.onValueChanged.AddListener(delegate(bool v)
        {
            setter(v);
            AmbientLabMarkDirty();
        });
        return toggle;
    }

    private Button AmbientLabCreateActionButton(
        Transform parent,
        string label,
        Vector2 pos,
        Vector2 size,
        Action action)
    {
        GameObject go = new GameObject("AmbientAction_" + label, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;

        Image img = go.GetComponent<Image>();
        img.color = new Color(0.04f, 0.30f, 0.34f, 1f);

        Button b = go.GetComponent<Button>();
        b.targetGraphic = img;
        b.onClick.AddListener(delegate { action(); });

        AmbientLabCreateText(go.transform, "Label", label, Vector2.zero, size, 15, TextAnchor.MiddleCenter);
        return b;
    }

    private static Image AmbientLabFindLargestImage(GameObject root)
    {
        if (root == null)
            return null;

        Image[] images = root.GetComponentsInChildren<Image>(true);
        Image best = null;
        float bestArea = -1f;

        for (int i = 0; i < images.Length; i++)
        {
            Image image = images[i];
            if (image == null)
                continue;

            RectTransform rt = image.GetComponent<RectTransform>();
            float area = 0f;

            if (rt != null)
            {
                area = Mathf.Abs(rt.rect.width * rt.rect.height);
                if (area < 1f)
                    area = Mathf.Abs(rt.sizeDelta.x * rt.sizeDelta.y);
            }

            if (area > bestArea)
            {
                bestArea = area;
                best = image;
            }
        }

        return best;
    }

    private static void AmbientLabCopyPanelImageStyle(Image target, GameObject nativePanel)
    {
        if (target == null || nativePanel == null)
            return;

        Image source = nativePanel.GetComponent<Image>();
        if (source == null)
            source = AmbientLabFindLargestImage(nativePanel);

        if (source == null)
        {
            target.color = new Color(0.02f, 0.12f, 0.14f, 0.97f);
            return;
        }

        target.sprite = source.sprite;
        target.overrideSprite = source.overrideSprite;
        target.type = source.type;
        target.color = source.color;
        target.material = source.material;
        target.preserveAspect = source.preserveAspect;
        target.raycastTarget = true;
    }

    private static void AmbientLabScaleUiFont(GameObject root, float factor)
    {
        if (root == null)
            return;

        Component[] components = root.GetComponentsInChildren<Component>(true);

        for (int i = 0; i < components.Length; i++)
        {
            Component c = components[i];
            if (c == null)
                continue;

            try
            {
                PropertyInfo p = c.GetType().GetProperty("fontSize", BindingFlags.Instance | BindingFlags.Public);
                if (p == null || !p.CanRead || !p.CanWrite)
                    continue;

                object value = p.GetValue(c, null);

                if (p.PropertyType == typeof(int))
                {
                    int v = (int)value;
                    p.SetValue(c, Mathf.Max(9, Mathf.RoundToInt(v * factor)), null);
                }
                else if (p.PropertyType == typeof(float))
                {
                    float v = (float)value;
                    p.SetValue(c, Mathf.Max(9f, v * factor), null);
                }
            }
            catch { }
        }
    }

    private static void AmbientLabForceSingleLine(GameObject root)
    {
        if (root == null)
            return;

        Text[] legacyTexts = root.GetComponentsInChildren<Text>(true);
        for (int i = 0; i < legacyTexts.Length; i++)
        {
            Text text = legacyTexts[i];
            if (text == null)
                continue;

            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.resizeTextForBestFit = false;
        }

        Component[] components = root.GetComponentsInChildren<Component>(true);
        for (int i = 0; i < components.Length; i++)
        {
            Component c = components[i];
            if (c == null)
                continue;

            try
            {
                PropertyInfo wrap = c.GetType().GetProperty(
                    "enableWordWrapping",
                    BindingFlags.Instance | BindingFlags.Public);

                if (wrap != null &&
                    wrap.CanWrite &&
                    wrap.PropertyType == typeof(bool))
                {
                    wrap.SetValue(c, false, null);
                }
            }
            catch { }

            try
            {
                PropertyInfo wrappingMode = c.GetType().GetProperty(
                    "textWrappingMode",
                    BindingFlags.Instance | BindingFlags.Public);

                if (wrappingMode != null &&
                    wrappingMode.CanWrite &&
                    wrappingMode.PropertyType.IsEnum)
                {
                    string[] names = Enum.GetNames(wrappingMode.PropertyType);
                    for (int n = 0; n < names.Length; n++)
                    {
                        if (string.Equals(names[n], "NoWrap", StringComparison.OrdinalIgnoreCase))
                        {
                            object value = Enum.Parse(wrappingMode.PropertyType, names[n]);
                            wrappingMode.SetValue(c, value, null);
                            break;
                        }
                    }
                }
            }
            catch { }
        }
    }

    private static bool AmbientLabObjectContainsText(GameObject go)
    {
        if (go == null)
            return false;

        Component[] components =
            go.GetComponentsInChildren<Component>(true);

        for (int i = 0; i < components.Length; i++)
        {
            Component c = components[i];

            if (c == null)
                continue;

            try
            {
                PropertyInfo p =
                    c.GetType().GetProperty(
                        "text",
                        BindingFlags.Instance |
                        BindingFlags.Public);

                if (p != null &&
                    p.PropertyType == typeof(string))
                {
                    return true;
                }
            }
            catch { }
        }

        return false;
    }

    private void AmbientLabCaptureTweaksNativeContent()
    {
        if (ambientLabTab2 == null ||
            ambientLabTweaksNativeUi.Count > 0)
        {
            return;
        }

        for (int i = 0; i < ambientLabTab2.transform.childCount; i++)
        {
            Transform child =
                ambientLabTab2.transform.GetChild(i);

            if (child == null ||
                child.gameObject == ambientLabPanel)
            {
                continue;
            }

            bool interactive =
                child.GetComponentInChildren<Selectable>(true) != null ||
                AmbientLabObjectContainsText(child.gameObject);

            if (!interactive)
                continue;

            ambientLabTweaksNativeUi.Add(child.gameObject);
            ambientLabTweaksNativeUiWasActive.Add(
                child.gameObject.activeSelf);
        }
    }

    private void AmbientLabSetTweaksNativeVisible(bool visible)
    {
        for (int i = 0; i < ambientLabTweaksNativeUi.Count; i++)
        {
            GameObject go = ambientLabTweaksNativeUi[i];

            if (go == null)
                continue;

            bool target =
                visible &&
                i < ambientLabTweaksNativeUiWasActive.Count &&
                ambientLabTweaksNativeUiWasActive[i];

            go.SetActive(target);
        }
    }

    private void AmbientLabCaptureNativeTabPositions()
    {
        if (ambientLabNativeTabPositionsCaptured)
            return;

        if (ambientLabMainButton == null || ambientLabTweaksButton == null)
            return;

        RectTransform mainRt =
            ambientLabMainButton.GetComponent<RectTransform>();

        RectTransform tweaksRt =
            ambientLabTweaksButton.GetComponent<RectTransform>();

        if (mainRt == null || tweaksRt == null)
            return;

        ambientLabNativeMainOriginalPos = mainRt.anchoredPosition;
        ambientLabNativeTweaksOriginalPos = tweaksRt.anchoredPosition;
        ambientLabNativeTabPositionsCaptured = true;

        Log(
            "AMBIANT_NATIVE_TAB_POS_CAPTURED " +
            "main=" + ambientLabNativeMainOriginalPos.ToString("F2") + " " +
            "tweaks=" + ambientLabNativeTweaksOriginalPos.ToString("F2"));
    }

    private void AmbientLabRestoreNativeTabPositions()
    {
        if (!ambientLabNativeTabPositionsCaptured)
            return;

        if (ambientLabMainButton == null || ambientLabTweaksButton == null)
            return;

        RectTransform mainRt =
            ambientLabMainButton.GetComponent<RectTransform>();

        RectTransform tweaksRt =
            ambientLabTweaksButton.GetComponent<RectTransform>();

        if (mainRt == null || tweaksRt == null)
            return;

        mainRt.anchoredPosition = ambientLabNativeMainOriginalPos;
        tweaksRt.anchoredPosition = ambientLabNativeTweaksOriginalPos;
    }

    private void AmbientLabShowPanel()
    {
        if (ambientLabTab1 != null)
            ambientLabTab1.SetActive(false);

        if (ambientLabTab2 != null)
            ambientLabTab2.SetActive(false);

        if (ambientLabPanel != null)
            ambientLabPanel.SetActive(true);
    }

    private void AmbientLabHidePanel()
    {
        if (ambientLabPanel != null)
            ambientLabPanel.SetActive(false);
    }

    private void AmbientLabTryBuildUi()
    {
        if (ambientLabUiBuilt)
            return;

        ambientLabUiBuildAttempts++;

        try
        {
            // Find the actual visible Main/Tweaks buttons FIRST.
            ambientLabMainButton =
                AmbientLabFindButton("Main");

            ambientLabTweaksButton =
                AmbientLabFindButton("Tweaks");

            AmbientLabCaptureNativeTabPositions();
            AmbientLabRestoreNativeTabPositions();

            if (ambientLabMainButton == null ||
                ambientLabTweaksButton == null)
            {
                ambientLabUiStatus =
                    "MAIN_OR_TWEAKS_BUTTON_NOT_FOUND";
                return;
            }

            Type tabManagerType = null;
            Assembly[] assemblies =
                AppDomain.CurrentDomain.GetAssemblies();

            for (int i = 0;
                 i < assemblies.Length &&
                 tabManagerType == null;
                 i++)
            {
                try
                {
                    tabManagerType =
                        assemblies[i].GetType(
                            "TabManager",
                            false);
                }
                catch { }
            }

            if (tabManagerType == null)
            {
                ambientLabUiStatus =
                    "TABMANAGER_TYPE_NOT_FOUND";
                return;
            }

            const BindingFlags flags =
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic;

            FieldInfo f1 =
                tabManagerType.GetField(
                    "Tab1Object",
                    flags);

            FieldInfo f2 =
                tabManagerType.GetField(
                    "Tab2Object",
                    flags);

            if (f1 == null || f2 == null)
            {
                ambientLabUiStatus =
                    "TAB_FIELDS_NOT_FOUND";
                return;
            }

            Canvas buttonCanvas =
                ambientLabTweaksButton
                    .GetComponentInParent<Canvas>();

            UnityEngine.Object[] managers =
                Resources.FindObjectsOfTypeAll(
                    tabManagerType);

            object selectedManager = null;
            GameObject selectedTab1 = null;
            GameObject selectedTab2 = null;

            for (int i = 0; i < managers.Length; i++)
            {
                Component c =
                    managers[i] as Component;

                if (c == null ||
                    c.gameObject == null ||
                    !c.gameObject.scene.IsValid())
                {
                    continue;
                }

                GameObject t1 =
                    f1.GetValue(managers[i])
                    as GameObject;

                GameObject t2 =
                    f2.GetValue(managers[i])
                    as GameObject;

                if (t1 == null || t2 == null)
                    continue;

                bool match = false;

                Canvas tabCanvas =
                    t1.GetComponentInParent<Canvas>();

                if (buttonCanvas != null &&
                    tabCanvas != null &&
                    buttonCanvas ==
                    tabCanvas)
                {
                    match = true;
                }

                if (!match)
                {
                    Transform parent =
                        ambientLabTweaksButton
                            .transform.parent;

                    if (parent != null &&
                        (parent.IsChildOf(c.transform) ||
                         c.transform.IsChildOf(parent)))
                    {
                        match = true;
                    }
                }

                if (!match)
                    continue;

                selectedManager = managers[i];
                selectedTab1 = t1;
                selectedTab2 = t2;
                break;
            }

            if (selectedManager == null)
            {
                ambientLabUiStatus =
                    "MATCHING_TABMANAGER_NOT_FOUND";
                return;
            }

            ambientLabTab1 = selectedTab1;
            ambientLabTab2 = selectedTab2;

            // Third tab is an exact Tweaks clone.
            GameObject clone =
                UnityEngine.Object.Instantiate(
                    ambientLabTweaksButton.gameObject,
                    ambientLabTweaksButton
                        .transform.parent);

            clone.name = "AMBIANT_Tab";
            AmbientLabSetUiText(
                clone,
                "Ambiant");
            // Keep the seven-letter title on one line inside Tweaks-sized tab.
            AmbientLabScaleUiFont(clone, 0.82f);
            AmbientLabForceSingleLine(clone);

            ambientLabTabButton =
                clone.GetComponent<Button>();

            ambientLabTabButton
                .onClick
                .RemoveAllListeners();

            ambientLabTabButton
                .onClick
                .AddListener(
                    delegate
                    {
                        AmbientLabShowPanel();
                    });

            RectTransform mainRt =
                ambientLabMainButton
                    .GetComponent<RectTransform>();

            RectTransform tweaksRt =
                ambientLabTweaksButton
                    .GetComponent<RectTransform>();

            RectTransform ambientRt =
                clone.GetComponent<RectTransform>();

            // Use the old two-button spacing as a native "slot".
            // Main shifts left one slot, Tweaks occupies old Main,
            // Ambiant occupies old Tweaks. Same size/style for all three.
            Vector2 oldMain =
                ambientLabNativeTabPositionsCaptured
                    ? ambientLabNativeMainOriginalPos
                    : mainRt.anchoredPosition;

            Vector2 oldTweaks =
                ambientLabNativeTabPositionsCaptured
                    ? ambientLabNativeTweaksOriginalPos
                    : tweaksRt.anchoredPosition;

            Vector2 slot =
                oldTweaks - oldMain;

            if (slot.sqrMagnitude < 100f)
            {
                float tabSlotWidth =
                    Mathf.Max(
                        90f,
                        Mathf.Abs(
                            tweaksRt.sizeDelta.x));

                slot =
                    new Vector2(
                        tabSlotWidth * 1.12f,
                        0f);
            }

            mainRt.anchoredPosition =
                oldMain - slot;

            tweaksRt.anchoredPosition =
                oldMain;

            ambientRt.anchoredPosition =
                oldTweaks;

            ambientRt.sizeDelta =
                tweaksRt.sizeDelta;

            ambientRt.localScale =
                tweaksRt.localScale;

            // Independent AMBIANT panel, sibling of native Tab1/Tab2.
            // No native controls remain behind it, so no superposition.
            GameObject panel =
                new GameObject(
                    "MaiMaiVR_AMBIANT_PANEL",
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Image));

            Transform panelParent =
                ambientLabTab2.transform.parent;

            panel.transform.SetParent(
                panelParent,
                false);

            ambientLabPanel = panel;

            RectTransform sourceRt =
                ambientLabTab2
                    .GetComponent<RectTransform>();

            RectTransform panelRt =
                panel.GetComponent<RectTransform>();

            panelRt.anchorMin = sourceRt.anchorMin;
            panelRt.anchorMax = sourceRt.anchorMax;
            panelRt.pivot = sourceRt.pivot;
            panelRt.anchoredPosition =
                sourceRt.anchoredPosition;
            panelRt.sizeDelta =
                sourceRt.sizeDelta;
            panelRt.localRotation =
                sourceRt.localRotation;
            panelRt.localScale =
                sourceRt.localScale;

            Image panelImage =
                panel.GetComponent<Image>();

            AmbientLabCopyPanelImageStyle(
                panelImage,
                ambientLabTab2);

            // Background is visual only; child controls receive raycasts.
            panelImage.raycastTarget = false;

            float width =
                Mathf.Abs(panelRt.rect.width);

            float height =
                Mathf.Abs(panelRt.rect.height);

            if (width < 400f)
                width =
                    Mathf.Abs(
                        panelRt.sizeDelta.x);

            if (height < 260f)
                height =
                    Mathf.Abs(
                        panelRt.sizeDelta.y);

            if (width < 400f)
                width = 1200f;

            if (height < 260f)
                height = 480f;

            AmbientLabCreateText(
                panel.transform,
                "Header",
                "AMBIANT  •  LIVE  •  AUTO-SAVE",
                new Vector2(
                    0f,
                    height * 0.365f),
                new Vector2(
                    width * 0.70f,
                    26f),
                11,
                TextAnchor.MiddleCenter);

            float colWidth =
                width * 0.36f;

            float leftX =
                -width * 0.205f;

            float rightX =
                width * 0.205f;

            float topY =
                height * 0.245f;

            float rowGap =
                Mathf.Min(
                    30f,
                    height * 0.062f);

            Vector2 rowSize =
                new Vector2(
                    colWidth,
                    rowGap - 2f);

            int l = 0;

            AmbientLabCreateToggle(
                panel.transform,
                "SCENE MASTER",
                ambientLabConfig.MasterEnabled,
                new Vector2(
                    leftX,
                    topY - rowGap * l++),
                rowSize,
                delegate(bool v)
                {
                    ambientLabConfig.MasterEnabled = v;
                });

            AmbientLabCreateSlider(
                panel.transform,
                "Room",
                0.05f,
                5.00f,
                ambientLabConfig.RoomLevel,
                new Vector2(
                    leftX,
                    topY - rowGap * l++),
                rowSize,
                delegate(float v)
                {
                    ambientLabConfig.RoomLevel = v;
                },
                "F2");

            AmbientLabCreateSlider(
                panel.transform,
                "Cabinet",
                0.05f,
                5.00f,
                ambientLabConfig.CabinetBody,
                new Vector2(
                    leftX,
                    topY - rowGap * l++),
                rowSize,
                delegate(float v)
                {
                    ambientLabConfig.CabinetBody = v;
                },
                "F2");

            AmbientLabCreateSlider(
                panel.transform,
                "Cabinet Emit",
                0f,
                2.00f,
                ambientLabConfig.CabinetEmission,
                new Vector2(
                    leftX,
                    topY - rowGap * l++),
                rowSize,
                delegate(float v)
                {
                    ambientLabConfig.CabinetEmission = v;
                },
                "F3");

            AmbientLabCreateSlider(
                panel.transform,
                "Ring Power",
                0.05f,
                10.00f,
                ambientLabConfig.RingPlastic,
                new Vector2(
                    leftX,
                    topY - rowGap * l++),
                rowSize,
                delegate(float v)
                {
                    ambientLabConfig.RingPlastic = v;
                },
                "F2");

            AmbientLabCreateToggle(
                panel.transform,
                "White Halo",
                ambientLabConfig.WhiteHaloEnabled,
                new Vector2(
                    leftX,
                    topY - rowGap * l++),
                rowSize,
                delegate(bool v)
                {
                    ambientLabConfig.WhiteHaloEnabled = v;
                });

            AmbientLabCreateSlider(
                panel.transform,
                "Halo Power",
                0f,
                1.00f,
                ambientLabConfig.WhiteHaloPower,
                new Vector2(
                    leftX,
                    topY - rowGap * l++),
                rowSize,
                delegate(float v)
                {
                    ambientLabConfig.WhiteHaloPower = v;
                },
                "F2");

            AmbientLabCreateSlider(
                panel.transform,
                "Halo Diameter",
                -0.05f,
                0.20f,
                ambientLabConfig.WhiteHaloRadiusOffset,
                new Vector2(
                    leftX,
                    topY - rowGap * l++),
                rowSize,
                delegate(float v)
                {
                    ambientLabConfig.WhiteHaloRadiusOffset = v;
                },
                "F3");

            AmbientLabCreateSlider(
                panel.transform,
                "Halo Thickness",
                0.02f,
                0.35f,
                ambientLabConfig.WhiteHaloRange,
                new Vector2(
                    leftX,
                    topY - rowGap * l++),
                rowSize,
                delegate(float v)
                {
                    ambientLabConfig.WhiteHaloRange = v;
                },
                "F3");

            AmbientLabCreateSlider(
                panel.transform,
                "Halo Points",
                8f,
                96f,
                ambientLabConfig.WhiteHaloPointCount,
                new Vector2(
                    leftX,
                    topY - rowGap * l++),
                rowSize,
                delegate(float v)
                {
                    int count =
                        Mathf.Clamp(
                            Mathf.RoundToInt(v),
                            8,
                            96);

                    if (ambientLabConfig.WhiteHaloPointCount != count)
                    {
                        ambientLabConfig.WhiteHaloPointCount = count;
                        ambientLabWhiteHaloBuilt = false;
                    }
                },
                "F0",
                true);

            int r = 0;

            AmbientLabCreateToggle(
                panel.transform,
                "Button Emission",
                ambientLabConfig.ButtonEmissionEnabled,
                new Vector2(
                    rightX,
                    topY - rowGap * r++),
                rowSize,
                delegate(bool v)
                {
                    ambientLabConfig.ButtonEmissionEnabled = v;
                });

            AmbientLabCreateSlider(
                panel.transform,
                "Button Emit",
                0f,
                4.00f,
                ambientLabConfig.ButtonEmission,
                new Vector2(
                    rightX,
                    topY - rowGap * r++),
                rowSize,
                delegate(float v)
                {
                    ambientLabConfig.ButtonEmission = v;
                },
                "F2");

            AmbientLabCreateSlider(
                panel.transform,
                "Active Opacity",
                0f,
                1.00f,
                ambientLabConfig.ButtonActiveOpacity,
                new Vector2(
                    rightX,
                    topY - rowGap * r++),
                rowSize,
                delegate(float v)
                {
                    ambientLabConfig.ButtonActiveOpacity = v;
                },
                "F2");

            AmbientLabCreateToggle(
                panel.transform,
                "Button Tip",
                ambientLabConfig.ButtonTipLightEnabled,
                new Vector2(
                    rightX,
                    topY - rowGap * r++),
                rowSize,
                delegate(bool v)
                {
                    ambientLabConfig.ButtonTipLightEnabled = v;
                });

            AmbientLabCreateSlider(
                panel.transform,
                "Tip Power",
                0f,
                3.00f,
                ambientLabConfig.ButtonTipIntensity,
                new Vector2(
                    rightX,
                    topY - rowGap * r++),
                rowSize,
                delegate(float v)
                {
                    ambientLabConfig.ButtonTipIntensity = v;
                },
                "F2");

            AmbientLabCreateSlider(
                panel.transform,
                "Tip Range",
                0.02f,
                1.00f,
                ambientLabConfig.ButtonTipRange,
                new Vector2(
                    rightX,
                    topY - rowGap * r++),
                rowSize,
                delegate(float v)
                {
                    ambientLabConfig.ButtonTipRange = v;
                },
                "F2");

            AmbientLabCreateSlider(
                panel.transform,
                "Tip Size",
                -0.02f,
                0.08f,
                ambientLabConfig.ButtonTipDiameter,
                new Vector2(
                    rightX,
                    topY - rowGap * r++),
                rowSize,
                delegate(float v)
                {
                    ambientLabConfig.ButtonTipDiameter = v;
                },
                "F3");

            AmbientLabCreateSlider(
                panel.transform,
                "Tip Ring Offset",
                -0.12f,
                0.12f,
                ambientLabConfig.ButtonTipRingOffset,
                new Vector2(
                    rightX,
                    topY - rowGap * r++),
                rowSize,
                delegate(float v)
                {
                    ambientLabConfig.ButtonTipRingOffset = v;
                },
                "F3");

            AmbientLabCreateToggle(
                panel.transform,
                "Top + Side Glow",
                ambientLabConfig.AccentGlowEnabled,
                new Vector2(
                    rightX,
                    topY - rowGap * r++),
                rowSize,
                delegate(bool v)
                {
                    ambientLabConfig.AccentGlowEnabled = v;
                });

            AmbientLabCreateSlider(
                panel.transform,
                "Accent Power",
                0f,
                2.00f,
                ambientLabConfig.AccentGlowIntensity,
                new Vector2(
                    rightX,
                    topY - rowGap * r++),
                rowSize,
                delegate(float v)
                {
                    ambientLabConfig.AccentGlowIntensity = v;
                },
                "F3");

            float actionY = topY - rowGap * r++;
            float actionOffset = colWidth * 0.155f;
            Vector2 actionSize = new Vector2(
                colWidth * 0.285f,
                rowGap * 0.84f);

            AmbientLabCreateActionButton(
                panel.transform,
                "SAVE NOW",
                new Vector2(
                    rightX - actionOffset,
                    actionY),
                actionSize,
                delegate
                {
                    ambientLabDirty = true;
                    AmbientLabSaveConfig(true);
                });

            AmbientLabCreateActionButton(
                panel.transform,
                "DEFAULT",
                new Vector2(
                    rightX + actionOffset,
                    actionY),
                actionSize,
                delegate
                {
                    AmbientLabResetConfig();
                });

            panel.SetActive(false);

            // Native Main/Tweaks listeners still switch their own tabs.
            // These extra listeners only guarantee AMBIANT is hidden.
            ambientLabMainButton
                .onClick
                .AddListener(
                    delegate
                    {
                        AmbientLabHidePanel();
                    });

            ambientLabTweaksButton
                .onClick
                .AddListener(
                    delegate
                    {
                        AmbientLabHidePanel();
                    });

            ambientLabUiBuilt = true;
            ambientLabUiStatus = "BUILT";

            if (ambientLabReopenAfterReset)
            {
                ambientLabReopenAfterReset = false;
                AmbientLabShowPanel();
            }

            Log(
                "AMBIANT_UI_BUILT " +
                "tabs=3_NATIVE_SLOTS " +
                "panel=INDEPENDENT_NATIVE_STYLE " +
                "slider=NATIVE_CLONE_WITH_DRAG " +
                "controls=21 save=true default=true autosave=true driftfix=true");
        }
        catch (Exception ex)
        {
            ambientLabLastError =
                ex.GetType().Name +
                ":" +
                ex.Message;

            ambientLabUiStatus =
                "ERROR:" +
                ambientLabLastError;

            Log(
                "AMBIANT_UI_BUILD_FAIL " +
                ambientLabLastError);
        }
    }

    private void AmbientLabAppendStatus(System.Text.StringBuilder sb)
    {
        if (sb == null)
            return;

        sb.AppendLine("AmbientLab=True");
        sb.AppendLine("AmbientLabConfigPath=" + ambientLabConfigPath);
        sb.AppendLine("AmbientLabDefaultProfile=V0.3.1.10_USER_VALIDATED");
        sb.AppendLine("AmbientLabUiBuilt=" + ambientLabUiBuilt);
        sb.AppendLine("AmbientLabUiStatus=" + ambientLabUiStatus);
        sb.AppendLine("AmbientLabUiBuildAttempts=" + ambientLabUiBuildAttempts);
        sb.AppendLine("AmbientLabSaveCount=" + ambientLabSaveCount);
        sb.AppendLine("AmbientLabLoadCount=" + ambientLabLoadCount);
        sb.AppendLine("AmbientLabLastError=" + ambientLabLastError);
        sb.AppendLine("AmbientLabMaterialCount=" + ambientLabMaterials.Count);
        sb.AppendLine("AmbientLabMaterialScanCount=" + ambientLabMaterialScanCount);
        sb.AppendLine("AmbientLabMaterialApplyCount=" + ambientLabMaterialApplyCount);
        sb.AppendLine("AmbientLabP2MirrorBuilt=" + ambientLabP2MirrorBuilt);
        sb.AppendLine("AmbientLabP2MirrorBuildAttempts=" + ambientLabP2MirrorBuildAttempts);
        sb.AppendLine("AmbientLabP2ButtonLights=" + ambientLabP2ButtonLights.Count);
        sb.AppendLine("AmbientLabP2ContourLights=" + ambientLabP2ContourLights.Count);
        sb.AppendLine("AmbientLabP2AccentLights=" + ambientLabP2AccentLights.Count);
        sb.AppendLine("AmbientLabP2SourceRingLights=" + ambientLabP2SourceRingLights.Count);
        sb.AppendLine("AmbientLabP2BodyLed=" + (ambientLabP2BodyLed != null));
        sb.AppendLine("AmbientLabP2DisplayLed=" + (ambientLabP2DisplayLed != null));
        sb.AppendLine("AmbientLabDiffuseRingBuilt=" + ambientLabDiffuseRingBuilt);
        sb.AppendLine("AmbientLabDiffuseRingBuildAttempts=" + ambientLabDiffuseRingBuildAttempts);
        sb.AppendLine("AmbientLabP1DiffuseRingLayers=" + ambientLabP1DiffuseRing.Count);
        sb.AppendLine("AmbientLabP2DiffuseRingLayers=" + ambientLabP2DiffuseRing.Count);
        sb.AppendLine("AmbientLabLegacyP2MaterialPairs=" + ambientLabP2MaterialPairCount);
        sb.AppendLine("AmbientLabLegacyP2MaterialParityUpdates=" + ambientLabP2MaterialParityUpdates);
        sb.AppendLine("AmbientLabP2SourceLightClone=DISABLED");
        sb.AppendLine("AmbientLabExplicitP2Pairs=" + ambientLabExplicitP2Pairs.Count);
        sb.AppendLine("AmbientLabExplicitP2PairBuilds=" + ambientLabExplicitP2PairBuilds);
        sb.AppendLine("AmbientLabExplicitP2PairUpdates=" + ambientLabExplicitP2PairUpdates);
        sb.AppendLine("AmbientLabButtonFacesRuntime=RETIRED_NO_MESH");
        sb.AppendLine("AmbientLabButtonMode=VISIBLE_BUTTON_BASE_MATERIAL_RINGLED_DRIVEN_FALLBACK_ONLY");
        sb.AppendLine("AmbientLabStrictButtonBindingsReady=" + ambientLabStrictButtonBindingsReady);
        sb.AppendLine("AmbientLabStrictButtonBindingAttempts=" + ambientLabStrictButtonBindingAttempts);
        sb.AppendLine("AmbientLabStrictButtonHotPath=RETIRED_V0.6.1_NO_PER_FRAME_SCANS_OR_LOG_SPAM");
        sb.AppendLine("AmbientLabStrictP1Buttons=" + ambientLabStrictP1Buttons.Count);
        sb.AppendLine("AmbientLabStrictP2Buttons=" + ambientLabStrictP2Buttons.Count);
        sb.AppendLine("AmbientLabStrictButtonEmissionUpdates=" + ambientLabStrictButtonEmissionUpdates);
        sb.AppendLine("AmbientLabVisibleButtonFallbackUpdates=" + ambientLabVisibleButtonFallbackUpdates);
        sb.AppendLine("AmbientLabVisibleButtonFallbackRenderers=" + ambientLabVisibleButtonFallbackRenderers);
        sb.AppendLine("AmbientLabLegacyButtonEmissionLightsBuilt=" + ambientLabButtonEmissionLightsBuilt);
        sb.AppendLine("AmbientLabLegacyP1ButtonEmissionLights=" + ambientLabP1ButtonEmissionLights.Count);
        sb.AppendLine("AmbientLabLegacyP2ButtonEmissionLights=" + ambientLabP2ButtonEmissionLights.Count);
        int accentCount = 0;
        foreach (AmbientMaterialState state in ambientLabMaterials.Values)
            if (state != null && state.Category == AmbientSurfaceCategory.Accent) accentCount++;
        sb.AppendLine("AmbientLabAccentMaterials=" + accentCount);
        sb.AppendLine("AmbientLabWhiteHaloBuilt=" + ambientLabWhiteHaloBuilt);
        sb.AppendLine("AmbientLabP1WhiteHaloLights=" + ambientLabP1WhiteHaloLights.Count);
        sb.AppendLine("AmbientLabP2WhiteHaloLights=" + ambientLabP2WhiteHaloLights.Count);
        sb.AppendLine("AmbientLabWhiteHaloUpdates=" + ambientLabWhiteHaloUpdates);
        sb.AppendLine("AmbientLabWhiteHaloRebuilds=" + ambientLabWhiteHaloRebuilds);
        sb.AppendLine("AmbientLabSliderMode=NATIVE_VISUAL_CLONE_ISOLATED_EVENT_WITH_POINTER_DRAG");
        sb.AppendLine("AmbientLabSliderIsolation=PERSISTENT_LISTENERS_REPLACED_MAIDXR_SETTING_HANDLERS_DISABLED");
        sb.AppendLine("AmbientLabSliderForeignBehavioursDisabled=" + ambientLabSliderForeignBehavioursDisabled);
        sb.AppendLine("AmbientLabSliderLastForeignBehaviour=" + ambientLabSliderLastForeignBehaviour);
        sb.AppendLine("AmbientLabPanelMode=INDEPENDENT_NATIVE_STYLE_TAB");
        sb.AppendLine("AmbientLabHaloMode=ADJUSTABLE_8_TO_96_POINTS_QUADRATIC_POWER_NO_MESH");
        sb.AppendLine("AmbientLabButtonColorMode=RINGLED_HUE_SHARED_BY_CAP_AND_TIP");
        sb.AppendLine("AmbientLabRingMode=NATIVE_BASELINE_PLUS_OPTIONAL_HDR_ABOVE_5");

        if (ambientLabConfig != null)
        {
            sb.AppendLine("Ambient.Master=" + ambientLabConfig.MasterEnabled);
            sb.AppendLine("Ambient.RoomLevel=" + ambientLabConfig.RoomLevel.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("Ambient.CabinetBody=" + ambientLabConfig.CabinetBody.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("Ambient.CabinetEmission=" + ambientLabConfig.CabinetEmission.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("Ambient.RingPlastic=" + ambientLabConfig.RingPlastic.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("Ambient.RingEmissionMode=NATIVE_WHITE_AT_5_PLUS_HDR_ABOVE_5");
            sb.AppendLine("Ambient.WhiteHaloEnabled=" + ambientLabConfig.WhiteHaloEnabled);
            sb.AppendLine("Ambient.WhiteHaloPower=" + ambientLabConfig.WhiteHaloPower.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("Ambient.WhiteHaloDiameterOffset=" + ambientLabConfig.WhiteHaloRadiusOffset.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("Ambient.WhiteHaloThickness=" + ambientLabConfig.WhiteHaloRange.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("Ambient.WhiteHaloPointCount=" + ambientLabConfig.WhiteHaloPointCount);
            sb.AppendLine("Ambient.WhiteHaloLegacyIntensity=" + ambientLabConfig.WhiteHaloIntensity.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("Ambient.ButtonEmissionEnabled=" + ambientLabConfig.ButtonEmissionEnabled);
            sb.AppendLine("Ambient.ButtonEmission=" + ambientLabConfig.ButtonEmission.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("Ambient.ButtonActiveOpacity=" + ambientLabConfig.ButtonActiveOpacity.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("Ambient.ButtonTipLightEnabled=" + ambientLabConfig.ButtonTipLightEnabled);
            sb.AppendLine("Ambient.ButtonTipIntensity=" + ambientLabConfig.ButtonTipIntensity.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("Ambient.ButtonTipRange=" + ambientLabConfig.ButtonTipRange.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("Ambient.ButtonTipDiameter=" + ambientLabConfig.ButtonTipDiameter.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("Ambient.ButtonTipRingOffset=" + ambientLabConfig.ButtonTipRingOffset.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("Ambient.AccentGlowEnabled=" + ambientLabConfig.AccentGlowEnabled);
            sb.AppendLine("Ambient.AccentGlowIntensity=" + ambientLabConfig.AccentGlowIntensity.ToString("F3", CultureInfo.InvariantCulture));
        }
    }

}
