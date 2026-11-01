# macOS Compatibility Audit

**Branch:** `feature/macos-support`  
**Targets:** macOS Apple Silicon (`osx-arm64`) and Intel (`osx-x64`)  
**Flash direction:** Ruffle, with native Rust integration as the leading experiment  
**Status:** Initial static source audit; no macOS build or runtime test has been performed.

## Executive summary

VibeSkua is not currently a cross-platform application with a few Windows-specific edges. The Avalonia UI is cross-platform in principle, but the client runtime is tightly coupled to the Windows ActiveX Flash Player, WinForms hosting, Win32 window APIs, Windows hooks, and Windows native libraries.

The .NET scripting/compiler layer is in a much better position: the core project already targets `net10.0`, uses Roslyn for C# compilation, and loads script assemblies through `AssemblyLoadContext`. These APIs are available on macOS, although filesystem paths, dependency loading, and runtime-specific behavior still need validation.

The main architectural risk is Flash. Ruffle supports AVM1/AVM2 and has native desktop builds, but the browser/WebAssembly integration cannot be assumed to support AQW's raw TCP socket requirements. A native Ruffle integration with a custom host/backend needs a proof-of-concept before selecting the final architecture.

## Findings

### 1. Avalonia application target and build configuration

**Files inspected**
- `Skua.App.Avalonia/Skua.App.Avalonia.csproj`
- `Skua.Manager.Avalonia/Skua.Manager.Avalonia.csproj`
- `Skua.Shared.Avalonia/Skua.Shared.Avalonia.csproj`
- `Directory.Packages.props`
- `Program.cs`

**Findings**
- The client project targets `net10.0-windows`, not generic `net10.0`.
- Its runtime identifiers are only `win-x64;win-x86`.
- The manager already targets generic `net10.0`.
- Avalonia 11.3.12 packages are used, so the UI framework itself is not the primary blocker.
- The client project conditionally enables WinForms and references generated Shockwave Flash COM interop assemblies on Windows.
- The client project copies Windows CoreHook native DLLs into its output.
- The application startup code unconditionally declares and uses `kernel32.dll` for `SetDllDirectory`.
- Startup configures `Win32PlatformOptions` unconditionally, which is not an appropriate cross-platform startup path.
- The project uses `WinExe`, a Windows-oriented output type, and an ICO application icon/Windows manifest.

**Required work**
- Move the client to a cross-platform target framework and define explicit runtime identifiers for `osx-arm64` and `osx-x64` in publishing.
- Keep Windows-specific references, resources, native assets, and startup configuration behind OS-specific MSBuild conditions.
- Use Avalonia platform detection without forcing Win32 platform options on macOS.
- Review the custom title-bar and window placement behavior on macOS rather than assuming Windows chrome behavior maps directly.
- Keep the manager and client packaging pipelines platform-aware.

### 2. Flash host and C# ↔ ActionScript bridge

**Files inspected**
- `Skua.App.Avalonia/Controls/WinFormsFlashHost.cs`
- `Skua.App.Avalonia/Flash/FlashUtil.cs`
- `Skua.App.Avalonia/Flash/EoLHook.cs`
- `Skua.Core.Interfaces/Flash/IFlashUtil.cs`
- `Skua.Core/Flash/FlashObject.cs`
- `Skua.Core/Flash/FlashArray.cs`
- `Skua.AS3/skua/src/skua/Externalizer.as`

**Findings**
- The current host is a WinForms `NativeControlHost` containing an `AxShockwaveFlashObjects.AxShockwaveFlash` ActiveX control.
- Flash initialization, rendering, lifecycle, and calls use COM APIs such as `CallFunction`, `OcxState`, and COM object release.
- `FlashUtil` has a non-Windows stub, but its methods throw `NotImplementedException`; it is not a usable fallback.
- The C# ↔ ActionScript contract is substantial. The AS3 `Externalizer` registers callbacks through `ExternalInterface.addCallback` and calls host functions through `ExternalInterface.call`.
- The C# side serializes/deserializes Flash XML values and exposes object/array wrappers used throughout the core.
- The current end-of-life workaround uses CoreHook and a `kernel32.dll` hook to spoof the system year during ActiveX initialization.

