#include "frametrail/engine.h"
#include <iostream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

void require(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); }
int main() {
    try {
        require(ft_engine_abi_version() == 1, "ABI version");
        uint32_t required = 0;
        require(ft_engine_version(nullptr, 0, &required) == FT_OK && required == 6, "version size");
        char text[256]{};
        require(ft_engine_version(text, sizeof(text), &required) == FT_OK && std::string(text) == "0.1.0", "version");
        require(ft_engine_version(text, 1, &required) == FT_E_BUFFER_TOO_SMALL, "small buffer");
        ft_session_handle handle = 99;
        require(ft_session_create(99, &handle) == FT_E_ABI_MISMATCH && handle == 0, "ABI mismatch");
        require(ft_error_copy(text, sizeof(text), &required) == FT_OK && std::string(text).find("ABI") != std::string::npos, "error copy");
        require(ft_session_create(1, nullptr) == FT_E_INVALID_ARGUMENT, "null create");
        for (int i = 0; i < 1000; ++i) {
            require(ft_session_create(1, &handle) == FT_OK, "create");
            uint32_t cancelled = 9;
            require(ft_session_is_cancelled(handle, &cancelled) == FT_OK && cancelled == 0, "initial state");
            require(ft_session_cancel(handle) == FT_OK && ft_session_cancel(handle) == FT_OK, "idempotent cancel");
            require(ft_session_is_cancelled(handle, &cancelled) == FT_OK && cancelled == 1, "cancelled");
            require(ft_session_destroy(handle) == FT_OK, "destroy");
            require(ft_session_destroy(handle) == FT_E_INVALID_HANDLE, "double destroy");
            require(ft_session_cancel(handle) == FT_E_INVALID_HANDLE, "stale handle");
        }
        require(ft_session_create(1, &handle) == FT_OK, "concurrent create");
        std::vector<std::thread> threads;
        for (int i = 0; i < 8; ++i) threads.emplace_back([handle] { for (int n = 0; n < 1000; ++n) ft_session_cancel(handle); });
        for (auto& thread : threads) thread.join();
        require(ft_session_destroy(handle) == FT_OK, "concurrent destroy");
        std::cout << "PASS: engine ABI, buffers, cancellation, 1000 lifecycles, concurrency\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
