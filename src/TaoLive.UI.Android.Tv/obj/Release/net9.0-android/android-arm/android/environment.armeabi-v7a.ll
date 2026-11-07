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
	i1 true, ; bool aot_lazy_load
	i1 false, ; bool uses_assembly_preload
	i1 false, ; bool broken_exception_transitions
	i1 false, ; bool jni_add_native_method_registration_attribute_present
	i1 true, ; bool have_runtime_config_blob
	i1 true, ; bool have_assemblies_blob
	i1 false, ; bool marshal_methods_enabled
	i1 false, ; bool ignore_split_configs
	i8 0, ; uint8_t bound_stream_io_exception_type
	i32 3, ; uint32_t package_naming_policy
	i32 6, ; uint32_t environment_variable_count
	i32 0, ; uint32_t system_property_count
	i32 34, ; uint32_t number_of_assemblies_in_apk
	i32 0, ; uint32_t bundled_assembly_name_width
	i32 36, ; uint32_t number_of_dso_cache_entries
	i32 312, ; uint32_t number_of_aot_cache_entries
	i32 43, ; uint32_t number_of_shared_libraries
	i32 u0x020000e4, ; uint32_t android_runtime_jnienv_class_token
	i32 u0x06000690, ; uint32_t jnienv_initialize_method_token
	i32 u0x0600068f, ; uint32_t jnienv_registerjninatives_method_token
	i32 0, ; uint32_t jni_remapping_replacement_type_count
	i32 0, ; uint32_t jni_remapping_replacement_method_index_entry_count
	i32 u0x00000000, ; uint32_t mono_components_mask
	ptr @.ApplicationConfig.0_android_package_name; char* android_package_name
}, align 4

; DSO cache entries
@dso_cache = dso_local local_unnamed_addr global [36 x %struct.DSOCacheEntry] [
	%struct.DSOCacheEntry {
		i64 u0x000000000379a161, ; from name: libc++_shared.so
		i64 u0x000000000379a161, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.0_name, ; name: libc++_shared.so
		ptr null; void* handle
	}, ; 0
	%struct.DSOCacheEntry {
		i64 u0x00000000093625cd, ; from name: libSystem.Security.Cryptography.Native.Android
		i64 u0x000000007d3da8be, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.5_name, ; name: libSystem.Security.Cryptography.Native.Android.so
		ptr null; void* handle
	}, ; 1
	%struct.DSOCacheEntry {
		i64 u0x000000000daac0a4, ; from name: monodroid.so
		i64 u0x00000000cbfba5ef, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.8_name, ; name: libmonodroid.so
		ptr null; void* handle
	}, ; 2
	%struct.DSOCacheEntry {
		i64 u0x00000000129d396d, ; from name: System.Globalization.Native
		i64 u0x000000001acdcfe7, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.2_name, ; name: libSystem.Globalization.Native.so
		ptr null; void* handle
	}, ; 3
	%struct.DSOCacheEntry {
		i64 u0x00000000175bc058, ; from name: mono-component-marshal-ilgen.so
		i64 u0x000000007be514c4, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.6_name, ; name: libmono-component-marshal-ilgen.so
		ptr null; void* handle
	}, ; 4
	%struct.DSOCacheEntry {
		i64 u0x000000001acdcfe7, ; from name: libSystem.Globalization.Native.so
		i64 u0x000000001acdcfe7, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.2_name, ; name: libSystem.Globalization.Native.so
		ptr null; void* handle
	}, ; 5
	%struct.DSOCacheEntry {
		i64 u0x000000001dd6b3a3, ; from name: System.Native.so
		i64 u0x0000000079d6a0ba, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.4_name, ; name: libSystem.Native.so
		ptr null; void* handle
	}, ; 6
	%struct.DSOCacheEntry {
		i64 u0x000000002c9b28d2, ; from name: monodroid
		i64 u0x00000000cbfba5ef, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.8_name, ; name: libmonodroid.so
		ptr null; void* handle
	}, ; 7
	%struct.DSOCacheEntry {
		i64 u0x000000002fe2bf39, ; from name: c++_shared.so
		i64 u0x000000000379a161, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.0_name, ; name: libc++_shared.so
		ptr null; void* handle
	}, ; 8
	%struct.DSOCacheEntry {
		i64 u0x0000000033e41c10, ; from name: System.Security.Cryptography.Native.Android.so
		i64 u0x000000007d3da8be, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.5_name, ; name: libSystem.Security.Cryptography.Native.Android.so
		ptr null; void* handle
	}, ; 9
	%struct.DSOCacheEntry {
		i64 u0x000000005360f89d, ; from name: System.Security.Cryptography.Native.Android
		i64 u0x000000007d3da8be, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.5_name, ; name: libSystem.Security.Cryptography.Native.Android.so
		ptr null; void* handle
	}, ; 10
	%struct.DSOCacheEntry {
		i64 u0x00000000553b0cdb, ; from name: libc++_shared
		i64 u0x000000000379a161, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.0_name, ; name: libc++_shared.so
		ptr null; void* handle
	}, ; 11
	%struct.DSOCacheEntry {
		i64 u0x000000005825b448, ; from name: libmono-component-marshal-ilgen
		i64 u0x000000007be514c4, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.6_name, ; name: libmono-component-marshal-ilgen.so
		ptr null; void* handle
	}, ; 12
	%struct.DSOCacheEntry {
		i64 u0x000000005b9ade60, ; from name: libSystem.Native
		i64 u0x0000000079d6a0ba, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.4_name, ; name: libSystem.Native.so
		ptr null; void* handle
	}, ; 13
	%struct.DSOCacheEntry {
		i64 u0x0000000064cca98c, ; from name: vlc.so
		i64 u0x00000000aea50b3d, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.1_name, ; name: libvlc.so
		ptr null; void* handle
	}, ; 14
	%struct.DSOCacheEntry {
		i64 u0x0000000074cebc58, ; from name: System.IO.Compression.Native
		i64 u0x00000000af29a07d, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.3_name, ; name: libSystem.IO.Compression.Native.so
		ptr null; void* handle
	}, ; 15
	%struct.DSOCacheEntry {
		i64 u0x0000000079d6a0ba, ; from name: libSystem.Native.so
		i64 u0x0000000079d6a0ba, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.4_name, ; name: libSystem.Native.so
		ptr null; void* handle
	}, ; 16
	%struct.DSOCacheEntry {
		i64 u0x000000007b8c1361, ; from name: System.IO.Compression.Native.so
		i64 u0x00000000af29a07d, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.3_name, ; name: libSystem.IO.Compression.Native.so
		ptr null; void* handle
	}, ; 17
	%struct.DSOCacheEntry {
		i64 u0x000000007be514c4, ; from name: libmono-component-marshal-ilgen.so
		i64 u0x000000007be514c4, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.6_name, ; name: libmono-component-marshal-ilgen.so
		ptr null; void* handle
	}, ; 18
	%struct.DSOCacheEntry {
		i64 u0x000000007d3da8be, ; from name: libSystem.Security.Cryptography.Native.Android.so
		i64 u0x000000007d3da8be, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.5_name, ; name: libSystem.Security.Cryptography.Native.Android.so
		ptr null; void* handle
	}, ; 19
	%struct.DSOCacheEntry {
		i64 u0x0000000091ce13a0, ; from name: vlc
		i64 u0x00000000aea50b3d, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.1_name, ; name: libvlc.so
		ptr null; void* handle
	}, ; 20
	%struct.DSOCacheEntry {
		i64 u0x0000000094c7a87b, ; from name: libmonosgen-2.0
		i64 u0x00000000e391d1b5, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.7_name, ; name: libmonosgen-2.0.so
		ptr null; void* handle
	}, ; 21
	%struct.DSOCacheEntry {
		i64 u0x0000000099abd194, ; from name: System.Native
		i64 u0x0000000079d6a0ba, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.4_name, ; name: libSystem.Native.so
		ptr null; void* handle
	}, ; 22
	%struct.DSOCacheEntry {
		i64 u0x000000009d4c994f, ; from name: libvlc
		i64 u0x00000000aea50b3d, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.1_name, ; name: libvlc.so
		ptr null; void* handle
	}, ; 23
	%struct.DSOCacheEntry {
		i64 u0x000000009e770032, ; from name: monosgen-2.0.so
		i64 u0x00000000e391d1b5, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.7_name, ; name: libmonosgen-2.0.so
		ptr null; void* handle
	}, ; 24
	%struct.DSOCacheEntry {
		i64 u0x00000000a2d13151, ; from name: c++_shared
		i64 u0x000000000379a161, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.0_name, ; name: libc++_shared.so
		ptr null; void* handle
	}, ; 25
	%struct.DSOCacheEntry {
		i64 u0x00000000a66f1e5a, ; from name: libSystem.Globalization.Native
		i64 u0x000000001acdcfe7, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.2_name, ; name: libSystem.Globalization.Native.so
		ptr null; void* handle
	}, ; 26
	%struct.DSOCacheEntry {
		i64 u0x00000000aea50b3d, ; from name: libvlc.so
		i64 u0x00000000aea50b3d, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.1_name, ; name: libvlc.so
		ptr null; void* handle
	}, ; 27
	%struct.DSOCacheEntry {
		i64 u0x00000000af29a07d, ; from name: libSystem.IO.Compression.Native.so
		i64 u0x00000000af29a07d, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.3_name, ; name: libSystem.IO.Compression.Native.so
		ptr null; void* handle
	}, ; 28
	%struct.DSOCacheEntry {
		i64 u0x00000000afe3142c, ; from name: libSystem.IO.Compression.Native
		i64 u0x00000000af29a07d, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.3_name, ; name: libSystem.IO.Compression.Native.so
		ptr null; void* handle
	}, ; 29
	%struct.DSOCacheEntry {
		i64 u0x00000000cbfba5ef, ; from name: libmonodroid.so
		i64 u0x00000000cbfba5ef, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.8_name, ; name: libmonodroid.so
		ptr null; void* handle
	}, ; 30
	%struct.DSOCacheEntry {
		i64 u0x00000000d8bef4d7, ; from name: libmonodroid
		i64 u0x00000000cbfba5ef, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.8_name, ; name: libmonodroid.so
		ptr null; void* handle
	}, ; 31
	%struct.DSOCacheEntry {
		i64 u0x00000000e1ed3ce0, ; from name: monosgen-2.0
		i64 u0x00000000e391d1b5, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.7_name, ; name: libmonosgen-2.0.so
		ptr null; void* handle
	}, ; 32
	%struct.DSOCacheEntry {
		i64 u0x00000000e391d1b5, ; from name: libmonosgen-2.0.so
		i64 u0x00000000e391d1b5, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.7_name, ; name: libmonosgen-2.0.so
		ptr null; void* handle
	}, ; 33
	%struct.DSOCacheEntry {
		i64 u0x00000000f39dc351, ; from name: mono-component-marshal-ilgen
		i64 u0x000000007be514c4, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.6_name, ; name: libmono-component-marshal-ilgen.so
		ptr null; void* handle
	}, ; 34
	%struct.DSOCacheEntry {
		i64 u0x00000000f4494c74, ; from name: System.Globalization.Native.so
		i64 u0x000000001acdcfe7, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.2_name, ; name: libSystem.Globalization.Native.so
		ptr null; void* handle
	} ; 35
], align 8

