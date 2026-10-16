#include "protocol.hpp"
#include <array>
#include <iostream>
using namespace frametrail::android;
int main() {
    std::array<uint8_t,12> p{0x40,0,0,0,0,0,1,0,0,0,0,5};
    auto h = parse_header(p.data());
    if (!h.key || h.config || h.pts_us != 256 || h.size != 5) return 1;
    p[0]=0x80; h=parse_header(p.data()); if(!h.config || h.key) return 2;
    for (auto size : {0u, 16u*1024*1024+1, UINT32_MAX}) {
        p[8]=uint8_t(size>>24);p[9]=uint8_t(size>>16);p[10]=uint8_t(size>>8);p[11]=uint8_t(size);
        try { (void)parse_header(p.data()); return 3; } catch (const std::runtime_error&) { }
    }
    std::cout << "Android protocol flags, PTS and allocation bounds passed\n";
}
