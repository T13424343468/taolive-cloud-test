; ModuleID = 'environment.armeabi-v7a.ll'
source_filename = "environment.armeabi-v7a.ll"
target datalayout = "e-m:e-p:32:32-Fi8-i64:64-v128:64:128-a:0:32-n32-S64"
target triple = "armv7-unknown-linux-android21"

%struct.ApplicationConfig = type {
	i1, ; bool uses_mono_llvm
	i1, ; bool uses_mono_aot
	i1, ; bool aot_lazy_load
	i1, ; bool uses_assembly_preload
	i1, ; bool broken_exception_transitions
	i1, ; bool jni_add_native_method_registration_attribute_present
	i1, ; bool have_runtime_config_blob
	i1, ; bool have_assemblies_blob
	i1, ; bool marshal_methods_enabled
	i1, ; bool ignore_split_configs
	i8, ; uint8_t bound_stream_io_exception_type
	i32, ; uint32_t package_naming_policy
	i32, ; uint32_t environment_variable_count
	i32, ; uint32_t system_property_count
	i32, ; uint32_t number_of_assemblies_in_apk
	i32, ; uint32_t bundled_assembly_name_width
	i32, ; uint32_t number_of_dso_cache_entries
	i32, ; uint32_t number_of_aot_cache_entries
	i32, ; uint32_t number_of_shared_libraries
	i32, ; uint32_t android_runtime_jnienv_class_token
	i32, ; uint32_t jnienv_initialize_method_token
	i32, ; uint32_t jnienv_registerjninatives_method_token
	i32, ; uint32_t jni_remapping_replacement_type_count
	i32, ; uint32_t jni_remapping_replacement_method_index_entry_count
	i32, ; uint32_t mono_components_mask
	ptr ; char* android_package_name
}

%struct.AssemblyStoreAssemblyDescriptor = type {
	i32, ; uint32_t data_offset
	i32, ; uint32_t data_size
	i32, ; uint32_t debug_data_offset
	i32, ; uint32_t debug_data_size
	i32, ; uint32_t config_data_offset
	i32 ; uint32_t config_data_size
}

%struct.AssemblyStoreRuntimeData = type {
	ptr, ; uint8_t data_start
	i32, ; uint32_t assembly_count
	i32, ; uint32_t index_entry_count
	ptr ; AssemblyStoreAssemblyDescriptor assemblies
}

%struct.AssemblyStoreSingleAssemblyRuntimeData = type {
	ptr, ; uint8_t image_data
	ptr, ; uint8_t debug_info_data
	ptr, ; uint8_t config_data
	ptr ; AssemblyStoreAssemblyDescriptor descriptor
}

%struct.DSOApkEntry = type {
	i64, ; uint64_t name_hash
	i32, ; uint32_t offset
	i32 ; int32_t fd
}

%struct.DSOCacheEntry = type {
	i64, ; uint64_t hash
	i64, ; uint64_t real_name_hash
	i1, ; bool ignore
	ptr, ; char* name
	ptr ; void* handle
}

%struct.XamarinAndroidBundledAssembly = type {
	i32, ; int32_t file_fd
	ptr, ; char* file_name
	i32, ; uint32_t data_offset
	i32, ; uint32_t data_size
	ptr, ; uint8_t data
	i32, ; uint32_t name_length
	ptr ; char* name
}

; 0x25e6972616d58
@format_tag = dso_local local_unnamed_addr constant i64 666756936985944, align 8

@mono_aot_mode_name = dso_local local_unnamed_addr constant ptr @.str.0, align 4

; Application environment variables array, name:value
@app_environment_variables = dso_local local_unnamed_addr constant [6 x ptr] [
	ptr @.env.0, ; 0
	ptr @.env.1, ; 1
	ptr @.env.2, ; 2
	ptr @.env.3, ; 3
	ptr @.env.4, ; 4
	ptr @.env.5 ; 5
], align 4

; System properties defined by the application
@app_system_properties = dso_local local_unnamed_addr constant [0 x ptr] zeroinitializer, align 4

@application_config = dso_local local_unnamed_addr constant %struct.ApplicationConfig {
	i1 false, ; bool uses_mono_llvm
	i1 true, ; bool uses_mono_aot
	i1 false, ; bool aot_lazy_load
	i1 false, ; bool uses_assembly_preload
	i1 false, ; bool broken_exception_transitions
	i1 false, ; bool jni_add_native_method_registration_attribute_present
	i1 true, ; bool have_runtime_config_blob
	i1 false, ; bool have_assemblies_blob
	i1 false, ; bool marshal_methods_enabled
	i1 false, ; bool ignore_split_configs
	i8 0, ; uint8_t bound_stream_io_exception_type
	i32 3, ; uint32_t package_naming_policy
	i32 6, ; uint32_t environment_variable_count
	i32 0, ; uint32_t system_property_count
	i32 179, ; uint32_t number_of_assemblies_in_apk
	i32 66, ; uint32_t bundled_assembly_name_width
	i32 48, ; uint32_t number_of_dso_cache_entries
	i32 0, ; uint32_t number_of_aot_cache_entries
	i32 12, ; uint32_t number_of_shared_libraries
	i32 u0x0200135e, ; uint32_t android_runtime_jnienv_class_token
	i32 u0x0601395a, ; uint32_t jnienv_initialize_method_token
	i32 u0x06013959, ; uint32_t jnienv_registerjninatives_method_token
	i32 0, ; uint32_t jni_remapping_replacement_type_count
	i32 0, ; uint32_t jni_remapping_replacement_method_index_entry_count
	i32 u0x00000003, ; uint32_t mono_components_mask
	ptr @.ApplicationConfig.0_android_package_name; char* android_package_name
}, align 4