; AOT DSO cache entries
@aot_dso_cache = dso_local local_unnamed_addr global [312 x %struct.DSOCacheEntry] [
	%struct.DSOCacheEntry {
		i64 u0x0000000000728a64, ; from name: aot-TaoLive.BLL.dll.so
		i64 u0x0000000006f7b51a, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.10_name, ; name: libaot-TaoLive.BLL.dll.so
		ptr null; void* handle
	}, ; 0
	%struct.DSOCacheEntry {
		i64 u0x000000000138a661, ; from name: aot-System.Collections.NonGeneric.dll.so
		i64 u0x0000000045a298d3, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.16_name, ; name: libaot-System.Collections.NonGeneric.dll.so
		ptr null; void* handle
	}, ; 1
	%struct.DSOCacheEntry {
		i64 u0x0000000001c0fad7, ; from name: libaot-Mono.Android.Runtime
		i64 u0x000000009f9275a1, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.40_name, ; name: libaot-Mono.Android.Runtime.dll.so
		ptr null; void* handle
	}, ; 2
	%struct.DSOCacheEntry {
		i64 u0x0000000003e6dcec, ; from name: libaot-TaoLive.Model.dll.so
		i64 u0x0000000003e6dcec, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.11_name, ; name: libaot-TaoLive.Model.dll.so
		ptr null; void* handle
	}, ; 3
	%struct.DSOCacheEntry {
		i64 u0x00000000058fea29, ; from name: libaot-TaoLive.Model.so
		i64 u0x0000000003e6dcec, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.11_name, ; name: libaot-TaoLive.Model.dll.so
		ptr null; void* handle
	}, ; 4
	%struct.DSOCacheEntry {
		i64 u0x0000000006f7b51a, ; from name: libaot-TaoLive.BLL.dll.so
		i64 u0x0000000006f7b51a, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.10_name, ; name: libaot-TaoLive.BLL.dll.so
		ptr null; void* handle
	}, ; 5
	%struct.DSOCacheEntry {
		i64 u0x0000000006ffddbc, ; from name: System.Runtime.InteropServices
		i64 u0x000000008bbc0be1, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.32_name, ; name: libaot-System.Runtime.InteropServices.dll.so
		ptr null; void* handle
	}, ; 6
	%struct.DSOCacheEntry {
		i64 u0x000000000740e393, ; from name: libaot-LibVLCSharp
		i64 u0x00000000e3376983, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.13_name, ; name: libaot-LibVLCSharp.dll.so
		ptr null; void* handle
	}, ; 7
	%struct.DSOCacheEntry {
		i64 u0x000000000851c41e, ; from name: LibVLCSharp.dll.so
		i64 u0x00000000e3376983, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.13_name, ; name: libaot-LibVLCSharp.dll.so
		ptr null; void* handle
	}, ; 8
	%struct.DSOCacheEntry {
		i64 u0x0000000008ec69c8, ; from name: LibVLCSharp.so
		i64 u0x00000000e3376983, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.13_name, ; name: libaot-LibVLCSharp.dll.so
		ptr null; void* handle
	}, ; 9
	%struct.DSOCacheEntry {
		i64 u0x0000000009144460, ; from name: System.Collections.so
		i64 u0x00000000d3d7eec4, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.17_name, ; name: libaot-System.Collections.dll.so
		ptr null; void* handle
	}, ; 10
	%struct.DSOCacheEntry {
		i64 u0x000000000b580fcb, ; from name: libaot-Java.Interop.so
		i64 u0x000000007e561493, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.39_name, ; name: libaot-Java.Interop.dll.so
		ptr null; void* handle
	}, ; 11
	%struct.DSOCacheEntry {
		i64 u0x000000000c38ff48, ; from name: System.ComponentModel
		i64 u0x000000005334092f, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.20_name, ; name: libaot-System.ComponentModel.dll.so
		ptr null; void* handle
	}, ; 12
	%struct.DSOCacheEntry {
		i64 u0x000000000c4866a5, ; from name: aot-System.Net.Requests
		i64 u0x000000004506f4eb, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.29_name, ; name: libaot-System.Net.Requests.dll.so
		ptr null; void* handle
	}, ; 13
	%struct.DSOCacheEntry {
		i64 u0x000000000c772b7d, ; from name: libaot-TaoLive.Model
		i64 u0x0000000003e6dcec, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.11_name, ; name: libaot-TaoLive.Model.dll.so
		ptr null; void* handle
	}, ; 14
	%struct.DSOCacheEntry {
		i64 u0x000000000cb5a1d9, ; from name: aot-LibVLCSharp.Android.AWindowModern
		i64 u0x00000000cc2d8846, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.14_name, ; name: libaot-LibVLCSharp.Android.AWindowModern.dll.so
		ptr null; void* handle
	}, ; 15
	%struct.DSOCacheEntry {
		i64 u0x000000000d990325, ; from name: aot-LibVLCSharp.dll.so
		i64 u0x00000000e3376983, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.13_name, ; name: libaot-LibVLCSharp.dll.so
		ptr null; void* handle
	}, ; 16
	%struct.DSOCacheEntry {
		i64 u0x000000000dd2b6db, ; from name: libaot-System.Collections.Concurrent
		i64 u0x00000000681f5cdc, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.15_name, ; name: libaot-System.Collections.Concurrent.dll.so
		ptr null; void* handle
	}, ; 17
	%struct.DSOCacheEntry {
		i64 u0x000000000e836be3, ; from name: aot-System.Net.Http.so
		i64 u0x000000003b9fa69c, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.27_name, ; name: libaot-System.Net.Http.dll.so
		ptr null; void* handle
	}, ; 18
	%struct.DSOCacheEntry {
		i64 u0x000000000eb7b864, ; from name: libaot-System.Text.RegularExpressions.dll.so
		i64 u0x000000000eb7b864, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.36_name, ; name: libaot-System.Text.RegularExpressions.dll.so
		ptr null; void* handle
	}, ; 19
	%struct.DSOCacheEntry {
		i64 u0x000000001063cb23, ; from name: libaot-System.Linq.so
		i64 u0x000000003812966e, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.26_name, ; name: libaot-System.Linq.dll.so
		ptr null; void* handle
	}, ; 20
	%struct.DSOCacheEntry {
		i64 u0x00000000116fa401, ; from name: libaot-Mono.Android.dll.so
		i64 u0x00000000116fa401, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.41_name, ; name: libaot-Mono.Android.dll.so
		ptr null; void* handle
	}, ; 21
	%struct.DSOCacheEntry {
		i64 u0x000000001297d043, ; from name: System.Threading.so
		i64 u0x00000000f97d7588, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.37_name, ; name: libaot-System.Threading.dll.so
		ptr null; void* handle
	}, ; 22
	%struct.DSOCacheEntry {
		i64 u0x0000000013221fbc, ; from name: libaot-System.ComponentModel.Primitives.dll.so
		i64 u0x0000000013221fbc, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.18_name, ; name: libaot-System.ComponentModel.Primitives.dll.so
		ptr null; void* handle
	}, ; 23
	%struct.DSOCacheEntry {
		i64 u0x0000000013568aaf, ; from name: System.Runtime.so
		i64 u0x0000000087de61a5, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.34_name, ; name: libaot-System.Runtime.dll.so
		ptr null; void* handle
	}, ; 24
	%struct.DSOCacheEntry {
		i64 u0x0000000013eb1120, ; from name: aot-System.ComponentModel.Primitives
		i64 u0x0000000013221fbc, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.18_name, ; name: libaot-System.ComponentModel.Primitives.dll.so
		ptr null; void* handle
	}, ; 25
	%struct.DSOCacheEntry {
		i64 u0x00000000156cfb09, ; from name: libaot-System.Collections
		i64 u0x00000000d3d7eec4, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.17_name, ; name: libaot-System.Collections.dll.so
		ptr null; void* handle
	}, ; 26
	%struct.DSOCacheEntry {
		i64 u0x0000000016701fce, ; from name: libaot-System.Net.Http.so
		i64 u0x000000003b9fa69c, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.27_name, ; name: libaot-System.Net.Http.dll.so
		ptr null; void* handle
	}, ; 27
	%struct.DSOCacheEntry {
		i64 u0x000000001753f886, ; from name: libaot-System.Net.Requests.so
		i64 u0x000000004506f4eb, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.29_name, ; name: libaot-System.Net.Requests.dll.so
		ptr null; void* handle
	}, ; 28
	%struct.DSOCacheEntry {
		i64 u0x00000000177174fd, ; from name: aot-Java.Interop.so
		i64 u0x000000007e561493, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.39_name, ; name: libaot-Java.Interop.dll.so
		ptr null; void* handle
	}, ; 29
	%struct.DSOCacheEntry {
		i64 u0x000000001788889c, ; from name: aot-LibVLCSharp
		i64 u0x00000000e3376983, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.13_name, ; name: libaot-LibVLCSharp.dll.so
		ptr null; void* handle
	}, ; 30
	%struct.DSOCacheEntry {
		i64 u0x0000000017969339, ; from name: _Microsoft.Android.Resource.Designer
		i64 u0x00000000cee56696, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.9_name, ; name: libaot-_Microsoft.Android.Resource.Designer.dll.so
		ptr null; void* handle
	}, ; 31
	%struct.DSOCacheEntry {
		i64 u0x0000000018115aa0, ; from name: aot-TaoLive.UI.Android.Tv.dll.so
		i64 u0x00000000a81db7fd, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.12_name, ; name: libaot-TaoLive.UI.Android.Tv.dll.so
		ptr null; void* handle
	}, ; 32
	%struct.DSOCacheEntry {
		i64 u0x00000000184d471e, ; from name: aot-_Microsoft.Android.Resource.Designer.so
		i64 u0x00000000cee56696, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.9_name, ; name: libaot-_Microsoft.Android.Resource.Designer.dll.so
		ptr null; void* handle
	}, ; 33
	%struct.DSOCacheEntry {
		i64 u0x000000001a61054f, ; from name: System.Collections
		i64 u0x00000000d3d7eec4, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.17_name, ; name: libaot-System.Collections.dll.so
		ptr null; void* handle
	}, ; 34
	%struct.DSOCacheEntry {
		i64 u0x000000001aec5648, ; from name: libaot-System.IO.Compression
		i64 u0x00000000341d0942, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.25_name, ; name: libaot-System.IO.Compression.dll.so
		ptr null; void* handle
	}, ; 35
	%struct.DSOCacheEntry {
		i64 u0x000000001dbae811, ; from name: System.ObjectModel
		i64 u0x000000004f0f9830, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.30_name, ; name: libaot-System.ObjectModel.dll.so
		ptr null; void* handle
	}, ; 36
	%struct.DSOCacheEntry {
		i64 u0x000000001fd35dbe, ; from name: System.Collections.NonGeneric.so
		i64 u0x0000000045a298d3, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.16_name, ; name: libaot-System.Collections.NonGeneric.dll.so
		ptr null; void* handle
	}, ; 37
	%struct.DSOCacheEntry {
		i64 u0x0000000021034b24, ; from name: aot-System.Collections
		i64 u0x00000000d3d7eec4, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.17_name, ; name: libaot-System.Collections.dll.so
		ptr null; void* handle
	}, ; 38
	%struct.DSOCacheEntry {
		i64 u0x00000000211230ed, ; from name: aot-System.Net.Requests.dll.so
		i64 u0x000000004506f4eb, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.29_name, ; name: libaot-System.Net.Requests.dll.so
		ptr null; void* handle
	}, ; 39
	%struct.DSOCacheEntry {
		i64 u0x0000000022dd17c7, ; from name: aot-System.Runtime.Numerics
		i64 u0x00000000ad907b53, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.33_name, ; name: libaot-System.Runtime.Numerics.dll.so
		ptr null; void* handle
	}, ; 40
	%struct.DSOCacheEntry {
		i64 u0x0000000025ad2eff, ; from name: LibVLCSharp.Android.AWindowModern.dll.so
		i64 u0x00000000cc2d8846, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.14_name, ; name: libaot-LibVLCSharp.Android.AWindowModern.dll.so
		ptr null; void* handle
	}, ; 41
	%struct.DSOCacheEntry {
		i64 u0x000000002814a96c, ; from name: System.Collections.Concurrent
		i64 u0x00000000681f5cdc, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.15_name, ; name: libaot-System.Collections.Concurrent.dll.so
		ptr null; void* handle
	}, ; 42
	%struct.DSOCacheEntry {
		i64 u0x000000002849fa24, ; from name: System.Net.Requests.dll.so
		i64 u0x000000004506f4eb, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.29_name, ; name: libaot-System.Net.Requests.dll.so
		ptr null; void* handle
	}, ; 43
	%struct.DSOCacheEntry {
		i64 u0x000000002882b7c4, ; from name: aot-System.Runtime.InteropServices
		i64 u0x000000008bbc0be1, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.32_name, ; name: libaot-System.Runtime.InteropServices.dll.so
		ptr null; void* handle
	}, ; 44
	%struct.DSOCacheEntry {
		i64 u0x000000002967ccc0, ; from name: libaot-TaoLive.BLL
		i64 u0x0000000006f7b51a, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.10_name, ; name: libaot-TaoLive.BLL.dll.so
		ptr null; void* handle
	}, ; 45
	%struct.DSOCacheEntry {
		i64 u0x000000002a14f79d, ; from name: libaot-System.Private.Uri.so
		i64 u0x0000000099cbecc6, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.31_name, ; name: libaot-System.Private.Uri.dll.so
		ptr null; void* handle
	}, ; 46
	%struct.DSOCacheEntry {
		i64 u0x000000002a6d1cab, ; from name: System.Security.Cryptography.so
		i64 u0x00000000d72c936f, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.35_name, ; name: libaot-System.Security.Cryptography.dll.so
		ptr null; void* handle
	}, ; 47
	%struct.DSOCacheEntry {
		i64 u0x000000002aa19484, ; from name: libaot-System.Collections.NonGeneric.so
		i64 u0x0000000045a298d3, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.16_name, ; name: libaot-System.Collections.NonGeneric.dll.so
		ptr null; void* handle
	}, ; 48
	%struct.DSOCacheEntry {
		i64 u0x000000002b8f7d06, ; from name: libaot-LibVLCSharp.Android.AWindowModern.so
		i64 u0x00000000cc2d8846, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.14_name, ; name: libaot-LibVLCSharp.Android.AWindowModern.dll.so
		ptr null; void* handle
	}, ; 49
	%struct.DSOCacheEntry {
		i64 u0x000000002d445acd, ; from name: System.Net.Requests
		i64 u0x000000004506f4eb, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.29_name, ; name: libaot-System.Net.Requests.dll.so
		ptr null; void* handle
	}, ; 50
	%struct.DSOCacheEntry {
		i64 u0x000000002e394f87, ; from name: System.IO.Compression
		i64 u0x00000000341d0942, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.25_name, ; name: libaot-System.IO.Compression.dll.so
		ptr null; void* handle
	}, ; 51
	%struct.DSOCacheEntry {
		i64 u0x0000000030585722, ; from name: aot-_Microsoft.Android.Resource.Designer
		i64 u0x00000000cee56696, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.9_name, ; name: libaot-_Microsoft.Android.Resource.Designer.dll.so
		ptr null; void* handle
	}, ; 52
	%struct.DSOCacheEntry {
		i64 u0x00000000317d5b75, ; from name: System.IO.Compression.Brotli
		i64 u0x00000000e9f3ecfe, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.24_name, ; name: libaot-System.IO.Compression.Brotli.dll.so
		ptr null; void* handle
	}, ; 53
	%struct.DSOCacheEntry {
		i64 u0x000000003206adf2, ; from name: aot-TaoLive.UI.Android.Tv
		i64 u0x00000000a81db7fd, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.12_name, ; name: libaot-TaoLive.UI.Android.Tv.dll.so
		ptr null; void* handle
	}, ; 54
	%struct.DSOCacheEntry {
		i64 u0x0000000032670d13, ; from name: aot-System.Collections.dll.so
		i64 u0x00000000d3d7eec4, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.17_name, ; name: libaot-System.Collections.dll.so
		ptr null; void* handle
	}, ; 55
	%struct.DSOCacheEntry {
		i64 u0x000000003276cbc4, ; from name: aot-System.Collections.Concurrent.dll.so
		i64 u0x00000000681f5cdc, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.15_name, ; name: libaot-System.Collections.Concurrent.dll.so
		ptr null; void* handle
	}, ; 56
	%struct.DSOCacheEntry {
		i64 u0x00000000341d0942, ; from name: libaot-System.IO.Compression.dll.so
		i64 u0x00000000341d0942, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.25_name, ; name: libaot-System.IO.Compression.dll.so
		ptr null; void* handle
	}, ; 57
	%struct.DSOCacheEntry {
		i64 u0x000000003443b815, ; from name: libaot-System.Net.Primitives
		i64 u0x000000009dc5271e, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.28_name, ; name: libaot-System.Net.Primitives.dll.so
		ptr null; void* handle
	}, ; 58
	%struct.DSOCacheEntry {
		i64 u0x0000000034e97f3e, ; from name: aot-System.Linq.so
		i64 u0x000000003812966e, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.26_name, ; name: libaot-System.Linq.dll.so
		ptr null; void* handle
	}, ; 59
	%struct.DSOCacheEntry {
		i64 u0x0000000035a1cb38, ; from name: libaot-Mono.Android.so
		i64 u0x00000000116fa401, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.41_name, ; name: libaot-Mono.Android.dll.so
		ptr null; void* handle
	}, ; 60
	%struct.DSOCacheEntry {
		i64 u0x00000000361aa096, ; from name: libaot-System.Security.Cryptography
		i64 u0x00000000d72c936f, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.35_name, ; name: libaot-System.Security.Cryptography.dll.so
		ptr null; void* handle
	}, ; 61
	%struct.DSOCacheEntry {
		i64 u0x000000003812966e, ; from name: libaot-System.Linq.dll.so
		i64 u0x000000003812966e, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.26_name, ; name: libaot-System.Linq.dll.so
		ptr null; void* handle
	}, ; 62
	%struct.DSOCacheEntry {
		i64 u0x00000000389ead47, ; from name: System.Diagnostics.DiagnosticSource.so
		i64 u0x0000000091f7d332, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.22_name, ; name: libaot-System.Diagnostics.DiagnosticSource.dll.so
		ptr null; void* handle
	}, ; 63
	%struct.DSOCacheEntry {
		i64 u0x00000000389fb17f, ; from name: VLCSharp.dll.so
		i64 u0x00000000e3376983, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.13_name, ; name: libaot-LibVLCSharp.dll.so
		ptr null; void* handle
	}, ; 64
	%struct.DSOCacheEntry {
		i64 u0x0000000038d90609, ; from name: TaoLive.UI.Android.Tv.so
		i64 u0x00000000a81db7fd, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.12_name, ; name: libaot-TaoLive.UI.Android.Tv.dll.so
		ptr null; void* handle
	}, ; 65
	%struct.DSOCacheEntry {
		i64 u0x00000000397fbd36, ; from name: aot-System.Collections.Concurrent.so
		i64 u0x00000000681f5cdc, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.15_name, ; name: libaot-System.Collections.Concurrent.dll.so
		ptr null; void* handle
	}, ; 66
	%struct.DSOCacheEntry {
		i64 u0x00000000398d7157, ; from name: libaot-System
		i64 u0x00000000d4110d87, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.38_name, ; name: libaot-System.dll.so
		ptr null; void* handle
	}, ; 67
	%struct.DSOCacheEntry {
		i64 u0x000000003b11d4f0, ; from name: aot-System.ComponentModel.TypeConverter
		i64 u0x0000000081847c35, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.19_name, ; name: libaot-System.ComponentModel.TypeConverter.dll.so
		ptr null; void* handle
	}, ; 68
	%struct.DSOCacheEntry {
		i64 u0x000000003b78f103, ; from name: aot-System.so
		i64 u0x00000000d4110d87, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.38_name, ; name: libaot-System.dll.so
		ptr null; void* handle
	}, ; 69
	%struct.DSOCacheEntry {
		i64 u0x000000003b9fa69c, ; from name: libaot-System.Net.Http.dll.so
		i64 u0x000000003b9fa69c, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.27_name, ; name: libaot-System.Net.Http.dll.so
		ptr null; void* handle
	}, ; 70
	%struct.DSOCacheEntry {
		i64 u0x000000003c60436f, ; from name: aot-System.Diagnostics.DiagnosticSource.so
		i64 u0x0000000091f7d332, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.22_name, ; name: libaot-System.Diagnostics.DiagnosticSource.dll.so
		ptr null; void* handle
	}, ; 71
	%struct.DSOCacheEntry {
		i64 u0x000000003cff22c6, ; from name: aot-TaoLive.BLL.so
		i64 u0x0000000006f7b51a, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.10_name, ; name: libaot-TaoLive.BLL.dll.so
		ptr null; void* handle
	}, ; 72
	%struct.DSOCacheEntry {
		i64 u0x000000003da046a1, ; from name: System.ObjectModel.so
		i64 u0x000000004f0f9830, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.30_name, ; name: libaot-System.ObjectModel.dll.so
		ptr null; void* handle
	}, ; 73
	%struct.DSOCacheEntry {
		i64 u0x00000000408960ce, ; from name: libaot-System.Net.Primitives.so
		i64 u0x000000009dc5271e, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.28_name, ; name: libaot-System.Net.Primitives.dll.so
		ptr null; void* handle
	}, ; 74
	%struct.DSOCacheEntry {
		i64 u0x00000000408b17f4, ; from name: System.ComponentModel.TypeConverter
		i64 u0x0000000081847c35, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.19_name, ; name: libaot-System.ComponentModel.TypeConverter.dll.so
		ptr null; void* handle
	}, ; 75
	%struct.DSOCacheEntry {
		i64 u0x0000000040fb1487, ; from name: libaot-System.Runtime.so
		i64 u0x0000000087de61a5, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.34_name, ; name: libaot-System.Runtime.dll.so
		ptr null; void* handle
	}, ; 76
	%struct.DSOCacheEntry {
		i64 u0x0000000041761b2c, ; from name: System
		i64 u0x00000000d4110d87, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.38_name, ; name: libaot-System.dll.so
		ptr null; void* handle
	}, ; 77
	%struct.DSOCacheEntry {
		i64 u0x0000000041ffd8b9, ; from name: libaot-System.Formats.Asn1
		i64 u0x00000000c3325126, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.23_name, ; name: libaot-System.Formats.Asn1.dll.so
		ptr null; void* handle
	}, ; 78
	%struct.DSOCacheEntry {
		i64 u0x00000000443a17dc, ; from name: aot-System.Diagnostics.DiagnosticSource
		i64 u0x0000000091f7d332, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.22_name, ; name: libaot-System.Diagnostics.DiagnosticSource.dll.so
		ptr null; void* handle
	}, ; 79
	%struct.DSOCacheEntry {
		i64 u0x000000004506f4eb, ; from name: libaot-System.Net.Requests.dll.so
		i64 u0x000000004506f4eb, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.29_name, ; name: libaot-System.Net.Requests.dll.so
		ptr null; void* handle
	}, ; 80
	%struct.DSOCacheEntry {
		i64 u0x00000000458d887c, ; from name: System.Collections.Concurrent.dll.so
		i64 u0x00000000681f5cdc, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.15_name, ; name: libaot-System.Collections.Concurrent.dll.so
		ptr null; void* handle
	}, ; 81
	%struct.DSOCacheEntry {
		i64 u0x0000000045a298d3, ; from name: libaot-System.Collections.NonGeneric.dll.so
		i64 u0x0000000045a298d3, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.16_name, ; name: libaot-System.Collections.NonGeneric.dll.so
		ptr null; void* handle
	}, ; 82
	%struct.DSOCacheEntry {
		i64 u0x00000000470b60dd, ; from name: LibVLCSharp.Android.AWindowModern
		i64 u0x00000000cc2d8846, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.14_name, ; name: libaot-LibVLCSharp.Android.AWindowModern.dll.so
		ptr null; void* handle
	}, ; 83
	%struct.DSOCacheEntry {
		i64 u0x0000000048653367, ; from name: System.IO.Compression.Brotli.so
		i64 u0x00000000e9f3ecfe, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.24_name, ; name: libaot-System.IO.Compression.Brotli.dll.so
		ptr null; void* handle
	}, ; 84
	%struct.DSOCacheEntry {
		i64 u0x00000000490cd999, ; from name: libaot-System.IO.Compression.so
		i64 u0x00000000341d0942, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.25_name, ; name: libaot-System.IO.Compression.dll.so
		ptr null; void* handle
	}, ; 85
	%struct.DSOCacheEntry {
		i64 u0x000000004b9feaad, ; from name: aot-System.Diagnostics.DiagnosticSource.dll.so
		i64 u0x0000000091f7d332, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.22_name, ; name: libaot-System.Diagnostics.DiagnosticSource.dll.so
		ptr null; void* handle
	}, ; 86
	%struct.DSOCacheEntry {
		i64 u0x000000004c9ea507, ; from name: aot-System.dll.so
		i64 u0x00000000d4110d87, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.38_name, ; name: libaot-System.dll.so
		ptr null; void* handle
	}, ; 87
	%struct.DSOCacheEntry {
		i64 u0x000000004eed2679, ; from name: System.Linq
		i64 u0x000000003812966e, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.26_name, ; name: libaot-System.Linq.dll.so
		ptr null; void* handle
	}, ; 88
	%struct.DSOCacheEntry {
		i64 u0x000000004f0f9830, ; from name: libaot-System.ObjectModel.dll.so
		i64 u0x000000004f0f9830, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.30_name, ; name: libaot-System.ObjectModel.dll.so
		ptr null; void* handle
	}, ; 89
	%struct.DSOCacheEntry {
		i64 u0x000000004f4e6629, ; from name: libaot-System.ObjectModel
		i64 u0x000000004f0f9830, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.30_name, ; name: libaot-System.ObjectModel.dll.so
		ptr null; void* handle
	}, ; 90
	%struct.DSOCacheEntry {
		i64 u0x000000005207bb09, ; from name: aot-System.Private.CoreLib.dll.so
		i64 u0x00000000f5dad612, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.42_name, ; name: libaot-System.Private.CoreLib.dll.so
		ptr null; void* handle
	}, ; 91
	%struct.DSOCacheEntry {
		i64 u0x00000000526f0d98, ; from name: aot-System.Net.Http.dll.so
		i64 u0x000000003b9fa69c, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.27_name, ; name: libaot-System.Net.Http.dll.so
		ptr null; void* handle
	}, ; 92
	%struct.DSOCacheEntry {
		i64 u0x000000005272fbb2, ; from name: aot-System.ObjectModel
		i64 u0x000000004f0f9830, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.30_name, ; name: libaot-System.ObjectModel.dll.so
		ptr null; void* handle
	}, ; 93
	%struct.DSOCacheEntry {
		i64 u0x00000000529f390c, ; from name: libaot-System.Threading
		i64 u0x00000000f97d7588, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.37_name, ; name: libaot-System.Threading.dll.so
		ptr null; void* handle
	}, ; 94
	%struct.DSOCacheEntry {
		i64 u0x000000005334092f, ; from name: libaot-System.ComponentModel.dll.so
		i64 u0x000000005334092f, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.20_name, ; name: libaot-System.ComponentModel.dll.so
		ptr null; void* handle
	}, ; 95
	%struct.DSOCacheEntry {
		i64 u0x000000005516346a, ; from name: VLCSharp.so
		i64 u0x00000000e3376983, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.13_name, ; name: libaot-LibVLCSharp.dll.so
		ptr null; void* handle
	}, ; 96
	%struct.DSOCacheEntry {
		i64 u0x0000000056db2462, ; from name: aot-System.Collections.NonGeneric
		i64 u0x0000000045a298d3, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.16_name, ; name: libaot-System.Collections.NonGeneric.dll.so
		ptr null; void* handle
	}, ; 97
	%struct.DSOCacheEntry {
		i64 u0x00000000581ec19b, ; from name: libaot-_Microsoft.Android.Resource.Designer
		i64 u0x00000000cee56696, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.9_name, ; name: libaot-_Microsoft.Android.Resource.Designer.dll.so
		ptr null; void* handle
	}, ; 98
	%struct.DSOCacheEntry {
		i64 u0x0000000058724f0e, ; from name: System.Runtime.Numerics.dll.so
		i64 u0x00000000ad907b53, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.33_name, ; name: libaot-System.Runtime.Numerics.dll.so
		ptr null; void* handle
	}, ; 99
	%struct.DSOCacheEntry {
		i64 u0x0000000059a1a56a, ; from name: aot-System.Runtime
		i64 u0x0000000087de61a5, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.34_name, ; name: libaot-System.Runtime.dll.so
		ptr null; void* handle
	}, ; 100
	%struct.DSOCacheEntry {
		i64 u0x000000005a7e2e0e, ; from name: libaot-System.Collections.NonGeneric
		i64 u0x0000000045a298d3, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.16_name, ; name: libaot-System.Collections.NonGeneric.dll.so
		ptr null; void* handle
	}, ; 101
	%struct.DSOCacheEntry {
		i64 u0x000000005ac93109, ; from name: libaot-System.Private.CoreLib.so
		i64 u0x00000000f5dad612, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.42_name, ; name: libaot-System.Private.CoreLib.dll.so
		ptr null; void* handle
	}, ; 102
	%struct.DSOCacheEntry {
		i64 u0x000000005d68193d, ; from name: libaot-Mono.Android.Runtime.so
		i64 u0x000000009f9275a1, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.40_name, ; name: libaot-Mono.Android.Runtime.dll.so
		ptr null; void* handle
	}, ; 103
	%struct.DSOCacheEntry {
		i64 u0x000000005ec1fa0d, ; from name: libaot-System.ObjectModel.so
		i64 u0x000000004f0f9830, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.30_name, ; name: libaot-System.ObjectModel.dll.so
		ptr null; void* handle
	}, ; 104
	%struct.DSOCacheEntry {
		i64 u0x000000006015f389, ; from name: aot-LibVLCSharp.Android.AWindowModern.dll.so
		i64 u0x00000000cc2d8846, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.14_name, ; name: libaot-LibVLCSharp.Android.AWindowModern.dll.so
		ptr null; void* handle
	}, ; 105
	%struct.DSOCacheEntry {
		i64 u0x000000006189430b, ; from name: Mono.Android.Runtime.dll.so
		i64 u0x000000009f9275a1, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.40_name, ; name: libaot-Mono.Android.Runtime.dll.so
		ptr null; void* handle
	}, ; 106
	%struct.DSOCacheEntry {
		i64 u0x0000000061c036ca, ; from name: System.Text.RegularExpressions
		i64 u0x000000000eb7b864, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.36_name, ; name: libaot-System.Text.RegularExpressions.dll.so
		ptr null; void* handle
	}, ; 107
	%struct.DSOCacheEntry {
		i64 u0x00000000629ff9dc, ; from name: TaoLive.Model.dll.so
		i64 u0x0000000003e6dcec, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.11_name, ; name: libaot-TaoLive.Model.dll.so
		ptr null; void* handle
	}, ; 108
	%struct.DSOCacheEntry {
		i64 u0x0000000062c6282e, ; from name: System.Runtime
		i64 u0x0000000087de61a5, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.34_name, ; name: libaot-System.Runtime.dll.so
		ptr null; void* handle
	}, ; 109
	%struct.DSOCacheEntry {
		i64 u0x0000000062e64b62, ; from name: libaot-System.Collections.Concurrent.so
		i64 u0x00000000681f5cdc, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.15_name, ; name: libaot-System.Collections.Concurrent.dll.so
		ptr null; void* handle
	}, ; 110
	%struct.DSOCacheEntry {
		i64 u0x0000000062f3b49d, ; from name: libaot-System.Formats.Asn1.so
		i64 u0x00000000c3325126, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.23_name, ; name: libaot-System.Formats.Asn1.dll.so
		ptr null; void* handle
	}, ; 111
	%struct.DSOCacheEntry {
		i64 u0x00000000641f3e5a, ; from name: System.Security.Cryptography
		i64 u0x00000000d72c936f, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.35_name, ; name: libaot-System.Security.Cryptography.dll.so
		ptr null; void* handle
	}, ; 112
	%struct.DSOCacheEntry {
		i64 u0x000000006779d44a, ; from name: libaot-System.ComponentModel.Primitives.so
		i64 u0x0000000013221fbc, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.18_name, ; name: libaot-System.ComponentModel.Primitives.dll.so
		ptr null; void* handle
	}, ; 113
	%struct.DSOCacheEntry {
		i64 u0x00000000681f5cdc, ; from name: libaot-System.Collections.Concurrent.dll.so
		i64 u0x00000000681f5cdc, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.15_name, ; name: libaot-System.Collections.Concurrent.dll.so
		ptr null; void* handle
	}, ; 114
	%struct.DSOCacheEntry {
		i64 u0x00000000685591cb, ; from name: aot-System.Runtime.so
		i64 u0x0000000087de61a5, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.34_name, ; name: libaot-System.Runtime.dll.so
		ptr null; void* handle
	}, ; 115
	%struct.DSOCacheEntry {
		i64 u0x000000006a6d71ec, ; from name: System.Net.Primitives.so
		i64 u0x000000009dc5271e, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.28_name, ; name: libaot-System.Net.Primitives.dll.so
		ptr null; void* handle
	}, ; 116
	%struct.DSOCacheEntry {
		i64 u0x000000006c698ff0, ; from name: System.ComponentModel.TypeConverter.so
		i64 u0x0000000081847c35, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.19_name, ; name: libaot-System.ComponentModel.TypeConverter.dll.so
		ptr null; void* handle
	}, ; 117
	%struct.DSOCacheEntry {
		i64 u0x000000006dbd5170, ; from name: libaot-LibVLCSharp.Android.AWindowModern
		i64 u0x00000000cc2d8846, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.14_name, ; name: libaot-LibVLCSharp.Android.AWindowModern.dll.so
		ptr null; void* handle
	}, ; 118
	%struct.DSOCacheEntry {
		i64 u0x000000006ead11ed, ; from name: System.Formats.Asn1.so
		i64 u0x00000000c3325126, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.23_name, ; name: libaot-System.Formats.Asn1.dll.so
		ptr null; void* handle
	}, ; 119
	%struct.DSOCacheEntry {
		i64 u0x00000000710541ba, ; from name: aot-System.IO.Compression.Brotli.dll.so
		i64 u0x00000000e9f3ecfe, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.24_name, ; name: libaot-System.IO.Compression.Brotli.dll.so
		ptr null; void* handle
	}, ; 120
	%struct.DSOCacheEntry {
		i64 u0x00000000714af9d2, ; from name: libaot-System.Net.Http
		i64 u0x000000003b9fa69c, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.27_name, ; name: libaot-System.Net.Http.dll.so
		ptr null; void* handle
	}, ; 121
	%struct.DSOCacheEntry {
		i64 u0x0000000071e9cafa, ; from name: aot-System.Security.Cryptography.dll.so
		i64 u0x00000000d72c936f, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.35_name, ; name: libaot-System.Security.Cryptography.dll.so
		ptr null; void* handle
	}, ; 122
	%struct.DSOCacheEntry {
		i64 u0x000000007242e646, ; from name: libaot-_Microsoft.Android.Resource.Designer.so
		i64 u0x00000000cee56696, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.9_name, ; name: libaot-_Microsoft.Android.Resource.Designer.dll.so
		ptr null; void* handle
	}, ; 123
	%struct.DSOCacheEntry {
		i64 u0x00000000724739cf, ; from name: libaot-System.ComponentModel.TypeConverter
		i64 u0x0000000081847c35, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.19_name, ; name: libaot-System.ComponentModel.TypeConverter.dll.so
		ptr null; void* handle
	}, ; 124
	%struct.DSOCacheEntry {
		i64 u0x0000000072e88b63, ; from name: aot-System.IO.Compression.Brotli.so
		i64 u0x00000000e9f3ecfe, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.24_name, ; name: libaot-System.IO.Compression.Brotli.dll.so
		ptr null; void* handle
	}, ; 125
	%struct.DSOCacheEntry {
		i64 u0x0000000073963daa, ; from name: aot-System.Console
		i64 u0x0000000096dcc630, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.21_name, ; name: libaot-System.Console.dll.so
		ptr null; void* handle
	}, ; 126
	%struct.DSOCacheEntry {
		i64 u0x000000007426b8e5, ; from name: TaoLive.UI.Android.Tv.dll.so
		i64 u0x00000000a81db7fd, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.12_name, ; name: libaot-TaoLive.UI.Android.Tv.dll.so
		ptr null; void* handle
	}, ; 127
	%struct.DSOCacheEntry {
		i64 u0x00000000747626d0, ; from name: aot-System.Private.Uri.dll.so
		i64 u0x0000000099cbecc6, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.31_name, ; name: libaot-System.Private.Uri.dll.so
		ptr null; void* handle
	}, ; 128
	%struct.DSOCacheEntry {
		i64 u0x000000007483ee00, ; from name: libaot-System.Console
		i64 u0x0000000096dcc630, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.21_name, ; name: libaot-System.Console.dll.so
		ptr null; void* handle
	}, ; 129
	%struct.DSOCacheEntry {
		i64 u0x0000000074eac139, ; from name: aot-System.ComponentModel
		i64 u0x000000005334092f, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.20_name, ; name: libaot-System.ComponentModel.dll.so
		ptr null; void* handle
	}, ; 130
	%struct.DSOCacheEntry {
		i64 u0x00000000756e3671, ; from name: System.ComponentModel.Primitives.dll.so
		i64 u0x0000000013221fbc, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.18_name, ; name: libaot-System.ComponentModel.Primitives.dll.so
		ptr null; void* handle
	}, ; 131
	%struct.DSOCacheEntry {
		i64 u0x000000007587ffc6, ; from name: aot-System.Linq.dll.so
		i64 u0x000000003812966e, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.26_name, ; name: libaot-System.Linq.dll.so
		ptr null; void* handle
	}, ; 132
	%struct.DSOCacheEntry {
		i64 u0x0000000075bfa098, ; from name: libaot-Java.Interop
		i64 u0x000000007e561493, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.39_name, ; name: libaot-Java.Interop.dll.so
		ptr null; void* handle
	}, ; 133
	%struct.DSOCacheEntry {
		i64 u0x00000000768a50e3, ; from name: aot-LibVLCSharp.so
		i64 u0x00000000e3376983, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.13_name, ; name: libaot-LibVLCSharp.dll.so
		ptr null; void* handle
	}, ; 134
	%struct.DSOCacheEntry {
		i64 u0x0000000077bef378, ; from name: libaot-System.IO.Compression.Brotli.so
		i64 u0x00000000e9f3ecfe, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.24_name, ; name: libaot-System.IO.Compression.Brotli.dll.so
		ptr null; void* handle
	}, ; 135
	%struct.DSOCacheEntry {
		i64 u0x0000000077c79f26, ; from name: System.Diagnostics.DiagnosticSource.dll.so
		i64 u0x0000000091f7d332, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.22_name, ; name: libaot-System.Diagnostics.DiagnosticSource.dll.so
		ptr null; void* handle
	}, ; 136
	%struct.DSOCacheEntry {
		i64 u0x000000007816b13b, ; from name: aot-_Microsoft.Android.Resource.Designer.dll.so
		i64 u0x00000000cee56696, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.9_name, ; name: libaot-_Microsoft.Android.Resource.Designer.dll.so
		ptr null; void* handle
	}, ; 137
	%struct.DSOCacheEntry {
		i64 u0x0000000079cce98e, ; from name: aot-System.ComponentModel.dll.so
		i64 u0x000000005334092f, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.20_name, ; name: libaot-System.ComponentModel.dll.so
		ptr null; void* handle
	}, ; 138
	%struct.DSOCacheEntry {
		i64 u0x000000007d450f90, ; from name: System.ComponentModel.Primitives.so
		i64 u0x0000000013221fbc, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.18_name, ; name: libaot-System.ComponentModel.Primitives.dll.so
		ptr null; void* handle
	}, ; 139
	%struct.DSOCacheEntry {
		i64 u0x000000007e196819, ; from name: aot-System.Collections.Concurrent
		i64 u0x00000000681f5cdc, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.15_name, ; name: libaot-System.Collections.Concurrent.dll.so
		ptr null; void* handle
	}, ; 140
	%struct.DSOCacheEntry {
		i64 u0x000000007e561493, ; from name: libaot-Java.Interop.dll.so
		i64 u0x000000007e561493, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.39_name, ; name: libaot-Java.Interop.dll.so
		ptr null; void* handle
	}, ; 141
	%struct.DSOCacheEntry {
		i64 u0x000000007ec9ffe9, ; from name: System.Console
		i64 u0x0000000096dcc630, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.21_name, ; name: libaot-System.Console.dll.so
		ptr null; void* handle
	}, ; 142
	%struct.DSOCacheEntry {
		i64 u0x00000000801e2c57, ; from name: aot-System.Threading.dll.so
		i64 u0x00000000f97d7588, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.37_name, ; name: libaot-System.Threading.dll.so
		ptr null; void* handle
	}, ; 143
	%struct.DSOCacheEntry {
		i64 u0x0000000081847c35, ; from name: libaot-System.ComponentModel.TypeConverter.dll.so
		i64 u0x0000000081847c35, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.19_name, ; name: libaot-System.ComponentModel.TypeConverter.dll.so
		ptr null; void* handle
	}, ; 144
	%struct.DSOCacheEntry {
		i64 u0x00000000827b2589, ; from name: libaot-System.Security.Cryptography.so
		i64 u0x00000000d72c936f, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.35_name, ; name: libaot-System.Security.Cryptography.dll.so
		ptr null; void* handle
	}, ; 145
	%struct.DSOCacheEntry {
		i64 u0x000000008334206b, ; from name: System.Net.Http
		i64 u0x000000003b9fa69c, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.27_name, ; name: libaot-System.Net.Http.dll.so
		ptr null; void* handle
	}, ; 146
	%struct.DSOCacheEntry {
		i64 u0x000000008488661d, ; from name: libaot-System.ComponentModel.Primitives
		i64 u0x0000000013221fbc, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.18_name, ; name: libaot-System.ComponentModel.Primitives.dll.so
		ptr null; void* handle
	}, ; 147
	%struct.DSOCacheEntry {
		i64 u0x000000008527ac89, ; from name: libaot-System.Text.RegularExpressions.so
		i64 u0x000000000eb7b864, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.36_name, ; name: libaot-System.Text.RegularExpressions.dll.so
		ptr null; void* handle
	}, ; 148
	%struct.DSOCacheEntry {
		i64 u0x0000000086ea6b74, ; from name: System.Text.RegularExpressions.so
		i64 u0x000000000eb7b864, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.36_name, ; name: libaot-System.Text.RegularExpressions.dll.so
		ptr null; void* handle
	}, ; 149
	%struct.DSOCacheEntry {
		i64 u0x0000000087de61a5, ; from name: libaot-System.Runtime.dll.so
		i64 u0x0000000087de61a5, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.34_name, ; name: libaot-System.Runtime.dll.so
		ptr null; void* handle
	}, ; 150
	%struct.DSOCacheEntry {
		i64 u0x0000000088b53dd1, ; from name: aot-System.Threading.so
		i64 u0x00000000f97d7588, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.37_name, ; name: libaot-System.Threading.dll.so
		ptr null; void* handle
	}, ; 151
	%struct.DSOCacheEntry {
		i64 u0x000000008918fb3c, ; from name: aot-Java.Interop.dll.so
		i64 u0x000000007e561493, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.39_name, ; name: libaot-Java.Interop.dll.so
		ptr null; void* handle
	}, ; 152
	%struct.DSOCacheEntry {
		i64 u0x0000000089cc6fee, ; from name: LibVLCSharp
		i64 u0x00000000e3376983, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.13_name, ; name: libaot-LibVLCSharp.dll.so
		ptr null; void* handle
	}, ; 153
	%struct.DSOCacheEntry {
		i64 u0x000000008b320450, ; from name: libaot-System.Diagnostics.DiagnosticSource.so
		i64 u0x0000000091f7d332, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.22_name, ; name: libaot-System.Diagnostics.DiagnosticSource.dll.so
		ptr null; void* handle
	}, ; 154
	%struct.DSOCacheEntry {
		i64 u0x000000008bbc0be1, ; from name: libaot-System.Runtime.InteropServices.dll.so
		i64 u0x000000008bbc0be1, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.32_name, ; name: libaot-System.Runtime.InteropServices.dll.so
		ptr null; void* handle
	}, ; 155
	%struct.DSOCacheEntry {
		i64 u0x000000008c40e0db, ; from name: System.Net.Primitives
		i64 u0x000000009dc5271e, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.28_name, ; name: libaot-System.Net.Primitives.dll.so
		ptr null; void* handle
	}, ; 156
	%struct.DSOCacheEntry {
		i64 u0x000000008dd46f6f, ; from name: libaot-System.Collections.so
		i64 u0x00000000d3d7eec4, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.17_name, ; name: libaot-System.Collections.dll.so
		ptr null; void* handle
	}, ; 157
	%struct.DSOCacheEntry {
		i64 u0x000000008f71b3c1, ; from name: libaot-System.Runtime
		i64 u0x0000000087de61a5, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.34_name, ; name: libaot-System.Runtime.dll.so
		ptr null; void* handle
	}, ; 158
	%struct.DSOCacheEntry {
		i64 u0x000000008f8ce545, ; from name: System.Formats.Asn1.dll.so
		i64 u0x00000000c3325126, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.23_name, ; name: libaot-System.Formats.Asn1.dll.so
		ptr null; void* handle
	}, ; 159
	%struct.DSOCacheEntry {
		i64 u0x00000000909a39bf, ; from name: System.IO.Compression.Brotli.dll.so
		i64 u0x00000000e9f3ecfe, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.24_name, ; name: libaot-System.IO.Compression.Brotli.dll.so
		ptr null; void* handle
	}, ; 160
	%struct.DSOCacheEntry {
		i64 u0x000000009102fb0f, ; from name: aot-System.Text.RegularExpressions
		i64 u0x000000000eb7b864, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.36_name, ; name: libaot-System.Text.RegularExpressions.dll.so
		ptr null; void* handle
	}, ; 161
	%struct.DSOCacheEntry {
		i64 u0x0000000091ec9810, ; from name: System.Private.CoreLib.dll.so
		i64 u0x00000000f5dad612, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.42_name, ; name: libaot-System.Private.CoreLib.dll.so
		ptr null; void* handle
	}, ; 162
	%struct.DSOCacheEntry {
		i64 u0x0000000091f7d332, ; from name: libaot-System.Diagnostics.DiagnosticSource.dll.so
		i64 u0x0000000091f7d332, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.22_name, ; name: libaot-System.Diagnostics.DiagnosticSource.dll.so
		ptr null; void* handle
	}, ; 163
	%struct.DSOCacheEntry {
		i64 u0x000000009232e8d8, ; from name: aot-System.Linq
		i64 u0x000000003812966e, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.26_name, ; name: libaot-System.Linq.dll.so
		ptr null; void* handle
	}, ; 164
	%struct.DSOCacheEntry {
		i64 u0x00000000928ab1df, ; from name: System.Collections.dll.so
		i64 u0x00000000d3d7eec4, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.17_name, ; name: libaot-System.Collections.dll.so
		ptr null; void* handle
	}, ; 165
	%struct.DSOCacheEntry {
		i64 u0x0000000092af9d69, ; from name: System.Collections.Concurrent.so
		i64 u0x00000000681f5cdc, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.15_name, ; name: libaot-System.Collections.Concurrent.dll.so
		ptr null; void* handle
	}, ; 166
	%struct.DSOCacheEntry {
		i64 u0x000000009425ae65, ; from name: Java.Interop.so
		i64 u0x000000007e561493, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.39_name, ; name: libaot-Java.Interop.dll.so
		ptr null; void* handle
	}, ; 167
	%struct.DSOCacheEntry {
		i64 u0x000000009462c265, ; from name: TaoLive.BLL.dll.so
		i64 u0x0000000006f7b51a, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.10_name, ; name: libaot-TaoLive.BLL.dll.so
		ptr null; void* handle
	}, ; 168
	%struct.DSOCacheEntry {
		i64 u0x0000000096dcc630, ; from name: libaot-System.Console.dll.so
		i64 u0x0000000096dcc630, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.21_name, ; name: libaot-System.Console.dll.so
		ptr null; void* handle
	}, ; 169
	%struct.DSOCacheEntry {
		i64 u0x00000000976a9260, ; from name: libaot-System.so
		i64 u0x00000000d4110d87, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.38_name, ; name: libaot-System.dll.so
		ptr null; void* handle
	}, ; 170
	%struct.DSOCacheEntry {
		i64 u0x0000000098129075, ; from name: libaot-System.Console.so
		i64 u0x0000000096dcc630, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.21_name, ; name: libaot-System.Console.dll.so
		ptr null; void* handle
	}, ; 171
	%struct.DSOCacheEntry {
		i64 u0x00000000982549dc, ; from name: libaot-System.Diagnostics.DiagnosticSource
		i64 u0x0000000091f7d332, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.22_name, ; name: libaot-System.Diagnostics.DiagnosticSource.dll.so
		ptr null; void* handle
	}, ; 172
	%struct.DSOCacheEntry {
		i64 u0x0000000099bb36c9, ; from name: aot-System.Text.RegularExpressions.dll.so
		i64 u0x000000000eb7b864, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.36_name, ; name: libaot-System.Text.RegularExpressions.dll.so
		ptr null; void* handle
	}, ; 173
	%struct.DSOCacheEntry {
		i64 u0x0000000099cbecc6, ; from name: libaot-System.Private.Uri.dll.so
		i64 u0x0000000099cbecc6, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.31_name, ; name: libaot-System.Private.Uri.dll.so
		ptr null; void* handle
	}, ; 174
	%struct.DSOCacheEntry {
		i64 u0x0000000099faae71, ; from name: System.IO.Compression.dll.so
		i64 u0x00000000341d0942, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.25_name, ; name: libaot-System.IO.Compression.dll.so
		ptr null; void* handle
	}, ; 175
	%struct.DSOCacheEntry {
		i64 u0x000000009a112264, ; from name: libaot-System.Text.RegularExpressions
		i64 u0x000000000eb7b864, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.36_name, ; name: libaot-System.Text.RegularExpressions.dll.so
		ptr null; void* handle
	}, ; 176
	%struct.DSOCacheEntry {
		i64 u0x000000009a2a5e04, ; from name: aot-System.Private.Uri
		i64 u0x0000000099cbecc6, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.31_name, ; name: libaot-System.Private.Uri.dll.so
		ptr null; void* handle
	}, ; 177
	%struct.DSOCacheEntry {
		i64 u0x000000009ac35c46, ; from name: System.Runtime.Numerics.so
		i64 u0x00000000ad907b53, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.33_name, ; name: libaot-System.Runtime.Numerics.dll.so
		ptr null; void* handle
	}, ; 178
	%struct.DSOCacheEntry {
		i64 u0x000000009ac748ea, ; from name: aot-System.ComponentModel.TypeConverter.dll.so
		i64 u0x0000000081847c35, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.19_name, ; name: libaot-System.ComponentModel.TypeConverter.dll.so
		ptr null; void* handle
	}, ; 179
	%struct.DSOCacheEntry {
		i64 u0x000000009bde8c40, ; from name: System.Private.Uri.so
		i64 u0x0000000099cbecc6, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.31_name, ; name: libaot-System.Private.Uri.dll.so
		ptr null; void* handle
	}, ; 180
	%struct.DSOCacheEntry {
		i64 u0x000000009c2a4fe3, ; from name: System.Private.Uri.dll.so
		i64 u0x0000000099cbecc6, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.31_name, ; name: libaot-System.Private.Uri.dll.so
		ptr null; void* handle
	}, ; 181
	%struct.DSOCacheEntry {
		i64 u0x000000009c413e3d, ; from name: aot-System.ObjectModel.so
		i64 u0x000000004f0f9830, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.30_name, ; name: libaot-System.ObjectModel.dll.so
		ptr null; void* handle
	}, ; 182
	%struct.DSOCacheEntry {
		i64 u0x000000009d4f2592, ; from name: System.dll.so
		i64 u0x00000000d4110d87, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.38_name, ; name: libaot-System.dll.so
		ptr null; void* handle
	}, ; 183
	%struct.DSOCacheEntry {
		i64 u0x000000009dc5271e, ; from name: libaot-System.Net.Primitives.dll.so
		i64 u0x000000009dc5271e, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.28_name, ; name: libaot-System.Net.Primitives.dll.so
		ptr null; void* handle
	}, ; 184
	%struct.DSOCacheEntry {
		i64 u0x000000009dcae24b, ; from name: aot-TaoLive.BLL
		i64 u0x0000000006f7b51a, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.10_name, ; name: libaot-TaoLive.BLL.dll.so
		ptr null; void* handle
	}, ; 185
	%struct.DSOCacheEntry {
		i64 u0x000000009ebd82c2, ; from name: Java.Interop.dll.so
		i64 u0x000000007e561493, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.39_name, ; name: libaot-Java.Interop.dll.so
		ptr null; void* handle
	}, ; 186
	%struct.DSOCacheEntry {
		i64 u0x000000009ed873e2, ; from name: aot-System.Runtime.Numerics.so
		i64 u0x00000000ad907b53, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.33_name, ; name: libaot-System.Runtime.Numerics.dll.so
		ptr null; void* handle
	}, ; 187
	%struct.DSOCacheEntry {
		i64 u0x000000009ee7a9dc, ; from name: aot-TaoLive.Model
		i64 u0x0000000003e6dcec, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.11_name, ; name: libaot-TaoLive.Model.dll.so
		ptr null; void* handle
	}, ; 188
	%struct.DSOCacheEntry {
		i64 u0x000000009f9275a1, ; from name: libaot-Mono.Android.Runtime.dll.so
		i64 u0x000000009f9275a1, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.40_name, ; name: libaot-Mono.Android.Runtime.dll.so
		ptr null; void* handle
	}, ; 189
	%struct.DSOCacheEntry {
		i64 u0x00000000a13d041d, ; from name: System.Private.CoreLib.so
		i64 u0x00000000f5dad612, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.42_name, ; name: libaot-System.Private.CoreLib.dll.so
		ptr null; void* handle
	}, ; 190
	%struct.DSOCacheEntry {
		i64 u0x00000000a1830303, ; from name: aot-Mono.Android
		i64 u0x00000000116fa401, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.41_name, ; name: libaot-Mono.Android.dll.so
		ptr null; void* handle
	}, ; 191
	%struct.DSOCacheEntry {
		i64 u0x00000000a297ac88, ; from name: aot-System.Formats.Asn1.dll.so
		i64 u0x00000000c3325126, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.23_name, ; name: libaot-System.Formats.Asn1.dll.so
		ptr null; void* handle
	}, ; 192
	%struct.DSOCacheEntry {
		i64 u0x00000000a4a8694c, ; from name: System.IO.Compression.so
		i64 u0x00000000341d0942, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.25_name, ; name: libaot-System.IO.Compression.dll.so
		ptr null; void* handle
	}, ; 193
	%struct.DSOCacheEntry {
		i64 u0x00000000a5554491, ; from name: aot-TaoLive.UI.Android.Tv.so
		i64 u0x00000000a81db7fd, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.12_name, ; name: libaot-TaoLive.UI.Android.Tv.dll.so
		ptr null; void* handle
	}, ; 194
	%struct.DSOCacheEntry {
		i64 u0x00000000a671bacd, ; from name: System.Linq.so
		i64 u0x000000003812966e, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.26_name, ; name: libaot-System.Linq.dll.so
		ptr null; void* handle
	}, ; 195
	%struct.DSOCacheEntry {
		i64 u0x00000000a81db7fd, ; from name: libaot-TaoLive.UI.Android.Tv.dll.so
		i64 u0x00000000a81db7fd, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.12_name, ; name: libaot-TaoLive.UI.Android.Tv.dll.so
		ptr null; void* handle
	}, ; 196
	%struct.DSOCacheEntry {
		i64 u0x00000000a95072d7, ; from name: aot-Mono.Android.so
		i64 u0x00000000116fa401, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.41_name, ; name: libaot-Mono.Android.dll.so
		ptr null; void* handle
	}, ; 197
	%struct.DSOCacheEntry {
		i64 u0x00000000a954f7b1, ; from name: aot-System.Collections.NonGeneric.so
		i64 u0x0000000045a298d3, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.16_name, ; name: libaot-System.Collections.NonGeneric.dll.so
		ptr null; void* handle
	}, ; 198
	%struct.DSOCacheEntry {
		i64 u0x00000000a97b80ac, ; from name: aot-System.Console.dll.so
		i64 u0x0000000096dcc630, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.21_name, ; name: libaot-System.Console.dll.so
		ptr null; void* handle
	}, ; 199
	%struct.DSOCacheEntry {
		i64 u0x00000000ab47d595, ; from name: libaot-System.Runtime.Numerics.so
		i64 u0x00000000ad907b53, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.33_name, ; name: libaot-System.Runtime.Numerics.dll.so
		ptr null; void* handle
	}, ; 200
	%struct.DSOCacheEntry {
		i64 u0x00000000abcf77d1, ; from name: System.Security.Cryptography.dll.so
		i64 u0x00000000d72c936f, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.35_name, ; name: libaot-System.Security.Cryptography.dll.so
		ptr null; void* handle
	}, ; 201
	%struct.DSOCacheEntry {
		i64 u0x00000000ac4f8499, ; from name: libaot-System.ComponentModel.so
		i64 u0x000000005334092f, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.20_name, ; name: libaot-System.ComponentModel.dll.so
		ptr null; void* handle
	}, ; 202
	%struct.DSOCacheEntry {
		i64 u0x00000000ad2ab0cd, ; from name: System.Threading.dll.so
		i64 u0x00000000f97d7588, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.37_name, ; name: libaot-System.Threading.dll.so
		ptr null; void* handle
	}, ; 203
	%struct.DSOCacheEntry {
		i64 u0x00000000ad6f1e8a, ; from name: System.Private.CoreLib
		i64 u0x00000000f5dad612, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.42_name, ; name: libaot-System.Private.CoreLib.dll.so
		ptr null; void* handle
	}, ; 204
	%struct.DSOCacheEntry {
		i64 u0x00000000ad907b53, ; from name: libaot-System.Runtime.Numerics.dll.so
		i64 u0x00000000ad907b53, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.33_name, ; name: libaot-System.Runtime.Numerics.dll.so
		ptr null; void* handle
	}, ; 205
	%struct.DSOCacheEntry {
		i64 u0x00000000af8b9dff, ; from name: System.Collections.NonGeneric.dll.so
		i64 u0x0000000045a298d3, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.16_name, ; name: libaot-System.Collections.NonGeneric.dll.so
		ptr null; void* handle
	}, ; 206
	%struct.DSOCacheEntry {
		i64 u0x00000000b36218cc, ; from name: libaot-LibVLCSharp.so
		i64 u0x00000000e3376983, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.13_name, ; name: libaot-LibVLCSharp.dll.so
		ptr null; void* handle
	}, ; 207
	%struct.DSOCacheEntry {
		i64 u0x00000000b446a791, ; from name: aot-System.Runtime.InteropServices.dll.so
		i64 u0x000000008bbc0be1, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.32_name, ; name: libaot-System.Runtime.InteropServices.dll.so
		ptr null; void* handle
	}, ; 208
	%struct.DSOCacheEntry {
		i64 u0x00000000b5de0533, ; from name: System.Runtime.dll.so
		i64 u0x0000000087de61a5, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.34_name, ; name: libaot-System.Runtime.dll.so
		ptr null; void* handle
	}, ; 209
	%struct.DSOCacheEntry {
		i64 u0x00000000b62b4bfc, ; from name: aot-System.Net.Primitives.dll.so
		i64 u0x000000009dc5271e, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.28_name, ; name: libaot-System.Net.Primitives.dll.so
		ptr null; void* handle
	}, ; 210
	%struct.DSOCacheEntry {
		i64 u0x00000000b6480ca4, ; from name: System.Text.RegularExpressions.dll.so
		i64 u0x000000000eb7b864, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.36_name, ; name: libaot-System.Text.RegularExpressions.dll.so
		ptr null; void* handle
	}, ; 211
	%struct.DSOCacheEntry {
		i64 u0x00000000b65adef9, ; from name: Mono.Android.Runtime
		i64 u0x000000009f9275a1, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.40_name, ; name: libaot-Mono.Android.Runtime.dll.so
		ptr null; void* handle
	}, ; 212
	%struct.DSOCacheEntry {
		i64 u0x00000000b660be12, ; from name: System.ComponentModel.Primitives
		i64 u0x0000000013221fbc, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.18_name, ; name: libaot-System.ComponentModel.Primitives.dll.so
		ptr null; void* handle
	}, ; 213
	%struct.DSOCacheEntry {
		i64 u0x00000000b730e35b, ; from name: aot-System.Private.CoreLib
		i64 u0x00000000f5dad612, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.42_name, ; name: libaot-System.Private.CoreLib.dll.so
		ptr null; void* handle
	}, ; 214
	%struct.DSOCacheEntry {
		i64 u0x00000000b839bcce, ; from name: aot-System.Runtime.InteropServices.so
		i64 u0x000000008bbc0be1, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.32_name, ; name: libaot-System.Runtime.InteropServices.dll.so
		ptr null; void* handle
	}, ; 215
	%struct.DSOCacheEntry {
		i64 u0x00000000b8a3dab9, ; from name: System.Console.dll.so
		i64 u0x0000000096dcc630, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.21_name, ; name: libaot-System.Console.dll.so
		ptr null; void* handle
	}, ; 216
	%struct.DSOCacheEntry {
		i64 u0x00000000b8e599f0, ; from name: TaoLive.Model.so
		i64 u0x0000000003e6dcec, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.11_name, ; name: libaot-TaoLive.Model.dll.so
		ptr null; void* handle
	}, ; 217
	%struct.DSOCacheEntry {
		i64 u0x00000000b8fd311b, ; from name: System.Formats.Asn1
		i64 u0x00000000c3325126, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.23_name, ; name: libaot-System.Formats.Asn1.dll.so
		ptr null; void* handle
	}, ; 218
	%struct.DSOCacheEntry {
		i64 u0x00000000b9f21d8b, ; from name: Mono.Android.dll.so
		i64 u0x00000000116fa401, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.41_name, ; name: libaot-Mono.Android.dll.so
		ptr null; void* handle
	}, ; 219
	%struct.DSOCacheEntry {
		i64 u0x00000000ba6efa1f, ; from name: System.ObjectModel.dll.so
		i64 u0x000000004f0f9830, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.30_name, ; name: libaot-System.ObjectModel.dll.so
		ptr null; void* handle
	}, ; 220
	%struct.DSOCacheEntry {
		i64 u0x00000000ba8b5ccc, ; from name: libaot-System.Private.Uri
		i64 u0x0000000099cbecc6, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.31_name, ; name: libaot-System.Private.Uri.dll.so
		ptr null; void* handle
	}, ; 221
	%struct.DSOCacheEntry {
		i64 u0x00000000baeb2075, ; from name: VLCSharp
		i64 u0x00000000e3376983, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.13_name, ; name: libaot-LibVLCSharp.dll.so
		ptr null; void* handle
	}, ; 222
	%struct.DSOCacheEntry {
		i64 u0x00000000bb09e16b, ; from name: libaot-System.Net.Requests
		i64 u0x000000004506f4eb, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.29_name, ; name: libaot-System.Net.Requests.dll.so
		ptr null; void* handle
	}, ; 223
	%struct.DSOCacheEntry {
		i64 u0x00000000be129627, ; from name: aot-System.Runtime.Numerics.dll.so
		i64 u0x00000000ad907b53, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.33_name, ; name: libaot-System.Runtime.Numerics.dll.so
		ptr null; void* handle
	}, ; 224
	%struct.DSOCacheEntry {
		i64 u0x00000000be5fb5a0, ; from name: aot-System.Private.CoreLib.so
		i64 u0x00000000f5dad612, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.42_name, ; name: libaot-System.Private.CoreLib.dll.so
		ptr null; void* handle
	}, ; 225
	%struct.DSOCacheEntry {
		i64 u0x00000000bf824d6f, ; from name: aot-System.Net.Primitives
		i64 u0x000000009dc5271e, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.28_name, ; name: libaot-System.Net.Primitives.dll.so
		ptr null; void* handle
	}, ; 226
	%struct.DSOCacheEntry {
		i64 u0x00000000bff2e236, ; from name: System.Threading
		i64 u0x00000000f97d7588, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.37_name, ; name: libaot-System.Threading.dll.so
		ptr null; void* handle
	}, ; 227
	%struct.DSOCacheEntry {
		i64 u0x00000000c014eea3, ; from name: aot-System.Net.Primitives.so
		i64 u0x000000009dc5271e, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.28_name, ; name: libaot-System.Net.Primitives.dll.so
		ptr null; void* handle
	}, ; 228
	%struct.DSOCacheEntry {
		i64 u0x00000000c04c4f9e, ; from name: libaot-System.ComponentModel.TypeConverter.so
		i64 u0x0000000081847c35, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.19_name, ; name: libaot-System.ComponentModel.TypeConverter.dll.so
		ptr null; void* handle
	}, ; 229
	%struct.DSOCacheEntry {
		i64 u0x00000000c12d75d4, ; from name: aot-System.Threading
		i64 u0x00000000f97d7588, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.37_name, ; name: libaot-System.Threading.dll.so
		ptr null; void* handle
	}, ; 230
	%struct.DSOCacheEntry {
		i64 u0x00000000c306a37f, ; from name: System.ComponentModel.so
		i64 u0x000000005334092f, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.20_name, ; name: libaot-System.ComponentModel.dll.so
		ptr null; void* handle
	}, ; 231
	%struct.DSOCacheEntry {
		i64 u0x00000000c31879c3, ; from name: aot-System.Net.Requests.so
		i64 u0x000000004506f4eb, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.29_name, ; name: libaot-System.Net.Requests.dll.so
		ptr null; void* handle
	}, ; 232
	%struct.DSOCacheEntry {
		i64 u0x00000000c3325126, ; from name: libaot-System.Formats.Asn1.dll.so
		i64 u0x00000000c3325126, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.23_name, ; name: libaot-System.Formats.Asn1.dll.so
		ptr null; void* handle
	}, ; 233
	%struct.DSOCacheEntry {
		i64 u0x00000000c33e9cca, ; from name: System.Net.Http.dll.so
		i64 u0x000000003b9fa69c, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.27_name, ; name: libaot-System.Net.Http.dll.so
		ptr null; void* handle
	}, ; 234
	%struct.DSOCacheEntry {
		i64 u0x00000000c4e79cfe, ; from name: libaot-System.Runtime.InteropServices.so
		i64 u0x000000008bbc0be1, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.32_name, ; name: libaot-System.Runtime.InteropServices.dll.so
		ptr null; void* handle
	}, ; 235
	%struct.DSOCacheEntry {
		i64 u0x00000000c54ac704, ; from name: aot-System.Formats.Asn1.so
		i64 u0x00000000c3325126, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.23_name, ; name: libaot-System.Formats.Asn1.dll.so
		ptr null; void* handle
	}, ; 236
	%struct.DSOCacheEntry {
		i64 u0x00000000c5a35980, ; from name: aot-System.Collections.so
		i64 u0x00000000d3d7eec4, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.17_name, ; name: libaot-System.Collections.dll.so
		ptr null; void* handle
	}, ; 237
	%struct.DSOCacheEntry {
		i64 u0x00000000c5e63b90, ; from name: System.Linq.dll.so
		i64 u0x000000003812966e, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.26_name, ; name: libaot-System.Linq.dll.so
		ptr null; void* handle
	}, ; 238
	%struct.DSOCacheEntry {
		i64 u0x00000000c5ecf3d0, ; from name: aot-Mono.Android.Runtime.so
		i64 u0x000000009f9275a1, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.40_name, ; name: libaot-Mono.Android.Runtime.dll.so
		ptr null; void* handle
	}, ; 239
	%struct.DSOCacheEntry {
		i64 u0x00000000c5fb9722, ; from name: TaoLive.BLL.so
		i64 u0x0000000006f7b51a, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.10_name, ; name: libaot-TaoLive.BLL.dll.so
		ptr null; void* handle
	}, ; 240
	%struct.DSOCacheEntry {
		i64 u0x00000000c655b70e, ; from name: libaot-TaoLive.UI.Android.Tv.so
		i64 u0x00000000a81db7fd, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.12_name, ; name: libaot-TaoLive.UI.Android.Tv.dll.so
		ptr null; void* handle
	}, ; 241
	%struct.DSOCacheEntry {
		i64 u0x00000000c8a15e24, ; from name: VLCSharp.Android.AWindowModern.so
		i64 u0x00000000cc2d8846, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.14_name, ; name: libaot-LibVLCSharp.Android.AWindowModern.dll.so
		ptr null; void* handle
	}, ; 242
	%struct.DSOCacheEntry {
		i64 u0x00000000c8a662e9, ; from name: Java.Interop
		i64 u0x000000007e561493, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.39_name, ; name: libaot-Java.Interop.dll.so
		ptr null; void* handle
	}, ; 243
	%struct.DSOCacheEntry {
		i64 u0x00000000ca642251, ; from name: aot-System.Console.so
		i64 u0x0000000096dcc630, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.21_name, ; name: libaot-System.Console.dll.so
		ptr null; void* handle
	}, ; 244
	%struct.DSOCacheEntry {
		i64 u0x00000000ca72662c, ; from name: System.Net.Primitives.dll.so
		i64 u0x000000009dc5271e, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.28_name, ; name: libaot-System.Net.Primitives.dll.so
		ptr null; void* handle
	}, ; 245
	%struct.DSOCacheEntry {
		i64 u0x00000000cb055244, ; from name: VLCSharp.Android.AWindowModern.dll.so
		i64 u0x00000000cc2d8846, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.14_name, ; name: libaot-LibVLCSharp.Android.AWindowModern.dll.so
		ptr null; void* handle
	}, ; 246
	%struct.DSOCacheEntry {
		i64 u0x00000000cc2d8846, ; from name: libaot-LibVLCSharp.Android.AWindowModern.dll.so
		i64 u0x00000000cc2d8846, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.14_name, ; name: libaot-LibVLCSharp.Android.AWindowModern.dll.so
		ptr null; void* handle
	}, ; 247
	%struct.DSOCacheEntry {
		i64 u0x00000000cd600ec1, ; from name: aot-System.ComponentModel.Primitives.dll.so
		i64 u0x0000000013221fbc, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.18_name, ; name: libaot-System.ComponentModel.Primitives.dll.so
		ptr null; void* handle
	}, ; 248
	%struct.DSOCacheEntry {
		i64 u0x00000000cd878bcd, ; from name: aot-System.IO.Compression
		i64 u0x00000000341d0942, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.25_name, ; name: libaot-System.IO.Compression.dll.so
		ptr null; void* handle
	}, ; 249
	%struct.DSOCacheEntry {
		i64 u0x00000000ce0da789, ; from name: libaot-System.Threading.so
		i64 u0x00000000f97d7588, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.37_name, ; name: libaot-System.Threading.dll.so
		ptr null; void* handle
	}, ; 250
	%struct.DSOCacheEntry {
		i64 u0x00000000cee56696, ; from name: libaot-_Microsoft.Android.Resource.Designer.dll.so
		i64 u0x00000000cee56696, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.9_name, ; name: libaot-_Microsoft.Android.Resource.Designer.dll.so
		ptr null; void* handle
	}, ; 251
	%struct.DSOCacheEntry {
		i64 u0x00000000cf3163e6, ; from name: Mono.Android
		i64 u0x00000000116fa401, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.41_name, ; name: libaot-Mono.Android.dll.so
		ptr null; void* handle
	}, ; 252
	%struct.DSOCacheEntry {
		i64 u0x00000000cf82bcf7, ; from name: aot-System.Security.Cryptography.so
		i64 u0x00000000d72c936f, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.35_name, ; name: libaot-System.Security.Cryptography.dll.so
		ptr null; void* handle
	}, ; 253
	%struct.DSOCacheEntry {
		i64 u0x00000000d290822c, ; from name: aot-System.IO.Compression.so
		i64 u0x00000000341d0942, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.25_name, ; name: libaot-System.IO.Compression.dll.so
		ptr null; void* handle
	}, ; 254
	%struct.DSOCacheEntry {
		i64 u0x00000000d3634aa2, ; from name: System.so
		i64 u0x00000000d4110d87, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.38_name, ; name: libaot-System.dll.so
		ptr null; void* handle
	}, ; 255
	%struct.DSOCacheEntry {
		i64 u0x00000000d3d7eec4, ; from name: libaot-System.Collections.dll.so
		i64 u0x00000000d3d7eec4, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.17_name, ; name: libaot-System.Collections.dll.so
		ptr null; void* handle
	}, ; 256
	%struct.DSOCacheEntry {
		i64 u0x00000000d4110d87, ; from name: libaot-System.dll.so
		i64 u0x00000000d4110d87, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.38_name, ; name: libaot-System.dll.so
		ptr null; void* handle
	}, ; 257
	%struct.DSOCacheEntry {
		i64 u0x00000000d592f2e6, ; from name: Mono.Android.Runtime.so
		i64 u0x000000009f9275a1, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.40_name, ; name: libaot-Mono.Android.Runtime.dll.so
		ptr null; void* handle
	}, ; 258
	%struct.DSOCacheEntry {
		i64 u0x00000000d684ce80, ; from name: TaoLive.UI.Android.Tv
		i64 u0x00000000a81db7fd, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.12_name, ; name: libaot-TaoLive.UI.Android.Tv.dll.so
		ptr null; void* handle
	}, ; 259
	%struct.DSOCacheEntry {
		i64 u0x00000000d6b443a3, ; from name: TaoLive.BLL
		i64 u0x0000000006f7b51a, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.10_name, ; name: libaot-TaoLive.BLL.dll.so
		ptr null; void* handle
	}, ; 260
	%struct.DSOCacheEntry {
		i64 u0x00000000d72c936f, ; from name: libaot-System.Security.Cryptography.dll.so
		i64 u0x00000000d72c936f, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.35_name, ; name: libaot-System.Security.Cryptography.dll.so
		ptr null; void* handle
	}, ; 261
	%struct.DSOCacheEntry {
		i64 u0x00000000d7f58dd0, ; from name: libaot-System.Linq
		i64 u0x000000003812966e, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.26_name, ; name: libaot-System.Linq.dll.so
		ptr null; void* handle
	}, ; 262
	%struct.DSOCacheEntry {
		i64 u0x00000000d9654c6f, ; from name: Mono.Android.so
		i64 u0x00000000116fa401, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.41_name, ; name: libaot-Mono.Android.dll.so
		ptr null; void* handle
	}, ; 263
	%struct.DSOCacheEntry {
		i64 u0x00000000d969ebec, ; from name: System.Runtime.InteropServices.dll.so
		i64 u0x000000008bbc0be1, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.32_name, ; name: libaot-System.Runtime.InteropServices.dll.so
		ptr null; void* handle
	}, ; 264
	%struct.DSOCacheEntry {
		i64 u0x00000000d9719026, ; from name: libaot-System.Runtime.Numerics
		i64 u0x00000000ad907b53, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.33_name, ; name: libaot-System.Runtime.Numerics.dll.so
		ptr null; void* handle
	}, ; 265
	%struct.DSOCacheEntry {
		i64 u0x00000000dba80a9f, ; from name: libaot-System.IO.Compression.Brotli
		i64 u0x00000000e9f3ecfe, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.24_name, ; name: libaot-System.IO.Compression.Brotli.dll.so
		ptr null; void* handle
	}, ; 266
	%struct.DSOCacheEntry {
		i64 u0x00000000dd3ed183, ; from name: LibVLCSharp.Android.AWindowModern.so
		i64 u0x00000000cc2d8846, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.14_name, ; name: libaot-LibVLCSharp.Android.AWindowModern.dll.so
		ptr null; void* handle
	}, ; 267
	%struct.DSOCacheEntry {
		i64 u0x00000000ddc9eba8, ; from name: aot-System.IO.Compression.Brotli
		i64 u0x00000000e9f3ecfe, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.24_name, ; name: libaot-System.IO.Compression.Brotli.dll.so
		ptr null; void* handle
	}, ; 268
	%struct.DSOCacheEntry {
		i64 u0x00000000df20ae9c, ; from name: aot-TaoLive.Model.dll.so
		i64 u0x0000000003e6dcec, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.11_name, ; name: libaot-TaoLive.Model.dll.so
		ptr null; void* handle
	}, ; 269
	%struct.DSOCacheEntry {
		i64 u0x00000000df6f3870, ; from name: System.Diagnostics.DiagnosticSource
		i64 u0x0000000091f7d332, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.22_name, ; name: libaot-System.Diagnostics.DiagnosticSource.dll.so
		ptr null; void* handle
	}, ; 270
	%struct.DSOCacheEntry {
		i64 u0x00000000e1acf610, ; from name: VLCSharp.Android.AWindowModern
		i64 u0x00000000cc2d8846, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.14_name, ; name: libaot-LibVLCSharp.Android.AWindowModern.dll.so
		ptr null; void* handle
	}, ; 271
	%struct.DSOCacheEntry {
		i64 u0x00000000e1d2165c, ; from name: _Microsoft.Android.Resource.Designer.dll.so
		i64 u0x00000000cee56696, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.9_name, ; name: libaot-_Microsoft.Android.Resource.Designer.dll.so
		ptr null; void* handle
	}, ; 272
	%struct.DSOCacheEntry {
		i64 u0x00000000e2098b0b, ; from name: System.Collections.NonGeneric
		i64 u0x0000000045a298d3, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.16_name, ; name: libaot-System.Collections.NonGeneric.dll.so
		ptr null; void* handle
	}, ; 273
	%struct.DSOCacheEntry {
		i64 u0x00000000e3376983, ; from name: libaot-LibVLCSharp.dll.so
		i64 u0x00000000e3376983, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.13_name, ; name: libaot-LibVLCSharp.dll.so
		ptr null; void* handle
	}, ; 274
	%struct.DSOCacheEntry {
		i64 u0x00000000e3d09204, ; from name: libaot-TaoLive.UI.Android.Tv
		i64 u0x00000000a81db7fd, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.12_name, ; name: libaot-TaoLive.UI.Android.Tv.dll.so
		ptr null; void* handle
	}, ; 275
	%struct.DSOCacheEntry {
		i64 u0x00000000e3eeaf13, ; from name: aot-System.ObjectModel.dll.so
		i64 u0x000000004f0f9830, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.30_name, ; name: libaot-System.ObjectModel.dll.so
		ptr null; void* handle
	}, ; 276
	%struct.DSOCacheEntry {
		i64 u0x00000000e45c6f32, ; from name: System.ComponentModel.dll.so
		i64 u0x000000005334092f, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.20_name, ; name: libaot-System.ComponentModel.dll.so
		ptr null; void* handle
	}, ; 277
	%struct.DSOCacheEntry {
		i64 u0x00000000e62be61a, ; from name: aot-System.ComponentModel.so
		i64 u0x000000005334092f, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.20_name, ; name: libaot-System.ComponentModel.dll.so
		ptr null; void* handle
	}, ; 278
	%struct.DSOCacheEntry {
		i64 u0x00000000e6632886, ; from name: System.Net.Http.so
		i64 u0x000000003b9fa69c, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.27_name, ; name: libaot-System.Net.Http.dll.so
		ptr null; void* handle
	}, ; 279
	%struct.DSOCacheEntry {
		i64 u0x00000000e76d747b, ; from name: aot-Mono.Android.dll.so
		i64 u0x00000000116fa401, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.41_name, ; name: libaot-Mono.Android.dll.so
		ptr null; void* handle
	}, ; 280
	%struct.DSOCacheEntry {
		i64 u0x00000000e7c01049, ; from name: TaoLive.Model
		i64 u0x0000000003e6dcec, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.11_name, ; name: libaot-TaoLive.Model.dll.so
		ptr null; void* handle
	}, ; 281
	%struct.DSOCacheEntry {
		i64 u0x00000000e7f1899d, ; from name: aot-System.Net.Http
		i64 u0x000000003b9fa69c, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.27_name, ; name: libaot-System.Net.Http.dll.so
		ptr null; void* handle
	}, ; 282
	%struct.DSOCacheEntry {
		i64 u0x00000000e95e32cc, ; from name: aot-System.Security.Cryptography
		i64 u0x00000000d72c936f, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.35_name, ; name: libaot-System.Security.Cryptography.dll.so
		ptr null; void* handle
	}, ; 283
	%struct.DSOCacheEntry {
		i64 u0x00000000e9f3ecfe, ; from name: libaot-System.IO.Compression.Brotli.dll.so
		i64 u0x00000000e9f3ecfe, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.24_name, ; name: libaot-System.IO.Compression.Brotli.dll.so
		ptr null; void* handle
	}, ; 284
	%struct.DSOCacheEntry {
		i64 u0x00000000ea8868f3, ; from name: libaot-Mono.Android
		i64 u0x00000000116fa401, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.41_name, ; name: libaot-Mono.Android.dll.so
		ptr null; void* handle
	}, ; 285
	%struct.DSOCacheEntry {
		i64 u0x00000000eb082637, ; from name: System.Console.so
		i64 u0x0000000096dcc630, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.21_name, ; name: libaot-System.Console.dll.so
		ptr null; void* handle
	}, ; 286
	%struct.DSOCacheEntry {
		i64 u0x00000000eb681ff1, ; from name: aot-Mono.Android.Runtime.dll.so
		i64 u0x000000009f9275a1, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.40_name, ; name: libaot-Mono.Android.Runtime.dll.so
		ptr null; void* handle
	}, ; 287
	%struct.DSOCacheEntry {
		i64 u0x00000000ed41c4ee, ; from name: aot-System.Formats.Asn1
		i64 u0x00000000c3325126, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.23_name, ; name: libaot-System.Formats.Asn1.dll.so
		ptr null; void* handle
	}, ; 288
	%struct.DSOCacheEntry {
		i64 u0x00000000ed70fb7c, ; from name: aot-Java.Interop
		i64 u0x000000007e561493, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.39_name, ; name: libaot-Java.Interop.dll.so
		ptr null; void* handle
	}, ; 289
	%struct.DSOCacheEntry {
		i64 u0x00000000ed990d09, ; from name: System.Net.Requests.so
		i64 u0x000000004506f4eb, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.29_name, ; name: libaot-System.Net.Requests.dll.so
		ptr null; void* handle
	}, ; 290
	%struct.DSOCacheEntry {
		i64 u0x00000000ee36b04c, ; from name: aot-System.Runtime.dll.so
		i64 u0x0000000087de61a5, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.34_name, ; name: libaot-System.Runtime.dll.so
		ptr null; void* handle
	}, ; 291
	%struct.DSOCacheEntry {
		i64 u0x00000000f032a0f8, ; from name: aot-System.Private.Uri.so
		i64 u0x0000000099cbecc6, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.31_name, ; name: libaot-System.Private.Uri.dll.so
		ptr null; void* handle
	}, ; 292
	%struct.DSOCacheEntry {
		i64 u0x00000000f0f488d4, ; from name: libaot-System.ComponentModel
		i64 u0x000000005334092f, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.20_name, ; name: libaot-System.ComponentModel.dll.so
		ptr null; void* handle
	}, ; 293
	%struct.DSOCacheEntry {
		i64 u0x00000000f2336478, ; from name: aot-System.IO.Compression.dll.so
		i64 u0x00000000341d0942, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.25_name, ; name: libaot-System.IO.Compression.dll.so
		ptr null; void* handle
	}, ; 294
	%struct.DSOCacheEntry {
		i64 u0x00000000f2f8a313, ; from name: libaot-TaoLive.BLL.so
		i64 u0x0000000006f7b51a, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.10_name, ; name: libaot-TaoLive.BLL.dll.so
		ptr null; void* handle
	}, ; 295
	%struct.DSOCacheEntry {
		i64 u0x00000000f32fc333, ; from name: _Microsoft.Android.Resource.Designer.so
		i64 u0x00000000cee56696, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.9_name, ; name: libaot-_Microsoft.Android.Resource.Designer.dll.so
		ptr null; void* handle
	}, ; 296
	%struct.DSOCacheEntry {
		i64 u0x00000000f412c268, ; from name: aot-System.ComponentModel.TypeConverter.so
		i64 u0x0000000081847c35, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.19_name, ; name: libaot-System.ComponentModel.TypeConverter.dll.so
		ptr null; void* handle
	}, ; 297
	%struct.DSOCacheEntry {
		i64 u0x00000000f462c30d, ; from name: System.Private.Uri
		i64 u0x0000000099cbecc6, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.31_name, ; name: libaot-System.Private.Uri.dll.so
		ptr null; void* handle
	}, ; 298
	%struct.DSOCacheEntry {
		i64 u0x00000000f5b71069, ; from name: aot-LibVLCSharp.Android.AWindowModern.so
		i64 u0x00000000cc2d8846, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.14_name, ; name: libaot-LibVLCSharp.Android.AWindowModern.dll.so
		ptr null; void* handle
	}, ; 299
	%struct.DSOCacheEntry {
		i64 u0x00000000f5dad612, ; from name: libaot-System.Private.CoreLib.dll.so
		i64 u0x00000000f5dad612, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.42_name, ; name: libaot-System.Private.CoreLib.dll.so
		ptr null; void* handle
	}, ; 300
	%struct.DSOCacheEntry {
		i64 u0x00000000f5ed690d, ; from name: System.ComponentModel.TypeConverter.dll.so
		i64 u0x0000000081847c35, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.19_name, ; name: libaot-System.ComponentModel.TypeConverter.dll.so
		ptr null; void* handle
	}, ; 301
	%struct.DSOCacheEntry {
		i64 u0x00000000f6edac07, ; from name: aot-TaoLive.Model.so
		i64 u0x0000000003e6dcec, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.11_name, ; name: libaot-TaoLive.Model.dll.so
		ptr null; void* handle
	}, ; 302
	%struct.DSOCacheEntry {
		i64 u0x00000000f719e2d8, ; from name: System.Runtime.InteropServices.so
		i64 u0x000000008bbc0be1, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.32_name, ; name: libaot-System.Runtime.InteropServices.dll.so
		ptr null; void* handle
	}, ; 303
	%struct.DSOCacheEntry {
		i64 u0x00000000f7a11e55, ; from name: aot-System
		i64 u0x00000000d4110d87, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.38_name, ; name: libaot-System.dll.so
		ptr null; void* handle
	}, ; 304
	%struct.DSOCacheEntry {
		i64 u0x00000000f8088b43, ; from name: libaot-System.Private.CoreLib
		i64 u0x00000000f5dad612, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.42_name, ; name: libaot-System.Private.CoreLib.dll.so
		ptr null; void* handle
	}, ; 305
	%struct.DSOCacheEntry {
		i64 u0x00000000f97d7588, ; from name: libaot-System.Threading.dll.so
		i64 u0x00000000f97d7588, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.37_name, ; name: libaot-System.Threading.dll.so
		ptr null; void* handle
	}, ; 306
	%struct.DSOCacheEntry {
		i64 u0x00000000f98b7593, ; from name: aot-System.Text.RegularExpressions.so
		i64 u0x000000000eb7b864, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.36_name, ; name: libaot-System.Text.RegularExpressions.dll.so
		ptr null; void* handle
	}, ; 307
	%struct.DSOCacheEntry {
		i64 u0x00000000fa817a6a, ; from name: aot-System.ComponentModel.Primitives.so
		i64 u0x0000000013221fbc, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.18_name, ; name: libaot-System.ComponentModel.Primitives.dll.so
		ptr null; void* handle
	}, ; 308
	%struct.DSOCacheEntry {
		i64 u0x00000000fbc5aaca, ; from name: aot-Mono.Android.Runtime
		i64 u0x000000009f9275a1, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.40_name, ; name: libaot-Mono.Android.Runtime.dll.so
		ptr null; void* handle
	}, ; 309
	%struct.DSOCacheEntry {
		i64 u0x00000000fe2826de, ; from name: libaot-System.Runtime.InteropServices
		i64 u0x000000008bbc0be1, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.32_name, ; name: libaot-System.Runtime.InteropServices.dll.so
		ptr null; void* handle
	}, ; 310
	%struct.DSOCacheEntry {
		i64 u0x00000000fecef6ea, ; from name: System.Runtime.Numerics
		i64 u0x00000000ad907b53, ; uint64_t real_name_hash
		i1 false, ; bool ignore
		ptr @.DSOCacheEntry.33_name, ; name: libaot-System.Runtime.Numerics.dll.so
		ptr null; void* handle
	} ; 311
], align 8