**Required work**
- Preserve the existing `IFlashUtil` contract initially, but implement a new Ruffle-backed adapter instead of trying to emulate ActiveX.
- Define a per-player host/session abstraction so multiple Ruffle instances do not share callbacks or state accidentally.
- Reimplement the complete ExternalInterface bridge, including callback registration, invocation, argument conversion, return values, errors, and disposal.
- Replace the ActiveX-specific end-of-life hook only if the Ruffle runtime actually requires an equivalent workaround. Do not carry the CoreHook workaround forward by default.
- Verify SWF load/unload, resize, focus, keyboard input, audio, screenshots, and lifecycle behavior.

### 3. Ruffle integration: key architecture risk

**Research**
- Ruffle is a Rust Flash Player emulator targeting desktop and WebAssembly.
- Its desktop application supports macOS builds.
- Its AVM2 socket implementation routes connections through a `NavigatorBackend`.
- Ruffle's WebAssembly/browser build is subject to browser networking restrictions. A browser-hosted SWF must not be assumed to have arbitrary raw TCP access.
- Ruffle exposes networking configuration, but the web build's permissions do not remove browser sandbox restrictions.

**Decision**
- Do not select a WebView + Ruffle-Web implementation as the production architecture until an AQW login/network test proves it works.
- First investigate embedding the native Ruffle core (Rust) behind a narrow C ABI or other supported native boundary. The host would need to provide rendering, input, lifecycle, networking, and ExternalInterface integration.
- Evaluate the cost of maintaining a small Ruffle fork/custom backend against the complexity of a WebView bridge.
- A standalone Ruffle desktop process is useful for compatibility experiments, but is not by itself an integration solution for Skua's embedded client, multi-instance host, and bidirectional bridge.

**Proof-of-concept acceptance criteria**
1. Load the bundled `skua.swf` and the AQW game SWF it loads.
2. Complete the AQW server connection and login.
3. Demonstrate stable bidirectional TCP communication.
4. Demonstrate `ExternalInterface.addCallback` and `ExternalInterface.call` round-trips.
5. Run two isolated player instances simultaneously.
6. Verify that a native Ruffle instance can be resized, paused, resumed, and disposed cleanly.

If raw TCP support cannot be supplied reliably, stop and reassess before investing in the UI port.

### 4. C# compiler and dynamic script execution

**Files inspected**
- `Skua.Core/Skua.Core.csproj`
- `Skua.Core/Compiler.cs`
- `Skua.Core/ScriptLoadContext.cs`
- `Skua.Core/Scripts/ScriptManager.cs`

**Findings**
- `Skua.Core` already targets generic `net10.0`.
- Runtime C# compilation uses Roslyn (`Microsoft.CodeAnalysis`) and emits assemblies to memory or disk.
- Script assemblies are loaded through `Assembly.Load`, `Assembly.LoadFrom`, and a collectible `AssemblyLoadContext`.
- The project also references `Westwind.Scripting`.
- These are not inherently Windows-only APIs. The compiler itself is not the primary porting blocker.
- Script cache paths are derived from `ClientFileSources.SkuaScriptsDIR`; those paths and any assumptions about separators/casing/permissions need review.
- Runtime plugin and script dependency resolution needs testing on both macOS architectures.

**Required work**
- Keep the shared compiler in `Skua.Core`; avoid creating a separate Mac-only compiler.
- Verify Roslyn reference discovery and dynamic assembly loading in macOS self-contained and framework-dependent builds.
- Audit any script dependencies that load native DLLs or launch external Windows executables.
- Test include processing, cached assemblies, unload/reload, and plugin loading on macOS.
- Ensure cache and script paths use platform-specific application data directories.

### 5. ActionScript 3 build pipeline

**Files inspected**
- `Skua.AS3/Skua.AS3.csproj`
- `Skua.AS3/compile-as3.ps1`
- `Skua.AS3/Build.bat`
- `Skua.AS3/skua/src/skua/Main.as`

**Findings**
- The AS3 project currently targets `net10.0-windows`, despite being a build/content project.
- The build entry point is a PowerShell script invoked through a Windows `.bat` wrapper.
- The script searches Windows-only paths such as `C:\MoonshineSDKs`, `C:\ApacheFlex`, and Program Files.
- It expects `mxmlc.bat` and Windows-style Flex SDK paths.
- The compiler targets Flash Player 28.0 and emits `skua.swf`.
- The AS3 source uses `URLLoader`, `URLRequest`, `Security.allowDomain`, and a loader for game content.