; DSO cache entries
@dso_cache = dso_local local_unnamed_addr global [48 x %struct.DSOCacheEntry] [
	%struct.DSOCacheEntry {
		i64 u0x000000000379a161, ; from name: libc++_shared.so
		i64 u0x000000000379a161, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.0_name, ; name: libc++_shared.so
		ptr null; void* handle
	}, ; 0
	%struct.DSOCacheEntry {
		i64 u0x0000000005e00610, ; from name: libxamarin-debug-app-helper.so
		i64 u0x0000000005e00610, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.11_name, ; name: libxamarin-debug-app-helper.so
		ptr null; void* handle
	}, ; 1
	%struct.DSOCacheEntry {
		i64 u0x00000000093625cd, ; from name: libSystem.Security.Cryptography.Native.Android
		i64 u0x000000007d3da8be, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.5_name, ; name: libSystem.Security.Cryptography.Native.Android.so
		ptr null; void* handle
	}, ; 2
	%struct.DSOCacheEntry {
		i64 u0x000000000daac0a4, ; from name: monodroid.so
		i64 u0x00000000cbfba5ef, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.10_name, ; name: libmonodroid.so
		ptr null; void* handle
	}, ; 3
	%struct.DSOCacheEntry {
		i64 u0x00000000113ac2a1, ; from name: libmono-component-debugger
		i64 u0x00000000f77ff36c, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.6_name, ; name: libmono-component-debugger.so
		ptr null; void* handle
	}, ; 4
	%struct.DSOCacheEntry {
		i64 u0x00000000129d396d, ; from name: System.Globalization.Native
		i64 u0x000000001acdcfe7, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.2_name, ; name: libSystem.Globalization.Native.so
		ptr null; void* handle
	}, ; 5
	%struct.DSOCacheEntry {
		i64 u0x00000000175bc058, ; from name: mono-component-marshal-ilgen.so
		i64 u0x000000007be514c4, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.8_name, ; name: libmono-component-marshal-ilgen.so
		ptr null; void* handle
	}, ; 6
	%struct.DSOCacheEntry {
		i64 u0x000000001acdcfe7, ; from name: libSystem.Globalization.Native.so
		i64 u0x000000001acdcfe7, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.2_name, ; name: libSystem.Globalization.Native.so
		ptr null; void* handle
	}, ; 7
	%struct.DSOCacheEntry {
		i64 u0x000000001dd6b3a3, ; from name: System.Native.so
		i64 u0x0000000079d6a0ba, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.4_name, ; name: libSystem.Native.so
		ptr null; void* handle
	}, ; 8
	%struct.DSOCacheEntry {
		i64 u0x000000002a81d481, ; from name: libxamarin-debug-app-helper
		i64 u0x0000000005e00610, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.11_name, ; name: libxamarin-debug-app-helper.so
		ptr null; void* handle
	}, ; 9
	%struct.DSOCacheEntry {
		i64 u0x000000002c9b28d2, ; from name: monodroid
		i64 u0x00000000cbfba5ef, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.10_name, ; name: libmonodroid.so
		ptr null; void* handle
	}, ; 10
	%struct.DSOCacheEntry {
		i64 u0x000000002fe2bf39, ; from name: c++_shared.so
		i64 u0x000000000379a161, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.0_name, ; name: libc++_shared.so
		ptr null; void* handle
	}, ; 11
	%struct.DSOCacheEntry {
		i64 u0x0000000030366e51, ; from name: libmono-component-hot_reload.so
		i64 u0x0000000030366e51, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.7_name, ; name: libmono-component-hot_reload.so
		ptr null; void* handle
	}, ; 12
	%struct.DSOCacheEntry {
		i64 u0x0000000033e41c10, ; from name: System.Security.Cryptography.Native.Android.so
		i64 u0x000000007d3da8be, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.5_name, ; name: libSystem.Security.Cryptography.Native.Android.so
		ptr null; void* handle
	}, ; 13
	%struct.DSOCacheEntry {
		i64 u0x0000000050f66170, ; from name: mono-component-hot_reload
		i64 u0x0000000030366e51, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.7_name, ; name: libmono-component-hot_reload.so
		ptr null; void* handle
	}, ; 14
	%struct.DSOCacheEntry {
		i64 u0x000000005360f89d, ; from name: System.Security.Cryptography.Native.Android
		i64 u0x000000007d3da8be, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.5_name, ; name: libSystem.Security.Cryptography.Native.Android.so
		ptr null; void* handle
	}, ; 15
	%struct.DSOCacheEntry {
		i64 u0x00000000553b0cdb, ; from name: libc++_shared
		i64 u0x000000000379a161, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.0_name, ; name: libc++_shared.so
		ptr null; void* handle
	}, ; 16
	%struct.DSOCacheEntry {
		i64 u0x000000005825b448, ; from name: libmono-component-marshal-ilgen
		i64 u0x000000007be514c4, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.8_name, ; name: libmono-component-marshal-ilgen.so
		ptr null; void* handle
	}, ; 17
	%struct.DSOCacheEntry {
		i64 u0x000000005a5d38a3, ; from name: xamarin-debug-app-helper.so
		i64 u0x0000000005e00610, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.11_name, ; name: libxamarin-debug-app-helper.so
		ptr null; void* handle
	}, ; 18
	%struct.DSOCacheEntry {
		i64 u0x000000005b9ade60, ; from name: libSystem.Native
		i64 u0x0000000079d6a0ba, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.4_name, ; name: libSystem.Native.so
		ptr null; void* handle
	}, ; 19
	%struct.DSOCacheEntry {
		i64 u0x0000000064cca98c, ; from name: vlc.so
		i64 u0x00000000aea50b3d, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.1_name, ; name: libvlc.so
		ptr null; void* handle
	}, ; 20
	%struct.DSOCacheEntry {
		i64 u0x0000000074cebc58, ; from name: System.IO.Compression.Native
		i64 u0x00000000af29a07d, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.3_name, ; name: libSystem.IO.Compression.Native.so
		ptr null; void* handle
	}, ; 21
	%struct.DSOCacheEntry {
		i64 u0x0000000079d6a0ba, ; from name: libSystem.Native.so
		i64 u0x0000000079d6a0ba, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.4_name, ; name: libSystem.Native.so
		ptr null; void* handle
	}, ; 22
	%struct.DSOCacheEntry {
		i64 u0x000000007b8c1361, ; from name: System.IO.Compression.Native.so
		i64 u0x00000000af29a07d, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.3_name, ; name: libSystem.IO.Compression.Native.so
		ptr null; void* handle
	}, ; 23
	%struct.DSOCacheEntry {
		i64 u0x000000007be514c4, ; from name: libmono-component-marshal-ilgen.so
		i64 u0x000000007be514c4, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.8_name, ; name: libmono-component-marshal-ilgen.so
		ptr null; void* handle
	}, ; 24
	%struct.DSOCacheEntry {
		i64 u0x000000007d3da8be, ; from name: libSystem.Security.Cryptography.Native.Android.so
		i64 u0x000000007d3da8be, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.5_name, ; name: libSystem.Security.Cryptography.Native.Android.so
		ptr null; void* handle
	}, ; 25
	%struct.DSOCacheEntry {
		i64 u0x0000000084e63f2e, ; from name: xamarin-debug-app-helper
		i64 u0x0000000005e00610, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.11_name, ; name: libxamarin-debug-app-helper.so
		ptr null; void* handle
	}, ; 26
	%struct.DSOCacheEntry {
		i64 u0x0000000091ce13a0, ; from name: vlc
		i64 u0x00000000aea50b3d, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.1_name, ; name: libvlc.so
		ptr null; void* handle
	}, ; 27
	%struct.DSOCacheEntry {
		i64 u0x0000000094c7a87b, ; from name: libmonosgen-2.0
		i64 u0x00000000e391d1b5, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.9_name, ; name: libmonosgen-2.0.so
		ptr null; void* handle
	}, ; 28
	%struct.DSOCacheEntry {
		i64 u0x0000000099abd194, ; from name: System.Native
		i64 u0x0000000079d6a0ba, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.4_name, ; name: libSystem.Native.so
		ptr null; void* handle
	}, ; 29
	%struct.DSOCacheEntry {
		i64 u0x000000009d4c994f, ; from name: libvlc
		i64 u0x00000000aea50b3d, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.1_name, ; name: libvlc.so
		ptr null; void* handle
	}, ; 30
	%struct.DSOCacheEntry {
		i64 u0x000000009e770032, ; from name: monosgen-2.0.so
		i64 u0x00000000e391d1b5, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.9_name, ; name: libmonosgen-2.0.so
		ptr null; void* handle
	}, ; 31
	%struct.DSOCacheEntry {
		i64 u0x00000000a2d13151, ; from name: c++_shared
		i64 u0x000000000379a161, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.0_name, ; name: libc++_shared.so
		ptr null; void* handle
	}, ; 32
	%struct.DSOCacheEntry {
		i64 u0x00000000a66f1e5a, ; from name: libSystem.Globalization.Native
		i64 u0x000000001acdcfe7, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.2_name, ; name: libSystem.Globalization.Native.so
		ptr null; void* handle
	}, ; 33
	%struct.DSOCacheEntry {
		i64 u0x00000000aea50b3d, ; from name: libvlc.so
		i64 u0x00000000aea50b3d, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.1_name, ; name: libvlc.so
		ptr null; void* handle
	}, ; 34
	%struct.DSOCacheEntry {
		i64 u0x00000000af29a07d, ; from name: libSystem.IO.Compression.Native.so
		i64 u0x00000000af29a07d, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.3_name, ; name: libSystem.IO.Compression.Native.so
		ptr null; void* handle
	}, ; 35
	%struct.DSOCacheEntry {
		i64 u0x00000000afe3142c, ; from name: libSystem.IO.Compression.Native
		i64 u0x00000000af29a07d, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.3_name, ; name: libSystem.IO.Compression.Native.so
		ptr null; void* handle
	}, ; 36
	%struct.DSOCacheEntry {
		i64 u0x00000000bde425c4, ; from name: libmono-component-hot_reload
		i64 u0x0000000030366e51, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.7_name, ; name: libmono-component-hot_reload.so
		ptr null; void* handle
	}, ; 37
	%struct.DSOCacheEntry {
		i64 u0x00000000cbfba5ef, ; from name: libmonodroid.so
		i64 u0x00000000cbfba5ef, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.10_name, ; name: libmonodroid.so
		ptr null; void* handle
	}, ; 38
	%struct.DSOCacheEntry {
		i64 u0x00000000d8bef4d7, ; from name: libmonodroid
		i64 u0x00000000cbfba5ef, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.10_name, ; name: libmonodroid.so
		ptr null; void* handle
	}, ; 39
	%struct.DSOCacheEntry {
		i64 u0x00000000de7f8575, ; from name: mono-component-hot_reload.so
		i64 u0x0000000030366e51, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.7_name, ; name: libmono-component-hot_reload.so
		ptr null; void* handle
	}, ; 40
	%struct.DSOCacheEntry {
		i64 u0x00000000e1ed3ce0, ; from name: monosgen-2.0
		i64 u0x00000000e391d1b5, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.9_name, ; name: libmonosgen-2.0.so
		ptr null; void* handle
	}, ; 41
	%struct.DSOCacheEntry {
		i64 u0x00000000e391d1b5, ; from name: libmonosgen-2.0.so
		i64 u0x00000000e391d1b5, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.9_name, ; name: libmonosgen-2.0.so
		ptr null; void* handle
	}, ; 42
	%struct.DSOCacheEntry {
		i64 u0x00000000f39dc351, ; from name: mono-component-marshal-ilgen
		i64 u0x000000007be514c4, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.8_name, ; name: libmono-component-marshal-ilgen.so
		ptr null; void* handle
	}, ; 43
	%struct.DSOCacheEntry {
		i64 u0x00000000f4494c74, ; from name: System.Globalization.Native.so
		i64 u0x000000001acdcfe7, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.2_name, ; name: libSystem.Globalization.Native.so
		ptr null; void* handle
	}, ; 44
	%struct.DSOCacheEntry {
		i64 u0x00000000f77ff36c, ; from name: libmono-component-debugger.so
		i64 u0x00000000f77ff36c, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.6_name, ; name: libmono-component-debugger.so
		ptr null; void* handle
	}, ; 45
	%struct.DSOCacheEntry {
		i64 u0x00000000f96764cc, ; from name: mono-component-debugger.so
		i64 u0x00000000f77ff36c, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.6_name, ; name: libmono-component-debugger.so
		ptr null; void* handle
	}, ; 46
	%struct.DSOCacheEntry {
		i64 u0x00000000ff99af58, ; from name: mono-component-debugger
		i64 u0x00000000f77ff36c, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.6_name, ; name: libmono-component-debugger.so
		ptr null; void* handle
	} ; 47
], align 8

