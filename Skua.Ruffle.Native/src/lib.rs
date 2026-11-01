//! Native Ruffle integration boundary for VibeSkua.
//!
//! Keep Ruffle's Rust types behind this crate. Only expose a stable C ABI to .NET.

use std::any::Any;
use std::collections::{HashSet, VecDeque};
use std::ffi::{c_char, c_void, CStr};
use std::fs::File;
use std::io;
use std::path::{Path, PathBuf};
use std::ptr;
use std::rc::Rc;
use std::slice;
use std::sync::{Arc, Mutex, RwLock};
use std::thread::{self, ThreadId};
use url::Url;

use ruffle_core::backend::navigator::{OwnedFuture, SocketMode};
use ruffle_core::context::UpdateContext;
use ruffle_core::external::{ExternalInterfaceProvider, Value as ExternalValue};
use ruffle_core::loader::Error;
use ruffle_frontend_utils::backends::audio::CpalAudioBackend;
use ruffle_frontend_utils::backends::navigator::{
    ExternalNavigatorBackend, FutureSpawner, NavigatorInterface,
};
use ruffle_frontend_utils::content::{ContentDescriptor, PlayingContent};

struct SkuaFutureSpawner;

impl FutureSpawner<Error> for SkuaFutureSpawner {
    fn spawn(&self, future: OwnedFuture<(), Error>) {
        tokio::task::spawn_local(async move {
            if let Err(error) = future.await {
                tracing::error!("Ruffle navigator task failed: {error}");
            }
        });
    }
}

#[derive(Clone)]
struct SkuaNavigatorInterface {
    allowed_sockets: Arc<RwLock<HashSet<String>>>,
    pending_calls: Arc<Mutex<VecDeque<PendingFlashCall>>>,
}

impl SkuaNavigatorInterface {
    fn new(pending_calls: Arc<Mutex<VecDeque<PendingFlashCall>>>) -> Self {
        Self {
            allowed_sockets: Arc::new(RwLock::new(HashSet::new())),
            pending_calls,
        }
    }

    fn allow_socket(&self, host: &str, port: u16) {
        if let Ok(mut allowed_sockets) = self.allowed_sockets.write() {
            let host = host.to_ascii_lowercase();
            allowed_sockets.insert(format!("{host}:{port}"));
            if port != 843 {
                allowed_sockets.insert(format!("{host}:843"));
            }
        }
    }
}

impl NavigatorInterface for SkuaNavigatorInterface {
    fn navigate_to_website(&self, url: Url) {
        if let Ok(mut pending_calls) = self.pending_calls.lock() {
            pending_calls.push_back(PendingFlashCall {
                function: "openWebsite".to_string(),
                arguments: vec![serde_json::Value::String(url.to_string())],
            });
        }
    }

    async fn open_file(&self, path: &Path) -> io::Result<File> {
        File::open(path)
    }

    async fn confirm_socket(&self, host: &str, port: u16) -> bool {
        let address = format!("{}:{port}", host.to_ascii_lowercase());
        self.allowed_sockets
            .read()
            .map(|allowed_sockets| allowed_sockets.contains(&address))
            .unwrap_or(false)
    }
}

struct PendingFlashCall {
    function: String,
    arguments: Vec<serde_json::Value>,
}

#[derive(Clone)]
struct SkuaExternalInterfaceProvider {
    navigator_interface: SkuaNavigatorInterface,
    pending_calls: Arc<Mutex<VecDeque<PendingFlashCall>>>,
    callback_names: Arc<RwLock<HashSet<String>>>,
}

impl SkuaExternalInterfaceProvider {
    fn new(
        navigator_interface: SkuaNavigatorInterface,
        pending_calls: Arc<Mutex<VecDeque<PendingFlashCall>>>,
    ) -> Self {
        Self {
            navigator_interface,
            pending_calls,
            callback_names: Arc::new(RwLock::new(HashSet::new())),
        }
    }
}