**Required work**
- Make the AS3 project framework-neutral unless a real .NET Windows dependency is found.
- Replace the `.bat`-only entry point with a cross-platform build script.
- Resolve Apache Flex/MXMLC through explicit configuration or a documented SDK location, and invoke the Java compiler in a platform-neutral way.
- Validate the Flex SDK, Java, and playerglobal requirements on macOS ARM64 and x64.
- Keep generated SWF output deterministic and consumable by both Windows and macOS builds.

### 6. Win32 and native dependency inventory

**Confirmed from inspected files**
- `Program.cs`: `SetDllDirectory` from `kernel32.dll`, Windows-only native DLL resolver assumptions, Win32 Avalonia platform options.
- `App.axaml.cs`: `user32.dll` APIs for child-window embedding, reparenting, positioning, and messages.
- `EoLHook.cs`: CoreHook + `kernel32.dll`.
- `WinFormsFlashHost.cs`: WinForms, HWND platform handle, ActiveX hosting.
- Client project: Windows COM interop assemblies and CoreHook DLL assets.
- Tabbed host/manager process integration uses Windows window handles and message-based coordination.

**Required work**
- Search all client and shared projects for P/Invoke, COM, HWND, registry, WinForms, WMI, Windows services, and Windows-only process assumptions.
- Separate cross-platform process coordination from Windows HWND embedding. macOS must use a different host strategy; do not attempt to reproduce Win32 child-window semantics.
- Use named pipes or another portable IPC transport where appropriate, while verifying the current protocol and lifetime semantics.
- Keep platform-specific implementations behind interfaces and register them at startup.

### 7. Paths, settings, and distribution

**Findings**
- The project has a unified settings service, but this audit has not yet traced every path and file operation.
- The client and manager both use Velopack.
- Existing release scripts are Windows batch files.
- Application icons, manifests, shortcuts, update flows, and install paths need platform-specific treatment.

**Required work**
- Establish macOS application data/cache/log/script locations using .NET special folders and a consistent app identifier.
- Preserve import/migration of existing settings where possible.
- Add macOS packaging and update feeds independently of the Windows release.
- Plan code signing, notarization, hardened runtime entitlements, and distribution for both architectures.

## Recommended work order

| Order | Work item | Exit condition |
|---|---|---|
| 0 | Native Ruffle feasibility spike | AQW SWF loads, logs in, and communicates over TCP |
| 1 | Platform dependency inventory | Every Windows-only API/dependency has an owner and replacement plan |
| 2 | Cross-platform project/build targets | Client and manager compile for both macOS RIDs |
| 3 | Ruffle host and bridge | Existing `IFlashUtil` consumers work through the new adapter |
| 4 | Portable IPC and multi-client | Two or more independent clients work without HWND embedding |
| 5 | Script/compiler validation | Existing C# scripts, includes, caches, and plugins run |
| 6 | Packaging and QA | Signed/notarized ARM64 and x64 builds launch and update |

## Explicit non-goals for the first pass

- Do not rewrite the Skua scripting API.
- Do not fork the entire UI into a separate macOS codebase.
- Do not remove or destabilize the Windows ActiveX implementation while the native Ruffle proof-of-concept is being evaluated.
- Do not claim macOS compatibility based only on a successful Avalonia window launch.

## Audit limitations

This is an initial static audit of selected project files and source paths. It is not an exhaustive scan of every source file, package, native dependency, or runtime path. No macOS hardware/runtime was available in this audit, so no macOS compilation, signing, launch, game login, or networking tests have been run.


## Additional dependency tracing

A second pass followed the multi-client and network paths beyond the initial project-file review.

### 8. Multi-client host is more Windows-specific than the UI suggests

**Files inspected**
- `Skua.App.Avalonia/App.axaml.cs`
- `Skua.App.Avalonia/Views/TabbedHostWindow.axaml.cs`
- `Skua.Manager.Avalonia/Services/ProcessCloser.cs`

