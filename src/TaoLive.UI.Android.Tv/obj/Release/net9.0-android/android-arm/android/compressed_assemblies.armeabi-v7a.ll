; ModuleID = 'compressed_assemblies.armeabi-v7a.ll'
source_filename = "compressed_assemblies.armeabi-v7a.ll"
target datalayout = "e-m:e-p:32:32-Fi8-i64:64-v128:64:128-a:0:32-n32-S64"
target triple = "armv7-unknown-linux-android21"

%struct.CompressedAssemblies = type {
	i32, ; uint32_t count
	ptr ; CompressedAssemblyDescriptor descriptors
}

%struct.CompressedAssemblyDescriptor = type {
	i32, ; uint32_t uncompressed_file_size
	i1, ; bool loaded
	ptr ; uint8_t data
}

@compressed_assemblies = dso_local local_unnamed_addr global %struct.CompressedAssemblies {
	i32 34, ; uint32_t count
	ptr @compressed_assembly_descriptors; CompressedAssemblyDescriptor* descriptors
}, align 4

@compressed_assembly_descriptors = internal dso_local global [34 x %struct.CompressedAssemblyDescriptor] [
	%struct.CompressedAssemblyDescriptor {
		i32 2560, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_0; uint8_t* data
	}, ; 0: _Microsoft.Android.Resource.Designer
	%struct.CompressedAssemblyDescriptor {
		i32 40448, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_1; uint8_t* data
	}, ; 1: LibVLCSharp
	%struct.CompressedAssemblyDescriptor {
		i32 32768, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_2; uint8_t* data
	}, ; 2: LibVLCSharp.Android.AWindowModern
	%struct.CompressedAssemblyDescriptor {
		i32 60416, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_3; uint8_t* data
	}, ; 3: TaoLive.BLL
	%struct.CompressedAssemblyDescriptor {
		i32 9728, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_4; uint8_t* data
	}, ; 4: TaoLive.Model
	%struct.CompressedAssemblyDescriptor {
		i32 29696, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_5; uint8_t* data
	}, ; 5: TaoLive.UI.Android.Tv
	%struct.CompressedAssemblyDescriptor {
		i32 17408, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_6; uint8_t* data
	}, ; 6: System.Collections.Concurrent
	%struct.CompressedAssemblyDescriptor {
		i32 10752, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_7; uint8_t* data
	}, ; 7: System.Collections.NonGeneric
	%struct.CompressedAssemblyDescriptor {
		i32 23040, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_8; uint8_t* data
	}, ; 8: System.Collections
	%struct.CompressedAssemblyDescriptor {
		i32 10240, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_9; uint8_t* data
	}, ; 9: System.ComponentModel.Primitives
	%struct.CompressedAssemblyDescriptor {
		i32 10752, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_10; uint8_t* data
	}, ; 10: System.ComponentModel.TypeConverter
	%struct.CompressedAssemblyDescriptor {
		i32 5120, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_11; uint8_t* data
	}, ; 11: System.ComponentModel
	%struct.CompressedAssemblyDescriptor {
		i32 11776, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_12; uint8_t* data
	}, ; 12: System.Console
	%struct.CompressedAssemblyDescriptor {
		i32 18432, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_13; uint8_t* data
	}, ; 13: System.Diagnostics.DiagnosticSource
	%struct.CompressedAssemblyDescriptor {
		i32 59904, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_14; uint8_t* data
	}, ; 14: System.Formats.Asn1
	%struct.CompressedAssemblyDescriptor {
		i32 21504, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_15; uint8_t* data
	}, ; 15: System.IO.Compression.Brotli
	%struct.CompressedAssemblyDescriptor {
		i32 28672, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_16; uint8_t* data
	}, ; 16: System.IO.Compression
	%struct.CompressedAssemblyDescriptor {
		i32 27136, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_17; uint8_t* data
	}, ; 17: System.Linq
	%struct.CompressedAssemblyDescriptor {
		i32 123392, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_18; uint8_t* data
	}, ; 18: System.Net.Http
	%struct.CompressedAssemblyDescriptor {
		i32 35840, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_19; uint8_t* data
	}, ; 19: System.Net.Primitives
	%struct.CompressedAssemblyDescriptor {
		i32 7168, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_20; uint8_t* data
	}, ; 20: System.Net.Requests
	%struct.CompressedAssemblyDescriptor {
		i32 6144, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_21; uint8_t* data
	}, ; 21: System.ObjectModel
	%struct.CompressedAssemblyDescriptor {
		i32 66560, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_22; uint8_t* data
	}, ; 22: System.Private.Uri
	%struct.CompressedAssemblyDescriptor {
		i32 9728, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_23; uint8_t* data
	}, ; 23: System.Runtime.InteropServices
	%struct.CompressedAssemblyDescriptor {
		i32 66560, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_24; uint8_t* data
	}, ; 24: System.Runtime.Numerics
	%struct.CompressedAssemblyDescriptor {
		i32 7680, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_25; uint8_t* data
	}, ; 25: System.Runtime
	%struct.CompressedAssemblyDescriptor {
		i32 120832, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_26; uint8_t* data
	}, ; 26: System.Security.Cryptography
	%struct.CompressedAssemblyDescriptor {
		i32 303616, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_27; uint8_t* data
	}, ; 27: System.Text.RegularExpressions
	%struct.CompressedAssemblyDescriptor {
		i32 12288, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_28; uint8_t* data
	}, ; 28: System.Threading
	%struct.CompressedAssemblyDescriptor {
		i32 4608, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_29; uint8_t* data
	}, ; 29: System
	%struct.CompressedAssemblyDescriptor {
		i32 1607168, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_30; uint8_t* data
	}, ; 30: System.Private.CoreLib
	%struct.CompressedAssemblyDescriptor {
		i32 150528, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_31; uint8_t* data
	}, ; 31: Java.Interop
	%struct.CompressedAssemblyDescriptor {
		i32 19512, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_32; uint8_t* data
	}, ; 32: Mono.Android.Runtime
	%struct.CompressedAssemblyDescriptor {
		i32 553472, ; uint32_t uncompressed_file_size
		i1 false, ; bool loaded
		ptr @__compressedAssemblyData_33; uint8_t* data
	} ; 33: Mono.Android
], align 4

