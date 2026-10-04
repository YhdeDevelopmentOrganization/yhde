#pragma once

#include <godot_cpp/core/class_db.hpp>

// Called by Godot when the extension is loaded/unloaded.
// All GDExtension classes are registered/unregistered here.
void initialize_yhde_module(godot::ModuleInitializationLevel p_level);
void uninitialize_yhde_module(godot::ModuleInitializationLevel p_level);
