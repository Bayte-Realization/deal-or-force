package dof_odin

import gd "godot:gdext"

// Deliberately uses the upstream C-interface layer. The experimental Godin
// preprocessor and stale Temple generator are not required for a native class.
class_name, base_name, method_name, empty_name, empty_string: rawptr
argument_names: [3]rawptr
int_from_variant: gd.TypeFromVariantConstructorProc
variant_from_int: gd.VariantFromTypeConstructorProc

create_instance :: proc "c" (data: rawptr) -> gd.ObjectPtr {
    object := gd.classdb_construct_object(&base_name)
    instance := gd.mem_alloc(1)
    gd.object_set_instance(object, &class_name, instance)
    return object
}

free_instance :: proc "c" (data: rawptr, instance: gd.ExtensionClassInstancePtr) {
    gd.mem_free(instance)
}

sum_call :: proc "c" (data: rawptr, instance: gd.ExtensionClassInstancePtr,
    args: [^]gd.VariantPtr, count: i64, ret: gd.VariantPtr, error: ^gd.CallError) {
    if count != 3 {
        error.error = .Too_Few_Arguments if count < 3 else .Too_Many_Arguments
        error.expected = 3
        gd.variant_new_nil(ret)
        return
    }
    values: [3]i64
    for i in 0..<3 { int_from_variant(&values[i], args[i]) }
    value := values[0] + 2 * values[1] + 3 * values[2]
    variant_from_int(ret, &value)
    error.error = .Ok
}

sum_ptrcall :: proc "c" (data: rawptr, instance: gd.ExtensionClassInstancePtr,
    args: [^]gd.TypePtr, ret: gd.TypePtr) {
    result_ptr := cast(^i64)ret
    result_ptr^ = (cast(^i64)args[0])^ + 2 * (cast(^i64)args[1])^ + 3 * (cast(^i64)args[2])^
}

initialize :: proc "c" (data: rawptr, level: gd.InitializationLevel) {
    if level != .Scene { return }
    gd.string_name_new_with_latin1_chars(&class_name, "DofOdinKernel", false)
    gd.string_name_new_with_latin1_chars(&base_name, "RefCounted", false)
    gd.string_name_new_with_latin1_chars(&method_name, "weighted_sum3", false)
    gd.string_name_new_with_latin1_chars(&empty_name, "", false)
    gd.string_new_with_utf8_chars(&empty_string, "")
    gd.string_name_new_with_latin1_chars(&argument_names[0], "a", false)
    gd.string_name_new_with_latin1_chars(&argument_names[1], "b", false)
    gd.string_name_new_with_latin1_chars(&argument_names[2], "c", false)
    int_from_variant = gd.get_variant_to_type_constructor(.Int)
    variant_from_int = gd.get_variant_from_type_constructor(.Int)
    info := gd.ExtensionClassCreationInfo2 {
        is_exposed = true,
        create_instance_func = create_instance,
        free_instance_func = free_instance,
    }
    gd.classdb_register_extension_class2(gd.library, &class_name, &base_name, &info)
    ret := gd.PropertyInfo {type = .Int, name = &empty_name, class_name = &empty_name, hint_string = &empty_string}
    args := [3]gd.PropertyInfo {
        {type = .Int, name = &argument_names[0], class_name = &empty_name, hint_string = &empty_string},
        {type = .Int, name = &argument_names[1], class_name = &empty_name, hint_string = &empty_string},
        {type = .Int, name = &argument_names[2], class_name = &empty_name, hint_string = &empty_string},
    }
    metadata := [3]gd.ExtensionClassMethodArgumentMetadata {.Int_Is_Int64, .Int_Is_Int64, .Int_Is_Int64}
    method := gd.ExtensionClassMethodInfo {
        name = &method_name,
        call_func = sum_call,
        ptr_call_func = sum_ptrcall,
        method_flags = 5,
        has_return_value = true,
        return_value_info = &ret,
        return_value_metadata = .Int_Is_Int64,
        argument_count = 3,
        arguments_info = &args[0],
        arguments_metadata = &metadata[0],
    }
    gd.classdb_register_extension_class_method(gd.library, &class_name, &method)
}

deinitialize :: proc "c" (data: rawptr, level: gd.InitializationLevel) {
    if level != .Scene { return }
    gd.classdb_unregister_extension_class(gd.library, &class_name)
    destroy := gd.variant_get_ptr_destructor(.String_Name)
    destroy(&method_name)
    for i in 0..<3 { destroy(&argument_names[i]) }
    destroy(&base_name)
    destroy(&class_name)
    destroy(&empty_name)
    gd.variant_get_ptr_destructor(.String)(&empty_string)
}

@(export)
dof_odin_init :: proc "c" (get_proc: gd.ExtensionInterfaceGetProcAddress,
    library: gd.ExtensionClassLibraryPtr, initialization: ^gd.Initialization) -> bool {
    gd.init(library, get_proc)
    initialization.initialize = initialize
    initialization.deinitialize = deinitialize
    initialization.minimum_initialization_level = .Scene
    return true
}