@__compressedAssemblyData_0 = internal dso_local global [2560 x i8] zeroinitializer, align 1
@__compressedAssemblyData_1 = internal dso_local global [40448 x i8] zeroinitializer, align 1
@__compressedAssemblyData_2 = internal dso_local global [32768 x i8] zeroinitializer, align 1
@__compressedAssemblyData_3 = internal dso_local global [60416 x i8] zeroinitializer, align 1
@__compressedAssemblyData_4 = internal dso_local global [9728 x i8] zeroinitializer, align 1
@__compressedAssemblyData_5 = internal dso_local global [29696 x i8] zeroinitializer, align 1
@__compressedAssemblyData_6 = internal dso_local global [17408 x i8] zeroinitializer, align 1
@__compressedAssemblyData_7 = internal dso_local global [10752 x i8] zeroinitializer, align 1
@__compressedAssemblyData_8 = internal dso_local global [23040 x i8] zeroinitializer, align 1
@__compressedAssemblyData_9 = internal dso_local global [10240 x i8] zeroinitializer, align 1
@__compressedAssemblyData_10 = internal dso_local global [10752 x i8] zeroinitializer, align 1
@__compressedAssemblyData_11 = internal dso_local global [5120 x i8] zeroinitializer, align 1
@__compressedAssemblyData_12 = internal dso_local global [11776 x i8] zeroinitializer, align 1
@__compressedAssemblyData_13 = internal dso_local global [18432 x i8] zeroinitializer, align 1
@__compressedAssemblyData_14 = internal dso_local global [59904 x i8] zeroinitializer, align 1
@__compressedAssemblyData_15 = internal dso_local global [21504 x i8] zeroinitializer, align 1
@__compressedAssemblyData_16 = internal dso_local global [28672 x i8] zeroinitializer, align 1
@__compressedAssemblyData_17 = internal dso_local global [27136 x i8] zeroinitializer, align 1
@__compressedAssemblyData_18 = internal dso_local global [123392 x i8] zeroinitializer, align 1
@__compressedAssemblyData_19 = internal dso_local global [35840 x i8] zeroinitializer, align 1
@__compressedAssemblyData_20 = internal dso_local global [7168 x i8] zeroinitializer, align 1
@__compressedAssemblyData_21 = internal dso_local global [6144 x i8] zeroinitializer, align 1
@__compressedAssemblyData_22 = internal dso_local global [66560 x i8] zeroinitializer, align 1
@__compressedAssemblyData_23 = internal dso_local global [9728 x i8] zeroinitializer, align 1
@__compressedAssemblyData_24 = internal dso_local global [66560 x i8] zeroinitializer, align 1
@__compressedAssemblyData_25 = internal dso_local global [7680 x i8] zeroinitializer, align 1
@__compressedAssemblyData_26 = internal dso_local global [120832 x i8] zeroinitializer, align 1
@__compressedAssemblyData_27 = internal dso_local global [303616 x i8] zeroinitializer, align 1
@__compressedAssemblyData_28 = internal dso_local global [12288 x i8] zeroinitializer, align 1
@__compressedAssemblyData_29 = internal dso_local global [4608 x i8] zeroinitializer, align 1
@__compressedAssemblyData_30 = internal dso_local global [1607168 x i8] zeroinitializer, align 1
@__compressedAssemblyData_31 = internal dso_local global [150528 x i8] zeroinitializer, align 1
@__compressedAssemblyData_32 = internal dso_local global [19512 x i8] zeroinitializer, align 1
@__compressedAssemblyData_33 = internal dso_local global [553472 x i8] zeroinitializer, align 1

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