**Findings**
- The tabbed host launches child client processes and embeds their native windows using HWND APIs.
- The host uses a large set of `user32.dll` and `comctl32.dll` calls, including child-window positioning, styles, custom messages, and process/window lifecycle handling.
- The client also uses Windows messages for army controls, grid mode, login/logout, options, and child readiness.
- Named pipes are already used for some host/client communication. That is a useful portable transport candidate, but it does not make the current architecture portable: the HWND embedding and custom window-message control plane remain Windows-only.
- The manager's process-closing helper also posts Windows messages to HWNDs.

**Implication**
- The macOS multi-client implementation should not try to reproduce HWND child-window embedding.
- A more portable design is a single Avalonia host window containing multiple independently hosted Ruffle surfaces/sessions, with host-to-session commands flowing through a typed IPC or in-process messaging abstraction.
- If clients remain separate processes, named pipes or Unix domain sockets can carry messages, but rendering must use a macOS-native hosting model rather than window reparenting.
- Existing army-control commands should be moved behind a platform-neutral message contract, not recreated as platform-specific window messages.

### 9. Network capture proxy is already based on portable .NET sockets

**File inspected**
- `Skua.Core/GameProxy/CaptureProxy.cs`

**Findings**
- The capture proxy uses `TcpListener`, `TcpClient`, and `IPAddress.Loopback` from .NET.
- The proxy itself does not show a Windows-only networking dependency in the inspected source.
- This is encouraging for macOS, but the proxy's lifecycle, port allocation, and interaction with the Flash runtime still need end-to-end testing.

**Implication**
- Keep the existing proxy in shared core unless runtime tests reveal an issue.
- Validate whether Ruffle's native socket backend can be directed through the existing local proxy, and whether the AQW client expects socket policy requests or other Flash-specific connection behavior.

### 10. FFDec and bundled Java tooling

**Finding**
- The repository contains a bundled FFDec distribution with Java JAR dependencies, in addition to Windows-oriented scripts elsewhere in the build/tooling tree.
- Java itself is cross-platform, but any launch scripts, executable names, path assumptions, and native helper dependencies around FFDec must be audited before calling the tool portable.

**Implication**
- Prefer invoking the Java entry point with a detected Java runtime on macOS, rather than depending on a Windows `.exe` or `.bat`.
- This is a tooling concern separate from the runtime Flash player and does not solve the Ruffle networking requirement.

## Revised risk ranking

| Area | Risk | Reason |
|---|---|---|
| Ruffle native integration and AQW sockets | Critical | Needs a native networking backend and a working AQW login proof |
| Tabbed host / multi-client rendering | Critical | Current design depends on HWND embedding and Windows messages |
| Flash ↔ C# ExternalInterface bridge | Critical | ActiveX COM callbacks must be replaced without breaking the existing API contract |
| Windows native hooks / interop | High | CoreHook, kernel32, user32, comctl32, COM and native DLLs |
| Project targeting / packaging | High | Client is `net10.0-windows` with Windows-only RIDs and assets |
| AS3 build pipeline | Medium | Flex/MXMLC and Java are portable; current wrappers and SDK discovery are Windows-specific |
| C# runtime compiler | Low–Medium | Roslyn and collectible AssemblyLoadContext are cross-platform APIs; runtime dependencies still need validation |
| Core TCP capture proxy | Low–Medium | Uses portable .NET sockets; end-to-end runtime behavior remains unproven |
| Avalonia UI | Medium | Framework is cross-platform, but native hosting, chrome, shortcuts, dialogs, and window management need platform review |


## Additional pass: Avalonia service and packaging dependencies

Inspected the client, manager, and shared Avalonia project files plus their platform-facing services.

### 11. Client project has Windows-only build identity and native assets

The client project currently:
- Targets net10.0-windows and publishes only win-x64 / win-x86.
- Uses WinExe, a Windows application icon, and an application manifest.
- Adds WinForms only when the build host is Windows.
- Adds ActiveX COM interop assemblies only on Windows.
- References CoreHook unconditionally.
- Copies CoreHook/CoreLoad x86 and x64 Windows DLLs as content.
- Includes a Windows-only Flash host and Windows-specific EoL hook behind IS_WINDOWS compilation.

**Required work**
- Retarget the client to net10.0 and define runtime identifiers per publish target.
- Make output type, application icon, manifest, CoreHook package, COM references, and native DLL content conditional on Windows.
- Keep IS_WINDOWS defined only for Windows builds; introduce a separate platform abstraction rather than scattering new IS_MACOS checks through shared code.
- Add a macOS app bundle and bundle identifier, and ensure native Ruffle dylibs are included in the app bundle with correct architecture slices.

