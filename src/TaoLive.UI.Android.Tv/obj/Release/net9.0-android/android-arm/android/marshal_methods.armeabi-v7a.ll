; ModuleID = 'marshal_methods.armeabi-v7a.ll'
source_filename = "marshal_methods.armeabi-v7a.ll"
target datalayout = "e-m:e-p:32:32-Fi8-i64:64-v128:64:128-a:0:32-n32-S64"
target triple = "armv7-unknown-linux-android21"

%struct.MarshalMethodName = type {
	i64, ; uint64_t id
	ptr ; char* name
}

%struct.MarshalMethodsManagedClass = type {
	i32, ; uint32_t token
	ptr ; MonoClass klass
}

@assembly_image_cache = dso_local local_unnamed_addr global [34 x ptr] zeroinitializer, align 4

; Each entry maps hash of an assembly name to an index into the `assembly_image_cache` array
@assembly_image_cache_hashes = dso_local local_unnamed_addr constant [102 x i32] [
	i32 u0x00345a11, ; 0: lib_System.Net.Requests.dll.so => 20
	i32 u0x056606a6, ; 1: lib_System.Collections.NonGeneric.dll.so => 7
	i32 u0x06ffddbc, ; 2: System.Runtime.InteropServices => 23
	i32 u0x0c38ff48, ; 3: System.ComponentModel => 11
	i32 u0x136bf828, ; 4: lib_System.Runtime.dll.so => 25
	i32 u0x17969339, ; 5: _Microsoft.Android.Resource.Designer => 0
	i32 u0x1a61054f, ; 6: System.Collections => 8
	i32 u0x1b5932ea, ; 7: lib_Mono.Android.Runtime.dll.so => 32
	i32 u0x1bc6ffe7, ; 8: lib_Java.Interop.dll.so => 31
	i32 u0x1bff388e, ; 9: System.dll => 29
	i32 u0x1c78d08a, ; 10: lib_System.Private.Uri.dll.so => 22
	i32 u0x1dbae811, ; 11: System.ObjectModel => 21
	i32 u0x2450d7cc, ; 12: TaoLive.UI.Android.Tv.dll => 5
	i32 u0x2814a96c, ; 13: System.Collections.Concurrent => 6
	i32 u0x2d445acd, ; 14: System.Net.Requests => 20
	i32 u0x2e394f87, ; 15: System.IO.Compression => 16
	i32 u0x311247b5, ; 16: System.Private.Uri.dll => 22
	i32 u0x317d5b75, ; 17: System.IO.Compression.Brotli => 15
	i32 u0x35e25008, ; 18: System.ComponentModel.Primitives.dll => 9
	i32 u0x362ae188, ; 19: lib_LibVLCSharp.dll.so => 1
	i32 u0x3b2c715c, ; 20: System.Collections.dll => 8
	i32 u0x3ebd41f6, ; 21: lib_System.Collections.dll.so => 8
	i32 u0x408b17f4, ; 22: System.ComponentModel.TypeConverter => 10
	i32 u0x41761b2c, ; 23: System => 29
	i32 u0x444e5c8e, ; 24: lib_System.ComponentModel.TypeConverter.dll.so => 10
	i32 u0x44845810, ; 25: lib_System.Net.Http.dll.so => 18
	i32 u0x470b60dd, ; 26: LibVLCSharp.Android.AWindowModern => 2
	i32 u0x4eed2679, ; 27: System.Linq => 17
	i32 u0x533678bd, ; 28: lib_System.Private.CoreLib.dll.so => 30
	i32 u0x568cd628, ; 29: System.Formats.Asn1.dll => 14
	i32 u0x57261233, ; 30: System.IO.Compression.dll => 16
	i32 u0x583e844f, ; 31: System.IO.Compression.Brotli.dll => 15
	i32 u0x5bf8ca0f, ; 32: System.Text.RegularExpressions.dll => 27
	i32 u0x5e7321d2, ; 33: lib_System.ComponentModel.Primitives.dll.so => 9
	i32 u0x61b9038d, ; 34: System.Net.Http.dll => 18
	i32 u0x61c036ca, ; 35: System.Text.RegularExpressions => 27
	i32 u0x62021776, ; 36: lib_System.IO.Compression.dll.so => 16
	i32 u0x62c6282e, ; 37: System.Runtime => 25
	i32 u0x63fca3d0, ; 38: System.Net.Primitives.dll => 19
	i32 u0x641f3e5a, ; 39: System.Security.Cryptography => 26
	i32 u0x68f61ae4, ; 40: lib_System.Formats.Asn1.dll.so => 14
	i32 u0x6a216153, ; 41: Mono.Android.Runtime.dll => 32
	i32 u0x6afaf338, ; 42: lib_System.Threading.dll.so => 28
	i32 u0x6ca29590, ; 43: lib_TaoLive.BLL.dll.so => 3
	i32 u0x71dc7c8b, ; 44: System.Collections.NonGeneric.dll => 7
	i32 u0x731dd955, ; 45: lib_Mono.Android.dll.so => 33
	i32 u0x7b350579, ; 46: lib__Microsoft.Android.Resource.Designer.dll.so => 0
	i32 u0x7bf8cdab, ; 47: System.Runtime.dll => 25
	i32 u0x7ec9ffe9, ; 48: System.Console => 12
	i32 u0x816751d8, ; 49: lib_System.Diagnostics.DiagnosticSource.dll.so => 13
	i32 u0x82b6c85e, ; 50: System.ObjectModel.dll => 21
	i32 u0x8334206b, ; 51: System.Net.Http => 18
	i32 u0x896b7878, ; 52: System.Private.CoreLib.dll => 30
	i32 u0x89cc6fee, ; 53: LibVLCSharp => 1
	i32 u0x8c20f140, ; 54: lib_System.Console.dll.so => 12
	i32 u0x8c40e0db, ; 55: System.Net.Primitives => 19
	i32 u0x912896e5, ; 56: System.Console.dll => 12
	i32 u0x93918882, ; 57: Java.Interop.dll => 31
	i32 u0x9f2b7e4c, ; 58: lib_TaoLive.UI.Android.Tv.dll.so => 5
	i32 u0x9f7ea921, ; 59: lib_System.Runtime.InteropServices.dll.so => 23
	i32 u0xa0fb56af, ; 60: lib_System.Text.RegularExpressions.dll.so => 27
	i32 u0xa262a30f, ; 61: System.Runtime.Numerics.dll => 24
	i32 u0xa493aa02, ; 62: lib_System.Collections.Concurrent.dll.so => 6
	i32 u0xa81b119f, ; 63: lib_System.Security.Cryptography.dll.so => 26
	i32 u0xad6f1e8a, ; 64: System.Private.CoreLib => 30
	i32 u0xb0682092, ; 65: System.ComponentModel.dll => 11
	i32 u0xb33d5570, ; 66: LibVLCSharp.Android.AWindowModern.dll => 2
	i32 u0xb514b305, ; 67: _Microsoft.Android.Resource.Designer.dll => 0
	i32 u0xb65adef9, ; 68: Mono.Android.Runtime => 32
	i32 u0xb660be12, ; 69: System.ComponentModel.Primitives => 9
	i32 u0xb8fd311b, ; 70: System.Formats.Asn1 => 14
	i32 u0xbaa520e7, ; 71: lib_System.ObjectModel.dll.so => 21
	i32 u0xbff2e236, ; 72: System.Threading => 28
	i32 u0xc1cd9713, ; 73: TaoLive.Model.dll => 4
	i32 u0xc5b097e4, ; 74: System.Net.Requests.dll => 20
	i32 u0xc821fc10, ; 75: lib_System.ComponentModel.dll.so => 11
	i32 u0xc8a662e9, ; 76: Java.Interop => 31
	i32 u0xcbab0a1b, ; 77: lib_TaoLive.Model.dll.so => 4
	i32 u0xcef19b37, ; 78: System.ComponentModel.TypeConverter.dll => 10
	i32 u0xcf3163e6, ; 79: Mono.Android => 33
	i32 u0xd398dec9, ; 80: TaoLive.BLL.dll => 3
	i32 u0xd4045e1b, ; 81: lib_System.dll.so => 29
	i32 u0xd62c2dd3, ; 82: lib_LibVLCSharp.Android.AWindowModern.dll.so => 2
	i32 u0xd684ce80, ; 83: TaoLive.UI.Android.Tv => 5
	i32 u0xd6b443a3, ; 84: TaoLive.BLL => 3
	i32 u0xd715a361, ; 85: System.Linq.dll => 17
	i32 u0xdae8aa5e, ; 86: Mono.Android.dll => 33
	i32 u0xdf6f3870, ; 87: System.Diagnostics.DiagnosticSource => 13
	i32 u0xe12bf655, ; 88: LibVLCSharp.dll => 1
	i32 u0xe2098b0b, ; 89: System.Collections.NonGeneric => 7
	i32 u0xe2513246, ; 90: lib_System.Runtime.Numerics.dll.so => 24
	i32 u0xe3df9d2b, ; 91: System.Security.Cryptography.dll => 26
	i32 u0xe56ef253, ; 92: System.Runtime.InteropServices.dll => 23
	i32 u0xe7c01049, ; 93: TaoLive.Model => 4
	i32 u0xe839deed, ; 94: System.Collections.Concurrent.dll => 6
	i32 u0xed1090ae, ; 95: lib_System.Net.Primitives.dll.so => 19
	i32 u0xf2ce3c98, ; 96: System.Threading.dll => 28
	i32 u0xf462c30d, ; 97: System.Private.Uri => 22
	i32 u0xfa50891f, ; 98: lib_System.Linq.dll.so => 17
	i32 u0xfb1dad5d, ; 99: System.Diagnostics.DiagnosticSource.dll => 13
	i32 u0xfbc4b67c, ; 100: lib_System.IO.Compression.Brotli.dll.so => 15
	i32 u0xfecef6ea ; 101: System.Runtime.Numerics => 24
], align 4

