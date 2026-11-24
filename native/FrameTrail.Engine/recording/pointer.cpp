#include "recording.h"
#include <windows.h>
#include <dwmapi.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <atomic>
#include <algorithm>
#include <condition_variable>
#include <deque>
#include <filesystem>
#include <fstream>
#include <future>
#include <iomanip>
#include <mutex>
#include <sstream>
#include <thread>
#include <unordered_map>
#include <vector>
using Microsoft::WRL::ComPtr;
namespace {
int64_t qpc_now() { LARGE_INTEGER v{}; QueryPerformanceCounter(&v); return v.QuadPart; }
struct Event { int64_t qpc; POINT point; uint32_t kind; HCURSOR cursor; bool visible; RECT geometry; bool is_key{}; std::string key_label; };
class Pointer;
thread_local Pointer* current_pointer{};

bool is_modifier_vk(DWORD vk) {
    return vk == VK_SHIFT || vk == VK_LSHIFT || vk == VK_RSHIFT ||
           vk == VK_CONTROL || vk == VK_LCONTROL || vk == VK_RCONTROL ||
           vk == VK_MENU || vk == VK_LMENU || vk == VK_RMENU ||
           vk == VK_LWIN || vk == VK_RWIN;
}

std::string vk_key_name(DWORD vk) {
    if (vk >= 'A' && vk <= 'Z') return std::string(1, static_cast<char>(vk));
    if (vk >= '0' && vk <= '9') return std::string(1, static_cast<char>(vk));
    if (vk >= VK_F1 && vk <= VK_F24) return "F" + std::to_string(vk - VK_F1 + 1);
    switch (vk) {
    case VK_RETURN: return "Enter";
    case VK_ESCAPE: return "Esc";
    case VK_SPACE: return "Space";
    case VK_TAB: return "Tab";
    case VK_BACK: return "Backspace";
    case VK_DELETE: return "Del";
    case VK_INSERT: return "Ins";
    case VK_HOME: return "Home";
    case VK_END: return "End";
    case VK_PRIOR: return "PgUp";
    case VK_NEXT: return "PgDn";
    case VK_LEFT: return "Left";
    case VK_RIGHT: return "Right";
    case VK_UP: return "Up";
    case VK_DOWN: return "Down";
    case VK_OEM_PLUS: return "+";
    case VK_OEM_MINUS: return "-";
    case VK_OEM_COMMA: return ",";
    case VK_OEM_PERIOD: return ".";
    case VK_OEM_2: return "/";
    case VK_OEM_1: return ";";
    case VK_OEM_4: return "[";
    case VK_OEM_6: return "]";
    case VK_OEM_5: return "\\";
    case VK_OEM_3: return "`";
    case VK_SNAPSHOT: return "PrtSc";
    case VK_PAUSE: return "Pause";
    default: break;
    }
    const UINT mapped = MapVirtualKeyW(vk, MAPVK_VK_TO_CHAR) & 0xFFFF;
    if (mapped >= 0x21 && mapped <= 0x7E) return std::string(1, static_cast<char>(mapped));
    return {};
}

std::string json_escape(std::string_view text) {
    std::string out;
    out.reserve(text.size());
    for (unsigned char c : text) {
        if (c == '\\') out += "\\\\";
        else if (c == '"') out += "\\\"";
        else if (c >= 0x20 && c < 0x7F) out += static_cast<char>(c);
    }
    return out;
}

class Pointer {
    ft_pointer_options options_; std::wstring path_, shapes_;
    std::mutex mutex_; std::condition_variable changed_; std::deque<Event> events_;
    std::thread message_thread_, writer_thread_; DWORD thread_id_{}; std::atomic<bool> stopped_{};
    std::atomic<int64_t> end_qpc_{}; std::atomic<uint64_t> written_{}, dropped_{}; std::atomic<int32_t> error_{};
    RECT cached_bounds_{}; // Updated every timer tick on the hook thread; disk queue never re-queries old-event geometry.
    DWORD pressed_chord_vk_{};
public:
    explicit Pointer(const ft_pointer_options& options) : options_(options), path_(options.output_path), shapes_(options.shapes_directory) {}
    void enqueue(uint32_t kind, POINT point) {
        CURSORINFO cursor{sizeof(cursor)}; bool known = GetCursorInfo(&cursor) != FALSE;
        Event event{qpc_now(), point, kind, known ? cursor.hCursor : nullptr, known && (cursor.flags & CURSOR_SHOWING), cached_bounds_};
        std::lock_guard lock(mutex_); if (events_.size() == 2048) { ++dropped_; return; } events_.push_back(std::move(event)); changed_.notify_one();
    }
    void enqueue_key(std::string label) {
        if (label.empty()) return;
        Event event{};
        event.qpc = qpc_now();
        event.is_key = true;
        event.key_label = std::move(label);
        std::lock_guard lock(mutex_);
        if (events_.size() == 2048) { ++dropped_; return; }
        events_.push_back(std::move(event));
        changed_.notify_one();
    }
    static LRESULT CALLBACK hook(int code, WPARAM message, LPARAM data) {
        // Collect button transitions only.
        if (code == HC_ACTION && current_pointer) {
            uint32_t kind{};
            switch (message) { case WM_LBUTTONDOWN: kind = 1; break; case WM_LBUTTONUP: kind = 2; break; case WM_RBUTTONDOWN: kind = 3; break; case WM_RBUTTONUP: kind = 4; break; case WM_MBUTTONDOWN: kind = 5; break; case WM_MBUTTONUP: kind = 6; break; default: break; }
            if (kind) current_pointer->enqueue(kind, reinterpret_cast<MSLLHOOKSTRUCT*>(data)->pt);
        }
        return CallNextHookEx(nullptr, code, message, data);
    }
    static LRESULT CALLBACK keyboard_hook(int code, WPARAM message, LPARAM data) {
        // Privacy filter: capture ONLY modifier-chord shortcuts (Ctrl/Alt/Win + key) or F1..F12; never plain typing.
        if (code == HC_ACTION && current_pointer) {
            const auto* kb = reinterpret_cast<const KBDLLHOOKSTRUCT*>(data);
            const DWORD vk = kb->vkCode;
            if (message == WM_KEYUP || message == WM_SYSKEYUP) {
                if (vk == current_pointer->pressed_chord_vk_) current_pointer->pressed_chord_vk_ = 0;
            } else if ((message == WM_KEYDOWN || message == WM_SYSKEYDOWN) && !is_modifier_vk(vk)) {
                const bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                const bool alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0 || (kb->flags & LLKHF_ALTDOWN) != 0;
                const bool win = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
                const bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
                const bool is_fn = vk >= VK_F1 && vk <= VK_F12;
                if ((ctrl || alt || win || is_fn) && vk != current_pointer->pressed_chord_vk_) {
                    const auto key_name = vk_key_name(vk);
                    if (!key_name.empty()) {
                        std::string chord;
                        auto append = [&](std::string_view part) {
                            if (!chord.empty()) chord += " + ";
                            chord += part;
                        };
                        if (ctrl) append("Ctrl");
                        if (alt) append("Alt");
                        if (shift) append("Shift");
                        if (win) append("Win");
                        append(key_name);
                        current_pointer->pressed_chord_vk_ = vk;
                        current_pointer->enqueue_key(std::move(chord));
                    }
                }
            }
        }
        return CallNextHookEx(nullptr, code, message, data);
    }
    RECT bounds() {
        RECT rect{};
        if (options_.kind == 2) {
            HWND hwnd = reinterpret_cast<HWND>(options_.source_handle);
            if (FAILED(DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &rect, sizeof(rect)))) GetWindowRect(hwnd, &rect);
        } else {
            MONITORINFO info{sizeof(info)}; if (GetMonitorInfoW(reinterpret_cast<HMONITOR>(options_.source_handle), &info)) rect = info.rcMonitor;
            if (options_.kind == 3) { rect.left += options_.x; rect.top += options_.y; rect.right = rect.left + options_.width; rect.bottom = rect.top + options_.height; }
        }
        return rect;
    }
    bool save_cursor(HCURSOR cursor, const std::wstring& filename, UINT& hotspot_x, UINT& hotspot_y) {
        HICON copy = CopyIcon(cursor); if (!copy) return false;
        ICONINFO info{}; if (!GetIconInfo(copy, &info)) { DestroyIcon(copy); return false; }
        hotspot_x = info.xHotspot; hotspot_y = info.yHotspot;
        BITMAP bitmap{}; auto source = info.hbmColor ? info.hbmColor : info.hbmMask; GetObjectW(source, sizeof(bitmap), &bitmap);
        UINT width = static_cast<UINT>(bitmap.bmWidth), height = static_cast<UINT>(bitmap.bmHeight / (info.hbmColor ? 1 : 2));
        if (width == 0 || height == 0 || width > 512 || height > 512) { if (info.hbmColor) DeleteObject(info.hbmColor); if (info.hbmMask) DeleteObject(info.hbmMask); DestroyIcon(copy); return false; }
        BITMAPINFO dib{}; dib.bmiHeader.biSize = sizeof(BITMAPINFOHEADER); dib.bmiHeader.biWidth = static_cast<LONG>(width); dib.bmiHeader.biHeight = -static_cast<LONG>(height); dib.bmiHeader.biPlanes = 1; dib.bmiHeader.biBitCount = 32; dib.bmiHeader.biCompression = BI_RGB;
        void* pixels{}; HDC dc = CreateCompatibleDC(nullptr); HBITMAP image = CreateDIBSection(dc, &dib, DIB_RGB_COLORS, &pixels, nullptr, 0); auto previous = SelectObject(dc, image);
        memset(pixels, 0, static_cast<size_t>(width) * height * 4); DrawIconEx(dc, 0, 0, copy, width, height, 0, nullptr, DI_NORMAL);
        auto data = static_cast<uint8_t*>(pixels); bool alpha = false; for (size_t i = 3; i < static_cast<size_t>(width) * height * 4; i += 4) if (data[i]) alpha = true;
        // Legacy monochrome cursors have no alpha; derive coverage by drawing on white and black backgrounds.
        if (!alpha) {
            std::vector<uint8_t> black(data, data + static_cast<size_t>(width) * height * 4);
            memset(pixels, 255, static_cast<size_t>(width) * height * 4); DrawIconEx(dc, 0, 0, copy, width, height, 0, nullptr, DI_NORMAL);
            for (size_t i = 0; i < black.size(); i += 4) { data[i + 3] = (data[i] == 255 && data[i + 1] == 255 && data[i + 2] == 255 && black[i] == 0 && black[i + 1] == 0 && black[i + 2] == 0) ? 0 : 255; data[i] = black[i]; data[i + 1] = black[i + 1]; data[i + 2] = black[i + 2]; }
        }
        ComPtr<IWICImagingFactory> factory; ComPtr<IWICStream> stream; ComPtr<IWICBitmapEncoder> encoder; ComPtr<IWICBitmapFrameEncode> frame;
        HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory));
        if (SUCCEEDED(hr)) hr = factory->CreateStream(&stream); if (SUCCEEDED(hr)) hr = stream->InitializeFromFilename(filename.c_str(), GENERIC_WRITE);
        if (SUCCEEDED(hr)) hr = factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder); if (SUCCEEDED(hr)) hr = encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache);
        if (SUCCEEDED(hr)) hr = encoder->CreateNewFrame(&frame, nullptr); if (SUCCEEDED(hr)) hr = frame->Initialize(nullptr); if (SUCCEEDED(hr)) hr = frame->SetSize(width, height);
        auto format = GUID_WICPixelFormat32bppBGRA; if (SUCCEEDED(hr)) hr = frame->SetPixelFormat(&format); if (SUCCEEDED(hr)) hr = frame->WritePixels(height, width * 4, width * height * 4, data);
        if (SUCCEEDED(hr)) hr = frame->Commit(); if (SUCCEEDED(hr)) hr = encoder->Commit();
        SelectObject(dc, previous); DeleteObject(image); DeleteDC(dc); if (info.hbmColor) DeleteObject(info.hbmColor); if (info.hbmMask) DeleteObject(info.hbmMask); DestroyIcon(copy); return SUCCEEDED(hr);
    }
    HRESULT start() {
        if (std::filesystem::exists(path_)) return HRESULT_FROM_WIN32(ERROR_FILE_EXISTS);
        std::promise<HRESULT> ready; auto future = ready.get_future();
        message_thread_ = std::thread([this, ready = std::move(ready)]() mutable {
            thread_id_ = GetCurrentThreadId(); current_pointer = this; SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            cached_bounds_ = bounds();
            MSG message{}; PeekMessageW(&message, nullptr, 0, 0, PM_NOREMOVE);
            auto hook_handle = SetWindowsHookExW(WH_MOUSE_LL, hook, GetModuleHandleW(nullptr), 0);
            if (!hook_handle) { ready.set_value(HRESULT_FROM_WIN32(GetLastError())); return; }
            auto keyboard_hook_handle = SetWindowsHookExW(WH_KEYBOARD_LL, keyboard_hook, GetModuleHandleW(nullptr), 0);
            auto timer = SetTimer(nullptr, 0, 16, nullptr);
            if (!timer) { auto hr = HRESULT_FROM_WIN32(GetLastError()); if (keyboard_hook_handle) UnhookWindowsHookEx(keyboard_hook_handle); UnhookWindowsHookEx(hook_handle); ready.set_value(hr); return; }
            ready.set_value(S_OK);
            while (GetMessageW(&message, nullptr, 0, 0) > 0) { if (message.message == WM_TIMER) { cached_bounds_ = bounds(); POINT point{}; if (GetCursorPos(&point)) enqueue(0, point); } DispatchMessageW(&message); }
            KillTimer(nullptr, timer); if (keyboard_hook_handle) UnhookWindowsHookEx(keyboard_hook_handle); UnhookWindowsHookEx(hook_handle); current_pointer = nullptr;
        });
        auto hr = future.get(); if (FAILED(hr)) { message_thread_.join(); return hr; }
        std::promise<HRESULT> writer_ready; auto writer_future = writer_ready.get_future();
        writer_thread_ = std::thread([this, ready = std::move(writer_ready)]() mutable {
            auto co = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            std::ofstream output(std::filesystem::path(path_), std::ios::out | std::ios::binary);
            if (!output) { error_ = HRESULT_FROM_WIN32(ERROR_WRITE_FAULT); ready.set_value(error_); if (SUCCEEDED(co)) CoUninitialize(); return; }
            output << std::setprecision(9); ready.set_value(S_OK);
            std::unordered_map<HCURSOR, uint32_t> shapes;
            for (;;) {
                Event event{};
                { std::unique_lock lock(mutex_); changed_.wait(lock, [&] { return stopped_ || !events_.empty(); }); if (events_.empty()) { if (stopped_) break; continue; } event = events_.front(); events_.pop_front(); }
                if (event.qpc < options_.epoch_qpc || (end_qpc_ && event.qpc >= end_qpc_)) continue;
                auto ticks = static_cast<int64_t>((static_cast<long double>(event.qpc - options_.epoch_qpc) * 10'000'000) / options_.qpc_frequency);
                if (event.is_key) {
                    output << "{\"type\":\"key\",\"ticks\":" << ticks << ",\"timescale\":10000000,\"label\":\"" << json_escape(event.key_label) << "\"}\n";
                    ++written_; if (written_ % 60 == 0) output.flush();
                    if (!output) { error_ = HRESULT_FROM_WIN32(ERROR_WRITE_FAULT); break; }
                    continue;
                }
                const auto rect = event.geometry; const auto width = rect.right - rect.left, height = rect.bottom - rect.top;
                if (width <= 0 || height <= 0) continue;
                double x = static_cast<double>(event.point.x - rect.left) / width, y = static_cast<double>(event.point.y - rect.top) / height;
                const bool inside = x >= 0 && x <= 1 && y >= 0 && y <= 1;
                uint32_t shape{};
                if (event.cursor && shapes.size() < 256) {
                    auto found = shapes.find(event.cursor);
                    if (found != shapes.end()) shape = found->second;
                    else { shape = static_cast<uint32_t>(shapes.size()) + 1; UINT hx{}, hy{}; auto name = L"cursor-" + std::to_wstring(shape) + L".png";
                        if (save_cursor(event.cursor, (std::filesystem::path(shapes_) / name).wstring(), hx, hy)) {
                            output << "{\"type\":\"shape\",\"id\":" << shape << ",\"file\":\"" << std::filesystem::path(shapes_).filename().string() << "/cursor-" << shape << ".png\",\"hotspotX\":" << hx << ",\"hotspotY\":" << hy << "}\n"; shapes[event.cursor] = shape;
                        } else shape = 0;
                    }
                } else if (auto found = shapes.find(event.cursor); found != shapes.end()) shape = found->second;
                output << "{\"type\":\"pointer\",\"ticks\":" << ticks << ",\"timescale\":10000000,\"x\":" << std::clamp(x, 0.0, 1.0) << ",\"y\":" << std::clamp(y, 0.0, 1.0)
                    << ",\"inside\":" << (inside ? "true" : "false") << ",\"visible\":" << (event.visible ? "true" : "false") << ",\"kind\":" << event.kind << ",\"shape\":" << shape << ",\"fallback\":\"arrow\",\"geometry\":[" << rect.left << ',' << rect.top << ',' << width << ',' << height << "]}\n";
                ++written_; if (written_ % 60 == 0) output.flush();
                if (!output) { error_ = HRESULT_FROM_WIN32(ERROR_WRITE_FAULT); break; }
            }
            output.flush(); if (!output) error_ = HRESULT_FROM_WIN32(ERROR_WRITE_FAULT);
            if (SUCCEEDED(co)) CoUninitialize();
        });
        return writer_future.get();
    }
    void stop(int64_t qpc) { if (!end_qpc_) end_qpc_ = qpc; if (message_thread_.joinable()) { PostThreadMessageW(thread_id_, WM_QUIT, 0, 0); message_thread_.join(); } stopped_ = true; changed_.notify_all(); if (writer_thread_.joinable()) writer_thread_.join(); }
    ft_pointer_status status() const { return {sizeof(ft_pointer_status), error_, written_, dropped_}; }
};
}
extern "C" {
int32_t __cdecl ft_pointer_start(const ft_pointer_options* options, ft_pointer_handle* handle) {
    if (!options || !handle || options->size != sizeof(*options) || !options->output_path || !options->shapes_directory || options->epoch_qpc <= 0 || options->qpc_frequency <= 0 || options->kind < 1 || options->kind > 3) return E_INVALIDARG;
    *handle = nullptr;
    try { auto pointer = new Pointer(*options); auto hr = pointer->start(); if (FAILED(hr)) { pointer->stop(qpc_now()); delete pointer; return hr; } *handle = pointer; return S_OK; } catch (...) { return E_FAIL; }
}
int32_t __cdecl ft_pointer_stop_at(ft_pointer_handle handle, int64_t qpc) { if (!handle || qpc <= 0) return E_INVALIDARG; try { static_cast<Pointer*>(handle)->stop(qpc); return S_OK; } catch (...) { return E_FAIL; } }
int32_t __cdecl ft_pointer_get_status(ft_pointer_handle handle, ft_pointer_status* status) { if (!handle || !status || status->size != sizeof(*status)) return E_INVALIDARG; *status = static_cast<Pointer*>(handle)->status(); return S_OK; }
void __cdecl ft_pointer_destroy(ft_pointer_handle handle) { if (handle) { auto pointer = static_cast<Pointer*>(handle); pointer->stop(qpc_now()); delete pointer; } }
int32_t __cdecl ft_recording_exclude_window(uintptr_t hwnd, uint32_t exclude) { return SetWindowDisplayAffinity(reinterpret_cast<HWND>(hwnd), exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE) ? S_OK : HRESULT_FROM_WIN32(GetLastError()); }
int32_t __cdecl ft_recording_hotkey(uintptr_t hwnd, int32_t id, uint32_t modifiers, uint32_t key, uint32_t enable) { BOOL result = enable ? RegisterHotKey(reinterpret_cast<HWND>(hwnd), id, modifiers | MOD_NOREPEAT, key) : UnregisterHotKey(reinterpret_cast<HWND>(hwnd), id); return result ? S_OK : HRESULT_FROM_WIN32(GetLastError()); }
}