; AOT DSO cache entries
@aot_dso_cache = dso_local local_unnamed_addr global [0 x %struct.DSOCacheEntry] zeroinitializer, align 8

@dso_apk_entries = dso_local local_unnamed_addr global [12 x %struct.DSOApkEntry] zeroinitializer, align 8

@_XamarinAndroidBundledAssembly_file_name_0_0 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_0_0 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_1_1 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_1_1 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_2_2 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_2_2 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_3_3 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_3_3 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_4_4 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_4_4 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_5_5 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_5_5 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_6_6 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_6_6 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_7_7 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_7_7 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_8_8 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_8_8 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_9_9 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_9_9 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_a_a = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_a_a = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_b_b = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_b_b = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_c_c = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_c_c = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_d_d = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_d_d = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_e_e = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_e_e = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_f_f = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_f_f = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_10_10 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_10_10 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_11_11 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_11_11 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_12_12 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_12_12 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_13_13 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_13_13 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_14_14 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_14_14 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_15_15 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_15_15 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_16_16 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_16_16 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_17_17 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_17_17 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_18_18 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_18_18 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_19_19 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_19_19 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_1a_1a = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_1a_1a = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_1b_1b = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_1b_1b = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_1c_1c = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_1c_1c = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_1d_1d = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_1d_1d = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_1e_1e = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_1e_1e = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_1f_1f = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_1f_1f = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_20_20 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_20_20 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_21_21 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_21_21 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_22_22 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_22_22 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_23_23 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_23_23 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_24_24 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_24_24 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_25_25 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_25_25 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_26_26 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_26_26 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_27_27 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_27_27 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_28_28 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_28_28 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_29_29 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_29_29 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_2a_2a = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_2a_2a = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_2b_2b = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_2b_2b = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_2c_2c = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_2c_2c = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_2d_2d = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_2d_2d = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_2e_2e = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_2e_2e = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_2f_2f = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_2f_2f = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_30_30 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_30_30 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_31_31 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_31_31 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_32_32 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_32_32 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_33_33 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_33_33 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_34_34 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_34_34 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_35_35 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_35_35 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_36_36 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_36_36 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_37_37 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_37_37 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_38_38 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_38_38 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_39_39 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_39_39 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_3a_3a = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_3a_3a = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_3b_3b = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_3b_3b = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_3c_3c = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_3c_3c = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_3d_3d = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_3d_3d = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_3e_3e = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_3e_3e = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_3f_3f = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_3f_3f = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_40_40 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_40_40 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_41_41 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_41_41 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_42_42 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_42_42 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_43_43 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_43_43 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_44_44 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_44_44 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_45_45 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_45_45 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_46_46 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_46_46 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_47_47 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_47_47 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_48_48 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_48_48 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_49_49 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_49_49 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_4a_4a = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_4a_4a = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_4b_4b = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_4b_4b = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_4c_4c = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_4c_4c = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_4d_4d = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_4d_4d = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_4e_4e = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_4e_4e = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_4f_4f = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_4f_4f = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_50_50 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_50_50 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_51_51 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_51_51 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_52_52 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_52_52 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_53_53 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_53_53 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_54_54 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_54_54 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_55_55 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_55_55 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_56_56 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_56_56 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_57_57 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_57_57 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_58_58 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_58_58 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_59_59 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_59_59 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_5a_5a = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_5a_5a = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_5b_5b = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_5b_5b = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_5c_5c = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_5c_5c = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_5d_5d = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_5d_5d = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_5e_5e = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_5e_5e = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_5f_5f = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_5f_5f = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_60_60 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_60_60 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_61_61 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_61_61 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_62_62 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_62_62 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_63_63 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_63_63 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_64_64 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_64_64 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_65_65 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_65_65 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_66_66 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_66_66 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_67_67 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_67_67 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_68_68 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_68_68 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_69_69 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_69_69 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_6a_6a = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_6a_6a = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_6b_6b = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_6b_6b = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_6c_6c = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_6c_6c = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_6d_6d = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_6d_6d = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_6e_6e = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_6e_6e = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_6f_6f = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_6f_6f = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_70_70 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_70_70 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_71_71 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_71_71 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_72_72 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_72_72 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_73_73 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_73_73 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_74_74 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_74_74 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_75_75 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_75_75 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_76_76 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_76_76 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_77_77 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_77_77 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_78_78 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_78_78 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_79_79 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_79_79 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_7a_7a = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_7a_7a = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_7b_7b = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_7b_7b = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_7c_7c = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_7c_7c = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_7d_7d = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_7d_7d = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_7e_7e = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_7e_7e = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_7f_7f = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_7f_7f = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_80_80 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_80_80 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_81_81 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_81_81 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_82_82 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_82_82 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_83_83 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_83_83 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_84_84 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_84_84 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_85_85 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_85_85 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_86_86 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_86_86 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_87_87 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_87_87 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_88_88 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_88_88 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_89_89 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_89_89 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_8a_8a = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_8a_8a = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_8b_8b = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_8b_8b = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_8c_8c = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_8c_8c = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_8d_8d = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_8d_8d = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_8e_8e = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_8e_8e = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_8f_8f = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_8f_8f = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_90_90 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_90_90 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_91_91 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_91_91 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_92_92 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_92_92 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_93_93 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_93_93 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_94_94 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_94_94 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_95_95 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_95_95 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_96_96 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_96_96 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_97_97 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_97_97 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_98_98 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_98_98 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_99_99 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_99_99 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_9a_9a = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_9a_9a = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_9b_9b = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_9b_9b = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_9c_9c = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_9c_9c = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_9d_9d = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_9d_9d = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_9e_9e = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_9e_9e = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_9f_9f = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_9f_9f = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_a0_a0 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_a0_a0 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_a1_a1 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_a1_a1 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_a2_a2 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_a2_a2 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_a3_a3 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_a3_a3 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_a4_a4 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_a4_a4 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_a5_a5 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_a5_a5 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_a6_a6 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_a6_a6 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_a7_a7 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_a7_a7 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_a8_a8 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_a8_a8 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_a9_a9 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_a9_a9 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_aa_aa = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_aa_aa = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_ab_ab = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_ab_ab = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_ac_ac = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_ac_ac = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_ad_ad = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_ad_ad = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_ae_ae = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_ae_ae = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_af_af = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_af_af = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_b0_b0 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_b0_b0 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_b1_b1 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_b1_b1 = internal dso_local global [66 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_file_name_b2_b2 = internal dso_local global [74 x i8] zeroinitializer, align 1
@_XamarinAndroidBundledAssembly_name_b2_b2 = internal dso_local global [66 x i8] zeroinitializer, align 1

; Bundled assembly name buffers, all 66 bytes long
@bundled_assemblies = dso_local local_unnamed_addr global [179 x %struct.XamarinAndroidBundledAssembly] [
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_0_0, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_0_0; char* name
	}, ; 0
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_1_1, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_1_1; char* name
	}, ; 1
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_2_2, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_2_2; char* name
	}, ; 2
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_3_3, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_3_3; char* name
	}, ; 3
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_4_4, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_4_4; char* name
	}, ; 4
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_5_5, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_5_5; char* name
	}, ; 5
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_6_6, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_6_6; char* name
	}, ; 6
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_7_7, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_7_7; char* name
	}, ; 7
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_8_8, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_8_8; char* name
	}, ; 8
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_9_9, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_9_9; char* name
	}, ; 9
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_a_a, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_a_a; char* name
	}, ; 10
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_b_b, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_b_b; char* name
	}, ; 11
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_c_c, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_c_c; char* name
	}, ; 12
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_d_d, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_d_d; char* name
	}, ; 13
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_e_e, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_e_e; char* name
	}, ; 14
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_f_f, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_f_f; char* name
	}, ; 15
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_10_10, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_10_10; char* name
	}, ; 16
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_11_11, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_11_11; char* name
	}, ; 17
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_12_12, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_12_12; char* name
	}, ; 18
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_13_13, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_13_13; char* name
	}, ; 19
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_14_14, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_14_14; char* name
	}, ; 20
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_15_15, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_15_15; char* name
	}, ; 21
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_16_16, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_16_16; char* name
	}, ; 22
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_17_17, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_17_17; char* name
	}, ; 23
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_18_18, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_18_18; char* name
	}, ; 24
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_19_19, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_19_19; char* name
	}, ; 25
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_1a_1a, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_1a_1a; char* name
	}, ; 26
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_1b_1b, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_1b_1b; char* name
	}, ; 27
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_1c_1c, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_1c_1c; char* name
	}, ; 28
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_1d_1d, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_1d_1d; char* name
	}, ; 29
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_1e_1e, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_1e_1e; char* name
	}, ; 30
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_1f_1f, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_1f_1f; char* name
	}, ; 31
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_20_20, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_20_20; char* name
	}, ; 32
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_21_21, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_21_21; char* name
	}, ; 33
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_22_22, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_22_22; char* name
	}, ; 34
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_23_23, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_23_23; char* name
	}, ; 35
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_24_24, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_24_24; char* name
	}, ; 36
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_25_25, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_25_25; char* name
	}, ; 37
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_26_26, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_26_26; char* name
	}, ; 38
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_27_27, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_27_27; char* name
	}, ; 39
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_28_28, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_28_28; char* name
	}, ; 40
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_29_29, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_29_29; char* name
	}, ; 41
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_2a_2a, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_2a_2a; char* name
	}, ; 42
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_2b_2b, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_2b_2b; char* name
	}, ; 43
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_2c_2c, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_2c_2c; char* name
	}, ; 44
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_2d_2d, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_2d_2d; char* name
	}, ; 45
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_2e_2e, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_2e_2e; char* name
	}, ; 46
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_2f_2f, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_2f_2f; char* name
	}, ; 47
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_30_30, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_30_30; char* name
	}, ; 48
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_31_31, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_31_31; char* name
	}, ; 49
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_32_32, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_32_32; char* name
	}, ; 50
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_33_33, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_33_33; char* name
	}, ; 51
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_34_34, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_34_34; char* name
	}, ; 52
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_35_35, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_35_35; char* name
	}, ; 53
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_36_36, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_36_36; char* name
	}, ; 54
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_37_37, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_37_37; char* name
	}, ; 55
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_38_38, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_38_38; char* name
	}, ; 56
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_39_39, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_39_39; char* name
	}, ; 57
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_3a_3a, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_3a_3a; char* name
	}, ; 58
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_3b_3b, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_3b_3b; char* name
	}, ; 59
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_3c_3c, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_3c_3c; char* name
	}, ; 60
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_3d_3d, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_3d_3d; char* name
	}, ; 61
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_3e_3e, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_3e_3e; char* name
	}, ; 62
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_3f_3f, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_3f_3f; char* name
	}, ; 63
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_40_40, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_40_40; char* name
	}, ; 64
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_41_41, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_41_41; char* name
	}, ; 65
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_42_42, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_42_42; char* name
	}, ; 66
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_43_43, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_43_43; char* name
	}, ; 67
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_44_44, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_44_44; char* name
	}, ; 68
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_45_45, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_45_45; char* name
	}, ; 69
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_46_46, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_46_46; char* name
	}, ; 70
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_47_47, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_47_47; char* name
	}, ; 71
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_48_48, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_48_48; char* name
	}, ; 72
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_49_49, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_49_49; char* name
	}, ; 73
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_4a_4a, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_4a_4a; char* name
	}, ; 74
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_4b_4b, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_4b_4b; char* name
	}, ; 75
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_4c_4c, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_4c_4c; char* name
	}, ; 76
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_4d_4d, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_4d_4d; char* name
	}, ; 77
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_4e_4e, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_4e_4e; char* name
	}, ; 78
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_4f_4f, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_4f_4f; char* name
	}, ; 79
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_50_50, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_50_50; char* name
	}, ; 80
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_51_51, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_51_51; char* name
	}, ; 81
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_52_52, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_52_52; char* name
	}, ; 82
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_53_53, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_53_53; char* name
	}, ; 83
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_54_54, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_54_54; char* name
	}, ; 84
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_55_55, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_55_55; char* name
	}, ; 85
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_56_56, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_56_56; char* name
	}, ; 86
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_57_57, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_57_57; char* name
	}, ; 87
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_58_58, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_58_58; char* name
	}, ; 88
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_59_59, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_59_59; char* name
	}, ; 89
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_5a_5a, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_5a_5a; char* name
	}, ; 90
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_5b_5b, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_5b_5b; char* name
	}, ; 91
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_5c_5c, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_5c_5c; char* name
	}, ; 92
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_5d_5d, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_5d_5d; char* name
	}, ; 93
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_5e_5e, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_5e_5e; char* name
	}, ; 94
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_5f_5f, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_5f_5f; char* name
	}, ; 95
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_60_60, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_60_60; char* name
	}, ; 96
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_61_61, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_61_61; char* name
	}, ; 97
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_62_62, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_62_62; char* name
	}, ; 98
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_63_63, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_63_63; char* name
	}, ; 99
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_64_64, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_64_64; char* name
	}, ; 100
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_65_65, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_65_65; char* name
	}, ; 101
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_66_66, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_66_66; char* name
	}, ; 102
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_67_67, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_67_67; char* name
	}, ; 103
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_68_68, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_68_68; char* name
	}, ; 104
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_69_69, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_69_69; char* name
	}, ; 105
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_6a_6a, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_6a_6a; char* name
	}, ; 106
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_6b_6b, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_6b_6b; char* name
	}, ; 107
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_6c_6c, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_6c_6c; char* name
	}, ; 108
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_6d_6d, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_6d_6d; char* name
	}, ; 109
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_6e_6e, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_6e_6e; char* name
	}, ; 110
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_6f_6f, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_6f_6f; char* name
	}, ; 111
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_70_70, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_70_70; char* name
	}, ; 112
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_71_71, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_71_71; char* name
	}, ; 113
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_72_72, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_72_72; char* name
	}, ; 114
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_73_73, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_73_73; char* name
	}, ; 115
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_74_74, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_74_74; char* name
	}, ; 116
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_75_75, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_75_75; char* name
	}, ; 117
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_76_76, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_76_76; char* name
	}, ; 118
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_77_77, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_77_77; char* name
	}, ; 119
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_78_78, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_78_78; char* name
	}, ; 120
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_79_79, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_79_79; char* name
	}, ; 121
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_7a_7a, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_7a_7a; char* name
	}, ; 122
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_7b_7b, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_7b_7b; char* name
	}, ; 123
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_7c_7c, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_7c_7c; char* name
	}, ; 124
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_7d_7d, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_7d_7d; char* name
	}, ; 125
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_7e_7e, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_7e_7e; char* name
	}, ; 126
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_7f_7f, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_7f_7f; char* name
	}, ; 127
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_80_80, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_80_80; char* name
	}, ; 128
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_81_81, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_81_81; char* name
	}, ; 129
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_82_82, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_82_82; char* name
	}, ; 130
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_83_83, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_83_83; char* name
	}, ; 131
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_84_84, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_84_84; char* name
	}, ; 132
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_85_85, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_85_85; char* name
	}, ; 133
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_86_86, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_86_86; char* name
	}, ; 134
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_87_87, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_87_87; char* name
	}, ; 135
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_88_88, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_88_88; char* name
	}, ; 136
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_89_89, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_89_89; char* name
	}, ; 137
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_8a_8a, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_8a_8a; char* name
	}, ; 138
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_8b_8b, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_8b_8b; char* name
	}, ; 139
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_8c_8c, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_8c_8c; char* name
	}, ; 140
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_8d_8d, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_8d_8d; char* name
	}, ; 141
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_8e_8e, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_8e_8e; char* name
	}, ; 142
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_8f_8f, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_8f_8f; char* name
	}, ; 143
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_90_90, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_90_90; char* name
	}, ; 144
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_91_91, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_91_91; char* name
	}, ; 145
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_92_92, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_92_92; char* name
	}, ; 146
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_93_93, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_93_93; char* name
	}, ; 147
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_94_94, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_94_94; char* name
	}, ; 148
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_95_95, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_95_95; char* name
	}, ; 149
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_96_96, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_96_96; char* name
	}, ; 150
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_97_97, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_97_97; char* name
	}, ; 151
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_98_98, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_98_98; char* name
	}, ; 152
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_99_99, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_99_99; char* name
	}, ; 153
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_9a_9a, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_9a_9a; char* name
	}, ; 154
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_9b_9b, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_9b_9b; char* name
	}, ; 155
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_9c_9c, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_9c_9c; char* name
	}, ; 156
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_9d_9d, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_9d_9d; char* name
	}, ; 157
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_9e_9e, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_9e_9e; char* name
	}, ; 158
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_9f_9f, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_9f_9f; char* name
	}, ; 159
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_a0_a0, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_a0_a0; char* name
	}, ; 160
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_a1_a1, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_a1_a1; char* name
	}, ; 161
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_a2_a2, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_a2_a2; char* name
	}, ; 162
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_a3_a3, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_a3_a3; char* name
	}, ; 163
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_a4_a4, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_a4_a4; char* name
	}, ; 164
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_a5_a5, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_a5_a5; char* name
	}, ; 165
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_a6_a6, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_a6_a6; char* name
	}, ; 166
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_a7_a7, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_a7_a7; char* name
	}, ; 167
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_a8_a8, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_a8_a8; char* name
	}, ; 168
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_a9_a9, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_a9_a9; char* name
	}, ; 169
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_aa_aa, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_aa_aa; char* name
	}, ; 170
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_ab_ab, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_ab_ab; char* name
	}, ; 171
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_ac_ac, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_ac_ac; char* name
	}, ; 172
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_ad_ad, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_ad_ad; char* name
	}, ; 173
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_ae_ae, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_ae_ae; char* name
	}, ; 174
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_af_af, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_af_af; char* name
	}, ; 175
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_b0_b0, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_b0_b0; char* name
	}, ; 176
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_b1_b1, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_b1_b1; char* name
	}, ; 177
	%struct.XamarinAndroidBundledAssembly {
		i32 -1, ; int32_t file_fd
		ptr @_XamarinAndroidBundledAssembly_file_name_b2_b2, ; char* file_name
		i32 0, ; uint32_t data_offset
		i32 0, ; uint32_t data_size
		ptr null, ; uint8_t* data
		i32 66, ; uint32_t name_length
		ptr @_XamarinAndroidBundledAssembly_name_b2_b2; char* name
	} ; 178
], align 4