@assembly_image_cache_indices = dso_local local_unnamed_addr constant [102 x i32] [
	i32 20, i32 7, i32 23, i32 11, i32 25, i32 0, i32 8, i32 32,
	i32 31, i32 29, i32 22, i32 21, i32 5, i32 6, i32 20, i32 16,
	i32 22, i32 15, i32 9, i32 1, i32 8, i32 8, i32 10, i32 29,
	i32 10, i32 18, i32 2, i32 17, i32 30, i32 14, i32 16, i32 15,
	i32 27, i32 9, i32 18, i32 27, i32 16, i32 25, i32 19, i32 26,
	i32 14, i32 32, i32 28, i32 3, i32 7, i32 33, i32 0, i32 25,
	i32 12, i32 13, i32 21, i32 18, i32 30, i32 1, i32 12, i32 19,
	i32 12, i32 31, i32 5, i32 23, i32 27, i32 24, i32 6, i32 26,
	i32 30, i32 11, i32 2, i32 0, i32 32, i32 9, i32 14, i32 21,
	i32 28, i32 4, i32 20, i32 11, i32 31, i32 4, i32 10, i32 33,
	i32 3, i32 29, i32 2, i32 5, i32 3, i32 17, i32 33, i32 13,
	i32 1, i32 7, i32 24, i32 26, i32 23, i32 4, i32 6, i32 19,
	i32 28, i32 22, i32 17, i32 13, i32 15, i32 24
], align 4

