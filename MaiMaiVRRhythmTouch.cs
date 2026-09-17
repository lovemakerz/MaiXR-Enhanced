// MaiMaiVR V0.6.6 - MIT. High-speed rhythm touch recovery for fast sweeps.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

[DefaultExecutionOrder(31000)]
public sealed class MaiMaiVRRhythmTouch : MonoBehaviour
{
    private sealed class TouchTarget
    {
        public Collider Collider;
        public MonoBehaviour Manager;
        public Action<MonoBehaviour,Collider> Enter;
        public Action<MonoBehaviour,Collider> Exit;
    }

    public bool IsLeft { get; private set; }
    public int Sweeps { get; private set; }
    public int SyntheticPresses { get; private set; }
    public int SyntheticReleases { get; private set; }
    public int SweepBufferSaturations { get; private set; }
    public int TouchTargets { get { return targetsByCollider.Count; } }

    private SphereCollider hand;
    private bool initialized;
    private bool havePrevious;
    private Vector3 previousCenter;
    private readonly RaycastHit[] sweepHits = new RaycastHit[64];
    private readonly Collider[] overlapHits = new Collider[64];
    private readonly Dictionary<int,TouchTarget> targetsByCollider = new Dictionary<int,TouchTarget>(64);
    private readonly Dictionary<int,TouchTarget> synthetic = new Dictionary<int,TouchTarget>(32);
    private readonly HashSet<int> currentOverlap = new HashSet<int>();
    private readonly List<int> releaseIds = new List<int>(32);

    public bool Initialize(SphereCollider source,bool left)
    {
        if(initialized)return true;
        if(source==null)return false;
        hand=source;IsLeft=left;
        BuildTouchTargetCache();
        initialized=true;
        CaptureCurrent(out previousCenter);
        havePrevious=true;
        return true;
    }

    private static Action<MonoBehaviour,Collider> BuildInvoker(MethodInfo method)
    {
        if(method==null)return null;
        try {
            DynamicMethod dm=new DynamicMethod(
                "MaiMaiVR_"+method.Name,
                typeof(void),
                new Type[]{typeof(MonoBehaviour),typeof(Collider)},
                typeof(MaiMaiVRRhythmTouch),
                true);
            ILGenerator il=dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass,method.DeclaringType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(method.IsVirtual?OpCodes.Callvirt:OpCodes.Call,method);
            il.Emit(OpCodes.Ret);
            return (Action<MonoBehaviour,Collider>)dm.CreateDelegate(typeof(Action<MonoBehaviour,Collider>));
        } catch { return null; }
    }

    private void BuildTouchTargetCache()
    {
        targetsByCollider.Clear();
        MonoBehaviour[] allBehaviours=UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
        Dictionary<int,MonoBehaviour> managers=new Dictionary<int,MonoBehaviour>(8);
        Dictionary<Type,Action<MonoBehaviour,Collider>> enterByType=new Dictionary<Type,Action<MonoBehaviour,Collider>>();
        Dictionary<Type,Action<MonoBehaviour,Collider>> exitByType=new Dictionary<Type,Action<MonoBehaviour,Collider>>();
        BindingFlags flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        for(int i=0;i<allBehaviours.Length;i++) {
            MonoBehaviour mb=allBehaviours[i];
            if(mb==null || !string.Equals(mb.GetType().Name,"TouchPanelManager",StringComparison.Ordinal))continue;
            Type type=mb.GetType();
            Action<MonoBehaviour,Collider> enter,exit;
            if(!enterByType.TryGetValue(type,out enter)) {
                MethodInfo mi=type.GetMethod("OnTriggerEnter",flags,null,new Type[]{typeof(Collider)},null);
                enter=BuildInvoker(mi);
                if(enter==null && mi!=null) enter=(manager,other)=>mi.Invoke(manager,new object[]{other});
                enterByType[type]=enter;
            }
            if(!exitByType.TryGetValue(type,out exit)) {
                MethodInfo mi=type.GetMethod("OnTriggerExit",flags,null,new Type[]{typeof(Collider)},null);
                exit=BuildInvoker(mi);
                if(exit==null && mi!=null) exit=(manager,other)=>mi.Invoke(manager,new object[]{other});
                exitByType[type]=exit;
            }
            if(enter!=null && exit!=null)managers[mb.transform.GetInstanceID()]=mb;
        }
        if(managers.Count==0)return;
        Collider[] colliders=UnityEngine.Object.FindObjectsOfType<Collider>();
        for(int i=0;i<colliders.Length;i++) {
            Collider c=colliders[i];if(c==null || c==hand)continue;
            Transform t=c.transform;MonoBehaviour manager=null;int guard=0;
            while(t!=null && guard++<16) {
                if(managers.TryGetValue(t.GetInstanceID(),out manager))break;
                t=t.parent;
            }
            if(manager==null)continue;
            Type type=manager.GetType();
            TouchTarget target=new TouchTarget();
            target.Collider=c;target.Manager=manager;target.Enter=enterByType[type];target.Exit=exitByType[type];
            targetsByCollider[c.GetInstanceID()]=target;
        }
    }

