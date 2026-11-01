# Ruffle Native Integration Spike

**Repository:** wtffidy/VibeSkua  
**Branch:** feature/macos-support  
**Status:** Architecture investigation; no Ruffle code vendored or compiled yet.

## Outcome

Ruffle's current Rust core exposes the integration seams Skua needs:

- PlayerBuilder accepts custom navigator, renderer, audio, UI, storage, video, and ExternalInterface backends.
- Player exposes tick, render, set_viewport_dimensions, update_sockets, update_net_connections, and call_internal_interface.
- NavigatorBackend explicitly delegates raw TCP socket creation to the host through connect_socket.
- ExternalInterfaceProvider receives calls made by Flash and is notified when ActionScript callbacks become available.
- The host can invoke registered ActionScript callbacks with Player::call_internal_interface.

This is substantially more promising than patching AVM2 socket internals or using browser JavaScript.

## Verified upstream API details

### Player lifecycle and backends

The public ruffle_core crate re-exports Player and PlayerBuilder. PlayerBuilder supports custom backends through with_navigator, with_renderer, with_audio, with_storage, with_ui, with_video, with_external_interface, and with_fs_commands.

The player is built as Arc<Mutex<Player>>. The host owns the update/render cadence and must coordinate access to the player.

Relevant public methods confirmed in core/src/player.rs:
- tick(FloatDuration)
- render()
- set_viewport_dimensions(...)
- update_sockets()
- update_net_connections()
- set_external_interface_provider(...)
- call_internal_interface(name, args)
- set_is_playing(bool)

**Consequence:** We do not need to embed Ruffle's desktop GUI. We can use the core player directly and supply a Skua-owned host.

### Raw TCP sockets

NavigatorBackend::connect_socket is the explicit socket boundary. It receives the host and port, timeout, Ruffle socket handle, a channel receiver for outgoing byte buffers, and a channel sender for connect/data/close actions back into the AVM.

The Ruffle core handles ActionScript socket object state and dispatches connection/data/close events. The navigator backend owns the actual network stream.

Ruffle's frontend-utils ExternalNavigatorBackend already implements raw TCP connections using its async runtime, socket allow-list, and SocketMode (Allow / Deny / Ask).

**Consequence:** Browser sandbox restrictions are not relevant if we host the native core. We can either:
1. Reuse/adapt Ruffle's native navigator backend and add Skua's policy/proxy behavior; or
2. Implement NavigatorBackend directly and route connections through the existing Skua capture proxy.

The second option gives Skua explicit control over connection routing and logging. First inspect how CaptureProxy expects connections to be established and whether it can preserve each destination host/port.

### ExternalInterface bridge

Ruffle's ExternalInterfaceProvider has three methods:
- call_method(context, name, args): ActionScript calls into the host.
- on_callback_available(name): ActionScript registers a callback that the host can invoke.
- get_id(): supplies the ExternalInterface ID.

Ruffle's Player::call_internal_interface(name, args) looks up the registered ActionScript callback and invokes it.

Ruffle's ExternalValue is an intermediate, potentially lossy value model:
- Undefined / Null
- Bool / Number / String
- Object (string-keyed map)
- List

This is sufficient for a first bridge prototype, but the current Skua Flash XML serializer needs to be compared carefully. Test object types, numeric edge cases, XML escaping, undefined/null distinctions, and nested arrays/objects.

**Consequence:** The current Skua Externalizer.as API can likely be preserved at the ActionScript level. A Rust provider can forward host calls and callback registration to .NET, with a .NET-to-Rust call invoking Player::call_internal_interface.

## Proposed integration architecture

Start with an in-process Rust native library (cdylib) that wraps one Ruffle Player per Skua client session.

    VibeSkua (.NET 10 / Avalonia)
        |
        | P/Invoke C ABI (opaque session handles)
        v
    Skua.Ruffle.Native (Rust cdylib)
        |
        +-- Ruffle Player / AVM1 / AVM2
        |
        +-- Skua NavigatorBackend
        |      +-- HTTP fetch / URL resolution
        |      +-- raw TCP socket tasks
        |      +-- optional Skua CaptureProxy routing
        |
        +-- Skua ExternalInterfaceProvider
        |      +-- ActionScript -> .NET calls
        |      +-- callback registration notifications
        |      +-- .NET -> ActionScript callback invocation
        |
        +-- Rendering adapter
               +-- first spike: offscreen frame output
               +-- later: native GPU surface if host lifecycle permits

The C ABI should expose only stable, blittable values and opaque handles. Do not expose Rust structs, trait objects, Arc, Mutex, or Ruffle types directly across P/Invoke.