@dso_apk_entries = dso_local local_unnamed_addr global [43 x %struct.DSOApkEntry] zeroinitializer, align 8

; Bundled assembly name buffers, all empty (unused when assembly stores are enabled)
@bundled_assemblies = dso_local local_unnamed_addr global [0 x %struct.XamarinAndroidBundledAssembly] zeroinitializer, align 4

@assembly_store_bundled_assemblies = dso_local local_unnamed_addr global [34 x %struct.AssemblyStoreSingleAssemblyRuntimeData] zeroinitializer, align 4

@assembly_store = dso_local local_unnamed_addr global %struct.AssemblyStoreRuntimeData {
	ptr null, ; uint8_t* data_start
	i32 0, ; uint32_t assembly_count
	i32 0, ; uint32_t index_entry_count
	ptr null; AssemblyStoreAssemblyDescriptor* assemblies
}, align 4

; Strings
@.str.0 = private unnamed_addr constant [7 x i8] c"normal\00", align 1

; Application environment variables name:value pairs
@.env.0 = private unnamed_addr constant [15 x i8] c"MONO_GC_PARAMS\00", align 1
@.env.1 = private unnamed_addr constant [21 x i8] c"major=marksweep-conc\00", align 1
@.env.2 = private unnamed_addr constant [28 x i8] c"XA_HTTP_CLIENT_HANDLER_TYPE\00", align 1
@.env.3 = private unnamed_addr constant [42 x i8] c"Xamarin.Android.Net.AndroidMessageHandler\00", align 1
@.env.4 = private unnamed_addr constant [29 x i8] c"__XA_PACKAGE_NAMING_POLICY__\00", align 1
@.env.5 = private unnamed_addr constant [15 x i8] c"LowercaseCrc64\00", align 1

