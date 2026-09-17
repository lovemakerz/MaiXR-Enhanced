// MaiMaiVR V0.6.6 - MIT. Surface contact + rhythm touch integration. No COM writes in this module.
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.XR;

public sealed class MaiMaiVRBoundSurface
{
    public readonly MaiMaiVRSurfaceDefinition Definition;
    public readonly Transform Anchor,Target;
    private MaiMaiVRSurfaceShape shape;
    private Vector3 scale;
    public MaiMaiVRBoundSurface(MaiMaiVRSurfaceDefinition d,Transform anchor,Transform target)
    {Definition=d;Anchor=anchor;Target=target;}
    public bool Resolve(Vector3 raw,float radius,out Vector3 correction)
    {
        correction=Vector3.zero;
        if(Anchor==null || Target==null || !Target.gameObject.activeInHierarchy)return false;
        Vector3 s=Anchor.lossyScale;s=new Vector3(Mathf.Abs(s.x),Mathf.Abs(s.y),Mathf.Abs(s.z));
        if(s.x<0.00001f || s.y<0.00001f || s.z<0.00001f)return false;
        if(shape==null || (s-scale).sqrMagnitude>0.00000001f) {
            scale=s;float[] src=Definition.Vertices;float[] scaled=new float[src.Length];
            for(int i=0;i<src.Length;i+=3) {
                scaled[i]=src[i]*s.x;
                scaled[i+1]=src[i+1]*(Definition.Axis==1?s.z:s.y);
                scaled[i+2]=src[i+2]*(Definition.Axis==1?s.y:s.z);
            }
            shape=new MaiMaiVRSurfaceShape(scaled);
        }
        Vector3 local=Anchor.InverseTransformPoint(raw);
        float x=local.x*s.x,y=Definition.Axis==1?local.z*s.z:local.y*s.y;
        float depth=Definition.Axis==1?-local.y*s.y:local.z*s.z;
        float support;
        if(!shape.Support(x,y,radius,out support) || depth>=support)return false;
        Vector3 normal=Definition.Axis==1?-Anchor.up:Anchor.forward;
        correction=normal*(support-depth);return true;
    }
}

[DefaultExecutionOrder(30000)]
public sealed class MaiMaiVRSurfaceHand : MonoBehaviour
{
    public const float PenetrationDiameterFraction=0.10f;
    public bool ContactActive {get;private set;}
    public string SurfaceName {get;private set;}
    public float CorrectionMeters {get;private set;}
    private IList<MaiMaiVRBoundSurface> surfaces;
    private SphereCollider sphere;
    private Vector3 originalCenter;
    private MeshRenderer originalRenderer,visualRenderer;
    private GameObject visual;
    private bool originalForceOff,initialized,isLeft;
    private InputDevice xrDevice;