Suggested initial C ABI:
- skua_ruffle_create(config, callbacks, user_data) -> session_handle
- skua_ruffle_load(session, url_or_bytes)
- skua_ruffle_resize(session, width, height, scale_factor)
- skua_ruffle_tick(session, elapsed_microseconds)
- skua_ruffle_render(session, output_buffer, stride, width, height)
- skua_ruffle_call(session, callback_name, serialized_args, result_buffer)
- skua_ruffle_key_event(...)
- skua_ruffle_pointer_event(...)
- skua_ruffle_dispose(session)

This is a proposed interface, not an implemented or upstream Ruffle API. Exported functions must catch Rust panics before returning across the C ABI. Callback ownership, threading, buffer lifetime, UTF-8 encoding, and reentrancy need explicit contracts.

## Rendering decision: do not start with a separate desktop process

Ruffle's desktop frontend uses Winit, egui, and WGPU. Its desktop app is a useful reference for constructing a player and native backends, but its GUI is not a reusable embeddable control.

A separate Ruffle process would introduce difficult cross-process rendering and window embedding. The existing Skua tab host already demonstrates why OS-native child-window embedding is a poor portability boundary.

For the first experiment, prefer an in-process Rust library with an offscreen renderer and a pixel buffer. This is the simplest way to prove player, socket, and ExternalInterface integration without coupling the first milestone to AppKit/Winit event-loop integration.

Offscreen rendering is a spike, not necessarily the final high-performance renderer. Measure frame-copy cost and CPU/GPU utilization before accepting it. If inadequate, investigate a native Metal/WGPU surface hosted through Avalonia's macOS native view support.

## Spike milestones

### R0 — Build upstream core for macOS
- Pin a specific Ruffle revision.
- Build a small Rust crate depending on ruffle_core for aarch64-apple-darwin and x86_64-apple-darwin.
- Confirm public APIs and feature set compile for both targets.
- Produce a dylib and call a trivial exported function from a .NET 10 macOS test host.

### R1 — Player lifecycle
- Create one player session.
- Load a small local AVM2 SWF.
- Tick and render it offscreen.
- Resize, pause, resume, and dispose.
- Verify there are no Rust panics or native memory lifetime errors.

### R2 — ExternalInterface
- Implement a Rust provider that records ActionScript -> host calls.
- Notify .NET when ActionScript callbacks are registered.
- Invoke callbacks from .NET using call_internal_interface.
- Test all supported value types and error cases.
- Port the real Skua Externalizer.as contract into an integration test.

### R3 — Networking
- Implement or adapt NavigatorBackend::connect_socket.
- Connect to a local TCP test server.
- Verify binary payload integrity, partial reads, write ordering, close, timeout, and failure events.
- Test the existing Skua capture proxy separately.
- Test the actual AQW SWF connection only after local protocol tests pass.

### R4 — AQW compatibility gate
- Load the real Skua bootstrap SWF and game SWF.
- Verify all HTTP asset requests and redirects.
- Verify AQW socket connection, handshake, login, and sustained gameplay.
- Compare callback names and call order against the existing ActiveX client.
- Run two isolated sessions simultaneously.

## Risks and unresolved questions

1. Ruffle core API stability: upstream Rust APIs are not a stable ABI. Pin a commit and keep all Ruffle-specific types behind the Rust wrapper.
2. Rendering: Ruffle's RenderBackend is a substantial interface. The first spike needs a viable offscreen backend; the desktop WGPU backend is tied to its own window/event-loop setup.
3. ExternalInterface behavior: provider callbacks are exposed, but all current Skua XML/object semantics must be tested against Ruffle's value conversion.
4. Networking policy: Ruffle's desktop navigator has socket allow/deny/ask policy and raw TCP support. Skua must decide whether to reuse this policy, route through CaptureProxy, or combine both.
5. HTTP behavior: the native navigator must reproduce expected headers, cookies, redirects, cache behavior, URL resolution, and crossdomain policy behavior of the current Flash environment.
6. Flash API coverage: a successful socket connection does not prove all AQW APIs work. The actual game SWF is the compatibility test.
7. macOS build prerequisites: both Apple Silicon and Intel targets need Rust toolchains, .NET 10 SDK, native linker setup, and CI runners or cross-compilation strategy.
8. License review: Ruffle is MIT/Apache-2.0 dual-licensed, but all transitive dependencies and redistributed binaries must be reviewed before packaging.

## Recommendation

Proceed with R0 and R1 before changing the existing Skua client.

Do not patch the current Windows Flash host yet. Keep Windows behavior intact while the native Ruffle wrapper proves that Ruffle can load a SWF, render it, expose the Skua callback bridge, and connect to an AQW-compatible TCP endpoint on both macOS architectures.


## Follow-up: existing Skua CaptureProxy is not a drop-in Ruffle socket backend

Inspected current Skua source at Skua.Core/GameProxy/CaptureProxy.cs.

The existing CaptureProxy is a message-aware inspection proxy, not a general-purpose multi-destination TCP router:

- It is configured with one Destination IPEndPoint.
- It listens on the configured destination port on loopback.
- Every accepted local connection is forwarded to that same configured destination.
- It inspects zero-delimited text packets and has AQW-specific Z-prefixed zlib/Base64 decompression and recompression behavior.
- It maintains single current-client/current-forwarder fields, so lifecycle and concurrency need review before using it for multiple simultaneous game sessions.

**Decision:** Do not wire Ruffle's NavigatorBackend directly to the existing CaptureProxy yet. Ruffle may request connections to more than one host/port (game, policy, and other Flash socket endpoints), and a transparent native socket backend must preserve arbitrary binary stream semantics.

Preferred sequence:
1. Implement direct raw TCP support in the Rust NavigatorBackend first.
2. Prove AQW login and sustained gameplay with no packet inspection.
3. If Skua packet inspection is required, extract the packet framing/interception logic into a reusable stream interceptor with explicit destination routing and per-connection state.
4. Keep the current CaptureProxy behavior working for Windows while the shared interceptor is validated.


## Follow-up: Ruffle player and async task thread affinity

The current upstream types impose an important host design constraint.

- Ruffle's ExternalInterface stores its provider through Rc, and the navigator implementation uses Rc-owned state. The Player must therefore be treated as thread-affine; do not assume it can be moved freely between .NET threads.
- Ruffle's FutureSpawner abstraction accepts an OwnedFuture that is not declared Send.
- The upstream native navigator uses Tokio TCP streams and timers, while frontend-utils exposes a FutureSpawner seam for scheduling those tasks.

**Revised host design:** Create and own each Player on a dedicated Rust thread, with a current-thread Tokio runtime and LocalSet for non-Send Ruffle futures. Route managed commands to that thread through a thread-safe command channel. Do not expose Player pointers to arbitrary .NET threads.

This gives each client session a clear owner for:
- Player tick/render and viewport changes.
- Native socket and HTTP task scheduling.
- ActionScript callback invocation.
- Disposal and socket shutdown.

The C ABI needs a deliberate callback/reentrancy contract. A Rust-to-.NET callback may execute while the Rust owner thread is inside an AVM call. If the .NET callback synchronously calls back into the same session, a naive command queue can deadlock. The spike must either support reentrant owner-thread calls safely or design the ExternalInterface dispatch so callbacks are marshalled without re-entering a blocked owner thread.

Rendering is the other boundary: an offscreen frame can be copied to a managed buffer, but the owner-thread design must avoid blocking the UI thread while rendering or waiting on network tasks.


## Follow-up: mapping the existing Skua IFlashUtil contract

Inspected the actual Skua interfaces and Externalizer.as.

The compatibility layer is more specific than a generic ExternalInterface bridge:

- IFlashUtil.Call(function, args) invokes a named ActionScript callback and expects a converted return value.
- IFlashUtil.FlashCall is a void event raised when ActionScript calls a host function.
- CreateFlashObject, FlashObject, and FlashArray are wrappers over the RemoteRegistry callbacks.
- The Externalizer registers the RemoteRegistry family (lnkCreate, lnkDestroy, lnkGetChild, lnkDeleteChild, lnkGetValue, lnkSetValue, lnkCall, lnkGetArray, lnkSetArray) plus the Function Call Registry and the game-facing callbacks.
- The AS3 Externalizer.call method uses ExternalInterface.call, which maps to Ruffle's ExternalInterfaceProvider.call_method.
- Every Externalizer.addCallback registration maps to Ruffle's on_callback_available notification and can be invoked from .NET through Player::call_internal_interface.

**Compatibility decision:** Keep IFlashUtil, IFlashObject<T>, IFlashArray<T>, and the current AS3 callback names intact. Implement a Ruffle-backed IFlashUtil adapter that translates between .NET values and Ruffle ExternalValue. This avoids rewriting Skua.Core and the script-facing API.

**Value conversion tests required:**
- null, undefined, true, false
- integer, float, and large numeric values
- strings requiring XML escaping
- arrays with nested arrays/objects
- objects with numeric and string properties
- missing callback names and callback exceptions
- return values from RemoteRegistry and game callbacks

The current FlashUtil XML parser has legacy behavior (for example, number conversion to int/float and default values on failures). Match observed Windows behavior before attempting to improve it, because existing scripts may rely on those conversions.


## Follow-up: AQW explicitly requests socket policy files, but Ruffle's stub may not block connectivity

Inspected the complete Skua AS3 Server.as implementation and Ruffle's AVM2 Security implementation.

Skua calls Security.loadPolicyFile("xmlsocket://host:port") for server-list entries and again before selecting/connecting to a server. Adobe's Flash behavior is to send a null-terminated <policy-file-request/> over TCP and wait for the policy response before allowing the subsequent socket connection.