@marshal_methods_number_of_classes = dso_local local_unnamed_addr constant i32 0, align 4

@marshal_methods_class_cache = dso_local local_unnamed_addr global [0 x %struct.MarshalMethodsManagedClass] zeroinitializer, align 4

; Names of classes in which marshal methods reside
@mm_class_names = dso_local local_unnamed_addr constant [0 x ptr] zeroinitializer, align 4

@mm_method_names = dso_local local_unnamed_addr constant [1 x %struct.MarshalMethodName] [
	%struct.MarshalMethodName {
		i64 u0x0000000000000000, ; name: 
		ptr @.MarshalMethodName.0_name; char* name
	} ; 0
], align 8

; get_function_pointer (uint32_t mono_image_index, uint32_t class_index, uint32_t method_token, void*& target_ptr)
@get_function_pointer = internal dso_local unnamed_addr global ptr null, align 4

; Functions

; Function attributes: memory(write, argmem: none, inaccessiblemem: none) "min-legal-vector-width"="0" mustprogress "no-trapping-math"="true" nofree norecurse nosync nounwind "stack-protector-buffer-size"="8" uwtable willreturn
define void @xamarin_app_init(ptr nocapture noundef readnone %env, ptr noundef %fn) local_unnamed_addr #0
{
	%fnIsNull = icmp eq ptr %fn, null
	br i1 %fnIsNull, label %1, label %2

1: ; preds = %0
	%putsResult = call noundef i32 @puts(ptr @.str.0)
	call void @abort()
	unreachable 

2: ; preds = %1, %0
	store ptr %fn, ptr @get_function_pointer, align 4, !tbaa !3
	ret void
}

; Strings
@.str.0 = private unnamed_addr constant [40 x i8] c"get_function_pointer MUST be specified\0A\00", align 1

;MarshalMethodName
@.MarshalMethodName.0_name = private unnamed_addr constant [1 x i8] c"\00", align 1

; External functions

; Function attributes: "no-trapping-math"="true" noreturn nounwind "stack-protector-buffer-size"="8"
declare void @abort() local_unnamed_addr #2

; Function attributes: nofree nounwind
declare noundef i32 @puts(ptr noundef) local_unnamed_addr #1
attributes #0 = { memory(write, argmem: none, inaccessiblemem: none) "min-legal-vector-width"="0" mustprogress "no-trapping-math"="true" nofree norecurse nosync nounwind "stack-protector-buffer-size"="8" "target-cpu"="generic" "target-features"="+armv7-a,+d32,+dsp,+fp64,+neon,+vfp2,+vfp2sp,+vfp3,+vfp3d16,+vfp3d16sp,+vfp3sp,-aes,-fp-armv8,-fp-armv8d16,-fp-armv8d16sp,-fp-armv8sp,-fp16,-fp16fml,-fullfp16,-sha2,-thumb-mode,-vfp4,-vfp4d16,-vfp4d16sp,-vfp4sp" uwtable willreturn }
attributes #1 = { nofree nounwind }
attributes #2 = { "no-trapping-math"="true" noreturn nounwind "stack-protector-buffer-size"="8" "target-cpu"="generic" "target-features"="+armv7-a,+d32,+dsp,+fp64,+neon,+vfp2,+vfp2sp,+vfp3,+vfp3d16,+vfp3d16sp,+vfp3sp,-aes,-fp-armv8,-fp-armv8d16,-fp-armv8d16sp,-fp-armv8sp,-fp16,-fp16fml,-fullfp16,-sha2,-thumb-mode,-vfp4,-vfp4d16,-vfp4d16sp,-vfp4sp" }

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