impl ExternalInterfaceProvider for SkuaExternalInterfaceProvider {
    fn call_method(
        &self,
        _context: &mut UpdateContext<'_>,
        name: &str,
        args: &[ExternalValue],
    ) -> ExternalValue {
        // Externalizer.call(name, ...rest) passes its rest array as one
        // ExternalInterface argument. The existing ActiveX host exposed the
        // array's elements as individual arguments, so preserve that contract.
        let args = match args {
            [ExternalValue::List(values)] => values.as_slice(),
            _ => args,
        };

        // Socket permissions are handled inside Rust to avoid re-entering the
        // native session from a managed callback while its player is borrowed.
        if name == "allowSocket" {
            if let [ExternalValue::String(host), ExternalValue::Number(port), ..] = args {
                if port.is_finite() && *port >= 1.0 && *port <= f64::from(u16::MAX) {
                    self.navigator_interface.allow_socket(host, *port as u16);
                }
            }
            return ExternalValue::Undefined;
        }

        let arguments = args.iter().map(external_value_to_json).collect();
        if let Ok(mut pending_calls) = self.pending_calls.lock() {
            pending_calls.push_back(PendingFlashCall {
                function: name.to_string(),
                arguments,
            });
        }

        ExternalValue::Undefined
    }

    fn on_callback_available(&self, name: &str) {
        if let Ok(mut callback_names) = self.callback_names.write() {
            callback_names.insert(name.to_string());
        }
    }

    fn get_id(&self) -> Option<String> {
        Some("VibeSkua".to_string())
    }
}

struct RuffleSession {
    owner_thread: ThreadId,
    runtime: tokio::runtime::Runtime,
    local_set: tokio::task::LocalSet,
    player: Arc<Mutex<ruffle_core::Player>>,
    navigator_interface: SkuaNavigatorInterface,
    pending_calls: Arc<Mutex<VecDeque<PendingFlashCall>>>,
    width: u32,
    height: u32,
}

impl RuffleSession {
    fn new(swf_bytes: &[u8], swf_path: &Path, width: u32, height: u32) -> Result<Self, String> {
        if width == 0 || height == 0 {
            return Err("Viewport dimensions must be greater than zero.".to_string());
        }

        let swf_path = std::fs::canonicalize(swf_path).map_err(|error| error.to_string())?;
        let swf_url = Url::from_file_path(&swf_path).map_err(|_| "SWF path could not be converted to a file URL.".to_string())?;
        let movie = ruffle_core::tag_utils::SwfMovieData::from_data(swf_bytes, swf_url.to_string(), None, None)
            .map_err(|error| format!("Ruffle could not parse the SWF: {error}"))?;
        let content_descriptor = ContentDescriptor::new_local(&swf_path, swf_path.parent().map(Path::to_path_buf))
            .ok_or_else(|| "Ruffle could not create a local SWF content descriptor.".to_string())?;

        let pending_calls = Arc::new(Mutex::new(VecDeque::new()));
        let navigator_interface = SkuaNavigatorInterface::new(pending_calls.clone());
        let game_base_url = Url::parse("https://game.aq.com/game/")
            .map_err(|error| format!("Invalid AQW base URL: {error}"))?;
        let navigator = ExternalNavigatorBackend::new(
            game_base_url.clone(),
            Some(game_base_url),
            None,
            SkuaFutureSpawner,
            None,
            false,
            HashSet::new(),
            SocketMode::Ask,
            Rc::new(PlayingContent::DirectFile(content_descriptor)),
            navigator_interface.clone(),
        );

        let renderer = ruffle_render_wgpu::backend::WgpuRenderBackend::for_offscreen(
            (width, height),
            ruffle_render_wgpu::wgpu::Backends::METAL,
            ruffle_render_wgpu::wgpu::PowerPreference::HighPerformance,
        )
        .map_err(|error| format!("Ruffle could not initialize the Metal renderer: {error}"))?;

        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .map_err(|error| format!("Could not initialize the Ruffle Tokio runtime: {error}"))?;
        let local_set = tokio::task::LocalSet::new();
        let external_interface = SkuaExternalInterfaceProvider::new(navigator_interface.clone(), pending_calls.clone());

        let mut builder = ruffle_core::PlayerBuilder::new()
            .with_movie(movie)
            .with_navigator(navigator)
            .with_renderer(renderer)
            .with_viewport_dimensions(width, height, 1.0)
            .with_external_interface(Box::new(external_interface));

        match CpalAudioBackend::new(None) {
            Ok(audio) => builder = builder.with_audio(audio),
            Err(error) => tracing::error!("Ruffle audio initialization failed: {error}"),
        }

        let player = local_set.block_on(&runtime, async { builder.build() });

        Ok(Self {
            owner_thread: thread::current().id(),
            runtime,
            local_set,
            player,
            navigator_interface,
            pending_calls,
            width,
            height,
        })
    }