@assembly_store_bundled_assemblies = dso_local local_unnamed_addr global [0 x %struct.AssemblyStoreSingleAssemblyRuntimeData] zeroinitializer, align 4

@assembly_store = dso_local local_unnamed_addr global %struct.AssemblyStoreRuntimeData {
	ptr null, ; uint8_t* data_start
	i32 0, ; uint32_t assembly_count
	i32 0, ; uint32_t index_entry_count
	ptr null; AssemblyStoreAssemblyDescriptor* assemblies
}, align 4

; Strings
@.str.0 = private unnamed_addr constant [7 x i8] c"interp\00", align 1

; Application environment variables name:value pairs
@.env.0 = private unnamed_addr constant [15 x i8] c"MONO_GC_PARAMS\00", align 1
@.env.1 = private unnamed_addr constant [21 x i8] c"major=marksweep-conc\00", align 1
@.env.2 = private unnamed_addr constant [15 x i8] c"MONO_LOG_LEVEL\00", align 1
@.env.3 = private unnamed_addr constant [5 x i8] c"info\00", align 1
@.env.4 = private unnamed_addr constant [28 x i8] c"XA_HTTP_CLIENT_HANDLER_TYPE\00", align 1
@.env.5 = private unnamed_addr constant [42 x i8] c"Xamarin.Android.Net.AndroidMessageHandler\00", align 1