    public bool Initialize(SphereCollider source,bool left,IList<MaiMaiVRBoundSurface> bound)
    {
        if(initialized)return true;
        MeshFilter filter=source.GetComponent<MeshFilter>();
        originalRenderer=source.GetComponent<MeshRenderer>();
        if(filter==null || filter.sharedMesh==null || originalRenderer==null)return false;
        sphere=source;originalCenter=source.center;surfaces=bound;isLeft=left;
        originalForceOff=originalRenderer.forceRenderingOff;
        visual=new GameObject("MaiMaiVR Surface Hand Visual");
        visual.layer=source.gameObject.layer;visual.transform.SetParent(source.transform,false);
        visual.AddComponent<MeshFilter>().sharedMesh=filter.sharedMesh;
        visualRenderer=visual.AddComponent<MeshRenderer>();
        visualRenderer.sharedMaterials=originalRenderer.sharedMaterials;
        visualRenderer.shadowCastingMode=originalRenderer.shadowCastingMode;
        visualRenderer.receiveShadows=originalRenderer.receiveShadows;
        visualRenderer.lightProbeUsage=originalRenderer.lightProbeUsage;
        visualRenderer.reflectionProbeUsage=originalRenderer.reflectionProbeUsage;
        visualRenderer.motionVectorGenerationMode=originalRenderer.motionVectorGenerationMode;
        visualRenderer.enabled=originalRenderer.enabled;
        originalRenderer.forceRenderingOff=true;
        initialized=true;
        Application.onBeforeRender+=BeforeRender;
        Apply();return true;
    }
    private void FixedUpdate(){Apply();}
    private void LateUpdate(){Apply();}
    [BeforeRenderOrder(30000)]
    private void BeforeRender(){Apply();}
    private void OnEnable()
    {
        if(initialized){originalRenderer.forceRenderingOff=true;visual.SetActive(true);}
    }
    private bool IsTracked()
    {
        if(!xrDevice.isValid)xrDevice=InputDevices.GetDeviceAtXRNode(isLeft?XRNode.LeftHand:XRNode.RightHand);
        bool tracked;
        return xrDevice.isValid && (!xrDevice.TryGetFeatureValue(CommonUsages.isTracked,out tracked) || tracked);
    }
    private void Apply()
    {
        if(!initialized || !isActiveAndEnabled || sphere==null || visual==null)return;
        ContactActive=false;CorrectionMeters=0;SurfaceName="Free";
        // Preserve the configured native transform and radius. Only the collider's
        // centre and its visual child move, so native hand-position sliders stay valid.
        Vector3 raw=transform.TransformPoint(originalCenter);
        if(!IsTracked()) {
            // Let the next native physics step deliver normal trigger exits. Disabling
            // a collider alone does not reliably generate those exits in Unity.
            sphere.center=originalCenter+transform.InverseTransformVector(Vector3.up*1000f);
            visualRenderer.enabled=false;SurfaceName="Tracking unavailable";return;
        }
        visualRenderer.enabled=originalRenderer.enabled && !originalForceOff;
        Vector3 s=transform.lossyScale;
        float radius=sphere.radius*Mathf.Max(Mathf.Abs(s.x),Mathf.Abs(s.y),Mathf.Abs(s.z));
        float blockingRadius=radius*(1f-2f*PenetrationDiameterFraction);
        Vector3 best=Vector3.zero;float bestSquared=0;
        if(sphere.enabled) for(int i=0;i<surfaces.Count;i++) {
            Vector3 candidate;
            if(surfaces[i].Resolve(raw,blockingRadius,out candidate)) {
                float length=candidate.sqrMagnitude;
                if(length>bestSquared){best=candidate;bestSquared=length;SurfaceName=surfaces[i].Definition.Name;}
            }
        }
        Vector3 offset=transform.InverseTransformVector(best);
        sphere.center=originalCenter+offset;
        visual.transform.localPosition=offset;
        ContactActive=bestSquared>0.0000000001f;CorrectionMeters=Mathf.Sqrt(bestSquared);
    }
    private void Restore()
    {
        if(!initialized)return;
        if(sphere!=null)sphere.center=originalCenter;
        if(originalRenderer!=null)originalRenderer.forceRenderingOff=originalForceOff;
        if(visual!=null)visual.SetActive(false);
        ContactActive=false;
    }
    private void OnDisable(){Restore();}
    private void OnDestroy()
    {
        Application.onBeforeRender-=BeforeRender;Restore();
        if(visual!=null)Destroy(visual);
    }
}

