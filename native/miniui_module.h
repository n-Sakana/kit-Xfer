#ifndef MINIUI_MODULE_H
#define MINIUI_MODULE_H
#include <stdint.h>
#include <stddef.h>
#define MINIUI_ABI_VERSION 2
#ifdef _WIN32
#define MINIUI_EXPORT __declspec(dllexport)
#define MINIUI_CALL __cdecl
#else
#define MINIUI_EXPORT __attribute__((visibility("default")))
#define MINIUI_CALL
#endif
#ifdef __cplusplus
extern "C" {
#endif
/* Trusted native code. All pointers are borrowed until return, UTF-8 strings
   are NUL terminated. No exception/panic may unwind across this interface.
   init/call/request/cancel/image/view/sink/shutdown run on the document's UI
   thread. push/reply/present_frame/log may run on any thread; host copies before
   returning. append is only valid during call/image, with that invocation's out.
   shutdown stops and joins senders; no callback may follow its return.
   init failure must leave *instance NULL and retain no host pointer. */
typedef struct miniui_bytes { const uint8_t *ptr; size_t len; } miniui_bytes;
typedef uint32_t miniui_kind; /* 0 UTF-8 text, 1 binary */
typedef struct miniui_rect { double x, y, width, height; } miniui_rect;
/* kind: 0 headless (no view), 1 X11 window XID, 2 HWND, 3 NSView*.
   handle is an OS-owned borrowed parent, valid until all views are destroyed.
   display is reserved (zero); X11 parts open their own DISPLAY connection.
   Rectangles are CSS pixels relative to parent; scale converts to device px. */
typedef struct miniui_view_parent { uint32_t size, kind; uintptr_t handle; uintptr_t display; } miniui_view_parent;
/* Top-down premultiplied BGRA8. stride >= width*4; pixels.len >= stride*height.
   width/height 1..8192, at most 64 MiB. pts_ns is presentation time. */
typedef struct miniui_frame { uint32_t size, width, height, stride; int64_t pts_ns; miniui_bytes pixels; } miniui_frame;
typedef struct miniui_host_v2 {
  uint32_t size;
  void *ctx;
  int32_t (MINIUI_CALL *push)(void *, miniui_bytes, miniui_kind);
  int32_t (MINIUI_CALL *reply)(void *, uint64_t, int32_t, miniui_bytes, miniui_kind);
  int32_t (MINIUI_CALL *present_frame)(void *, uint32_t, const miniui_frame *);
  void (MINIUI_CALL *log)(void *, int32_t, const char *, miniui_bytes);
  int32_t (MINIUI_CALL *append)(void *, void *, miniui_bytes);
  /* Copies a live document blob: URL into the receiver during this call.
     May run on any thread. No JS runs. Receiver kind=0 is an authorized
     whole-file UTF-8 path, kind=1 is immutable bytes (including file slices).
     Receiver returns 1 on acceptance.
     1=read, 0=unavailable/revoked/rejected. An accepted request owns its copy. */
  int32_t (MINIUI_CALL *read_url)(void *, const char *, void *, int32_t (MINIUI_CALL *)(void *, miniui_bytes, miniui_kind));
} miniui_host_v2;
typedef struct miniui_module_info { uint32_t size; const char *root_dir, *state_dir, *app_id; miniui_kind preferred_kind; } miniui_module_info;
/* call is unframed (64 KiB input/output). request is command UTF-8, NUL, then
   body; kind describes the body. Command is nonempty, <=256 bytes, no NUL.
   This encoding carries binary without JSON/base64. Total <=256 MiB.
   request returns 0 on acceptance; exactly one reply completes it. Cancellation
   invalidates its id immediately; late/duplicate reply returns 0. A rejected
   reply may be retried until cancellation/shutdown. Negative status rejects the
   page Promise, preserving status/body. push <=16 MiB; the document's shared
   queue (including frames/logs) holds <=128 messages and <=64 MiB.
   sink frames coalesce by sink; newest replaces pending. No callbacks invoke JS.
   All functions returning status use 0=success, negative=failure, except host
   callbacks: 1=accepted, 0=rejected. Optional families must be complete.
   Libraries remain loaded until process exit. Instances belong to documents. */
MINIUI_EXPORT uint32_t MINIUI_CALL miniui_module_abi_version(void);
MINIUI_EXPORT int32_t MINIUI_CALL miniui_module_init_v2(const miniui_host_v2 *, const miniui_module_info *, void **, char *, size_t);
MINIUI_EXPORT int32_t MINIUI_CALL miniui_module_call_v2(void *, miniui_bytes, miniui_kind, void *, miniui_kind *);
MINIUI_EXPORT int32_t MINIUI_CALL miniui_module_request_v2(void *, uint64_t, miniui_bytes, miniui_kind);
MINIUI_EXPORT int32_t MINIUI_CALL miniui_module_cancel_v2(void *, uint64_t);
MINIUI_EXPORT void MINIUI_CALL miniui_module_shutdown_v2(void *);
MINIUI_EXPORT int32_t MINIUI_CALL miniui_module_image_v2(void *, const char *, void *, int32_t *, int32_t *);
MINIUI_EXPORT int32_t MINIUI_CALL miniui_module_view_create_v2(void *, const miniui_view_parent *, uint32_t *, char *, size_t);
MINIUI_EXPORT int32_t MINIUI_CALL miniui_module_view_bounds_v2(void *, uint32_t, const miniui_rect *, const miniui_rect *, double, int32_t);
MINIUI_EXPORT void MINIUI_CALL miniui_module_view_destroy_v2(void *, uint32_t);
MINIUI_EXPORT int32_t MINIUI_CALL miniui_module_sink_create_v2(void *, uint32_t, int32_t, int32_t);
MINIUI_EXPORT void MINIUI_CALL miniui_module_sink_destroy_v2(void *, uint32_t);
#ifdef __cplusplus
}
#endif
#endif
