#include "fennara/csharp/project.hpp"
#include "fennara/file_utils.hpp"
#include "fennara/addon_access.hpp"
#include <godot_cpp/classes/ref_counted.hpp>
#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/godot.hpp>

// Only addon access policy is stubbed (deny by default). The actual patched
// project discovery and file scanners run against Godot's native DirAccess.
namespace fennara::addon_access {
godot::String addon_root_for_path(const godot::String &path) {
    return path.begins_with("res://addons/") ? "res://addons" : "";
}
bool is_addon_root_allowed(const godot::String &) { return false; }
}
class DofEditorScanProbe : public godot::RefCounted {
    GDCLASS(DofEditorScanProbe, godot::RefCounted)
protected:
    static void _bind_methods() {
        godot::ClassDB::bind_method(godot::D_METHOD("inspect"), &DofEditorScanProbe::inspect);
    }
public:
    godot::Dictionary inspect() {
        godot::Dictionary result;
        result["csharp"] = fennara::csharp_support::inspect_project();
        result["scripts"] = fennara::file_utils::find_all_diagnostic_files();
        return result;
    }
};
static void initialize_probe(godot::ModuleInitializationLevel level) {
    if (level == godot::MODULE_INITIALIZATION_LEVEL_SCENE)
        godot::ClassDB::register_class<DofEditorScanProbe>();
}
static void terminate_probe(godot::ModuleInitializationLevel) {}
extern "C" GDExtensionBool GDE_EXPORT fennara_scan_probe_init(
    GDExtensionInterfaceGetProcAddress get_proc, GDExtensionClassLibraryPtr library,
    GDExtensionInitialization *initialization) {
    godot::GDExtensionBinding::InitObject binding(get_proc, library, initialization);
    binding.register_initializer(initialize_probe);
    binding.register_terminator(terminate_probe);
    binding.set_minimum_library_initialization_level(godot::MODULE_INITIALIZATION_LEVEL_SCENE);
    return binding.init();
}