    private bool CaptureCurrent(out Vector3 center)
    {
        center=Vector3.zero;
        if(hand==null || !hand.enabled || !hand.gameObject.activeInHierarchy)return false;
        center=hand.transform.TransformPoint(hand.center);return true;
    }

    private float WorldRadius()
    {
        Vector3 s=hand.transform.lossyScale;
        return hand.radius*Mathf.Max(Mathf.Abs(s.x),Mathf.Abs(s.y),Mathf.Abs(s.z));
    }

    private void BuildCurrentOverlap(Vector3 center,float radius)
    {
        currentOverlap.Clear();
        int count=Physics.OverlapSphereNonAlloc(center,radius,overlapHits,~0,QueryTriggerInteraction.Collide);
        for(int i=0;i<count;i++) {
            Collider c=overlapHits[i];if(c==null)continue;
            int id=c.GetInstanceID();if(targetsByCollider.ContainsKey(id))currentOverlap.Add(id);
        }
    }

    private void ReleaseExpiredSynthetic()
    {
        if(synthetic.Count==0)return;
        releaseIds.Clear();
        foreach(KeyValuePair<int,TouchTarget> kv in synthetic) {
            // If the hand now genuinely overlaps the zone, Unity owns the contact;
            // do not emit a synthetic exit that could cancel the native state.
            if(currentOverlap.Contains(kv.Key)) { releaseIds.Add(kv.Key); continue; }
            TouchTarget target=kv.Value;
            if(target!=null && target.Manager!=null && target.Exit!=null) {
                try { target.Exit(target.Manager,hand); SyntheticReleases++; } catch { }
            }
            releaseIds.Add(kv.Key);
        }
        for(int i=0;i<releaseIds.Count;i++)synthetic.Remove(releaseIds[i]);
    }

    private void FixedUpdate()
    {
        if(!initialized || hand==null)return;
        Vector3 current;
        if(!CaptureCurrent(out current)) { havePrevious=false;return; }
        float radius=WorldRadius();
        if(radius<=0.0001f) { previousCenter=current;havePrevious=true;return; }

        BuildCurrentOverlap(current,radius);
        ReleaseExpiredSynthetic();

        if(havePrevious && targetsByCollider.Count>0) {
            Vector3 delta=current-previousCenter;
            float distance=delta.magnitude;
            if(distance>0.00005f) {
                int count=Physics.SphereCastNonAlloc(previousCenter,radius,delta/distance,sweepHits,distance,~0,QueryTriggerInteraction.Collide);
                Sweeps++;
                if(count>=sweepHits.Length)SweepBufferSaturations++;
                for(int i=0;i<count;i++) {
                    Collider c=sweepHits[i].collider;if(c==null)continue;
                    int id=c.GetInstanceID();
                    TouchTarget target;
                    if(!targetsByCollider.TryGetValue(id,out target))continue;
                    // End-overlap is handled by ordinary Unity trigger callbacks.
                    if(currentOverlap.Contains(id) || synthetic.ContainsKey(id))continue;
                    if(target.Manager==null || target.Enter==null)continue;
                    try {
                        target.Enter(target.Manager,hand);
                        synthetic[id]=target;
                        SyntheticPresses++;
                    } catch { }
                }
            }
        }
        previousCenter=current;havePrevious=true;
    }

    private void FlushSynthetic()
    {
        foreach(KeyValuePair<int,TouchTarget> kv in synthetic) {
            TouchTarget target=kv.Value;
            if(target!=null && target.Manager!=null && target.Exit!=null) {
                try { target.Exit(target.Manager,hand); } catch { }
            }
        }
        synthetic.Clear();
    }
    private void OnDisable(){FlushSynthetic();havePrevious=false;}
    private void OnDestroy(){FlushSynthetic();}
}