public partial class MaiMaiVRIntegratedBaseline
{
    private readonly List<MaiMaiVRBoundSurface> contactSurfaces=new List<MaiMaiVRBoundSurface>();
    private MaiMaiVRSurfaceDefinition[] contactDefinitions;
    private MaiMaiVRSurfaceHand contactLeft,contactRight;
    private MaiMaiVRRhythmTouch rhythmLeft,rhythmRight;
    private Transform contactCabinet;
    private int contactBindAttempts;
    private bool contactBound,contactFailureLogged;
    private void DiscoverSurfaceContact()
    {
        if(contactBound && contactCabinet==null){contactSurfaces.Clear();contactBound=false;contactBindAttempts=0;}
        if(!contactBound && contactBindAttempts++<60) {
            GameObject cabinet=GameObject.Find("DX Unity");
            if(cabinet!=null) {
                contactCabinet=cabinet.transform;
                if(contactDefinitions==null)contactDefinitions=MaiMaiVRSurfaceData.Create();
                contactSurfaces.Clear();
                for(int i=0;i<contactDefinitions.Length;i++) {
                    MaiMaiVRSurfaceDefinition d=contactDefinitions[i];
                    Transform anchor=contactCabinet.Find(d.AnchorName);
                    Transform target=d.Axis==1?contactCabinet.Find(d.TargetName):(anchor==null?null:anchor.Find(d.TargetName));
                    if(anchor!=null && target!=null)contactSurfaces.Add(new MaiMaiVRBoundSurface(d,anchor,target));
                }
                contactBound=contactSurfaces.Count==contactDefinitions.Length;
                if(contactBound)Log("SURFACE_CONTACT_READY patches="+contactSurfaces.Count+" penetration=10%_diameter visual_and_native_collider=aligned");
            }
        }
        if(!contactBound) {
            if(contactBindAttempts>=60 && !contactFailureLogged) {
                contactFailureLogged=true;contactSurfaces.Clear();
                Log("SURFACE_CONTACT_UNAVAILABLE: scene anchors missing; original hands retained.");
            }
            return;
        }
        if(contactLeft==null)contactLeft=AttachSurfaceHand(leftTouchHapticCollider,true);
        if(contactRight==null)contactRight=AttachSurfaceHand(rightTouchHapticCollider,false);
        if(contactLeft!=null && rhythmLeft==null)rhythmLeft=AttachRhythmTouch(leftTouchHapticCollider,true);
        if(contactRight!=null && rhythmRight==null)rhythmRight=AttachRhythmTouch(rightTouchHapticCollider,false);
    }
    private MaiMaiVRSurfaceHand AttachSurfaceHand(Collider hand,bool left)
    {
        SphereCollider sphere=hand as SphereCollider;if(sphere==null)return null;
        MaiMaiVRSurfaceHand proxy=hand.GetComponent<MaiMaiVRSurfaceHand>();
        if(proxy==null)proxy=hand.gameObject.AddComponent<MaiMaiVRSurfaceHand>();
        if(!proxy.Initialize(sphere,left,contactSurfaces)){Destroy(proxy);return null;}
        Log("SURFACE_HAND_READY side="+(left?"left":"right")+" original_transform_and_settings=preserved");return proxy;
    }
    private MaiMaiVRRhythmTouch AttachRhythmTouch(Collider hand,bool left)
    {
        SphereCollider sphere=hand as SphereCollider;if(sphere==null)return null;
        MaiMaiVRRhythmTouch runtime=hand.GetComponent<MaiMaiVRRhythmTouch>();
        if(runtime==null)runtime=hand.gameObject.AddComponent<MaiMaiVRRhythmTouch>();
        if(!runtime.Initialize(sphere,left)){Destroy(runtime);return null;}
        Log("RHYTHM_TOUCH_READY side="+(left?"left":"right")+" physics=60Hz hybrid=native+sphere_sweep targets="+runtime.TouchTargets);return runtime;
    }
    private void SurfaceContactStatus(StringBuilder sb)
    {
        sb.AppendLine("SurfaceContactVersion=0.6.6");
        sb.AppendLine("SurfaceContactReady="+contactBound);
        sb.AppendLine("SurfaceContactPatches="+contactSurfaces.Count);
        sb.AppendLine("SurfaceContactPenetration=10% of sphere diameter");
        sb.AppendLine("SurfaceContactLeft="+(contactLeft==null?"Unavailable":contactLeft.SurfaceName));
        sb.AppendLine("SurfaceContactRight="+(contactRight==null?"Unavailable":contactRight.SurfaceName));
        sb.AppendLine("RhythmTouchMode=HYBRID_NATIVE_PLUS_SPHERE_SWEEP");
        sb.AppendLine("RhythmPhysicsHz=60");
        sb.AppendLine("RhythmTouchLeft="+(rhythmLeft==null?"Unavailable":("targets="+rhythmLeft.TouchTargets+" sweeps="+rhythmLeft.Sweeps+" recovered="+rhythmLeft.SyntheticPresses)));
        sb.AppendLine("RhythmTouchRight="+(rhythmRight==null?"Unavailable":("targets="+rhythmRight.TouchTargets+" sweeps="+rhythmRight.Sweeps+" recovered="+rhythmRight.SyntheticPresses)));
    }
    private void SurfaceContactDestroy()
    {
        if(rhythmLeft!=null){rhythmLeft.enabled=false;Destroy(rhythmLeft);}
        if(rhythmRight!=null){rhythmRight.enabled=false;Destroy(rhythmRight);}
        if(contactLeft!=null){contactLeft.enabled=false;Destroy(contactLeft);}
        if(contactRight!=null){contactRight.enabled=false;Destroy(contactRight);}
    }
}