;ApplicationConfig
@.ApplicationConfig.0_android_package_name = private unnamed_addr constant [15 x i8] c"com.taolive.tv\00", align 1

;DSOCacheEntry
@.DSOCacheEntry.0_name = private unnamed_addr constant [17 x i8] c"libc++_shared.so\00", align 1
@.DSOCacheEntry.1_name = private unnamed_addr constant [10 x i8] c"libvlc.so\00", align 1
@.DSOCacheEntry.2_name = private unnamed_addr constant [34 x i8] c"libSystem.Globalization.Native.so\00", align 1
@.DSOCacheEntry.3_name = private unnamed_addr constant [35 x i8] c"libSystem.IO.Compression.Native.so\00", align 1
@.DSOCacheEntry.4_name = private unnamed_addr constant [20 x i8] c"libSystem.Native.so\00", align 1
@.DSOCacheEntry.5_name = private unnamed_addr constant [50 x i8] c"libSystem.Security.Cryptography.Native.Android.so\00", align 1
@.DSOCacheEntry.6_name = private unnamed_addr constant [35 x i8] c"libmono-component-marshal-ilgen.so\00", align 1
@.DSOCacheEntry.7_name = private unnamed_addr constant [19 x i8] c"libmonosgen-2.0.so\00", align 1
@.DSOCacheEntry.8_name = private unnamed_addr constant [16 x i8] c"libmonodroid.so\00", align 1
@.DSOCacheEntry.9_name = private unnamed_addr constant [51 x i8] c"libaot-_Microsoft.Android.Resource.Designer.dll.so\00", align 1
@.DSOCacheEntry.10_name = private unnamed_addr constant [26 x i8] c"libaot-TaoLive.BLL.dll.so\00", align 1
@.DSOCacheEntry.11_name = private unnamed_addr constant [28 x i8] c"libaot-TaoLive.Model.dll.so\00", align 1
@.DSOCacheEntry.12_name = private unnamed_addr constant [36 x i8] c"libaot-TaoLive.UI.Android.Tv.dll.so\00", align 1
@.DSOCacheEntry.13_name = private unnamed_addr constant [26 x i8] c"libaot-LibVLCSharp.dll.so\00", align 1
@.DSOCacheEntry.14_name = private unnamed_addr constant [48 x i8] c"libaot-LibVLCSharp.Android.AWindowModern.dll.so\00", align 1
@.DSOCacheEntry.15_name = private unnamed_addr constant [44 x i8] c"libaot-System.Collections.Concurrent.dll.so\00", align 1
@.DSOCacheEntry.16_name = private unnamed_addr constant [44 x i8] c"libaot-System.Collections.NonGeneric.dll.so\00", align 1
@.DSOCacheEntry.17_name = private unnamed_addr constant [33 x i8] c"libaot-System.Collections.dll.so\00", align 1
@.DSOCacheEntry.18_name = private unnamed_addr constant [47 x i8] c"libaot-System.ComponentModel.Primitives.dll.so\00", align 1
@.DSOCacheEntry.19_name = private unnamed_addr constant [50 x i8] c"libaot-System.ComponentModel.TypeConverter.dll.so\00", align 1
@.DSOCacheEntry.20_name = private unnamed_addr constant [36 x i8] c"libaot-System.ComponentModel.dll.so\00", align 1
@.DSOCacheEntry.21_name = private unnamed_addr constant [29 x i8] c"libaot-System.Console.dll.so\00", align 1
@.DSOCacheEntry.22_name = private unnamed_addr constant [50 x i8] c"libaot-System.Diagnostics.DiagnosticSource.dll.so\00", align 1
@.DSOCacheEntry.23_name = private unnamed_addr constant [34 x i8] c"libaot-System.Formats.Asn1.dll.so\00", align 1
@.DSOCacheEntry.24_name = private unnamed_addr constant [43 x i8] c"libaot-System.IO.Compression.Brotli.dll.so\00", align 1
@.DSOCacheEntry.25_name = private unnamed_addr constant [36 x i8] c"libaot-System.IO.Compression.dll.so\00", align 1
@.DSOCacheEntry.26_name = private unnamed_addr constant [26 x i8] c"libaot-System.Linq.dll.so\00", align 1
@.DSOCacheEntry.27_name = private unnamed_addr constant [30 x i8] c"libaot-System.Net.Http.dll.so\00", align 1
@.DSOCacheEntry.28_name = private unnamed_addr constant [36 x i8] c"libaot-System.Net.Primitives.dll.so\00", align 1
@.DSOCacheEntry.29_name = private unnamed_addr constant [34 x i8] c"libaot-System.Net.Requests.dll.so\00", align 1
@.DSOCacheEntry.30_name = private unnamed_addr constant [33 x i8] c"libaot-System.ObjectModel.dll.so\00", align 1
@.DSOCacheEntry.31_name = private unnamed_addr constant [33 x i8] c"libaot-System.Private.Uri.dll.so\00", align 1
@.DSOCacheEntry.32_name = private unnamed_addr constant [45 x i8] c"libaot-System.Runtime.InteropServices.dll.so\00", align 1
@.DSOCacheEntry.33_name = private unnamed_addr constant [38 x i8] c"libaot-System.Runtime.Numerics.dll.so\00", align 1
@.DSOCacheEntry.34_name = private unnamed_addr constant [29 x i8] c"libaot-System.Runtime.dll.so\00", align 1
@.DSOCacheEntry.35_name = private unnamed_addr constant [43 x i8] c"libaot-System.Security.Cryptography.dll.so\00", align 1
@.DSOCacheEntry.36_name = private unnamed_addr constant [45 x i8] c"libaot-System.Text.RegularExpressions.dll.so\00", align 1
@.DSOCacheEntry.37_name = private unnamed_addr constant [31 x i8] c"libaot-System.Threading.dll.so\00", align 1
@.DSOCacheEntry.38_name = private unnamed_addr constant [21 x i8] c"libaot-System.dll.so\00", align 1
@.DSOCacheEntry.39_name = private unnamed_addr constant [27 x i8] c"libaot-Java.Interop.dll.so\00", align 1
@.DSOCacheEntry.40_name = private unnamed_addr constant [35 x i8] c"libaot-Mono.Android.Runtime.dll.so\00", align 1
@.DSOCacheEntry.41_name = private unnamed_addr constant [27 x i8] c"libaot-Mono.Android.dll.so\00", align 1
@.DSOCacheEntry.42_name = private unnamed_addr constant [37 x i8] c"libaot-System.Private.CoreLib.dll.so\00", align 1

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
