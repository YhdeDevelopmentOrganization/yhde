cmake_minimum_required(VERSION 3.22)
project(yhde_gdextension LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# godot-cpp (GDExtension bindings)
# godot-cpp v10 is versioned independently from Godot and targets a chosen
# engine API. YHDE targets Godot 4.7 (tested on 4.7.2).
#
# The native core is editor-only: it hooks the Godot editor, so we always build
# the "editor" target. To build offline, point
# FETCHCONTENT_SOURCE_DIR_GODOT_CPP at a local godot-cpp checkout.
set(GODOTCPP_TARGET "editor" CACHE STRING "godot-cpp target (YHDE is editor-only)" FORCE)
set(GODOTCPP_API_VERSION "4.7" CACHE STRING "Godot API version to target")

include(FetchContent)
FetchContent_Declare(
    godot_cpp
    GIT_REPOSITORY https://github.com/godotengine/godot-cpp.git
    GIT_TAG        10.0.0-stable
    GIT_SHALLOW    TRUE
)
FetchContent_MakeAvailable(godot_cpp)

# Extension shared library
add_library(yhde SHARED
    src/register_types.cpp
    src/core/uuid.cpp
    src/core/msgpack.cpp
    src/core/sha256.cpp
    src/assets/asset_worker.cpp
    src/assets/asset_sync.cpp
    src/text/text_operation.cpp
    src/text/text_sync.cpp
    src/networking/protocol.cpp
    src/networking/yhde_networking.cpp
    src/operation_queue/yhde_operation_queue.cpp
    src/local_cache/yhde_local_cache.cpp
    src/sync/variant_codec.cpp
    src/sync/property_watch.cpp
    src/sync/scene_document.cpp
    src/sync/sync_engine.cpp
    src/presence/yhde_presence.cpp
    src/yhde_session.cpp
)

# Test-only hooks (never enable for builds you distribute).
option(YHDE_DIAGNOSTICS "Build the YhdeDiagnostics test class" OFF)
if(YHDE_DIAGNOSTICS)
    target_sources(yhde PRIVATE src/diagnostics/yhde_diagnostics.cpp)
    target_compile_definitions(yhde PRIVATE YHDE_DIAGNOSTICS)
endif()

target_include_directories(yhde PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/src
)

find_package(Threads REQUIRED)
target_link_libraries(yhde PRIVATE godot::cpp Threads::Threads)

if(MSVC)
    target_compile_options(yhde PRIVATE /W4 /permissive-)
else()
    target_compile_options(yhde PRIVATE -Wall -Wextra -Wno-unused-parameter)
endif()

get_target_property(GODOTCPP_SUFFIX godot::cpp GODOTCPP_SUFFIX)

# The library lands directly inside the Godot addon so the editor finds it.
# Names must match the [libraries] entries in addons/yhde/yhde.gdextension.
set(YHDE_BIN_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../godot/addons/yhde/bin")

set_target_properties(yhde PROPERTIES
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON
    POSITION_INDEPENDENT_CODE ON
    PREFIX "lib"
    OUTPUT_NAME "yhde${GODOTCPP_SUFFIX}"
    LIBRARY_OUTPUT_DIRECTORY "$<1:${YHDE_BIN_DIR}>"
    RUNTIME_OUTPUT_DIRECTORY "$<1:${YHDE_BIN_DIR}>"
)

if(CMAKE_SYSTEM_NAME STREQUAL "Darwin")
    set_target_properties(yhde PROPERTIES SUFFIX ".dylib")
endif()

# Strip symbols from release builds: the core is compiled so its internals are
# not shipped as readable script (see docs/security.md: obscurity is not a
# security boundary; the server still re-validates everything).
if(NOT MSVC AND NOT CMAKE_SYSTEM_NAME STREQUAL "Darwin")
    target_link_options(yhde PRIVATE $<$<CONFIG:Release>:-s>)
endif()
