#include "sync/property_watch.h"

#include <godot_cpp/classes/resource.hpp>
#include <godot_cpp/variant/callable.hpp>
#include <godot_cpp/variant/callable_method_pointer.hpp>

#include <unordered_set>

using namespace godot;

namespace yhde {

namespace {

std::unordered_set<uint64_t> &watched() {
	static std::unordered_set<uint64_t> s;
	return s;
}

std::unordered_set<uint64_t> &changed() {
	static std::unordered_set<uint64_t> s;
	return s;
}

void on_property_list_changed(uint64_t object_id) {
	changed().insert(object_id);
}

} // namespace

void watch_property_list(Object *object) {
	if (!object) return;
	uint64_t id = object->get_instance_id();
	if (!watched().insert(id).second) return;
	object->connect("property_list_changed", callable_mp_static(&on_property_list_changed).bind(id));
	if (Object::cast_to<Resource>(object)) object->connect("changed", callable_mp_static(&on_property_list_changed).bind(id));
}

bool take_property_list_change(uint64_t object_id) {
	return changed().erase(object_id) > 0;
}

void clear_property_watches() {
	// Connections die with their objects; the bookkeeping is just forgotten.
	watched().clear();
	changed().clear();
}

} // namespace yhde
