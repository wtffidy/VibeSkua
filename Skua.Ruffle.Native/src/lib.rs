//! Native Ruffle integration boundary for VibeSkua.
//!
//! Keep Ruffle's Rust types behind this crate. Only expose a stable C ABI to .NET.

use std::any::Any;

/// Returns the native bridge ABI version.
#[unsafe(no_mangle)]
pub extern "C" fn skua_ruffle_native_abi_version() -> u32 {
    1
}

/// Compile-time probe for the pinned upstream Ruffle core API.
///
/// This intentionally does not create a player or initialize graphics/audio.
/// It verifies that the selected Ruffle revision can be consumed by this crate.
#[unsafe(no_mangle)]
pub extern "C" fn skua_ruffle_core_api_probe() -> u32 {
    let _builder = ruffle_core::PlayerBuilder::new();
    1
}

/// Compile-time probe for creating a player with Ruffle's offscreen WGPU renderer.
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