    fn check_thread(&self) -> Result<(), String> {
        if thread::current().id() != self.owner_thread {
            return Err("Ruffle session calls must be made on the session's creating thread.".to_string());
        }
        Ok(())
    }

    fn tick(&mut self, elapsed_ms: f64) -> Result<(), String> {
        self.check_thread()?;
        if !elapsed_ms.is_finite() || elapsed_ms < 0.0 {
            return Err("Elapsed time must be a finite, non-negative value.".to_string());
        }

        let player = self.player.clone();
        self.local_set.block_on(&self.runtime, async move {
            let mut player = player.lock().map_err(|_| "Ruffle player mutex poisoned.".to_string())?;
            player.tick(ruffle_core::FloatDuration::from_millis(elapsed_ms));
            player.render();
            tokio::task::yield_now().await;
            Ok::<(), String>(())
        })
    }

    fn render(&mut self, output: *mut u8, capacity: usize) -> Result<usize, String> {
        self.check_thread()?;
        let mut player = self.player.lock().map_err(|_| "Ruffle player mutex poisoned.".to_string())?;
        let renderer = <dyn Any>::downcast_mut::<
            ruffle_render_wgpu::backend::WgpuRenderBackend<
                ruffle_render_wgpu::target::TextureTarget,
            >,
        >(player.renderer_mut())
        .ok_or_else(|| "Ruffle session does not have an offscreen WGPU renderer.".to_string())?;

        let frame = renderer.capture_frame().ok_or_else(|| "Ruffle did not produce an offscreen frame.".to_string())?;
        let bytes = frame.as_raw();
        if output.is_null() || capacity < bytes.len() {
            return Ok(bytes.len());
        }

        unsafe {
            ptr::copy_nonoverlapping(bytes.as_ptr(), output, bytes.len());
        }
        Ok(bytes.len())
    }

    fn drain_callbacks(&mut self, output: *mut c_char, capacity: usize) -> Result<usize, String> {
        self.check_thread()?;
        let mut pending_calls = self.pending_calls.lock().map_err(|_| "Ruffle callback queue mutex poisoned.".to_string())?;
        let calls = pending_calls
            .iter()
            .map(|call| serde_json::json!({
                "function": call.function,
                "arguments": call.arguments
            }))
            .collect::<Vec<_>>();
        let json = serde_json::to_string(&calls).map_err(|error| error.to_string())?;
        let bytes = json.as_bytes();
        if output.is_null() || capacity < bytes.len() + 1 {
            return Err(format!("Callback buffer requires {} bytes.", bytes.len() + 1));
        }

        unsafe {
            ptr::copy_nonoverlapping(bytes.as_ptr(), output.cast::<u8>(), bytes.len());
            *output.add(bytes.len()) = 0;
        }
        pending_calls.clear();
        Ok(bytes.len() + 1)
    }

    fn call(&mut self, function_name: &str, arguments_json: &str) -> Result<String, String> {
        self.check_thread()?;
        let json_arguments: Vec<serde_json::Value> = serde_json::from_str(arguments_json)
            .map_err(|error| format!("Invalid ExternalInterface arguments JSON: {error}"))?;
        let arguments = json_arguments.into_iter().map(json_to_external_value).collect::<Vec<_>>();
        let player = self.player.clone();

        let result = self.local_set.block_on(&self.runtime, async move {
            let mut player = player.lock().map_err(|_| "Ruffle player mutex poisoned.".to_string())?;
            Ok::<Option<ExternalValue>, String>(player.call_internal_interface(function_name, &arguments))
        })?;

        let result = result.unwrap_or(ExternalValue::Undefined);
        serde_json::to_string(&external_value_to_json(&result))
            .map_err(|error| format!("Could not serialize the Ruffle return value: {error}"))
    }