### 12. Hotkeys and file dialogs already have partial platform separation

**Files inspected**
- Skua.App.Avalonia/Services/HotKeyService.cs
- Skua.App.Avalonia/Services/FileDialogService.cs
- Skua.App.Avalonia/Services/ScreenshotService.cs
- Skua.App.Avalonia/Services/ClipboardService.cs

**Findings**
- HotKeyService already uses Avalonia key gesture routing and compiles its low-level global keyboard hook only under IS_WINDOWS. The Windows hook is used for host/global behavior and cannot be assumed to have a macOS equivalent.
- FileDialogService has a Windows Forms implementation and a separate non-Windows path. This is a useful pattern to retain, but the non-Windows implementation must be validated on macOS.
- ScreenshotService has platform-specific branches.
- ClipboardService uses Avalonia's clipboard abstraction and is a good example of a shared implementation.

**Required work**
- Keep Avalonia-local hotkeys shared.
- Decide explicitly whether macOS global hotkeys are needed; if so, implement them through a macOS-specific native service with appropriate permissions, not a Windows hook port.
- Test the non-Windows file-dialog path on macOS, including security-scoped file access where applicable.
- Retain Avalonia clipboard unless real platform testing exposes a limitation.

### 13. Manager process and executable discovery are Windows-specific

**Files inspected**
- Skua.Manager.Avalonia/Services/ProcessCloser.cs
- Skua.Manager.Avalonia/Services/SkuaExecutableLocator.cs
- Skua.Shared.Avalonia/Utils/AppShortcutHelper.cs
- Skua.Shared.Avalonia/Services/WindowPlacementHelper.cs

**Findings**
- ProcessCloser posts WM_CLOSE to enumerated top-level HWND windows using user32.dll.
- SkuaExecutableLocator searches only for VibeSkua.exe / Skua.exe and hardcodes net10.0-windows build output paths.
- AppShortcutHelper is already guarded by OperatingSystem.IsWindows() and uses Windows .lnk shortcut creation. This is safe as a Windows-only feature, but macOS needs its own packaging/shortcut strategy if desired.
- WindowPlacementHelper contains user32 imports, but the relevant native behavior is guarded by Windows OS checks. This is a better model than unconditional native calls.

**Required work**
- Replace executable discovery with an OS-aware application locator that understands macOS app bundles and executable paths.
- Replace ProcessCloser with a process lifecycle abstraction. On macOS, use Process APIs / explicit shutdown IPC rather than trying to post WM_CLOSE.
- Keep Windows shortcut creation isolated; macOS app bundle registration and Dock behavior should be handled by packaging or native macOS integration.

### 14. Cross-platform manager is closer than the client, but not ready

The manager targets generic net10.0 and uses Avalonia, but it depends on the client executable locator, process closing, Windows-oriented app manifest/icon configuration, and client launch assumptions. Its UI may compile on macOS before its launcher and lifecycle features work.

**Implication**
- Treat manager UI portability and manager-to-client lifecycle portability as separate acceptance gates.
- First get the client process launching and communicating on macOS, then complete manager account launch, stop, update, and multi-client workflows.


## 15. AQW socket policy calls versus Ruffle security model

A deeper ActionScript review found that Skua's Server.as explicitly calls Security.loadPolicyFile using xmlsocket:// URLs for server-list entries and before connecting to a selected game server.

Ruffle's current AVM2 implementation stubs flash.system.Security.loadPolicyFile. Ruffle's native socket implementation instead delegates connection requests to NavigatorBackend, where the desktop frontend applies its own SocketMode allow/deny/ask policy.

This is a confirmed API behavior difference, but not yet a proven AQW login blocker: Ruffle may intentionally replace Flash's policy-file enforcement with host-level socket permissions. The compatibility test must determine whether AQW's server requires the XML socket policy request as part of its connection flow. If not, use an explicit Skua host allow-list. If yes, implement xmlsocket policy retrieval in the native navigator or contribute support upstream.

Adobe's documented behavior for xmlsocket policy loading is to send a null-terminated policy-file request and await a null-terminated policy response before proceeding with the main socket connection. This should be included in the ActiveX-vs-Ruffle network trace comparison.


