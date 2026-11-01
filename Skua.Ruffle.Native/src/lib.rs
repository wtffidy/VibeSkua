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
