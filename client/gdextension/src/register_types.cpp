#include "register_types.h"

#include "yhde_session.h"
#ifdef YHDE_DIAGNOSTICS
#include "diagnostics/yhde_diagnostics.h"
#endif

#include <gdextension_interface.h>
#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/core/defs.hpp>
#include <godot_cpp/godot.hpp>

using namespace godot;

// YHDE is an editor extension: its classes hook the Godot editor, so they are
// registered at the EDITOR level and never exist in exported games.
void initialize_yhde_module(ModuleInitializationLevel p_level) {
	if (p_level != MODULE_INITIALIZATION_LEVEL_EDITOR) {
		return;
	}
	GDREGISTER_CLASS(YhdeSession);
#ifdef YHDE_DIAGNOSTICS
	GDREGISTER_CLASS(YhdeDiagnostics);
#endif
}

void uninitialize_yhde_module(ModuleInitializationLevel p_level) {
}

// GDExtension entry point: required symbol that Godot calls on load.
extern "C" {
GDExtensionBool GDE_EXPORT yhde_library_init(
		GDExtensionInterfaceGetProcAddress p_get_proc_address,
		const GDExtensionClassLibraryPtr p_library,
		GDExtensionInitialization *r_initialization) {
	GDExtensionBinding::InitObject init_obj(p_get_proc_address, p_library, r_initialization);

	init_obj.register_initializer(initialize_yhde_module);
	init_obj.register_terminator(uninitialize_yhde_module);
	init_obj.set_minimum_library_initialization_level(MODULE_INITIALIZATION_LEVEL_EDITOR);

	return init_obj.init();
}
} // extern "C"
