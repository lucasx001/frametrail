#pragma once
#include "effects.hpp"
#include <algorithm>
#include <cmath>
#include <numbers>

namespace frametrail::core {
// Homogeneous clip coordinates shared by rendering and editor hit geometry.
inline std::array<double, 3> plane_vertex(double u, double v, const PlaneMotion& m, double cw, double ch) {
    const double scale=m.scale;
    double px=std::clamp(cw*0.5-m.focus_x*cw*scale,std::min(0.0,cw*(1-scale)),std::max(0.0,cw*(1-scale)));
    double py=std::clamp(ch*0.5-m.focus_y*ch*scale,std::min(0.0,ch*(1-scale)),std::max(0.0,ch*(1-scale)));
    const double ox=m.fixed_anchor?m.focus_x*cw:cw/2, oy=m.fixed_anchor?m.focus_y*ch:ch/2;
    if(m.fixed_anchor){px=(1-scale)*ox;py=(1-scale)*oy;}
    const double x=u*cw*scale+px-ox+m.offset_x*cw, y=v*ch*scale+py-oy+m.offset_y*ch;
    const double ax=m.angle_x*std::numbers::pi/180, ay=m.angle_y*std::numbers::pi/180;
    double z=y*std::sin(ax), yy=y*std::cos(ax), xx=x*std::cos(ay)+z*std::sin(ay);z=-x*std::sin(ay)+z*std::cos(ay);
    const double w=1+z/(2.5*std::max(cw,ch));
    return {2*xx/cw+(2*ox/cw-1)*w,-2*yy/ch-(2*oy/ch-1)*w,w};
}
}
