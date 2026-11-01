//! Native Ruffle integration boundary for VibeSkua.
//!
//! Keep Ruffle's Rust types behind this crate. Only expose a stable C ABI to .NET.

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

/// Compile-time probe for Ruffle's native offscreen WGPU renderer.
///
/// This function is intentionally not called by the ABI probe, so CI can check
/// the API without requiring an attached GPU or initializing a graphics device.
#[allow(dead_code)]
fn _ruffle_offscreen_renderer_api_probe() {
    let _renderer = ruffle_render_wgpu::backend::WgpuRenderBackend::for_offscreen(
        (1, 1),
        ruffle_render_wgpu::wgpu::Backends::METAL,
        ruffle_render_wgpu::wgpu::PowerPreference::LowPower,
    );
}
