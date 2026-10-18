using FrameTrail.Domain;

namespace FrameTrail.App;

// This only converts native projected coordinates into pointer-local coordinates;
// authored time mapping and scene/projection evaluation remain in C++.
internal sealed class CanvasPlane
{
    private readonly double[] h;
    internal CanvasPlane(double[][] quad)
    {
        var a = new double[8, 9]; double[][] uv = [[0,0],[1,0],[1,1],[0,1]];
        for (int i = 0; i < 4; ++i) {
            double u=uv[i][0],v=uv[i][1],x=quad[i][0],y=quad[i][1]; int r=i*2;
            a[r,0]=u;a[r,1]=v;a[r,2]=1;a[r,6]=-x*u;a[r,7]=-x*v;a[r,8]=x;
            a[r+1,3]=u;a[r+1,4]=v;a[r+1,5]=1;a[r+1,6]=-y*u;a[r+1,7]=-y*v;a[r+1,8]=y;
        }
        for (int col=0;col<8;++col) {
            int pivot=col;for(int r=col+1;r<8;++r)if(Math.Abs(a[r,col])>Math.Abs(a[pivot,col]))pivot=r;
            if(Math.Abs(a[pivot,col])<1e-12)throw new ArgumentException("退化的画布投影。");
            for(int c=col;c<9;++c)(a[col,c],a[pivot,c])=(a[pivot,c],a[col,c]);
            double d=a[col,col];for(int c=col;c<9;++c)a[col,c]/=d;
            for(int r=0;r<8;++r)if(r!=col){d=a[r,col];for(int c=col;c<9;++c)a[r,c]-=d*a[col,c];}
        }
        h=Enumerable.Range(0,8).Select(i=>a[i,8]).ToArray();
    }
    internal (double X,double Y) Map(double u,double v)
    { double w=h[6]*u+h[7]*v+1;return((h[0]*u+h[1]*v+h[2])/w,(h[3]*u+h[4]*v+h[5])/w); }
    internal (double X,double Y) Inverse(double x,double y)
    {
        double a=h[0]-x*h[6],b=h[1]-x*h[7],c=x-h[2],d=h[3]-y*h[6],e=h[4]-y*h[7],f=y-h[5],det=a*e-b*d;
        if(Math.Abs(det)<1e-12)throw new ArgumentException("画布投影无法命中。");return((c*e-b*f)/det,(a*f-c*d)/det);
    }
    internal static NormalizedRect Drag(NormalizedRect r, double dx,double dy,int corner,double rotation=0,double aspect=1)
    {
        if(corner<0)return r with { X=Math.Clamp(r.X+dx,0,1-r.Width),Y=Math.Clamp(r.Y+dy,0,1-r.Height) };
        if(Math.Abs(rotation)>0.0001) {
            double a=rotation*Math.PI/180,cos=Math.Cos(a),sin=Math.Sin(a);
            double ux=cos*dx+sin*dy/aspect,uy=-sin*dx*aspect+cos*dy;
            int sx=corner is 0 or 3?-1:1,sy=corner is 0 or 1?-1:1;
            double w=Math.Clamp(r.Width+sx*ux,0.001,1),h=Math.Clamp(r.Height+sy*uy,0.001,1);
            double cx=r.X+r.Width/2,cy=r.Y+r.Height/2;
            double ax=cx-cos*sx*r.Width/2+sin*sy*r.Height/(2*aspect),ay=cy-sin*sx*r.Width*aspect/2-cos*sy*r.Height/2;
            cx=ax+cos*sx*w/2-sin*sy*h/(2*aspect);cy=ay+sin*sx*w*aspect/2+cos*sy*h/2;
            return new(){X=Math.Clamp(cx-w/2,0,1-w),Y=Math.Clamp(cy-h/2,0,1-h),Width=w,Height=h};
        }
        double l=r.X,t=r.Y,right=l+r.Width,bottom=t+r.Height;
        if(corner is 0 or 3)l=Math.Clamp(l+dx,0,right-0.001);else right=Math.Clamp(right+dx,l+0.001,1);
        if(corner is 0 or 1)t=Math.Clamp(t+dy,0,bottom-0.001);else bottom=Math.Clamp(bottom+dy,t+0.001,1);
        return new(){X=l,Y=t,Width=right-l,Height=bottom-t};
    }
    internal double[][] Quad(NormalizedRect r,double rotation=0,double aspect=1)
    {
        double a=rotation*Math.PI/180,cos=Math.Cos(a),sin=Math.Sin(a),cx=r.X+r.Width/2,cy=r.Y+r.Height/2;
        return new[]{(-r.Width/2,-r.Height/2),(r.Width/2,-r.Height/2),(r.Width/2,r.Height/2),(-r.Width/2,r.Height/2)}
            .Select(v=>Map(cx+v.Item1*cos-v.Item2*sin/aspect,cy+v.Item1*sin*aspect+v.Item2*cos)).Select(p=>new[]{p.X,p.Y}).ToArray();
    }
}
