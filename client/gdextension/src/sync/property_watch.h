#pragma once

#include <godot_cpp/classes/object.hpp>

#include <cstdint>

namespace yhde {

// Reading an object's property list is the most expensive part of a scan, and
// it rarely changes. The engine announces every change of an object's property
// list with the `property_list_changed` signal (the inspector relies on it), so
// cached lists are reused until that signal fires for the object. Some
// resources grow and shrink their list without it (TileSet sources and layers,
// TileSetAtlasSource tiles and tile data) and only emit `changed`, so for
// resources `changed` counts as a possible list change too.
void watch_property_list(godot::Object *object);
// True (once) if the object's property list changed since the last call.
bool take_property_list_change(uint64_t object_id);
void clear_property_watches();

} // namespace yhde
