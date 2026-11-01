//! Native Ruffle integration boundary for VibeSkua.
//!
//! Keep Ruffle's Rust types behind this crate. Only expose a stable C ABI to .NET.

use std::any::Any;
use std::collections::HashSet;
use std::fs::File;
use std::io;
use std::path::Path;
use std::rc::Rc;
use std::sync::{Arc, RwLock};
use url::Url;

use ruffle_core::backend::navigator::{OwnedFuture, SocketMode};
use ruffle_core::context::UpdateContext;
use ruffle_core::external::{ExternalInterfaceProvider, Value as ExternalValue};
use ruffle_core::loader::Error;
use ruffle_frontend_utils::backends::navigator::{
    ExternalNavigatorBackend, FutureSpawner, NavigatorInterface,
};
use ruffle_frontend_utils::content::{ContentDescriptor, PlayingContent};

/// Returns the native bridge ABI version.
#[unsafe(no_mangle)]
pub extern "C" fn skua_ruffle_native_abi_version() -> u32 {
    1
}

/// Compile-time probe for the pinned upstream Ruffle core API.
#[unsafe(no_mangle)]
pub extern "C" fn skua_ruffle_core_api_probe() -> u32 {
    let _builder = ruffle_core::PlayerBuilder::new();
    1
}

/// Schedules Ruffle's non-Send navigator futures on the player thread's Tokio LocalSet.
///
/// Every call to spawn must occur while the session's LocalSet is entered.
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

/// Host callbacks and socket permissions shared with the Ruffle navigator.
#[derive(Clone, Default)]
struct SkuaNavigatorInterface {
    allowed_sockets: Arc<RwLock<HashSet<String>>>,
}

impl SkuaNavigatorInterface {
    fn allow_socket(&self, host: &str, port: u16) {
        if let Ok(mut allowed_sockets) = self.allowed_sockets.write() {
            allowed_sockets.insert(format!("{host}:{port}"));
        }
    }
}

impl NavigatorInterface for SkuaNavigatorInterface {
    fn navigate_to_website(&self, url: Url) {
        tracing::info!("SWF requested external navigation: {url}");
    }

    async fn open_file(&self, path: &Path) -> io::Result<File> {
        File::open(path)
    }

    async fn confirm_socket(&self, host: &str, port: u16) -> bool {
        let address = format!("{host}:{port}");
        self.allowed_sockets
            .read()
            .map(|allowed_sockets| allowed_sockets.contains(&address))
            .unwrap_or(false)
    }
}

/// Receives calls from ActionScript and tracks callbacks registered by the SWF.
#[derive(Default)]
struct SkuaExternalInterfaceProvider {
    callback_names: Arc<RwLock<HashSet<String>>>,
}

impl ExternalInterfaceProvider for SkuaExternalInterfaceProvider {
    fn call_method(
        &self,
        _context: &mut UpdateContext<'_>,
        name: &str,
        _args: &[ExternalValue],
    ) -> ExternalValue {
        tracing::debug!("ActionScript called host function: {name}");
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

/// Compile-time probe for creating a full Ruffle player with the native navigator,
/// ExternalInterface provider, and offscreen WGPU renderer.
///
/// This function is intentionally not called by CI; it type-checks the integration
/// without requiring a real SWF file or an attached GPU.
#[allow(dead_code)]
fn _ruffle_full_player_api_probe(swf_bytes: &[u8], swf_path: &Path) {
    let Ok(swf_path) = std::fs::canonicalize(swf_path) else {
        return;
    };
    let Ok(swf_url) = Url::from_file_path(&swf_path) else {
        return;
    };
    let Ok(movie) = ruffle_core::tag_utils::SwfMovieData::from_data(
        swf_bytes,
        swf_url.to_string(),
        None,
        None,
    ) else {
        return;
    };
    let Some(content_descriptor) = ContentDescriptor::new_local(
        &swf_path,
        swf_path.parent().map(Path::to_path_buf),
    ) else {
        return;
    };

    let navigator_interface = SkuaNavigatorInterface::default();
    let navigator = ExternalNavigatorBackend::new(
        swf_url,
        None,
        None,
        SkuaFutureSpawner,
        None,
        false,
        HashSet::new(),
        SocketMode::Ask,
        Rc::new(PlayingContent::DirectFile(content_descriptor)),
        navigator_interface,
    );

    let Ok(renderer) = ruffle_render_wgpu::backend::WgpuRenderBackend::for_offscreen(
        (640, 480),
        ruffle_render_wgpu::wgpu::Backends::METAL,
        ruffle_render_wgpu::wgpu::PowerPreference::LowPower,
    ) else {
        return;
    };

    let Ok(runtime) = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
    else {
        return;
    };
    let local_set = tokio::task::LocalSet::new();

    let player = local_set.block_on(&runtime, async {
        ruffle_core::PlayerBuilder::new()
            .with_movie(movie)
            .with_navigator(navigator)
            .with_renderer(renderer)
            .with_viewport_dimensions(640, 480, 1.0)
            .with_external_interface(Box::new(SkuaExternalInterfaceProvider::default()))
            .build()
    });

    let mut player = player.lock().expect("Ruffle player mutex poisoned");
    player.render();

    let renderer = <dyn Any>::downcast_mut::<
        ruffle_render_wgpu::backend::WgpuRenderBackend<
            ruffle_render_wgpu::target::TextureTarget,
        >,
    >(player.renderer_mut())
    .expect("Ruffle player must use the offscreen WGPU renderer");

    let _frame = renderer.capture_frame();
}