Ruffle's current flash.system.Security.loadPolicyFile implementation is a stub. However, Ruffle's AVM2 Socket implementation delegates directly to NavigatorBackend, and the native desktop navigator applies its own SocketMode allow/deny/ask policy. It does not rely on the Flash policy-file call as the same security gate.

**Revised assessment:** This is a confirmed ActionScript API compatibility gap, but it is not yet a proven AQW connectivity blocker. Ruffle may intentionally replace Flash's policy-file enforcement with its host-level socket permissions. We must test whether AQW's server requires the policy request as part of its connection flow, rather than assuming the stub prevents login.

**Test cases:**
1. Record the policy-file requests made by the current ActiveX client when the server list loads and when a server is selected.
2. Run the same SWF in native Ruffle with its normal socket policy and observe whether the game can connect without policy requests.
3. If AQW requires the request, implement xmlsocket:// policy retrieval in a custom navigator/host extension or upstream Ruffle contribution.
4. If it does not, document the intentional security-model difference and make sure Skua's host-level socket allow-list is restrictive and explicit.


## Follow-up: Ruffle already provides a native offscreen WGPU renderer

The first rendering plan assumed we would need to implement a custom RenderBackend or adapt the desktop swap chain. A deeper source read found that Ruffle already exposes an offscreen target:

- ruffle_render_wgpu::backend::WgpuRenderBackend::for_offscreen(size, backends, power_preference)
- WgpuRenderBackend::capture_frame() -> Option<image::RgbaImage>

The offscreen constructor creates a native WGPU instance/device and a TextureTarget without a window surface. On macOS we can request the Metal backend. The returned WgpuRenderBackend implements Ruffle's RenderBackend trait and can be passed directly to PlayerBuilder::with_renderer.

**Architecture improvement:** Do not implement RenderBackend for the first spike. Use Ruffle's own offscreen WGPU renderer, capture the RGBA image, and copy it through the C ABI into a managed Avalonia bitmap.

The native crate now depends on the pinned ruffle_render_wgpu package and includes a compile-time API probe for this offscreen constructor. The probe does not initialize a GPU; it only type-checks the API.

This makes the first Ruffle player host significantly smaller. It does not yet prove that capture_frame has acceptable latency or throughput. Measure frame readback cost, CPU usage, GPU usage, and frame pacing before deciding whether to keep CPU pixel copies or later integrate a native Metal surface.


## Follow-up: reuse Ruffle's native HTTP/TCP navigator before writing a custom one

Ruffle's frontend-utils crate exposes ExternalNavigatorBackend, FutureSpawner, and NavigatorInterface. The native backend already implements:

- HTTP fetches through reqwest, including cookies, referer, optional proxy, and user-agent configuration.
- URL resolution and relative asset loading.
- Raw TCP Socket/XMLSocket transport through Tokio TcpStream.
- Socket allow/deny/ask behavior and a NavigatorInterface confirmation callback.
- Async task scheduling through an injected FutureSpawner.

The native crate now references the pinned ruffle_frontend_utils package with its navigator and native-tls features, and has a compile-time type probe for these APIs.

**Preferred first implementation:** Reuse ExternalNavigatorBackend. Implement only Skua's FutureSpawner and NavigatorInterface, and configure the correct base URL, cookies/referer, proxy, content descriptor, and socket policy. This avoids reimplementing HTTP behavior and raw TCP stream handling.

The FutureSpawner must run on the same dedicated Rust owner thread as the Player. Its futures are not declared Send, and the native navigator uses Tokio networking/timers. A current-thread Tokio runtime with a LocalSet is the leading implementation, but it must be validated against the pinned Ruffle revision and tested for cancellation/disposal.

Only replace ExternalNavigatorBackend with a custom NavigatorBackend if Skua needs to intercept or redirect individual socket streams in a way the existing backend cannot support. The existing CaptureProxy is not a drop-in replacement for the native raw TCP backend (see the earlier CaptureProxy section).


## Follow-up: current FlashCall event handler does not synchronously re-enter Flash

Inspected the existing ScriptInterface subscription and HandleFlashCall implementation.

- ScriptInterface subscribes to IFlashUtil.FlashCall.
- The "loaded" callback only starts the ScriptInterface worker thread.
- "pre-load" schedules its Flash.Call for later on a separate task.
- Other callbacks update state, send messenger events, log, or open external URLs.
- No synchronous Flash.Call was found inside the current HandleFlashCall switch.

This reduces the immediate reentrancy risk for forwarding ExternalInterface.call from the Rust owner thread into the existing .NET FlashCall event. Still keep the C ABI callback contract explicit and marshal any UI-bound work to Avalonia's dispatcher. Add a regression test that fails if a future FlashCall subscriber synchronously calls back into the same Ruffle session.