    fn resize(&mut self, width: u32, height: u32, scale_factor: f64) -> Result<(), String> {
        self.check_thread()?;
        if width == 0 || height == 0 || !scale_factor.is_finite() || scale_factor <= 0.0 {
            return Err("Viewport dimensions and scale factor must be positive.".to_string());
        }

        let mut player = self.player.lock().map_err(|_| "Ruffle player mutex poisoned.".to_string())?;
        player.set_viewport_dimensions(ruffle_core::ViewportDimensions {
            width,
            height,
            scale_factor,
        });
        self.width = width;
        self.height = height;
        Ok(())
    }
}

fn external_value_to_json(value: &ExternalValue) -> serde_json::Value {
    match value {
        ExternalValue::Undefined => serde_json::json!({ "$ruffle": "undefined" }),
        ExternalValue::Null => serde_json::Value::Null,
        ExternalValue::Bool(value) => serde_json::Value::Bool(*value),
        ExternalValue::Number(value) => serde_json::Number::from_f64(*value)
            .map(serde_json::Value::Number)
            .unwrap_or(serde_json::Value::Null),
        ExternalValue::String(value) => serde_json::Value::String(value.clone()),
        ExternalValue::Object(value) => {
            let object = value
                .iter()
                .map(|(key, value)| (key.clone(), external_value_to_json(value)))
                .collect();
            serde_json::Value::Object(object)
        }
        ExternalValue::List(value) => {
            serde_json::Value::Array(value.iter().map(external_value_to_json).collect())
        }
    }
}

fn json_to_external_value(value: serde_json::Value) -> ExternalValue {
    match value {
        serde_json::Value::Null => ExternalValue::Null,
        serde_json::Value::Bool(value) => ExternalValue::Bool(value),
        serde_json::Value::Number(value) => ExternalValue::Number(value.as_f64().unwrap_or_default()),
        serde_json::Value::String(value) => ExternalValue::String(value),
        serde_json::Value::Array(value) => ExternalValue::List(value.into_iter().map(json_to_external_value).collect()),
        serde_json::Value::Object(mut value) => {
            if value.len() == 1 && value.get("$ruffle").and_then(serde_json::Value::as_str) == Some("undefined") {
                return ExternalValue::Undefined;
            }
            let object = value.into_iter().map(|(key, value)| (key, json_to_external_value(value))).collect();
            ExternalValue::Object(object)
        }
    }
}

unsafe fn write_error(buffer: *mut c_char, capacity: usize, error: &str) {
    if buffer.is_null() || capacity == 0 {
        return;
    }

    let bytes = error.as_bytes();
    let length = bytes.len().min(capacity - 1);
    unsafe {
        ptr::copy_nonoverlapping(bytes.as_ptr(), buffer.cast::<u8>(), length);
        *buffer.add(length) = 0;
    }
}

unsafe fn get_session<'a>(session: *mut c_void) -> Result<&'a mut RuffleSession, String> {
    if session.is_null() {
        return Err("Ruffle session handle is null.".to_string());
    }
    Ok(unsafe { &mut *(session.cast::<RuffleSession>()) })
}

/// Creates a Ruffle session. The session is bound to the calling thread.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn skua_ruffle_session_create(
    swf_data: *const u8,
    swf_length: usize,
    swf_path: *const c_char,
    width: u32,
    height: u32,
    error_buffer: *mut c_char,
    error_capacity: usize,
) -> *mut c_void {
    if swf_data.is_null() || swf_path.is_null() || swf_length == 0 {
        unsafe { write_error(error_buffer, error_capacity, "SWF data and path are required.") };
        return ptr::null_mut();
    }

    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let swf_bytes = unsafe { slice::from_raw_parts(swf_data, swf_length) };
        let swf_path = match unsafe { CStr::from_ptr(swf_path) }.to_str() {
            Ok(path) => PathBuf::from(path),
            Err(error) => return Err(format!("SWF path is not valid UTF-8: {error}")),
        };
        RuffleSession::new(swf_bytes, &swf_path, width, height)
    }));

    match result {
        Ok(Ok(session)) => Box::into_raw(Box::new(session)).cast::<c_void>(),
        Ok(Err(error)) => {
            unsafe { write_error(error_buffer, error_capacity, &error) };
            ptr::null_mut()
        }
        Err(_) => {
            unsafe { write_error(error_buffer, error_capacity, "Ruffle session creation panicked.") };
            ptr::null_mut()
        }
    }
}

