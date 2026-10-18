using FrameTrail.App;
using FrameTrail.Domain;
using Xunit;

namespace FrameTrail.App.Tests;

public class CanvasEditGeometryTests
{
    [Fact]
    public void DegenerateHiddenPlaneCannotBecomeAGesture()
        => Assert.Throws<ArgumentException>(()=>new CanvasPlane([[0,0],[0,0],[0,0],[0,0]]));
    [Fact]
    public void PerspectiveInverseMatchesNativeQuadAtEveryZoom()
    {
        double[][] q=[[0.1,0.2],[0.8,0.08],[0.95,0.9],[0.25,0.7]];
        var plane=new CanvasPlane(q);
        foreach(double scale in new[]{0.5,1,1.25,2,4}) foreach(var uv in new[]{(0.0,0.0),(1.0,1.0),(0.4,0.7)}) {
            var p=plane.Map(uv.Item1,uv.Item2);var v=plane.Inverse(p.X*scale/scale,p.Y*scale/scale);
            Assert.Equal(uv.Item1,v.X,9);Assert.Equal(uv.Item2,v.Y,9);
        }
    }
    [Fact]
    public void ResizingKeepsOppositeCornerAndMovingStaysInside()
    {
        var r=new NormalizedRect{X=0.2,Y=0.25,Width=0.4,Height=0.3};
        var resized=CanvasPlane.Drag(r,-1,-1,0);
        Assert.Equal(0,resized.X);Assert.Equal(0,resized.Y);Assert.Equal(0.6,resized.Width,8);Assert.Equal(0.55,resized.Height,8);
        var moved=CanvasPlane.Drag(r,1,1,-1);Assert.Equal(0.6,moved.X,8);Assert.Equal(0.7,moved.Y,8);
        var minimum=CanvasPlane.Drag(r,1,1,0);Assert.True(minimum.Width>=0.000999 && minimum.Height>=0.000999);
    }
    [Theory]
    [InlineData(25,0)] [InlineData(-35,1)] [InlineData(90,2)] [InlineData(15,3)]
    public void RotatedResizeKeepsOppositeHandle(double rotation,int corner)
    {
        var p=new CanvasPlane([[0,0],[1,0],[1,1],[0,1]]);
        var r=new NormalizedRect{X=0.3,Y=0.3,Width=0.25,Height=0.2};
        var changed=CanvasPlane.Drag(r,0.01,0.02,corner,rotation,16.0/9);
        var a=p.Quad(r,rotation,16.0/9);var b=p.Quad(changed,rotation,16.0/9);int opposite=(corner+2)%4;
        Assert.Equal(a[opposite][0],b[opposite][0],8);Assert.Equal(a[opposite][1],b[opposite][1],8);
    }
}