## 16. AS3 build portability work started on the macOS branch

The audit findings have been converted into initial build changes on feature/macos-support:

- Skua.AS3.csproj now targets generic net10.0 instead of net10.0-windows.
- compile-as3.ps1 now uses platform-neutral path separators, supports the Unix mxmlc launcher, and searches macOS/Linux Flex SDK locations as well as the existing Windows locations.
- Skua.AS3/build-as3.sh provides a native Unix launcher that invokes the Flex mxmlc shell executable directly.
- The existing Windows Build.bat entry point remains unchanged.

The new shell script passed bash -n syntax validation in the available environment. The Flex compiler itself is not installed here, and no SWF was produced. The PowerShell script has not been executed because PowerShell is not installed in the available environment. A real macOS build still needs to verify the installed Flex SDK, Java runtime, playerglobal SWC, and target-player 28 compilation.


## 17. Shared core portability fixes started

Two additional Windows assumptions were found in otherwise platform-neutral core code:

- Script compilation used a named mutex prefixed with Global\\. The compiler now retains that global namespace on Windows and uses a platform-neutral mutex name on macOS/Linux.
- Settings migration used a hardcoded Global\\VibeSkua.Migration.Init mutex. It now selects the Windows global name only on Windows and uses a portable name elsewhere.

Other reviewed core paths already use Environment.SpecialFolder.ApplicationData and Path.Combine, which map naturally to macOS application support paths through .NET.

ProcessStartService was also adjusted:
- Opening URLs no longer specifies the Windows-only "open" shell verb.
- VS Code is launched with ArgumentList instead of a manually concatenated command line.
- The fallback text editor uses Notepad on Windows, open -t on macOS, and xdg-open on Linux.

FlashTrustManager now exits immediately on non-Windows platforms because the Flash Player trust-folder mechanism is only relevant to the existing Windows ActiveX player.


## 18. Core hotkey forwarding had unguarded Win32 calls

Skua.Core.AppStartup.HotKeys contains user32 calls for:
- Enumerating client windows and broadcasting hotkey reloads.
- Forwarding host hotkey actions to embedded child HWNDs.
- Sending army-control messages to other client windows.
- Checking the foreground window before executing a hotkey.

These methods were reachable from shared hotkey commands even though the low-level global hook itself is Windows-only.

The macOS branch now:
- Uses local hotkey execution instead of HWND forwarding.
- Skips cross-process hotkey and army broadcasts on non-Windows platforms.
- Avoids foreground-window Win32 checks outside Windows.
- Retains the existing Windows forwarding behavior unchanged.

This is an initial compatibility fallback. Native macOS multi-client/army control still needs a platform-neutral message transport and host UI.


## 19. Manager launch and process shutdown portability started

The manager previously searched only for VibeSkua.exe / Skua.exe and Windows net10.0-windows build folders. It now has separate Windows and Unix executable discovery paths, including:
- A sibling VibeSkua.app bundle.
- A VibeSkua.app beside the manager bundle.
- /Applications and ~/Applications installations.
- osx-arm64 and osx-x64 development build outputs.
- Portable Unix apphost output.

ProcessCloser now retains its HWND/WM_CLOSE implementation on Windows and uses Process.CloseMainWindow on non-Windows platforms before falling back to process termination.

This does not yet replace the Windows tabbed host. macOS manager launch is intended to start standalone client processes until a portable host/session protocol is implemented.


## 20. Avalonia startup no longer forces Win32 on macOS

The client startup path had two additional Windows assumptions:
- Program.BuildAvaloniaApp unconditionally applied Win32PlatformOptions after UsePlatformDetect.
- App startup accepted --embed / --host-pid arguments and could enter the HWND child-window branch on any OS.

The macOS branch now:
- Applies Win32PlatformOptions only on Windows.
- Ignores HWND embedding arguments outside Windows, keeping the macOS client standalone.
- Runs the ActiveX SWF preload only on Windows.
- Uses RuntimeInformation.RuntimeIdentifier to locate native runtime assets.
- Resolves platform native library names using .dll, .dylib, or .so extensions and searches the application native asset directories.

This gets the Avalonia bootstrap closer to a real macOS launch, but it does not make the game playable yet. The non-Windows FlashUtil remains a NotImplementedException stub and must be replaced with the Ruffle-backed IFlashUtil implementation before macOS gameplay can work.