/// Advances the player, renders the next frame, and polls local navigator tasks.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn skua_ruffle_session_tick(session: *mut c_void, elapsed_ms: f64) -> i32 {
    let result = unsafe { get_session(session) }.and_then(|session| session.tick(elapsed_ms));
    result.map(|_| 0).unwrap_or(-1)
}

/// Copies the most recently rendered RGBA frame into the supplied buffer.
///
/// Returns the required buffer size. Pass a null buffer to query the size.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn skua_ruffle_session_render(
    session: *mut c_void,
    output: *mut u8,
    capacity: usize,
) -> usize {
    unsafe { get_session(session) }
        .and_then(|session| session.render(output, capacity))
        .unwrap_or(usize::MAX)
}

/// Copies queued ActionScript-to-host ExternalInterface calls into a UTF-8 JSON buffer.
///
/// Each entry contains a function name and its arguments. The queue is only cleared
/// after the complete result has been copied successfully.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn skua_ruffle_session_drain_callbacks(
    session: *mut c_void,
    output: *mut c_char,
    capacity: usize,
) -> usize {
    unsafe { get_session(session) }
        .and_then(|session| session.drain_callbacks(output, capacity))
        .unwrap_or(usize::MAX)
}

/// Calls an ActionScript callback registered through ExternalInterface.addCallback.
///
/// Returns the required UTF-8 JSON buffer size, or usize::MAX on error.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn skua_ruffle_session_call(
    session: *mut c_void,
    function_name: *const c_char,
    arguments_json: *const c_char,
    output: *mut c_char,
    capacity: usize,
) -> usize {
    if function_name.is_null() || arguments_json.is_null() {
        return usize::MAX;
    }

    let function_name = match unsafe { CStr::from_ptr(function_name) }.to_str() {
        Ok(value) => value.to_string(),
        Err(_) => return usize::MAX,
    };
    let arguments_json = match unsafe { CStr::from_ptr(arguments_json) }.to_str() {
        Ok(value) => value.to_string(),
        Err(_) => return usize::MAX,
    };

    let Ok(session) = (unsafe { get_session(session) }) else {
        return usize::MAX;
    };
    let Ok(result) = session.call(&function_name, &arguments_json) else {
        return usize::MAX;
    };
    let bytes = result.as_bytes();
    if output.is_null() || capacity < bytes.len() + 1 {
        return bytes.len() + 1;
    }

    unsafe {
        ptr::copy_nonoverlapping(bytes.as_ptr(), output.cast::<u8>(), bytes.len());
        *output.add(bytes.len()) = 0;
    }
    bytes.len() + 1
}

/// Adds an explicit host:port socket permission to this session.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn skua_ruffle_session_allow_socket(
    session: *mut c_void,
    host: *const c_char,
    port: u16,
) -> bool {
    if host.is_null() {
        return false;
    }
    let Ok(host) = unsafe { CStr::from_ptr(host) }.to_str() else {
        return false;
    };
    let Ok(session) = (unsafe { get_session(session) }) else {
        return false;
    };
    if session.check_thread().is_err() {
        return false;
    }
    session.navigator_interface.allow_socket(host, port);
    true
}

/// Updates the player viewport dimensions.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn skua_ruffle_session_resize(
    session: *mut c_void,
    width: u32,
    height: u32,
    scale_factor: f64,
) -> bool {
    unsafe { get_session(session) }
        .and_then(|session| session.resize(width, height, scale_factor))
        .is_ok()
}

/// Returns the current viewport width and height.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn skua_ruffle_session_get_size(
    session: *mut c_void,
    width: *mut u32,
    height: *mut u32,
) -> bool {
    if width.is_null() || height.is_null() {
        return false;
    }
    let Ok(session) = (unsafe { get_session(session) }) else {
        return false;
    };
    unsafe {
        *width = session.width;
        *height = session.height;
    }
    true
}

/// Destroys a Ruffle session. Must be called on the creating thread.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn skua_ruffle_session_destroy(session: *mut c_void) {
    if session.is_null() {
        return;
    }
    let session = unsafe { &mut *(session.cast::<RuffleSession>()) };
    if session.check_thread().is_err() {
        tracing::error!("Refusing to destroy Ruffle session from a non-owner thread.");
        return;
    }
    drop(unsafe { Box::from_raw(session as *mut RuffleSession) });
}
