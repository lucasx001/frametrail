#pragma once
#include <stdint.h>

#if defined(_WIN32)
# if defined(FRAMETRAIL_ENGINE_EXPORTS)
#  define FT_API __declspec(dllexport)
# else
#  define FT_API __declspec(dllimport)
# endif
# define FT_CALL __cdecl
#else
# define FT_API
# define FT_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* ABI v1: x64 only, cdecl, UTF-8 strings, uint32 capacities include NUL.
 * Integer handles are owned by the caller, never pointers; 0 is invalid.
 * All calls are synchronous and thread-safe. No callbacks enter managed code.
 * Errors belong to the calling OS thread; read immediately after failure.
 * No native exception crosses this boundary. No pixel arrays cross the ABI. */
typedef uint64_t ft_session_handle;
typedef int32_t ft_result;
enum {
    FT_OK = 0,
    FT_E_INVALID_ARGUMENT = 1,
    FT_E_ABI_MISMATCH = 2,
    FT_E_INVALID_HANDLE = 3,
    FT_E_BUFFER_TOO_SMALL = 4,
    FT_E_INTERNAL = 5,
    FT_E_CANCELLED = 6,
    FT_E_INVALID_PROJECT = 7,
    FT_E_STALE_REVISION = 8,
    FT_E_OUT_OF_RANGE = 9,
    FT_E_NO_SNAPSHOT = 10
};
#define FT_ABI_VERSION 1u

FT_API uint32_t FT_CALL ft_engine_abi_version(void);
/* Writes the required byte count (including NUL). Null/0 queries the size. */
FT_API ft_result FT_CALL ft_engine_version(char* utf8, uint32_t capacity, uint32_t* required);
FT_API ft_result FT_CALL ft_session_create(uint32_t abi_version, ft_session_handle* session);
/* Returns INVALID_HANDLE on double destroy; never dereferences stale handles. */
FT_API ft_result FT_CALL ft_session_destroy(ft_session_handle session);
FT_API ft_result FT_CALL ft_session_cancel(ft_session_handle session);
FT_API ft_result FT_CALL ft_session_is_cancelled(ft_session_handle session, uint32_t* cancelled);
/* Schema 1 UTF-8 JSON bytes (no terminating NUL required), maximum 16 MiB.
 * Validation is stateless and MUST precede committing/persisting a candidate.
 * Load accepts strictly increasing revisions for one project per session. */
FT_API ft_result FT_CALL ft_project_validate(const char* utf8, uint32_t length);
FT_API ft_result FT_CALL ft_session_load_project(ft_session_handle session, const char* utf8, uint32_t length);
FT_API ft_result FT_CALL ft_session_output_duration(ft_session_handle session, int64_t* ticks);

/* Time arguments/results use 10,000,000 ticks/second. All intervals are half-open.
 * FT_E_OUT_OF_RANGE is an expected absence and not a fabricated final frame. */
typedef struct ft_mapped_time { int64_t ticks; char segment_id[129]; } ft_mapped_time;
FT_API ft_result FT_CALL ft_session_map_output(ft_session_handle session, int64_t ticks, ft_mapped_time* mapped);
FT_API ft_result FT_CALL ft_session_map_source(ft_session_handle session, int64_t ticks, ft_mapped_time* mapped);
typedef struct ft_rect { double x, y, width, height; } ft_rect;
typedef struct ft_media_frame { int64_t local_ticks; ft_rect output_rect; uint32_t mirror; char media_id[129]; } ft_media_frame;
typedef struct ft_frame_scene {
    int64_t revision, output_ticks, source_ticks;
    uint32_t width, height;
    double background_r, background_g, background_b, background_a;
    ft_media_frame screen, camera;
    uint32_t has_camera;
    char segment_id[129];
} ft_frame_scene;
FT_API ft_result FT_CALL ft_session_evaluate_scene(ft_session_handle session, int64_t ticks, ft_frame_scene* scene);
/* Does not clear or overwrite the stored error, even on a too-small buffer. */
FT_API ft_result FT_CALL ft_error_copy(char* utf8, uint32_t capacity, uint32_t* required);

#ifdef __cplusplus
}
#endif