## 21. Client and manager project targets are now platform-selectable

The Avalonia client project now selects:
- net10.0-windows on Windows by default, preserving the existing ActiveX/WinForms implementation.
- net10.0 on macOS/Linux by default, compiling the non-Windows FlashUtil placeholder until the Ruffle adapter is implemented.

Windows Forms, the IS_WINDOWS symbol, ActiveX COM references, CoreHook, CoreHook native DLL content, the Windows icon, and the application manifest are limited to the net10.0-windows target.

The client runtime identifiers are scoped by target framework: win-x64/win-x86 for net10.0-windows and osx-arm64/osx-x64 for net10.0. The manager now emits WinExe only on Windows, uses Exe elsewhere, and limits its ICO/manifest settings to Windows.

**Important:** This is a build-target foundation, not a successful build. The non-Windows FlashUtil is still a deliberate placeholder that throws NotImplementedException. The next major implementation remains the Ruffle-backed IFlashUtil and player host.


## 22. Non-Windows XAML still needs a Flash host control type

MainWindow.axaml always declares WinFormsFlashHost, even though its real NativeControlHost/WinForms implementation is Windows-only. The non-Windows target therefore needs a placeholder Avalonia Control with the same XAML type name and a no-op SetScaleMode method for shared MainWindow code.

The macOS branch now provides that placeholder. The actual Ruffle renderer will replace this placeholder with a proper cross-platform game surface; it is not a rendering implementation.


## 23. macOS distribution can reuse the existing Velopack pipeline

Current Velopack documentation confirms that macOS packages use .app bundles. Velopack can generate the app bundle itself from a publish directory when supplied with a valid .icns icon, or accept a prebuilt .app directory. It can also generate a signed .pkg installer and portable .zip. A custom Info.plist / bundle identifier can be supplied.

This means we should keep Velopack as the cross-platform updater/packager instead of introducing a separate macOS update framework.

**Distribution plan:**
- Publish the client and manager separately for osx-arm64 and osx-x64.
- Package each architecture as its own Velopack macOS release initially.
- Supply a real .icns icon and a stable reverse-DNS bundle identifier.
- Sign and notarize release bundles before distribution.
- Do not enable the macOS App Sandbox when using Velopack's in-place updater; its updater needs to replace the .app bundle and may need elevation when installed under /Applications.
- Consider a universal app only after proving that every native Mach-O dependency (including the Ruffle dylib and Avalonia native components) can be merged and re-signed correctly.

Separate architecture packages are the lower-risk first release. .NET does not produce universal macOS binaries directly; it requires per-architecture publishing and lipo merging of native executables, with corresponding work for bundled native libraries.


## 24. macOS now bypasses the Windows tabbed-host process model

A deeper startup trace found that the client always created TabbedHostWindow as its main window when not launched with --embed. That path owns Windows named-pipe single-instance forwarding, HWND embedding, and Win32 window messaging.

The macOS branch now:
- Ignores --host-instance and related tab-host identity arguments.
- Skips Windows named-pipe / named-mutex single-instance forwarding.
- Creates the normal MainWindow directly on non-Windows platforms.
- Leaves the existing Windows TabbedHostWindow and child HWND embedding flow unchanged.

This gives macOS a standalone client window and allows the manager to launch separate client processes. It deliberately does not provide the current Windows tabbed multi-client host on macOS; that requires a separate cross-platform session/IPC design.


## 25. Manager startup had Windows-only single-instance and Win32 initialization

The manager's Program.cs used a named Global\\ EventWaitHandle, unconditionally called SetDllDirectory, resolved only Windows native DLLs, and always applied Win32PlatformOptions. This would prevent or break a native macOS manager launch even though the manager project itself targets generic net10.0.

The macOS branch now:
- Runs the Windows named-event single-instance/wake behavior only on Windows.
- Skips SetDllDirectory outside Windows.
- Resolves native assets using the current runtime identifier and platform extension.
- Applies Win32 Avalonia rendering/composition options only when the Windows compilation symbol is defined.
- Defines IS_WINDOWS for the manager only on Windows.

The macOS manager currently permits multiple manager instances rather than trying to emulate the Windows wake-event behavior. A native macOS single-instance / reopen-window implementation can be added separately if desired.
