// MaiMaiVR V0.6.6 - MIT. Pure geometry, shared by the runtime and regression tests.
using System;

public struct MaiMaiVRSurfaceVertex
{
    public float X, Y, Z;
    public MaiMaiVRSurfaceVertex(float x, float y, float z) { X=x; Y=y; Z=z; }
}

public sealed class MaiMaiVRSurfaceTriangle
{
    private readonly MaiMaiVRSurfaceVertex a,b,c;
    private readonly float minX,maxX,minY,maxY,dx,dy,det,slopeX,slopeY;
    public MaiMaiVRSurfaceTriangle(MaiMaiVRSurfaceVertex av, MaiMaiVRSurfaceVertex bv, MaiMaiVRSurfaceVertex cv)
    {
        a=av;b=bv;c=cv;
        minX=Math.Min(a.X,Math.Min(b.X,c.X));maxX=Math.Max(a.X,Math.Max(b.X,c.X));
        minY=Math.Min(a.Y,Math.Min(b.Y,c.Y));maxY=Math.Max(a.Y,Math.Max(b.Y,c.Y));
        dx=b.X-a.X;dy=b.Y-a.Y;det=dx*(c.Y-a.Y)-dy*(c.X-a.X);
        if(Math.Abs(det)>1e-15f) {
            slopeX=((b.Z-a.Z)*(c.Y-a.Y)-(c.Z-a.Z)*dy)/det;
            slopeY=(dx*(c.Z-a.Z)-(c.X-a.X)*(b.Z-a.Z))/det;
        }
    }
    // Upper envelope of a sphere swept along local +Z. Reducing the blocking
    // radius to 80% permits precisely 10% of the original diameter to penetrate.
    // The optimum is either inside a face or on one of its edges (including vertices).
    public bool Support(float x,float y,float radius,ref float highest)
    {
        if(x<minX-radius || x>maxX+radius || y<minY-radius || y>maxY+radius) return false;
        bool found=false;
        if(Math.Abs(det)>1e-15f) {
            float norm=(float)Math.Sqrt(1+slopeX*slopeX+slopeY*slopeY);
            float qx=x+radius*slopeX/norm, qy=y+radius*slopeY/norm;
            float u=((qx-a.X)*(c.Y-a.Y)-(qy-a.Y)*(c.X-a.X))/det;
            float v=(dx*(qy-a.Y)-dy*(qx-a.X))/det;
            if(u>=-1e-6f && v>=-1e-6f && u+v<=1.000001f) {
                highest=Math.Max(highest,a.Z+slopeX*(x-a.X)+slopeY*(y-a.Y)+radius*norm);found=true;
            }
        }
        found=Edge(a,b,x,y,radius,ref highest)||found;
        found=Edge(b,c,x,y,radius,ref highest)||found;
        return Edge(c,a,x,y,radius,ref highest)||found;
    }
    private static bool Edge(MaiMaiVRSurfaceVertex a,MaiMaiVRSurfaceVertex b,float x,float y,float r,ref float highest)
    {
        float dx=b.X-a.X,dy=b.Y-a.Y,len2=dx*dx+dy*dy;
        if(len2<1e-18f) {
            float rem=r*r-(x-a.X)*(x-a.X)-(y-a.Y)*(y-a.Y);
            if(rem<0) return false;
            highest=Math.Max(highest,Math.Max(a.Z,b.Z)+(float)Math.Sqrt(rem));return true;
        }
        float len=(float)Math.Sqrt(len2),ux=dx/len,uy=dy/len;
        float along=(x-a.X)*ux+(y-a.Y)*uy,perp=(x-a.X)*uy-(y-a.Y)*ux;
        float remaining=r*r-perp*perp;
        if(remaining<0) return false;
        float slope=(b.Z-a.Z)/len;
        float u=along+(float)Math.Sqrt(remaining)*slope/(float)Math.Sqrt(1+slope*slope);
        u=Math.Max(0,Math.Min(len,u));
        remaining-= (u-along)*(u-along);
        if(remaining<0) return false;
        highest=Math.Max(highest,a.Z+u*slope+(float)Math.Sqrt(remaining));return true;
    }
}

public sealed class MaiMaiVRSurfaceShape
{
    public readonly MaiMaiVRSurfaceTriangle[] Triangles;
    public readonly float MinX,MaxX,MinY,MaxY;
    public MaiMaiVRSurfaceShape(float[] vertices)
    {
        MinX=MinY=float.PositiveInfinity;MaxX=MaxY=float.NegativeInfinity;
        Triangles=new MaiMaiVRSurfaceTriangle[vertices.Length/9];
        for(int i=0;i<vertices.Length;i+=3) {
            MinX=Math.Min(MinX,vertices[i]);MaxX=Math.Max(MaxX,vertices[i]);
            MinY=Math.Min(MinY,vertices[i+1]);MaxY=Math.Max(MaxY,vertices[i+1]);
        }
        for(int i=0;i<Triangles.Length;i++) {
            int j=i*9;
            Triangles[i]=new MaiMaiVRSurfaceTriangle(new MaiMaiVRSurfaceVertex(vertices[j],vertices[j+1],vertices[j+2]),new MaiMaiVRSurfaceVertex(vertices[j+3],vertices[j+4],vertices[j+5]),new MaiMaiVRSurfaceVertex(vertices[j+6],vertices[j+7],vertices[j+8]));
        }
    }
    public bool Support(float x,float y,float radius,out float height)
    {
        height=float.NegativeInfinity;
        if(x<MinX-radius || x>MaxX+radius || y<MinY-radius || y>MaxY+radius)return false;
        bool found=false;
        for(int i=0;i<Triangles.Length;i++) found=Triangles[i].Support(x,y,radius,ref height)||found;
        return found;
    }
}

public sealed class MaiMaiVRSurfaceDefinition
{
    public readonly string Name,AnchorName,TargetName;
    public readonly int Axis;
    public readonly float[] Vertices;
    public MaiMaiVRSurfaceDefinition(string name,string anchor,string target,int axis,float[] vertices)
    {Name=name;AnchorName=anchor;TargetName=target;Axis=axis;Vertices=vertices;}
}

