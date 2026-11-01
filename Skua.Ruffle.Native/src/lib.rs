//! Native Ruffle integration boundary for VibeSkua.
//!
//! Keep Ruffle's Rust types behind this crate. Only expose a stable C ABI to .NET.

use std::any::Any;
use std::collections::HashSet;
use std::fs::File;
use std::io;
use std::path::Path;
use std::sync::{Arc, RwLock};
use url::Url;

use ruffle_core::backend::navigator::OwnedFuture;
use ruffle_core::loader::Error;
use ruffle_frontend_utils::backends::navigator::{
    ExternalNavigatorBackend, FutureSpawner, NavigatorInterface,
};

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

/// Compile-time probe for Ruffle's native offscreen WGPU renderer and player API.
///
/// This function is intentionally not called by the ABI probe, so CI can check
/// the API without requiring an attached GPU or initializing a graphics device.
#[allow(dead_code)]
fn _ruffle_player_api_probe(swf_bytes: &[u8]) {
    let Ok(movie) = ruffle_core::tag_utils::SwfMovieData::from_data(
        swf_bytes,
        "file:///skua.swf".to_string(),
        None,
        None,
    ) else {
        return;
    };

    let Ok(renderer) = ruffle_render_wgpu::backend::WgpuRenderBackend::for_offscreen(
        (640, 480),
        ruffle_render_wgpu::wgpu::Backends::METAL,
        ruffle_render_wgpu::wgpu::PowerPreference::LowPower,
    ) else {
        return;
    };

    let player = ruffle_core::PlayerBuilder::new()
        .with_movie(movie)
        .with_renderer(renderer)
        .with_viewport_dimensions(640, 480, 1.0)
        .build();

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

/// Compile-time probe for the built-in native HTTP and raw TCP navigator.
#[allow(dead_code)]
fn _ruffle_navigator_api_probe<F, I>(
    _navigator: &ExternalNavigatorBackend<F, I>,
)
where
    F: FutureSpawner<Error>,
    I: NavigatorInterface,
{
}