;ApplicationConfig
@.ApplicationConfig.0_android_package_name = private unnamed_addr constant [15 x i8] c"com.taolive.tv\00", align 1

;DSOCacheEntry
@.DSOCacheEntry.0_name = private unnamed_addr constant [17 x i8] c"libc++_shared.so\00", align 1
@.DSOCacheEntry.1_name = private unnamed_addr constant [10 x i8] c"libvlc.so\00", align 1
@.DSOCacheEntry.2_name = private unnamed_addr constant [34 x i8] c"libSystem.Globalization.Native.so\00", align 1
@.DSOCacheEntry.3_name = private unnamed_addr constant [35 x i8] c"libSystem.IO.Compression.Native.so\00", align 1
@.DSOCacheEntry.4_name = private unnamed_addr constant [20 x i8] c"libSystem.Native.so\00", align 1
@.DSOCacheEntry.5_name = private unnamed_addr constant [50 x i8] c"libSystem.Security.Cryptography.Native.Android.so\00", align 1
@.DSOCacheEntry.6_name = private unnamed_addr constant [30 x i8] c"libmono-component-debugger.so\00", align 1
@.DSOCacheEntry.7_name = private unnamed_addr constant [32 x i8] c"libmono-component-hot_reload.so\00", align 1
@.DSOCacheEntry.8_name = private unnamed_addr constant [35 x i8] c"libmono-component-marshal-ilgen.so\00", align 1
@.DSOCacheEntry.9_name = private unnamed_addr constant [19 x i8] c"libmonosgen-2.0.so\00", align 1
@.DSOCacheEntry.10_name = private unnamed_addr constant [16 x i8] c"libmonodroid.so\00", align 1
@.DSOCacheEntry.11_name = private unnamed_addr constant [31 x i8] c"libxamarin-debug-app-helper.so\00", align 1

; Metadata
!llvm.module.flags = !{!0, !1, !7}
!0 = !{i32 1, !"wchar_size", i32 4}
!1 = !{i32 7, !"PIC Level", i32 2}
!llvm.ident = !{!2}
!2 = !{!".NET for Android remotes/origin/release/9.0.1xx @ 1dcfb6f8779c33b6f768c996495cb90ecd729329"}
!3 = !{!4, !4, i64 0}
!4 = !{!"any pointer", !5, i64 0}
!5 = !{!"omnipotent char", !6, i64 0}
!6 = !{!"Simple C++ TBAA"}
!7 = !{i32 1, !"min_enum_size", i32 4}
