; ModuleID = 'typemaps.armeabi-v7a.ll'
source_filename = "typemaps.armeabi-v7a.ll"
target datalayout = "e-m:e-p:32:32-Fi8-i64:64-v128:64:128-a:0:32-n32-S64"
target triple = "armv7-unknown-linux-android21"

%struct.TypeMapJava = type {
	i32, ; uint32_t module_index
	i32, ; uint32_t type_token_id
	i32 ; uint32_t java_name_index
}

%struct.TypeMapModule = type {
	[16 x i8], ; uint8_t module_uuid[16]
	i32, ; uint32_t entry_count
	i32, ; uint32_t duplicate_count
	ptr, ; TypeMapModuleEntry map
	ptr, ; TypeMapModuleEntry duplicate_map
	ptr, ; char* assembly_name
	ptr, ; MonoImage image
	i32, ; uint32_t java_name_width
	ptr ; uint8_t java_map
}

%struct.TypeMapModuleEntry = type {
	i32, ; uint32_t type_token_id
	i32 ; uint32_t java_map_index
}

@map_module_count = dso_local local_unnamed_addr constant i32 4, align 4

@java_type_count = dso_local local_unnamed_addr constant i32 161, align 4

; Managed modules map
@map_modules = dso_local local_unnamed_addr global [4 x %struct.TypeMapModule] [
	%struct.TypeMapModule {
		[16 x i8] [ i8 u0x30, i8 u0xb1, i8 u0x16, i8 u0xc3, i8 u0xb6, i8 u0x23, i8 u0x88, i8 u0x42, i8 u0x90, i8 u0x47, i8 u0x49, i8 u0xfb, i8 u0x97, i8 u0xf0, i8 u0x19, i8 u0xcc ], ; module_uuid: c316b130-23b6-4288-9047-49fb97f019cc
		i32 149, ; uint32_t entry_count
		i32 58, ; uint32_t duplicate_count
		ptr @module0_managed_to_java, ; TypeMapModuleEntry* map
		ptr @module0_managed_to_java_duplicates, ; TypeMapModuleEntry* duplicate_map
		ptr @.TypeMapModule.0_assembly_name, ; assembly_name: Mono.Android
		ptr null, ; MonoImage* image
		i32 0, ; uint32_t java_name_width
		ptr null; uint8_t* java_map
	}, ; 0
	%struct.TypeMapModule {
		[16 x i8] [ i8 u0x5a, i8 u0x3d, i8 u0xce, i8 u0xcb, i8 u0xf8, i8 u0x48, i8 u0x50, i8 u0x42, i8 u0x84, i8 u0xcd, i8 u0x45, i8 u0x0f, i8 u0x39, i8 u0x46, i8 u0x82, i8 u0x84 ], ; module_uuid: cbce3d5a-48f8-4250-84cd-450f39468284
		i32 6, ; uint32_t entry_count
		i32 0, ; uint32_t duplicate_count
		ptr @module1_managed_to_java, ; TypeMapModuleEntry* map
		ptr null, ; TypeMapModuleEntry* duplicate_map
		ptr @.TypeMapModule.1_assembly_name, ; assembly_name: TaoLive.UI.Android.Tv
		ptr null, ; MonoImage* image
		i32 0, ; uint32_t java_name_width
		ptr null; uint8_t* java_map
	}, ; 1
	%struct.TypeMapModule {
		[16 x i8] [ i8 u0xcc, i8 u0x8a, i8 u0x0c, i8 u0x1c, i8 u0x7e, i8 u0x8c, i8 u0x22, i8 u0x44, i8 u0xb1, i8 u0xc6, i8 u0x8f, i8 u0xef, i8 u0x5f, i8 u0x52, i8 u0x56, i8 u0x6c ], ; module_uuid: 1c0c8acc-8c7e-4422-b1c6-8fef5f52566c
		i32 5, ; uint32_t entry_count
		i32 4, ; uint32_t duplicate_count
		ptr @module2_managed_to_java, ; TypeMapModuleEntry* map
		ptr @module2_managed_to_java_duplicates, ; TypeMapModuleEntry* duplicate_map
		ptr @.TypeMapModule.2_assembly_name, ; assembly_name: LibVLCSharp.Android.AWindowModern
		ptr null, ; MonoImage* image
		i32 0, ; uint32_t java_name_width
		ptr null; uint8_t* java_map
	}, ; 2
	%struct.TypeMapModule {
		[16 x i8] [ i8 u0xeb, i8 u0xb1, i8 u0x1e, i8 u0x9a, i8 u0x30, i8 u0x6a, i8 u0x14, i8 u0x44, i8 u0x9e, i8 u0x2b, i8 u0xd6, i8 u0xd3, i8 u0x97, i8 u0xa4, i8 u0x52, i8 u0xad ], ; module_uuid: 9a1eb1eb-6a30-4414-9e2b-d6d397a452ad
		i32 1, ; uint32_t entry_count
		i32 0, ; uint32_t duplicate_count
		ptr @module3_managed_to_java, ; TypeMapModuleEntry* map
		ptr null, ; TypeMapModuleEntry* duplicate_map
		ptr @.TypeMapModule.3_assembly_name, ; assembly_name: LibVLCSharp
		ptr null, ; MonoImage* image
		i32 0, ; uint32_t java_name_width
		ptr null; uint8_t* java_map
	} ; 3
], align 4

; Java types name hashes
@map_java_hashes = dso_local local_unnamed_addr constant [161 x i32] [
	i32 u0x00bc506a, ; 0 => java/lang/Object
	i32 u0x01c47bce, ; 1 => android/widget/ListAdapter
	i32 u0x01e97a1e, ; 2 => java/security/cert/Certificate
	i32 u0x046d7780, ; 3 => android/view/ViewGroup
	i32 u0x083c5433, ; 4 => javax/net/ssl/SSLSessionContext
	i32 u0x084d3fe5, ; 5 => android/view/KeyEvent
	i32 u0x0a8831f3, ; 6 => java/lang/IllegalArgumentException
	i32 u0x0f52f1a6, ; 7 => java/lang/ReflectiveOperationException
	i32 u0x100ba9c7, ; 8 => javax/security/cert/X509Certificate
	i32 u0x1079b5f8, ; 9 => java/security/spec/EncodedKeySpec
	i32 u0x10abc779, ; 10 => android/content/SharedPreferences$Editor
	i32 u0x15d8dfc9, ; 11 => android/view/ViewGroup$LayoutParams
	i32 u0x17725ef2, ; 12 => mono/java/lang/RunnableImplementor
	i32 u0x189a6365, ; 13 => java/lang/Long
	i32 u0x18feeb05, ; 14 => java/util/Iterator
	i32 u0x19100f08, ; 15 => java/net/ConnectException
	i32 u0x1a6b349b, ; 16 => java/lang/LinkageError
	i32 u0x1de7d866, ; 17 => android/view/ViewGroup$MarginLayoutParams
	i32 u0x1f20e490, ; 18 => crc6487f41b6f8dfb94e3/MainActivity_ChannelAdapter
	i32 u0x1fa9731c, ; 19 => mono/android/runtime/OutputStreamAdapter
	i32 u0x22a2d798, ; 20 => java/nio/channels/FileChannel
	i32 u0x22d2aa9f, ; 21 => java/lang/IllegalStateException
	i32 u0x23464fac, ; 22 => android/os/Bundle
	i32 u0x23e6e7ee, ; 23 => java/security/spec/PKCS8EncodedKeySpec
	i32 u0x242020ac, ; 24 => java/io/Serializable
	i32 u0x24e61bfb, ; 25 => java/net/URL
	i32 u0x294d1f6f, ; 26 => java/util/ArrayList
	i32 u0x2e841628, ; 27 => java/lang/CharSequence
	i32 u0x2e8cec9f, ; 28 => java/io/PrintWriter
	i32 u0x2f523ac2, ; 29 => java/lang/Integer
	i32 u0x3016caa7, ; 30 => java/lang/Thread
	i32 u0x303d6f8b, ; 31 => java/lang/UnsatisfiedLinkError
	i32 u0x31fd4970, ; 32 => java/lang/NullPointerException
	i32 u0x33e04adb, ; 33 => crc64a1b52d66edecd4d3/UncaughtCrashPeer
	i32 u0x34408f1d, ; 34 => javax/net/ssl/TrustManager
	i32 u0x353fa59a, ; 35 => java/lang/Short
	i32 u0x38b53f42, ; 36 => android/app/ActivityManager
	i32 u0x3c238b9c, ; 37 => android/graphics/Color
	i32 u0x3d2f4240, ; 38 => java/net/SocketAddress
	i32 u0x3d6f597a, ; 39 => android/database/DataSetObserver
	i32 u0x403b5100, ; 40 => java/util/function/Consumer
	i32 u0x41066ac4, ; 41 => javax/net/ssl/KeyManagerFactory
	i32 u0x4411b6b5, ; 42 => java/util/Enumeration
	i32 u0x45767f3a, ; 43 => crc64a1b52d66edecd4d3/LiveSurfaceView
	i32 u0x4612c890, ; 44 => java/lang/ClassNotFoundException
	i32 u0x4923b010, ; 45 => javax/security/cert/Certificate
	i32 u0x4bbb3aaa, ; 46 => java/net/SocketTimeoutException
	i32 u0x4d64d6f9, ; 47 => java/lang/Throwable
	i32 u0x4ee6065b, ; 48 => javax/net/ssl/SSLContext
	i32 u0x4f93fcd4, ; 49 => java/util/Collection
	i32 u0x51907546, ; 50 => java/lang/ClassCastException
	i32 u0x51dff242, ; 51 => javax/net/ssl/KeyManager
	i32 u0x54fbd6e1, ; 52 => java/lang/SecurityException
	i32 u0x551e4b18, ; 53 => java/net/HttpURLConnection
	i32 u0x56442f9e, ; 54 => android/widget/LinearLayout$LayoutParams
	i32 u0x57f522af, ; 55 => java/util/HashMap
	i32 u0x57fe728e, ; 56 => javax/security/auth/Subject
	i32 u0x58c968b2, ; 57 => java/nio/channels/spi/AbstractInterruptibleChannel
	i32 u0x5aeca5f1, ; 58 => java/security/PrivateKey
	i32 u0x5c10ee2c, ; 59 => java/io/File
	i32 u0x5e956e3c, ; 60 => android/os/Handler
	i32 u0x61a146b7, ; 61 => java/security/Principal
	i32 u0x622147f6, ; 62 => android/view/View
	i32 u0x62545cb7, ; 63 => java/lang/RuntimeException
	i32 u0x62c5df0e, ; 64 => java/lang/IndexOutOfBoundsException
	i32 u0x642f84c3, ; 65 => java/lang/Byte
	i32 u0x666aa4c6, ; 66 => java/lang/Character
	i32 u0x67c2b7a7, ; 67 => android/widget/FrameLayout
	i32 u0x689f8691, ; 68 => java/util/Random
	i32 u0x68d070f5, ; 69 => android/os/BaseBundle
	i32 u0x6e5f2b64, ; 70 => java/lang/Runnable
	i32 u0x6ece3e1d, ; 71 => android/widget/LinearLayout
	i32 u0x73e1105c, ; 72 => java/io/OutputStream
	i32 u0x74c082d0, ; 73 => org/videolan/libvlc/AWindow$SurfaceCallback
	i32 u0x74f3ac63, ; 74 => android/content/BroadcastReceiver
	i32 u0x765ee0ac, ; 75 => android/app/Activity
	i32 u0x767c0d39, ; 76 => java/lang/Boolean
	i32 u0x76a65018, ; 77 => android/widget/AdapterView$OnItemClickListener
	i32 u0x77b0a344, ; 78 => android/content/Intent
	i32 u0x78cbc3b9, ; 79 => android/widget/FrameLayout$LayoutParams
	i32 u0x78dd82d8, ; 80 => android/view/ContextThemeWrapper
	i32 u0x791579f7, ; 81 => android/widget/AdapterView
	i32 u0x7bff2bcd, ; 82 => java/lang/Thread$UncaughtExceptionHandler
	i32 u0x7c04bc64, ; 83 => java/security/SecureRandom
	i32 u0x814f71b8, ; 84 => android/app/ActivityManager$MemoryInfo
	i32 u0x828c7aaa, ; 85 => java/security/Key
	i32 u0x873fa2a1, ; 86 => java/net/UnknownServiceException
	i32 u0x875b8bea, ; 87 => java/net/Proxy$Type
	i32 u0x882d17e1, ; 88 => android/app/Application
	i32 u0x8ca033b7, ; 89 => crc6487f41b6f8dfb94e3/BootReceiver
	i32 u0x8ce3a5d6, ; 90 => java/lang/Enum
	i32 u0x8ecc3a71, ; 91 => android/view/View$OnLayoutChangeListener
	i32 u0x8fbb1ca5, ; 92 => java/lang/UnsupportedOperationException
	i32 u0x91a3eaf3, ; 93 => java/net/SocketException
	i32 u0x96f82d4f, ; 94 => android/content/SharedPreferences$OnSharedPreferenceChangeListener
	i32 u0x987a2d5e, ; 95 => java/io/FileInputStream
	i32 u0x9aa0facc, ; 96 => android/widget/BaseAdapter
	i32 u0x9e3b164d, ; 97 => java/io/InterruptedIOException
	i32 u0x9ed79303, ; 98 => javax/net/ssl/HostnameVerifier
	i32 u0x9fd00367, ; 99 => android/widget/Adapter
	i32 u0xa0343f64, ; 100 => android/widget/TextView
	i32 u0xa2384ed3, ; 101 => android/graphics/drawable/Drawable
	i32 u0xa4ab3847, ; 102 => java/lang/Float
	i32 u0xa7d2e3c3, ; 103 => android/os/Build
	i32 u0xa7fdd220, ; 104 => crc64a1b52d66edecd4d3/LiveTextureView
	i32 u0xab39fc65, ; 105 => java/lang/System
	i32 u0xab580b31, ; 106 => java/lang/StackTraceElement
	i32 u0xade06031, ; 107 => android/view/SurfaceHolder
	i32 u0xaed01dcc, ; 108 => android/view/InputEvent
	i32 u0xaeddab28, ; 109 => android/util/AttributeSet
	i32 u0xaf6773fc, ; 110 => java/lang/Exception
	i32 u0xaf862ea3, ; 111 => android/widget/ListView
	i32 u0xb1a6fc2a, ; 112 => mono/android/runtime/JavaArray
	i32 u0xb1d7f461, ; 113 => mono/android/TypeManager
	i32 u0xb26af8c8, ; 114 => org/videolan/libvlc/AWindow
	i32 u0xb37d2108, ; 115 => android/view/Surface
	i32 u0xb4c4fd79, ; 116 => java/io/StringWriter
	i32 u0xc4bfd570, ; 117 => javax/net/ssl/SSLSession
	i32 u0xc5df2394, ; 118 => java/net/Proxy
	i32 u0xcad0ba90, ; 119 => android/graphics/SurfaceTexture
	i32 u0xcb379d37, ; 120 => javax/net/ssl/HttpsURLConnection
	i32 u0xcc0df96f, ; 121 => java/lang/Number
	i32 u0xcc7ee4cc, ; 122 => android/content/SharedPreferences
	i32 u0xd1cde4e5, ; 123 => java/net/URLConnection
	i32 u0xd52920d3, ; 124 => android/runtime/JavaProxyThrowable
	i32 u0xd66fd9dd, ; 125 => android/widget/AbsListView
	i32 u0xda867062, ; 126 => java/lang/String
	i32 u0xdab17055, ; 127 => java/net/InetSocketAddress
	i32 u0xdb8b0f9a, ; 128 => mono/android/runtime/JavaObject
	i32 u0xdcab8f7d, ; 129 => java/lang/Double
	i32 u0xdd7b8a2d, ; 130 => android/os/Build$VERSION
	i32 u0xdde616ce, ; 131 => javax/net/SocketFactory
	i32 u0xde20a2a0, ; 132 => java/net/ProtocolException
	i32 u0xe0e388f2, ; 133 => org/videolan/libvlc/IVLCVout
	i32 u0xe1f6cebd, ; 134 => java/security/spec/KeySpec
	i32 u0xe45cbbbd, ; 135 => crc6487f41b6f8dfb94e3/MainActivity
	i32 u0xe54e9c9d, ; 136 => crc64fd41ab9b8a8239c2/LayoutChangeListener
	i32 u0xe76b5314, ; 137 => java/lang/Class
	i32 u0xe87e252b, ; 138 => java/io/InputStream
	i32 u0xe9334697, ; 139 => java/security/KeyStore
	i32 u0xea708f4b, ; 140 => android/graphics/Rect
	i32 u0xec180e14, ; 141 => android/widget/Toast
	i32 u0xeca128e8, ; 142 => mono/android/runtime/InputStreamAdapter
	i32 u0xecedb4bd, ; 143 => javax/net/ssl/SSLSocketFactory
	i32 u0xee05559f, ; 144 => android/content/ContextWrapper
	i32 u0xee250f69, ; 145 => android/graphics/Canvas
	i32 u0xefa10a0f, ; 146 => java/lang/Error
	i32 u0xf18125ef, ; 147 => android/content/Context
	i32 u0xf1cbf0e8, ; 148 => java/lang/NoClassDefFoundError
	i32 u0xf37de54d, ; 149 => android/view/SurfaceView
	i32 u0xf475d75a, ; 150 => java/io/Writer
	i32 u0xf570a2f1, ; 151 => android/view/SurfaceHolder$Callback
	i32 u0xf58117fa, ; 152 => android/os/Looper
	i32 u0xf60116c5, ; 153 => mono/android/widget/AdapterView_OnItemClickListenerImplementor
	i32 u0xf639ab34, ; 154 => java/security/KeyFactory
	i32 u0xf6d436d4, ; 155 => android/view/TextureView
	i32 u0xf74684cd, ; 156 => javax/net/ssl/TrustManagerFactory
	i32 u0xf76c86f8, ; 157 => org/videolan/libvlc/IVLCVout$Callback
	i32 u0xf7d32035, ; 158 => java/io/IOException
	i32 u0xfc49ff4f, ; 159 => java/util/HashSet
	i32 u0xfd70ba43 ; 160 => org/videolan/libvlc/IVLCVout$OnNewVideoLayoutListener
], align 4

@module0_managed_to_java = internal dso_local constant [149 x %struct.TypeMapModuleEntry] [
	%struct.TypeMapModuleEntry {
		i32 u0x02000063, ; uint32_t type_token_id
		i32 45; uint32_t java_map_index
	}, ; 0
	%struct.TypeMapModuleEntry {
		i32 u0x02000065, ; uint32_t type_token_id
		i32 8; uint32_t java_map_index
	}, ; 1
	%struct.TypeMapModuleEntry {
		i32 u0x02000067, ; uint32_t type_token_id
		i32 56; uint32_t java_map_index
	}, ; 2
	%struct.TypeMapModuleEntry {
		i32 u0x02000068, ; uint32_t type_token_id
		i32 131; uint32_t java_map_index
	}, ; 3
	%struct.TypeMapModuleEntry {
		i32 u0x0200006a, ; uint32_t type_token_id
		i32 120; uint32_t java_map_index
	}, ; 4
	%struct.TypeMapModuleEntry {
		i32 u0x0200006c, ; uint32_t type_token_id
		i32 98; uint32_t java_map_index
	}, ; 5
	%struct.TypeMapModuleEntry {
		i32 u0x0200006e, ; uint32_t type_token_id
		i32 51; uint32_t java_map_index
	}, ; 6
	%struct.TypeMapModuleEntry {
		i32 u0x02000070, ; uint32_t type_token_id
		i32 117; uint32_t java_map_index
	}, ; 7
	%struct.TypeMapModuleEntry {
		i32 u0x02000072, ; uint32_t type_token_id
		i32 4; uint32_t java_map_index
	}, ; 8
	%struct.TypeMapModuleEntry {
		i32 u0x02000074, ; uint32_t type_token_id
		i32 34; uint32_t java_map_index
	}, ; 9
	%struct.TypeMapModuleEntry {
		i32 u0x02000076, ; uint32_t type_token_id
		i32 41; uint32_t java_map_index
	}, ; 10
	%struct.TypeMapModuleEntry {
		i32 u0x02000077, ; uint32_t type_token_id
		i32 48; uint32_t java_map_index
	}, ; 11
	%struct.TypeMapModuleEntry {
		i32 u0x02000078, ; uint32_t type_token_id
		i32 143; uint32_t java_map_index
	}, ; 12
	%struct.TypeMapModuleEntry {
		i32 u0x0200007a, ; uint32_t type_token_id
		i32 156; uint32_t java_map_index
	}, ; 13
	%struct.TypeMapModuleEntry {
		i32 u0x0200007b, ; uint32_t type_token_id
		i32 39; uint32_t java_map_index
	}, ; 14
	%struct.TypeMapModuleEntry {
		i32 u0x0200007d, ; uint32_t type_token_id
		i32 125; uint32_t java_map_index
	}, ; 15
	%struct.TypeMapModuleEntry {
		i32 u0x02000080, ; uint32_t type_token_id
		i32 81; uint32_t java_map_index
	}, ; 16
	%struct.TypeMapModuleEntry {
		i32 u0x02000081, ; uint32_t type_token_id
		i32 77; uint32_t java_map_index
	}, ; 17
	%struct.TypeMapModuleEntry {
		i32 u0x02000084, ; uint32_t type_token_id
		i32 153; uint32_t java_map_index
	}, ; 18
	%struct.TypeMapModuleEntry {
		i32 u0x02000089, ; uint32_t type_token_id
		i32 96; uint32_t java_map_index
	}, ; 19
	%struct.TypeMapModuleEntry {
		i32 u0x0200008a, ; uint32_t type_token_id
		i32 100; uint32_t java_map_index
	}, ; 20
	%struct.TypeMapModuleEntry {
		i32 u0x0200008e, ; uint32_t type_token_id
		i32 67; uint32_t java_map_index
	}, ; 21
	%struct.TypeMapModuleEntry {
		i32 u0x0200008f, ; uint32_t type_token_id
		i32 79; uint32_t java_map_index
	}, ; 22
	%struct.TypeMapModuleEntry {
		i32 u0x02000090, ; uint32_t type_token_id
		i32 99; uint32_t java_map_index
	}, ; 23
	%struct.TypeMapModuleEntry {
		i32 u0x02000092, ; uint32_t type_token_id
		i32 1; uint32_t java_map_index
	}, ; 24
	%struct.TypeMapModuleEntry {
		i32 u0x02000094, ; uint32_t type_token_id
		i32 71; uint32_t java_map_index
	}, ; 25
	%struct.TypeMapModuleEntry {
		i32 u0x02000095, ; uint32_t type_token_id
		i32 54; uint32_t java_map_index
	}, ; 26
	%struct.TypeMapModuleEntry {
		i32 u0x02000096, ; uint32_t type_token_id
		i32 111; uint32_t java_map_index
	}, ; 27
	%struct.TypeMapModuleEntry {
		i32 u0x02000098, ; uint32_t type_token_id
		i32 141; uint32_t java_map_index
	}, ; 28
	%struct.TypeMapModuleEntry {
		i32 u0x0200009a, ; uint32_t type_token_id
		i32 109; uint32_t java_map_index
	}, ; 29
	%struct.TypeMapModuleEntry {
		i32 u0x0200009c, ; uint32_t type_token_id
		i32 60; uint32_t java_map_index
	}, ; 30
	%struct.TypeMapModuleEntry {
		i32 u0x0200009d, ; uint32_t type_token_id
		i32 69; uint32_t java_map_index
	}, ; 31
	%struct.TypeMapModuleEntry {
		i32 u0x0200009e, ; uint32_t type_token_id
		i32 103; uint32_t java_map_index
	}, ; 32
	%struct.TypeMapModuleEntry {
		i32 u0x0200009f, ; uint32_t type_token_id
		i32 130; uint32_t java_map_index
	}, ; 33
	%struct.TypeMapModuleEntry {
		i32 u0x020000a1, ; uint32_t type_token_id
		i32 22; uint32_t java_map_index
	}, ; 34
	%struct.TypeMapModuleEntry {
		i32 u0x020000a2, ; uint32_t type_token_id
		i32 152; uint32_t java_map_index
	}, ; 35
	%struct.TypeMapModuleEntry {
		i32 u0x020000a3, ; uint32_t type_token_id
		i32 62; uint32_t java_map_index
	}, ; 36
	%struct.TypeMapModuleEntry {
		i32 u0x020000a4, ; uint32_t type_token_id
		i32 91; uint32_t java_map_index
	}, ; 37
	%struct.TypeMapModuleEntry {
		i32 u0x020000a6, ; uint32_t type_token_id
		i32 5; uint32_t java_map_index
	}, ; 38
	%struct.TypeMapModuleEntry {
		i32 u0x020000a7, ; uint32_t type_token_id
		i32 80; uint32_t java_map_index
	}, ; 39
	%struct.TypeMapModuleEntry {
		i32 u0x020000a9, ; uint32_t type_token_id
		i32 108; uint32_t java_map_index
	}, ; 40
	%struct.TypeMapModuleEntry {
		i32 u0x020000ab, ; uint32_t type_token_id
		i32 151; uint32_t java_map_index
	}, ; 41
	%struct.TypeMapModuleEntry {
		i32 u0x020000ad, ; uint32_t type_token_id
		i32 107; uint32_t java_map_index
	}, ; 42
	%struct.TypeMapModuleEntry {
		i32 u0x020000b0, ; uint32_t type_token_id
		i32 115; uint32_t java_map_index
	}, ; 43
	%struct.TypeMapModuleEntry {
		i32 u0x020000b2, ; uint32_t type_token_id
		i32 149; uint32_t java_map_index
	}, ; 44
	%struct.TypeMapModuleEntry {
		i32 u0x020000b3, ; uint32_t type_token_id
		i32 155; uint32_t java_map_index
	}, ; 45
	%struct.TypeMapModuleEntry {
		i32 u0x020000b4, ; uint32_t type_token_id
		i32 3; uint32_t java_map_index
	}, ; 46
	%struct.TypeMapModuleEntry {
		i32 u0x020000b5, ; uint32_t type_token_id
		i32 11; uint32_t java_map_index
	}, ; 47
	%struct.TypeMapModuleEntry {
		i32 u0x020000b6, ; uint32_t type_token_id
		i32 17; uint32_t java_map_index
	}, ; 48
	%struct.TypeMapModuleEntry {
		i32 u0x020000cd, ; uint32_t type_token_id
		i32 142; uint32_t java_map_index
	}, ; 49
	%struct.TypeMapModuleEntry {
		i32 u0x020000cf, ; uint32_t type_token_id
		i32 112; uint32_t java_map_index
	}, ; 50
	%struct.TypeMapModuleEntry {
		i32 u0x020000d1, ; uint32_t type_token_id
		i32 49; uint32_t java_map_index
	}, ; 51
	%struct.TypeMapModuleEntry {
		i32 u0x020000d3, ; uint32_t type_token_id
		i32 55; uint32_t java_map_index
	}, ; 52
	%struct.TypeMapModuleEntry {
		i32 u0x020000dc, ; uint32_t type_token_id
		i32 26; uint32_t java_map_index
	}, ; 53
	%struct.TypeMapModuleEntry {
		i32 u0x020000de, ; uint32_t type_token_id
		i32 128; uint32_t java_map_index
	}, ; 54
	%struct.TypeMapModuleEntry {
		i32 u0x020000df, ; uint32_t type_token_id
		i32 124; uint32_t java_map_index
	}, ; 55
	%struct.TypeMapModuleEntry {
		i32 u0x020000e0, ; uint32_t type_token_id
		i32 159; uint32_t java_map_index
	}, ; 56
	%struct.TypeMapModuleEntry {
		i32 u0x020000ec, ; uint32_t type_token_id
		i32 19; uint32_t java_map_index
	}, ; 57
	%struct.TypeMapModuleEntry {
		i32 u0x020000f3, ; uint32_t type_token_id
		i32 145; uint32_t java_map_index
	}, ; 58
	%struct.TypeMapModuleEntry {
		i32 u0x020000f6, ; uint32_t type_token_id
		i32 37; uint32_t java_map_index
	}, ; 59
	%struct.TypeMapModuleEntry {
		i32 u0x020000f8, ; uint32_t type_token_id
		i32 140; uint32_t java_map_index
	}, ; 60
	%struct.TypeMapModuleEntry {
		i32 u0x020000f9, ; uint32_t type_token_id
		i32 119; uint32_t java_map_index
	}, ; 61
	%struct.TypeMapModuleEntry {
		i32 u0x020000fa, ; uint32_t type_token_id
		i32 101; uint32_t java_map_index
	}, ; 62
	%struct.TypeMapModuleEntry {
		i32 u0x020000fd, ; uint32_t type_token_id
		i32 147; uint32_t java_map_index
	}, ; 63
	%struct.TypeMapModuleEntry {
		i32 u0x020000fe, ; uint32_t type_token_id
		i32 78; uint32_t java_map_index
	}, ; 64
	%struct.TypeMapModuleEntry {
		i32 u0x02000100, ; uint32_t type_token_id
		i32 74; uint32_t java_map_index
	}, ; 65
	%struct.TypeMapModuleEntry {
		i32 u0x02000103, ; uint32_t type_token_id
		i32 144; uint32_t java_map_index
	}, ; 66
	%struct.TypeMapModuleEntry {
		i32 u0x02000105, ; uint32_t type_token_id
		i32 10; uint32_t java_map_index
	}, ; 67
	%struct.TypeMapModuleEntry {
		i32 u0x02000107, ; uint32_t type_token_id
		i32 94; uint32_t java_map_index
	}, ; 68
	%struct.TypeMapModuleEntry {
		i32 u0x02000109, ; uint32_t type_token_id
		i32 122; uint32_t java_map_index
	}, ; 69
	%struct.TypeMapModuleEntry {
		i32 u0x02000110, ; uint32_t type_token_id
		i32 75; uint32_t java_map_index
	}, ; 70
	%struct.TypeMapModuleEntry {
		i32 u0x02000111, ; uint32_t type_token_id
		i32 36; uint32_t java_map_index
	}, ; 71
	%struct.TypeMapModuleEntry {
		i32 u0x02000112, ; uint32_t type_token_id
		i32 84; uint32_t java_map_index
	}, ; 72
	%struct.TypeMapModuleEntry {
		i32 u0x02000113, ; uint32_t type_token_id
		i32 88; uint32_t java_map_index
	}, ; 73
	%struct.TypeMapModuleEntry {
		i32 u0x02000117, ; uint32_t type_token_id
		i32 15; uint32_t java_map_index
	}, ; 74
	%struct.TypeMapModuleEntry {
		i32 u0x02000119, ; uint32_t type_token_id
		i32 53; uint32_t java_map_index
	}, ; 75
	%struct.TypeMapModuleEntry {
		i32 u0x0200011b, ; uint32_t type_token_id
		i32 127; uint32_t java_map_index
	}, ; 76
	%struct.TypeMapModuleEntry {
		i32 u0x0200011c, ; uint32_t type_token_id
		i32 132; uint32_t java_map_index
	}, ; 77
	%struct.TypeMapModuleEntry {
		i32 u0x0200011d, ; uint32_t type_token_id
		i32 118; uint32_t java_map_index
	}, ; 78
	%struct.TypeMapModuleEntry {
		i32 u0x0200011e, ; uint32_t type_token_id
		i32 87; uint32_t java_map_index
	}, ; 79
	%struct.TypeMapModuleEntry {
		i32 u0x0200011f, ; uint32_t type_token_id
		i32 38; uint32_t java_map_index
	}, ; 80
	%struct.TypeMapModuleEntry {
		i32 u0x02000121, ; uint32_t type_token_id
		i32 93; uint32_t java_map_index
	}, ; 81
	%struct.TypeMapModuleEntry {
		i32 u0x02000122, ; uint32_t type_token_id
		i32 46; uint32_t java_map_index
	}, ; 82
	%struct.TypeMapModuleEntry {
		i32 u0x02000123, ; uint32_t type_token_id
		i32 86; uint32_t java_map_index
	}, ; 83
	%struct.TypeMapModuleEntry {
		i32 u0x02000124, ; uint32_t type_token_id
		i32 25; uint32_t java_map_index
	}, ; 84
	%struct.TypeMapModuleEntry {
		i32 u0x02000125, ; uint32_t type_token_id
		i32 123; uint32_t java_map_index
	}, ; 85
	%struct.TypeMapModuleEntry {
		i32 u0x02000127, ; uint32_t type_token_id
		i32 85; uint32_t java_map_index
	}, ; 86
	%struct.TypeMapModuleEntry {
		i32 u0x02000129, ; uint32_t type_token_id
		i32 61; uint32_t java_map_index
	}, ; 87
	%struct.TypeMapModuleEntry {
		i32 u0x0200012b, ; uint32_t type_token_id
		i32 58; uint32_t java_map_index
	}, ; 88
	%struct.TypeMapModuleEntry {
		i32 u0x0200012d, ; uint32_t type_token_id
		i32 154; uint32_t java_map_index
	}, ; 89
	%struct.TypeMapModuleEntry {
		i32 u0x0200012e, ; uint32_t type_token_id
		i32 139; uint32_t java_map_index
	}, ; 90
	%struct.TypeMapModuleEntry {
		i32 u0x0200012f, ; uint32_t type_token_id
		i32 83; uint32_t java_map_index
	}, ; 91
	%struct.TypeMapModuleEntry {
		i32 u0x02000130, ; uint32_t type_token_id
		i32 9; uint32_t java_map_index
	}, ; 92
	%struct.TypeMapModuleEntry {
		i32 u0x02000132, ; uint32_t type_token_id
		i32 134; uint32_t java_map_index
	}, ; 93
	%struct.TypeMapModuleEntry {
		i32 u0x02000134, ; uint32_t type_token_id
		i32 23; uint32_t java_map_index
	}, ; 94
	%struct.TypeMapModuleEntry {
		i32 u0x02000135, ; uint32_t type_token_id
		i32 2; uint32_t java_map_index
	}, ; 95
	%struct.TypeMapModuleEntry {
		i32 u0x02000137, ; uint32_t type_token_id
		i32 20; uint32_t java_map_index
	}, ; 96
	%struct.TypeMapModuleEntry {
		i32 u0x02000139, ; uint32_t type_token_id
		i32 57; uint32_t java_map_index
	}, ; 97
	%struct.TypeMapModuleEntry {
		i32 u0x0200013b, ; uint32_t type_token_id
		i32 59; uint32_t java_map_index
	}, ; 98
	%struct.TypeMapModuleEntry {
		i32 u0x0200013c, ; uint32_t type_token_id
		i32 95; uint32_t java_map_index
	}, ; 99
	%struct.TypeMapModuleEntry {
		i32 u0x0200013d, ; uint32_t type_token_id
		i32 138; uint32_t java_map_index
	}, ; 100
	%struct.TypeMapModuleEntry {
		i32 u0x0200013f, ; uint32_t type_token_id
		i32 97; uint32_t java_map_index
	}, ; 101
	%struct.TypeMapModuleEntry {
		i32 u0x02000140, ; uint32_t type_token_id
		i32 158; uint32_t java_map_index
	}, ; 102
	%struct.TypeMapModuleEntry {
		i32 u0x02000141, ; uint32_t type_token_id
		i32 24; uint32_t java_map_index
	}, ; 103
	%struct.TypeMapModuleEntry {
		i32 u0x02000143, ; uint32_t type_token_id
		i32 72; uint32_t java_map_index
	}, ; 104
	%struct.TypeMapModuleEntry {
		i32 u0x02000145, ; uint32_t type_token_id
		i32 28; uint32_t java_map_index
	}, ; 105
	%struct.TypeMapModuleEntry {
		i32 u0x02000146, ; uint32_t type_token_id
		i32 116; uint32_t java_map_index
	}, ; 106
	%struct.TypeMapModuleEntry {
		i32 u0x02000147, ; uint32_t type_token_id
		i32 150; uint32_t java_map_index
	}, ; 107
	%struct.TypeMapModuleEntry {
		i32 u0x02000149, ; uint32_t type_token_id
		i32 42; uint32_t java_map_index
	}, ; 108
	%struct.TypeMapModuleEntry {
		i32 u0x0200014b, ; uint32_t type_token_id
		i32 14; uint32_t java_map_index
	}, ; 109
	%struct.TypeMapModuleEntry {
		i32 u0x0200014d, ; uint32_t type_token_id
		i32 68; uint32_t java_map_index
	}, ; 110
	%struct.TypeMapModuleEntry {
		i32 u0x0200014e, ; uint32_t type_token_id
		i32 40; uint32_t java_map_index
	}, ; 111
	%struct.TypeMapModuleEntry {
		i32 u0x02000150, ; uint32_t type_token_id
		i32 76; uint32_t java_map_index
	}, ; 112
	%struct.TypeMapModuleEntry {
		i32 u0x02000151, ; uint32_t type_token_id
		i32 65; uint32_t java_map_index
	}, ; 113
	%struct.TypeMapModuleEntry {
		i32 u0x02000152, ; uint32_t type_token_id
		i32 66; uint32_t java_map_index
	}, ; 114
	%struct.TypeMapModuleEntry {
		i32 u0x02000153, ; uint32_t type_token_id
		i32 137; uint32_t java_map_index
	}, ; 115
	%struct.TypeMapModuleEntry {
		i32 u0x02000154, ; uint32_t type_token_id
		i32 44; uint32_t java_map_index
	}, ; 116
	%struct.TypeMapModuleEntry {
		i32 u0x02000155, ; uint32_t type_token_id
		i32 129; uint32_t java_map_index
	}, ; 117
	%struct.TypeMapModuleEntry {
		i32 u0x02000156, ; uint32_t type_token_id
		i32 110; uint32_t java_map_index
	}, ; 118
	%struct.TypeMapModuleEntry {
		i32 u0x02000157, ; uint32_t type_token_id
		i32 102; uint32_t java_map_index
	}, ; 119
	%struct.TypeMapModuleEntry {
		i32 u0x02000158, ; uint32_t type_token_id
		i32 27; uint32_t java_map_index
	}, ; 120
	%struct.TypeMapModuleEntry {
		i32 u0x02000159, ; uint32_t type_token_id
		i32 29; uint32_t java_map_index
	}, ; 121
	%struct.TypeMapModuleEntry {
		i32 u0x0200015a, ; uint32_t type_token_id
		i32 13; uint32_t java_map_index
	}, ; 122
	%struct.TypeMapModuleEntry {
		i32 u0x0200015b, ; uint32_t type_token_id
		i32 0; uint32_t java_map_index
	}, ; 123
	%struct.TypeMapModuleEntry {
		i32 u0x0200015c, ; uint32_t type_token_id
		i32 63; uint32_t java_map_index
	}, ; 124
	%struct.TypeMapModuleEntry {
		i32 u0x0200015d, ; uint32_t type_token_id
		i32 35; uint32_t java_map_index
	}, ; 125
	%struct.TypeMapModuleEntry {
		i32 u0x0200015e, ; uint32_t type_token_id
		i32 126; uint32_t java_map_index
	}, ; 126
	%struct.TypeMapModuleEntry {
		i32 u0x02000160, ; uint32_t type_token_id
		i32 30; uint32_t java_map_index
	}, ; 127
	%struct.TypeMapModuleEntry {
		i32 u0x02000161, ; uint32_t type_token_id
		i32 12; uint32_t java_map_index
	}, ; 128
	%struct.TypeMapModuleEntry {
		i32 u0x02000162, ; uint32_t type_token_id
		i32 82; uint32_t java_map_index
	}, ; 129
	%struct.TypeMapModuleEntry {
		i32 u0x02000164, ; uint32_t type_token_id
		i32 47; uint32_t java_map_index
	}, ; 130
	%struct.TypeMapModuleEntry {
		i32 u0x02000165, ; uint32_t type_token_id
		i32 50; uint32_t java_map_index
	}, ; 131
	%struct.TypeMapModuleEntry {
		i32 u0x02000166, ; uint32_t type_token_id
		i32 90; uint32_t java_map_index
	}, ; 132
	%struct.TypeMapModuleEntry {
		i32 u0x02000168, ; uint32_t type_token_id
		i32 146; uint32_t java_map_index
	}, ; 133
	%struct.TypeMapModuleEntry {
		i32 u0x0200016b, ; uint32_t type_token_id
		i32 6; uint32_t java_map_index
	}, ; 134
	%struct.TypeMapModuleEntry {
		i32 u0x0200016c, ; uint32_t type_token_id
		i32 21; uint32_t java_map_index
	}, ; 135
	%struct.TypeMapModuleEntry {
		i32 u0x0200016d, ; uint32_t type_token_id
		i32 64; uint32_t java_map_index
	}, ; 136
	%struct.TypeMapModuleEntry {
		i32 u0x0200016e, ; uint32_t type_token_id
		i32 70; uint32_t java_map_index
	}, ; 137
	%struct.TypeMapModuleEntry {
		i32 u0x02000170, ; uint32_t type_token_id
		i32 105; uint32_t java_map_index
	}, ; 138
	%struct.TypeMapModuleEntry {
		i32 u0x02000171, ; uint32_t type_token_id
		i32 16; uint32_t java_map_index
	}, ; 139
	%struct.TypeMapModuleEntry {
		i32 u0x02000172, ; uint32_t type_token_id
		i32 148; uint32_t java_map_index
	}, ; 140
	%struct.TypeMapModuleEntry {
		i32 u0x02000173, ; uint32_t type_token_id
		i32 32; uint32_t java_map_index
	}, ; 141
	%struct.TypeMapModuleEntry {
		i32 u0x02000174, ; uint32_t type_token_id
		i32 121; uint32_t java_map_index
	}, ; 142
	%struct.TypeMapModuleEntry {
		i32 u0x02000176, ; uint32_t type_token_id
		i32 7; uint32_t java_map_index
	}, ; 143
	%struct.TypeMapModuleEntry {
		i32 u0x02000177, ; uint32_t type_token_id
		i32 52; uint32_t java_map_index
	}, ; 144
	%struct.TypeMapModuleEntry {
		i32 u0x02000178, ; uint32_t type_token_id
		i32 106; uint32_t java_map_index
	}, ; 145
	%struct.TypeMapModuleEntry {
		i32 u0x02000179, ; uint32_t type_token_id
		i32 31; uint32_t java_map_index
	}, ; 146
	%struct.TypeMapModuleEntry {
		i32 u0x0200017a, ; uint32_t type_token_id
		i32 92; uint32_t java_map_index
	}, ; 147
	%struct.TypeMapModuleEntry {
		i32 u0x02000189, ; uint32_t type_token_id
		i32 113; uint32_t java_map_index
	} ; 148
], align 4

@module0_managed_to_java_duplicates = internal dso_local constant [58 x %struct.TypeMapModuleEntry] [
	%struct.TypeMapModuleEntry {
		i32 u0x02000064, ; uint32_t type_token_id
		i32 45; uint32_t java_map_index
	}, ; 0
	%struct.TypeMapModuleEntry {
		i32 u0x02000066, ; uint32_t type_token_id
		i32 8; uint32_t java_map_index
	}, ; 1
	%struct.TypeMapModuleEntry {
		i32 u0x02000069, ; uint32_t type_token_id
		i32 131; uint32_t java_map_index
	}, ; 2
	%struct.TypeMapModuleEntry {
		i32 u0x0200006b, ; uint32_t type_token_id
		i32 120; uint32_t java_map_index
	}, ; 3
	%struct.TypeMapModuleEntry {
		i32 u0x0200006d, ; uint32_t type_token_id
		i32 98; uint32_t java_map_index
	}, ; 4
	%struct.TypeMapModuleEntry {
		i32 u0x0200006f, ; uint32_t type_token_id
		i32 51; uint32_t java_map_index
	}, ; 5
	%struct.TypeMapModuleEntry {
		i32 u0x02000071, ; uint32_t type_token_id
		i32 117; uint32_t java_map_index
	}, ; 6
	%struct.TypeMapModuleEntry {
		i32 u0x02000073, ; uint32_t type_token_id
		i32 4; uint32_t java_map_index
	}, ; 7
	%struct.TypeMapModuleEntry {
		i32 u0x02000075, ; uint32_t type_token_id
		i32 34; uint32_t java_map_index
	}, ; 8
	%struct.TypeMapModuleEntry {
		i32 u0x02000079, ; uint32_t type_token_id
		i32 143; uint32_t java_map_index
	}, ; 9
	%struct.TypeMapModuleEntry {
		i32 u0x0200007c, ; uint32_t type_token_id
		i32 39; uint32_t java_map_index
	}, ; 10
	%struct.TypeMapModuleEntry {
		i32 u0x0200007f, ; uint32_t type_token_id
		i32 125; uint32_t java_map_index
	}, ; 11
	%struct.TypeMapModuleEntry {
		i32 u0x02000082, ; uint32_t type_token_id
		i32 77; uint32_t java_map_index
	}, ; 12
	%struct.TypeMapModuleEntry {
		i32 u0x02000088, ; uint32_t type_token_id
		i32 81; uint32_t java_map_index
	}, ; 13
	%struct.TypeMapModuleEntry {
		i32 u0x0200008b, ; uint32_t type_token_id
		i32 81; uint32_t java_map_index
	}, ; 14
	%struct.TypeMapModuleEntry {
		i32 u0x0200008c, ; uint32_t type_token_id
		i32 96; uint32_t java_map_index
	}, ; 15
	%struct.TypeMapModuleEntry {
		i32 u0x0200008d, ; uint32_t type_token_id
		i32 96; uint32_t java_map_index
	}, ; 16
	%struct.TypeMapModuleEntry {
		i32 u0x02000091, ; uint32_t type_token_id
		i32 99; uint32_t java_map_index
	}, ; 17
	%struct.TypeMapModuleEntry {
		i32 u0x02000093, ; uint32_t type_token_id
		i32 1; uint32_t java_map_index
	}, ; 18
	%struct.TypeMapModuleEntry {
		i32 u0x0200009b, ; uint32_t type_token_id
		i32 109; uint32_t java_map_index
	}, ; 19
	%struct.TypeMapModuleEntry {
		i32 u0x020000a5, ; uint32_t type_token_id
		i32 91; uint32_t java_map_index
	}, ; 20
	%struct.TypeMapModuleEntry {
		i32 u0x020000aa, ; uint32_t type_token_id
		i32 108; uint32_t java_map_index
	}, ; 21
	%struct.TypeMapModuleEntry {
		i32 u0x020000ac, ; uint32_t type_token_id
		i32 151; uint32_t java_map_index
	}, ; 22
	%struct.TypeMapModuleEntry {
		i32 u0x020000ae, ; uint32_t type_token_id
		i32 107; uint32_t java_map_index
	}, ; 23
	%struct.TypeMapModuleEntry {
		i32 u0x020000b7, ; uint32_t type_token_id
		i32 3; uint32_t java_map_index
	}, ; 24
	%struct.TypeMapModuleEntry {
		i32 u0x020000d2, ; uint32_t type_token_id
		i32 49; uint32_t java_map_index
	}, ; 25
	%struct.TypeMapModuleEntry {
		i32 u0x020000d8, ; uint32_t type_token_id
		i32 55; uint32_t java_map_index
	}, ; 26
	%struct.TypeMapModuleEntry {
		i32 u0x020000dd, ; uint32_t type_token_id
		i32 26; uint32_t java_map_index
	}, ; 27
	%struct.TypeMapModuleEntry {
		i32 u0x020000e1, ; uint32_t type_token_id
		i32 159; uint32_t java_map_index
	}, ; 28
	%struct.TypeMapModuleEntry {
		i32 u0x020000fb, ; uint32_t type_token_id
		i32 101; uint32_t java_map_index
	}, ; 29
	%struct.TypeMapModuleEntry {
		i32 u0x02000101, ; uint32_t type_token_id
		i32 74; uint32_t java_map_index
	}, ; 30
	%struct.TypeMapModuleEntry {
		i32 u0x02000102, ; uint32_t type_token_id
		i32 147; uint32_t java_map_index
	}, ; 31
	%struct.TypeMapModuleEntry {
		i32 u0x02000106, ; uint32_t type_token_id
		i32 10; uint32_t java_map_index
	}, ; 32
	%struct.TypeMapModuleEntry {
		i32 u0x02000108, ; uint32_t type_token_id
		i32 94; uint32_t java_map_index
	}, ; 33
	%struct.TypeMapModuleEntry {
		i32 u0x0200010a, ; uint32_t type_token_id
		i32 122; uint32_t java_map_index
	}, ; 34
	%struct.TypeMapModuleEntry {
		i32 u0x0200011a, ; uint32_t type_token_id
		i32 53; uint32_t java_map_index
	}, ; 35
	%struct.TypeMapModuleEntry {
		i32 u0x02000120, ; uint32_t type_token_id
		i32 38; uint32_t java_map_index
	}, ; 36
	%struct.TypeMapModuleEntry {
		i32 u0x02000126, ; uint32_t type_token_id
		i32 123; uint32_t java_map_index
	}, ; 37
	%struct.TypeMapModuleEntry {
		i32 u0x02000128, ; uint32_t type_token_id
		i32 85; uint32_t java_map_index
	}, ; 38
	%struct.TypeMapModuleEntry {
		i32 u0x0200012a, ; uint32_t type_token_id
		i32 61; uint32_t java_map_index
	}, ; 39
	%struct.TypeMapModuleEntry {
		i32 u0x0200012c, ; uint32_t type_token_id
		i32 58; uint32_t java_map_index
	}, ; 40
	%struct.TypeMapModuleEntry {
		i32 u0x02000131, ; uint32_t type_token_id
		i32 9; uint32_t java_map_index
	}, ; 41
	%struct.TypeMapModuleEntry {
		i32 u0x02000133, ; uint32_t type_token_id
		i32 134; uint32_t java_map_index
	}, ; 42
	%struct.TypeMapModuleEntry {
		i32 u0x02000136, ; uint32_t type_token_id
		i32 2; uint32_t java_map_index
	}, ; 43
	%struct.TypeMapModuleEntry {
		i32 u0x02000138, ; uint32_t type_token_id
		i32 20; uint32_t java_map_index
	}, ; 44
	%struct.TypeMapModuleEntry {
		i32 u0x0200013a, ; uint32_t type_token_id
		i32 57; uint32_t java_map_index
	}, ; 45
	%struct.TypeMapModuleEntry {
		i32 u0x0200013e, ; uint32_t type_token_id
		i32 138; uint32_t java_map_index
	}, ; 46
	%struct.TypeMapModuleEntry {
		i32 u0x02000142, ; uint32_t type_token_id
		i32 24; uint32_t java_map_index
	}, ; 47
	%struct.TypeMapModuleEntry {
		i32 u0x02000144, ; uint32_t type_token_id
		i32 72; uint32_t java_map_index
	}, ; 48
	%struct.TypeMapModuleEntry {
		i32 u0x02000148, ; uint32_t type_token_id
		i32 150; uint32_t java_map_index
	}, ; 49
	%struct.TypeMapModuleEntry {
		i32 u0x0200014a, ; uint32_t type_token_id
		i32 42; uint32_t java_map_index
	}, ; 50
	%struct.TypeMapModuleEntry {
		i32 u0x0200014c, ; uint32_t type_token_id
		i32 14; uint32_t java_map_index
	}, ; 51
	%struct.TypeMapModuleEntry {
		i32 u0x0200014f, ; uint32_t type_token_id
		i32 40; uint32_t java_map_index
	}, ; 52
	%struct.TypeMapModuleEntry {
		i32 u0x02000163, ; uint32_t type_token_id
		i32 82; uint32_t java_map_index
	}, ; 53
	%struct.TypeMapModuleEntry {
		i32 u0x02000167, ; uint32_t type_token_id
		i32 90; uint32_t java_map_index
	}, ; 54
	%struct.TypeMapModuleEntry {
		i32 u0x02000169, ; uint32_t type_token_id
		i32 27; uint32_t java_map_index
	}, ; 55
	%struct.TypeMapModuleEntry {
		i32 u0x0200016f, ; uint32_t type_token_id
		i32 70; uint32_t java_map_index
	}, ; 56
	%struct.TypeMapModuleEntry {
		i32 u0x02000175, ; uint32_t type_token_id
		i32 121; uint32_t java_map_index
	} ; 57
], align 4

@module1_managed_to_java = internal dso_local constant [6 x %struct.TypeMapModuleEntry] [
	%struct.TypeMapModuleEntry {
		i32 u0x02000002, ; uint32_t type_token_id
		i32 89; uint32_t java_map_index
	}, ; 0
	%struct.TypeMapModuleEntry {
		i32 u0x02000004, ; uint32_t type_token_id
		i32 135; uint32_t java_map_index
	}, ; 1
	%struct.TypeMapModuleEntry {
		i32 u0x02000008, ; uint32_t type_token_id
		i32 33; uint32_t java_map_index
	}, ; 2
	%struct.TypeMapModuleEntry {
		i32 u0x02000009, ; uint32_t type_token_id
		i32 43; uint32_t java_map_index
	}, ; 3
	%struct.TypeMapModuleEntry {
		i32 u0x0200000a, ; uint32_t type_token_id
		i32 104; uint32_t java_map_index
	}, ; 4
	%struct.TypeMapModuleEntry {
		i32 u0x0200000e, ; uint32_t type_token_id
		i32 18; uint32_t java_map_index
	} ; 5
], align 4

@module2_managed_to_java = internal dso_local constant [5 x %struct.TypeMapModuleEntry] [
	%struct.TypeMapModuleEntry {
		i32 u0x02000009, ; uint32_t type_token_id
		i32 114; uint32_t java_map_index
	}, ; 0
	%struct.TypeMapModuleEntry {
		i32 u0x0200000a, ; uint32_t type_token_id
		i32 73; uint32_t java_map_index
	}, ; 1
	%struct.TypeMapModuleEntry {
		i32 u0x0200000c, ; uint32_t type_token_id
		i32 133; uint32_t java_map_index
	}, ; 2
	%struct.TypeMapModuleEntry {
		i32 u0x0200000d, ; uint32_t type_token_id
		i32 157; uint32_t java_map_index
	}, ; 3
	%struct.TypeMapModuleEntry {
		i32 u0x0200000f, ; uint32_t type_token_id
		i32 160; uint32_t java_map_index
	} ; 4
], align 4

@module2_managed_to_java_duplicates = internal dso_local constant [4 x %struct.TypeMapModuleEntry] [
	%struct.TypeMapModuleEntry {
		i32 u0x0200000b, ; uint32_t type_token_id
		i32 73; uint32_t java_map_index
	}, ; 0
	%struct.TypeMapModuleEntry {
		i32 u0x0200000e, ; uint32_t type_token_id
		i32 157; uint32_t java_map_index
	}, ; 1
	%struct.TypeMapModuleEntry {
		i32 u0x02000010, ; uint32_t type_token_id
		i32 160; uint32_t java_map_index
	}, ; 2
	%struct.TypeMapModuleEntry {
		i32 u0x02000011, ; uint32_t type_token_id
		i32 133; uint32_t java_map_index
	} ; 3
], align 4

@module3_managed_to_java = internal dso_local constant [1 x %struct.TypeMapModuleEntry] [
	%struct.TypeMapModuleEntry {
		i32 u0x02000002, ; uint32_t type_token_id
		i32 136; uint32_t java_map_index
	} ; 0
], align 4

; Java to managed map
@map_java = dso_local local_unnamed_addr constant [161 x %struct.TypeMapJava] [
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200015b, ; uint32_t type_token_id
		i32 123; uint32_t java_name_index
	}, ; 0
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 24; uint32_t java_name_index
	}, ; 1
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000135, ; uint32_t type_token_id
		i32 95; uint32_t java_name_index
	}, ; 2
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000b4, ; uint32_t type_token_id
		i32 46; uint32_t java_name_index
	}, ; 3
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 8; uint32_t java_name_index
	}, ; 4
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000a6, ; uint32_t type_token_id
		i32 38; uint32_t java_name_index
	}, ; 5
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200016b, ; uint32_t type_token_id
		i32 134; uint32_t java_name_index
	}, ; 6
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000176, ; uint32_t type_token_id
		i32 143; uint32_t java_name_index
	}, ; 7
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000065, ; uint32_t type_token_id
		i32 1; uint32_t java_name_index
	}, ; 8
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000130, ; uint32_t type_token_id
		i32 92; uint32_t java_name_index
	}, ; 9
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 67; uint32_t java_name_index
	}, ; 10
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000b5, ; uint32_t type_token_id
		i32 47; uint32_t java_name_index
	}, ; 11
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000161, ; uint32_t type_token_id
		i32 128; uint32_t java_name_index
	}, ; 12
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200015a, ; uint32_t type_token_id
		i32 122; uint32_t java_name_index
	}, ; 13
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 109; uint32_t java_name_index
	}, ; 14
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000117, ; uint32_t type_token_id
		i32 74; uint32_t java_name_index
	}, ; 15
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000171, ; uint32_t type_token_id
		i32 139; uint32_t java_name_index
	}, ; 16
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000b6, ; uint32_t type_token_id
		i32 48; uint32_t java_name_index
	}, ; 17
	%struct.TypeMapJava {
		i32 1, ; uint32_t module_index
		i32 u0x0200000e, ; uint32_t type_token_id
		i32 151; uint32_t java_name_index
	}, ; 18
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000ec, ; uint32_t type_token_id
		i32 57; uint32_t java_name_index
	}, ; 19
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000137, ; uint32_t type_token_id
		i32 96; uint32_t java_name_index
	}, ; 20
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200016c, ; uint32_t type_token_id
		i32 135; uint32_t java_name_index
	}, ; 21
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000a1, ; uint32_t type_token_id
		i32 34; uint32_t java_name_index
	}, ; 22
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000134, ; uint32_t type_token_id
		i32 94; uint32_t java_name_index
	}, ; 23
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 103; uint32_t java_name_index
	}, ; 24
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000124, ; uint32_t type_token_id
		i32 84; uint32_t java_name_index
	}, ; 25
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000dc, ; uint32_t type_token_id
		i32 53; uint32_t java_name_index
	}, ; 26
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 120; uint32_t java_name_index
	}, ; 27
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000145, ; uint32_t type_token_id
		i32 105; uint32_t java_name_index
	}, ; 28
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000159, ; uint32_t type_token_id
		i32 121; uint32_t java_name_index
	}, ; 29
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000160, ; uint32_t type_token_id
		i32 127; uint32_t java_name_index
	}, ; 30
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000179, ; uint32_t type_token_id
		i32 146; uint32_t java_name_index
	}, ; 31
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000173, ; uint32_t type_token_id
		i32 141; uint32_t java_name_index
	}, ; 32
	%struct.TypeMapJava {
		i32 1, ; uint32_t module_index
		i32 u0x02000008, ; uint32_t type_token_id
		i32 152; uint32_t java_name_index
	}, ; 33
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 9; uint32_t java_name_index
	}, ; 34
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200015d, ; uint32_t type_token_id
		i32 125; uint32_t java_name_index
	}, ; 35
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000111, ; uint32_t type_token_id
		i32 71; uint32_t java_name_index
	}, ; 36
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000f6, ; uint32_t type_token_id
		i32 59; uint32_t java_name_index
	}, ; 37
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200011f, ; uint32_t type_token_id
		i32 80; uint32_t java_name_index
	}, ; 38
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200007b, ; uint32_t type_token_id
		i32 14; uint32_t java_name_index
	}, ; 39
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 111; uint32_t java_name_index
	}, ; 40
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000076, ; uint32_t type_token_id
		i32 10; uint32_t java_name_index
	}, ; 41
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 108; uint32_t java_name_index
	}, ; 42
	%struct.TypeMapJava {
		i32 1, ; uint32_t module_index
		i32 u0x02000009, ; uint32_t type_token_id
		i32 153; uint32_t java_name_index
	}, ; 43
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000154, ; uint32_t type_token_id
		i32 116; uint32_t java_name_index
	}, ; 44
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000063, ; uint32_t type_token_id
		i32 0; uint32_t java_name_index
	}, ; 45
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000122, ; uint32_t type_token_id
		i32 82; uint32_t java_name_index
	}, ; 46
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000164, ; uint32_t type_token_id
		i32 130; uint32_t java_name_index
	}, ; 47
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000077, ; uint32_t type_token_id
		i32 11; uint32_t java_name_index
	}, ; 48
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000d1, ; uint32_t type_token_id
		i32 51; uint32_t java_name_index
	}, ; 49
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000165, ; uint32_t type_token_id
		i32 131; uint32_t java_name_index
	}, ; 50
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 6; uint32_t java_name_index
	}, ; 51
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000177, ; uint32_t type_token_id
		i32 144; uint32_t java_name_index
	}, ; 52
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000119, ; uint32_t type_token_id
		i32 75; uint32_t java_name_index
	}, ; 53
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000095, ; uint32_t type_token_id
		i32 26; uint32_t java_name_index
	}, ; 54
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000d3, ; uint32_t type_token_id
		i32 52; uint32_t java_name_index
	}, ; 55
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000067, ; uint32_t type_token_id
		i32 2; uint32_t java_name_index
	}, ; 56
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000139, ; uint32_t type_token_id
		i32 97; uint32_t java_name_index
	}, ; 57
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 88; uint32_t java_name_index
	}, ; 58
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200013b, ; uint32_t type_token_id
		i32 98; uint32_t java_name_index
	}, ; 59
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200009c, ; uint32_t type_token_id
		i32 30; uint32_t java_name_index
	}, ; 60
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 87; uint32_t java_name_index
	}, ; 61
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000a3, ; uint32_t type_token_id
		i32 36; uint32_t java_name_index
	}, ; 62
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200015c, ; uint32_t type_token_id
		i32 124; uint32_t java_name_index
	}, ; 63
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200016d, ; uint32_t type_token_id
		i32 136; uint32_t java_name_index
	}, ; 64
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000151, ; uint32_t type_token_id
		i32 113; uint32_t java_name_index
	}, ; 65
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000152, ; uint32_t type_token_id
		i32 114; uint32_t java_name_index
	}, ; 66
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200008e, ; uint32_t type_token_id
		i32 21; uint32_t java_name_index
	}, ; 67
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200014d, ; uint32_t type_token_id
		i32 110; uint32_t java_name_index
	}, ; 68
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200009d, ; uint32_t type_token_id
		i32 31; uint32_t java_name_index
	}, ; 69
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 137; uint32_t java_name_index
	}, ; 70
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000094, ; uint32_t type_token_id
		i32 25; uint32_t java_name_index
	}, ; 71
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000143, ; uint32_t type_token_id
		i32 104; uint32_t java_name_index
	}, ; 72
	%struct.TypeMapJava {
		i32 2, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 156; uint32_t java_name_index
	}, ; 73
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000100, ; uint32_t type_token_id
		i32 65; uint32_t java_name_index
	}, ; 74
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000110, ; uint32_t type_token_id
		i32 70; uint32_t java_name_index
	}, ; 75
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000150, ; uint32_t type_token_id
		i32 112; uint32_t java_name_index
	}, ; 76
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 17; uint32_t java_name_index
	}, ; 77
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000fe, ; uint32_t type_token_id
		i32 64; uint32_t java_name_index
	}, ; 78
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200008f, ; uint32_t type_token_id
		i32 22; uint32_t java_name_index
	}, ; 79
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000a7, ; uint32_t type_token_id
		i32 39; uint32_t java_name_index
	}, ; 80
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000080, ; uint32_t type_token_id
		i32 16; uint32_t java_name_index
	}, ; 81
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 129; uint32_t java_name_index
	}, ; 82
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200012f, ; uint32_t type_token_id
		i32 91; uint32_t java_name_index
	}, ; 83
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000112, ; uint32_t type_token_id
		i32 72; uint32_t java_name_index
	}, ; 84
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 86; uint32_t java_name_index
	}, ; 85
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000123, ; uint32_t type_token_id
		i32 83; uint32_t java_name_index
	}, ; 86
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200011e, ; uint32_t type_token_id
		i32 79; uint32_t java_name_index
	}, ; 87
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000113, ; uint32_t type_token_id
		i32 73; uint32_t java_name_index
	}, ; 88
	%struct.TypeMapJava {
		i32 1, ; uint32_t module_index
		i32 u0x02000002, ; uint32_t type_token_id
		i32 149; uint32_t java_name_index
	}, ; 89
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000166, ; uint32_t type_token_id
		i32 132; uint32_t java_name_index
	}, ; 90
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 37; uint32_t java_name_index
	}, ; 91
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200017a, ; uint32_t type_token_id
		i32 147; uint32_t java_name_index
	}, ; 92
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000121, ; uint32_t type_token_id
		i32 81; uint32_t java_name_index
	}, ; 93
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 68; uint32_t java_name_index
	}, ; 94
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200013c, ; uint32_t type_token_id
		i32 99; uint32_t java_name_index
	}, ; 95
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 19; uint32_t java_name_index
	}, ; 96
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200013f, ; uint32_t type_token_id
		i32 101; uint32_t java_name_index
	}, ; 97
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 5; uint32_t java_name_index
	}, ; 98
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 23; uint32_t java_name_index
	}, ; 99
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200008a, ; uint32_t type_token_id
		i32 20; uint32_t java_name_index
	}, ; 100
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000fa, ; uint32_t type_token_id
		i32 62; uint32_t java_name_index
	}, ; 101
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000157, ; uint32_t type_token_id
		i32 119; uint32_t java_name_index
	}, ; 102
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200009e, ; uint32_t type_token_id
		i32 32; uint32_t java_name_index
	}, ; 103
	%struct.TypeMapJava {
		i32 1, ; uint32_t module_index
		i32 u0x0200000a, ; uint32_t type_token_id
		i32 154; uint32_t java_name_index
	}, ; 104
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000170, ; uint32_t type_token_id
		i32 138; uint32_t java_name_index
	}, ; 105
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000178, ; uint32_t type_token_id
		i32 145; uint32_t java_name_index
	}, ; 106
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 42; uint32_t java_name_index
	}, ; 107
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000a9, ; uint32_t type_token_id
		i32 40; uint32_t java_name_index
	}, ; 108
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 29; uint32_t java_name_index
	}, ; 109
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000156, ; uint32_t type_token_id
		i32 118; uint32_t java_name_index
	}, ; 110
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000096, ; uint32_t type_token_id
		i32 27; uint32_t java_name_index
	}, ; 111
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 50; uint32_t java_name_index
	}, ; 112
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000189, ; uint32_t type_token_id
		i32 148; uint32_t java_name_index
	}, ; 113
	%struct.TypeMapJava {
		i32 2, ; uint32_t module_index
		i32 u0x02000009, ; uint32_t type_token_id
		i32 155; uint32_t java_name_index
	}, ; 114
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000b0, ; uint32_t type_token_id
		i32 43; uint32_t java_name_index
	}, ; 115
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000146, ; uint32_t type_token_id
		i32 106; uint32_t java_name_index
	}, ; 116
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 7; uint32_t java_name_index
	}, ; 117
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200011d, ; uint32_t type_token_id
		i32 78; uint32_t java_name_index
	}, ; 118
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000f9, ; uint32_t type_token_id
		i32 61; uint32_t java_name_index
	}, ; 119
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200006a, ; uint32_t type_token_id
		i32 4; uint32_t java_name_index
	}, ; 120
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000174, ; uint32_t type_token_id
		i32 142; uint32_t java_name_index
	}, ; 121
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 69; uint32_t java_name_index
	}, ; 122
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000125, ; uint32_t type_token_id
		i32 85; uint32_t java_name_index
	}, ; 123
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000df, ; uint32_t type_token_id
		i32 55; uint32_t java_name_index
	}, ; 124
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200007d, ; uint32_t type_token_id
		i32 15; uint32_t java_name_index
	}, ; 125
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200015e, ; uint32_t type_token_id
		i32 126; uint32_t java_name_index
	}, ; 126
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200011b, ; uint32_t type_token_id
		i32 76; uint32_t java_name_index
	}, ; 127
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000de, ; uint32_t type_token_id
		i32 54; uint32_t java_name_index
	}, ; 128
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000155, ; uint32_t type_token_id
		i32 117; uint32_t java_name_index
	}, ; 129
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200009f, ; uint32_t type_token_id
		i32 33; uint32_t java_name_index
	}, ; 130
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000068, ; uint32_t type_token_id
		i32 3; uint32_t java_name_index
	}, ; 131
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200011c, ; uint32_t type_token_id
		i32 77; uint32_t java_name_index
	}, ; 132
	%struct.TypeMapJava {
		i32 2, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 157; uint32_t java_name_index
	}, ; 133
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 93; uint32_t java_name_index
	}, ; 134
	%struct.TypeMapJava {
		i32 1, ; uint32_t module_index
		i32 u0x02000004, ; uint32_t type_token_id
		i32 150; uint32_t java_name_index
	}, ; 135
	%struct.TypeMapJava {
		i32 3, ; uint32_t module_index
		i32 u0x02000002, ; uint32_t type_token_id
		i32 160; uint32_t java_name_index
	}, ; 136
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000153, ; uint32_t type_token_id
		i32 115; uint32_t java_name_index
	}, ; 137
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200013d, ; uint32_t type_token_id
		i32 100; uint32_t java_name_index
	}, ; 138
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200012e, ; uint32_t type_token_id
		i32 90; uint32_t java_name_index
	}, ; 139
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000f8, ; uint32_t type_token_id
		i32 60; uint32_t java_name_index
	}, ; 140
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000098, ; uint32_t type_token_id
		i32 28; uint32_t java_name_index
	}, ; 141
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000cd, ; uint32_t type_token_id
		i32 49; uint32_t java_name_index
	}, ; 142
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000078, ; uint32_t type_token_id
		i32 12; uint32_t java_name_index
	}, ; 143
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000103, ; uint32_t type_token_id
		i32 66; uint32_t java_name_index
	}, ; 144
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000f3, ; uint32_t type_token_id
		i32 58; uint32_t java_name_index
	}, ; 145
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000168, ; uint32_t type_token_id
		i32 133; uint32_t java_name_index
	}, ; 146
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000fd, ; uint32_t type_token_id
		i32 63; uint32_t java_name_index
	}, ; 147
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000172, ; uint32_t type_token_id
		i32 140; uint32_t java_name_index
	}, ; 148
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000b2, ; uint32_t type_token_id
		i32 44; uint32_t java_name_index
	}, ; 149
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000147, ; uint32_t type_token_id
		i32 107; uint32_t java_name_index
	}, ; 150
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 41; uint32_t java_name_index
	}, ; 151
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000a2, ; uint32_t type_token_id
		i32 35; uint32_t java_name_index
	}, ; 152
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000084, ; uint32_t type_token_id
		i32 18; uint32_t java_name_index
	}, ; 153
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200012d, ; uint32_t type_token_id
		i32 89; uint32_t java_name_index
	}, ; 154
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000b3, ; uint32_t type_token_id
		i32 45; uint32_t java_name_index
	}, ; 155
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x0200007a, ; uint32_t type_token_id
		i32 13; uint32_t java_name_index
	}, ; 156
	%struct.TypeMapJava {
		i32 2, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 158; uint32_t java_name_index
	}, ; 157
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x02000140, ; uint32_t type_token_id
		i32 102; uint32_t java_name_index
	}, ; 158
	%struct.TypeMapJava {
		i32 0, ; uint32_t module_index
		i32 u0x020000e0, ; uint32_t type_token_id
		i32 56; uint32_t java_name_index
	}, ; 159
	%struct.TypeMapJava {
		i32 2, ; uint32_t module_index
		i32 u0x00000000, ; uint32_t type_token_id
		i32 159; uint32_t java_name_index
	} ; 160
], align 4

; Java type names
@java_type_names = dso_local local_unnamed_addr constant [161 x ptr] [
	ptr @.str.0, ; 0
	ptr @.str.1, ; 1
	ptr @.str.2, ; 2
	ptr @.str.3, ; 3
	ptr @.str.4, ; 4
	ptr @.str.5, ; 5
	ptr @.str.6, ; 6
	ptr @.str.7, ; 7
	ptr @.str.8, ; 8
	ptr @.str.9, ; 9
	ptr @.str.10, ; 10
	ptr @.str.11, ; 11
	ptr @.str.12, ; 12
	ptr @.str.13, ; 13
	ptr @.str.14, ; 14
	ptr @.str.15, ; 15
	ptr @.str.16, ; 16
	ptr @.str.17, ; 17
	ptr @.str.18, ; 18
	ptr @.str.19, ; 19
	ptr @.str.20, ; 20
	ptr @.str.21, ; 21
	ptr @.str.22, ; 22
	ptr @.str.23, ; 23
	ptr @.str.24, ; 24
	ptr @.str.25, ; 25
	ptr @.str.26, ; 26
	ptr @.str.27, ; 27
	ptr @.str.28, ; 28
	ptr @.str.29, ; 29
	ptr @.str.30, ; 30
	ptr @.str.31, ; 31
	ptr @.str.32, ; 32
	ptr @.str.33, ; 33
	ptr @.str.34, ; 34
	ptr @.str.35, ; 35
	ptr @.str.36, ; 36
	ptr @.str.37, ; 37
	ptr @.str.38, ; 38
	ptr @.str.39, ; 39
	ptr @.str.40, ; 40
	ptr @.str.41, ; 41
	ptr @.str.42, ; 42
	ptr @.str.43, ; 43
	ptr @.str.44, ; 44
	ptr @.str.45, ; 45
	ptr @.str.46, ; 46
	ptr @.str.47, ; 47
	ptr @.str.48, ; 48
	ptr @.str.49, ; 49
	ptr @.str.50, ; 50
	ptr @.str.51, ; 51
	ptr @.str.52, ; 52
	ptr @.str.53, ; 53
	ptr @.str.54, ; 54
	ptr @.str.55, ; 55
	ptr @.str.56, ; 56
	ptr @.str.57, ; 57
	ptr @.str.58, ; 58
	ptr @.str.59, ; 59
	ptr @.str.60, ; 60
	ptr @.str.61, ; 61
	ptr @.str.62, ; 62
	ptr @.str.63, ; 63
	ptr @.str.64, ; 64
	ptr @.str.65, ; 65
	ptr @.str.66, ; 66
	ptr @.str.67, ; 67
	ptr @.str.68, ; 68
	ptr @.str.69, ; 69
	ptr @.str.70, ; 70
	ptr @.str.71, ; 71
	ptr @.str.72, ; 72
	ptr @.str.73, ; 73
	ptr @.str.74, ; 74
	ptr @.str.75, ; 75
	ptr @.str.76, ; 76
	ptr @.str.77, ; 77
	ptr @.str.78, ; 78
	ptr @.str.79, ; 79
	ptr @.str.80, ; 80
	ptr @.str.81, ; 81
	ptr @.str.82, ; 82
	ptr @.str.83, ; 83
	ptr @.str.84, ; 84
	ptr @.str.85, ; 85
	ptr @.str.86, ; 86
	ptr @.str.87, ; 87
	ptr @.str.88, ; 88
	ptr @.str.89, ; 89
	ptr @.str.90, ; 90
	ptr @.str.91, ; 91
	ptr @.str.92, ; 92
	ptr @.str.93, ; 93
	ptr @.str.94, ; 94
	ptr @.str.95, ; 95
	ptr @.str.96, ; 96
	ptr @.str.97, ; 97
	ptr @.str.98, ; 98
	ptr @.str.99, ; 99
	ptr @.str.100, ; 100
	ptr @.str.101, ; 101
	ptr @.str.102, ; 102
	ptr @.str.103, ; 103
	ptr @.str.104, ; 104
	ptr @.str.105, ; 105
	ptr @.str.106, ; 106
	ptr @.str.107, ; 107
	ptr @.str.108, ; 108
	ptr @.str.109, ; 109
	ptr @.str.110, ; 110
	ptr @.str.111, ; 111
	ptr @.str.112, ; 112
	ptr @.str.113, ; 113
	ptr @.str.114, ; 114
	ptr @.str.115, ; 115
	ptr @.str.116, ; 116
	ptr @.str.117, ; 117
	ptr @.str.118, ; 118
	ptr @.str.119, ; 119
	ptr @.str.120, ; 120
	ptr @.str.121, ; 121
	ptr @.str.122, ; 122
	ptr @.str.123, ; 123
	ptr @.str.124, ; 124
	ptr @.str.125, ; 125
	ptr @.str.126, ; 126
	ptr @.str.127, ; 127
	ptr @.str.128, ; 128
	ptr @.str.129, ; 129
	ptr @.str.130, ; 130
	ptr @.str.131, ; 131
	ptr @.str.132, ; 132
	ptr @.str.133, ; 133
	ptr @.str.134, ; 134
	ptr @.str.135, ; 135
	ptr @.str.136, ; 136
	ptr @.str.137, ; 137
	ptr @.str.138, ; 138
	ptr @.str.139, ; 139
	ptr @.str.140, ; 140
	ptr @.str.141, ; 141
	ptr @.str.142, ; 142
	ptr @.str.143, ; 143
	ptr @.str.144, ; 144
	ptr @.str.145, ; 145
	ptr @.str.146, ; 146
	ptr @.str.147, ; 147
	ptr @.str.148, ; 148
	ptr @.str.149, ; 149
	ptr @.str.150, ; 150
	ptr @.str.151, ; 151
	ptr @.str.152, ; 152
	ptr @.str.153, ; 153
	ptr @.str.154, ; 154
	ptr @.str.155, ; 155
	ptr @.str.156, ; 156
	ptr @.str.157, ; 157
	ptr @.str.158, ; 158
	ptr @.str.159, ; 159
	ptr @.str.160 ; 160
], align 4

; Strings
@.str.0 = private unnamed_addr constant [32 x i8] c"javax/security/cert/Certificate\00", align 1
@.str.1 = private unnamed_addr constant [36 x i8] c"javax/security/cert/X509Certificate\00", align 1
@.str.2 = private unnamed_addr constant [28 x i8] c"javax/security/auth/Subject\00", align 1
@.str.3 = private unnamed_addr constant [24 x i8] c"javax/net/SocketFactory\00", align 1
@.str.4 = private unnamed_addr constant [33 x i8] c"javax/net/ssl/HttpsURLConnection\00", align 1
@.str.5 = private unnamed_addr constant [31 x i8] c"javax/net/ssl/HostnameVerifier\00", align 1
@.str.6 = private unnamed_addr constant [25 x i8] c"javax/net/ssl/KeyManager\00", align 1
@.str.7 = private unnamed_addr constant [25 x i8] c"javax/net/ssl/SSLSession\00", align 1
@.str.8 = private unnamed_addr constant [32 x i8] c"javax/net/ssl/SSLSessionContext\00", align 1
@.str.9 = private unnamed_addr constant [27 x i8] c"javax/net/ssl/TrustManager\00", align 1
@.str.10 = private unnamed_addr constant [32 x i8] c"javax/net/ssl/KeyManagerFactory\00", align 1
@.str.11 = private unnamed_addr constant [25 x i8] c"javax/net/ssl/SSLContext\00", align 1
@.str.12 = private unnamed_addr constant [31 x i8] c"javax/net/ssl/SSLSocketFactory\00", align 1
@.str.13 = private unnamed_addr constant [34 x i8] c"javax/net/ssl/TrustManagerFactory\00", align 1
@.str.14 = private unnamed_addr constant [33 x i8] c"android/database/DataSetObserver\00", align 1
@.str.15 = private unnamed_addr constant [27 x i8] c"android/widget/AbsListView\00", align 1
@.str.16 = private unnamed_addr constant [27 x i8] c"android/widget/AdapterView\00", align 1
@.str.17 = private unnamed_addr constant [47 x i8] c"android/widget/AdapterView$OnItemClickListener\00", align 1
@.str.18 = private unnamed_addr constant [63 x i8] c"mono/android/widget/AdapterView_OnItemClickListenerImplementor\00", align 1
@.str.19 = private unnamed_addr constant [27 x i8] c"android/widget/BaseAdapter\00", align 1
@.str.20 = private unnamed_addr constant [24 x i8] c"android/widget/TextView\00", align 1
@.str.21 = private unnamed_addr constant [27 x i8] c"android/widget/FrameLayout\00", align 1
@.str.22 = private unnamed_addr constant [40 x i8] c"android/widget/FrameLayout$LayoutParams\00", align 1
@.str.23 = private unnamed_addr constant [23 x i8] c"android/widget/Adapter\00", align 1
@.str.24 = private unnamed_addr constant [27 x i8] c"android/widget/ListAdapter\00", align 1
@.str.25 = private unnamed_addr constant [28 x i8] c"android/widget/LinearLayout\00", align 1
@.str.26 = private unnamed_addr constant [41 x i8] c"android/widget/LinearLayout$LayoutParams\00", align 1
@.str.27 = private unnamed_addr constant [24 x i8] c"android/widget/ListView\00", align 1
@.str.28 = private unnamed_addr constant [21 x i8] c"android/widget/Toast\00", align 1
@.str.29 = private unnamed_addr constant [26 x i8] c"android/util/AttributeSet\00", align 1
@.str.30 = private unnamed_addr constant [19 x i8] c"android/os/Handler\00", align 1
@.str.31 = private unnamed_addr constant [22 x i8] c"android/os/BaseBundle\00", align 1
@.str.32 = private unnamed_addr constant [17 x i8] c"android/os/Build\00", align 1
@.str.33 = private unnamed_addr constant [25 x i8] c"android/os/Build$VERSION\00", align 1
@.str.34 = private unnamed_addr constant [18 x i8] c"android/os/Bundle\00", align 1
@.str.35 = private unnamed_addr constant [18 x i8] c"android/os/Looper\00", align 1
@.str.36 = private unnamed_addr constant [18 x i8] c"android/view/View\00", align 1
@.str.37 = private unnamed_addr constant [41 x i8] c"android/view/View$OnLayoutChangeListener\00", align 1
@.str.38 = private unnamed_addr constant [22 x i8] c"android/view/KeyEvent\00", align 1
@.str.39 = private unnamed_addr constant [33 x i8] c"android/view/ContextThemeWrapper\00", align 1
@.str.40 = private unnamed_addr constant [24 x i8] c"android/view/InputEvent\00", align 1
@.str.41 = private unnamed_addr constant [36 x i8] c"android/view/SurfaceHolder$Callback\00", align 1
@.str.42 = private unnamed_addr constant [27 x i8] c"android/view/SurfaceHolder\00", align 1
@.str.43 = private unnamed_addr constant [21 x i8] c"android/view/Surface\00", align 1
@.str.44 = private unnamed_addr constant [25 x i8] c"android/view/SurfaceView\00", align 1
@.str.45 = private unnamed_addr constant [25 x i8] c"android/view/TextureView\00", align 1
@.str.46 = private unnamed_addr constant [23 x i8] c"android/view/ViewGroup\00", align 1
@.str.47 = private unnamed_addr constant [36 x i8] c"android/view/ViewGroup$LayoutParams\00", align 1
@.str.48 = private unnamed_addr constant [42 x i8] c"android/view/ViewGroup$MarginLayoutParams\00", align 1
@.str.49 = private unnamed_addr constant [40 x i8] c"mono/android/runtime/InputStreamAdapter\00", align 1
@.str.50 = private unnamed_addr constant [31 x i8] c"mono/android/runtime/JavaArray\00", align 1
@.str.51 = private unnamed_addr constant [21 x i8] c"java/util/Collection\00", align 1
@.str.52 = private unnamed_addr constant [18 x i8] c"java/util/HashMap\00", align 1
@.str.53 = private unnamed_addr constant [20 x i8] c"java/util/ArrayList\00", align 1
@.str.54 = private unnamed_addr constant [32 x i8] c"mono/android/runtime/JavaObject\00", align 1
@.str.55 = private unnamed_addr constant [35 x i8] c"android/runtime/JavaProxyThrowable\00", align 1
@.str.56 = private unnamed_addr constant [18 x i8] c"java/util/HashSet\00", align 1
@.str.57 = private unnamed_addr constant [41 x i8] c"mono/android/runtime/OutputStreamAdapter\00", align 1
@.str.58 = private unnamed_addr constant [24 x i8] c"android/graphics/Canvas\00", align 1
@.str.59 = private unnamed_addr constant [23 x i8] c"android/graphics/Color\00", align 1
@.str.60 = private unnamed_addr constant [22 x i8] c"android/graphics/Rect\00", align 1
@.str.61 = private unnamed_addr constant [32 x i8] c"android/graphics/SurfaceTexture\00", align 1
@.str.62 = private unnamed_addr constant [35 x i8] c"android/graphics/drawable/Drawable\00", align 1
@.str.63 = private unnamed_addr constant [24 x i8] c"android/content/Context\00", align 1
@.str.64 = private unnamed_addr constant [23 x i8] c"android/content/Intent\00", align 1
@.str.65 = private unnamed_addr constant [34 x i8] c"android/content/BroadcastReceiver\00", align 1
@.str.66 = private unnamed_addr constant [31 x i8] c"android/content/ContextWrapper\00", align 1
@.str.67 = private unnamed_addr constant [41 x i8] c"android/content/SharedPreferences$Editor\00", align 1
@.str.68 = private unnamed_addr constant [67 x i8] c"android/content/SharedPreferences$OnSharedPreferenceChangeListener\00", align 1
@.str.69 = private unnamed_addr constant [34 x i8] c"android/content/SharedPreferences\00", align 1
@.str.70 = private unnamed_addr constant [21 x i8] c"android/app/Activity\00", align 1
@.str.71 = private unnamed_addr constant [28 x i8] c"android/app/ActivityManager\00", align 1
@.str.72 = private unnamed_addr constant [39 x i8] c"android/app/ActivityManager$MemoryInfo\00", align 1
@.str.73 = private unnamed_addr constant [24 x i8] c"android/app/Application\00", align 1
@.str.74 = private unnamed_addr constant [26 x i8] c"java/net/ConnectException\00", align 1
@.str.75 = private unnamed_addr constant [27 x i8] c"java/net/HttpURLConnection\00", align 1
@.str.76 = private unnamed_addr constant [27 x i8] c"java/net/InetSocketAddress\00", align 1
@.str.77 = private unnamed_addr constant [27 x i8] c"java/net/ProtocolException\00", align 1
@.str.78 = private unnamed_addr constant [15 x i8] c"java/net/Proxy\00", align 1
@.str.79 = private unnamed_addr constant [20 x i8] c"java/net/Proxy$Type\00", align 1
@.str.80 = private unnamed_addr constant [23 x i8] c"java/net/SocketAddress\00", align 1
@.str.81 = private unnamed_addr constant [25 x i8] c"java/net/SocketException\00", align 1
@.str.82 = private unnamed_addr constant [32 x i8] c"java/net/SocketTimeoutException\00", align 1
@.str.83 = private unnamed_addr constant [33 x i8] c"java/net/UnknownServiceException\00", align 1
@.str.84 = private unnamed_addr constant [13 x i8] c"java/net/URL\00", align 1
@.str.85 = private unnamed_addr constant [23 x i8] c"java/net/URLConnection\00", align 1
@.str.86 = private unnamed_addr constant [18 x i8] c"java/security/Key\00", align 1
@.str.87 = private unnamed_addr constant [24 x i8] c"java/security/Principal\00", align 1
@.str.88 = private unnamed_addr constant [25 x i8] c"java/security/PrivateKey\00", align 1
@.str.89 = private unnamed_addr constant [25 x i8] c"java/security/KeyFactory\00", align 1
@.str.90 = private unnamed_addr constant [23 x i8] c"java/security/KeyStore\00", align 1
@.str.91 = private unnamed_addr constant [27 x i8] c"java/security/SecureRandom\00", align 1
@.str.92 = private unnamed_addr constant [34 x i8] c"java/security/spec/EncodedKeySpec\00", align 1
@.str.93 = private unnamed_addr constant [27 x i8] c"java/security/spec/KeySpec\00", align 1
@.str.94 = private unnamed_addr constant [39 x i8] c"java/security/spec/PKCS8EncodedKeySpec\00", align 1
@.str.95 = private unnamed_addr constant [31 x i8] c"java/security/cert/Certificate\00", align 1
@.str.96 = private unnamed_addr constant [30 x i8] c"java/nio/channels/FileChannel\00", align 1
@.str.97 = private unnamed_addr constant [51 x i8] c"java/nio/channels/spi/AbstractInterruptibleChannel\00", align 1
@.str.98 = private unnamed_addr constant [13 x i8] c"java/io/File\00", align 1
@.str.99 = private unnamed_addr constant [24 x i8] c"java/io/FileInputStream\00", align 1
@.str.100 = private unnamed_addr constant [20 x i8] c"java/io/InputStream\00", align 1
@.str.101 = private unnamed_addr constant [31 x i8] c"java/io/InterruptedIOException\00", align 1
@.str.102 = private unnamed_addr constant [20 x i8] c"java/io/IOException\00", align 1
@.str.103 = private unnamed_addr constant [21 x i8] c"java/io/Serializable\00", align 1
@.str.104 = private unnamed_addr constant [21 x i8] c"java/io/OutputStream\00", align 1
@.str.105 = private unnamed_addr constant [20 x i8] c"java/io/PrintWriter\00", align 1
@.str.106 = private unnamed_addr constant [21 x i8] c"java/io/StringWriter\00", align 1
@.str.107 = private unnamed_addr constant [15 x i8] c"java/io/Writer\00", align 1
@.str.108 = private unnamed_addr constant [22 x i8] c"java/util/Enumeration\00", align 1
@.str.109 = private unnamed_addr constant [19 x i8] c"java/util/Iterator\00", align 1
@.str.110 = private unnamed_addr constant [17 x i8] c"java/util/Random\00", align 1
@.str.111 = private unnamed_addr constant [28 x i8] c"java/util/function/Consumer\00", align 1
@.str.112 = private unnamed_addr constant [18 x i8] c"java/lang/Boolean\00", align 1
@.str.113 = private unnamed_addr constant [15 x i8] c"java/lang/Byte\00", align 1
@.str.114 = private unnamed_addr constant [20 x i8] c"java/lang/Character\00", align 1
@.str.115 = private unnamed_addr constant [16 x i8] c"java/lang/Class\00", align 1
@.str.116 = private unnamed_addr constant [33 x i8] c"java/lang/ClassNotFoundException\00", align 1
@.str.117 = private unnamed_addr constant [17 x i8] c"java/lang/Double\00", align 1
@.str.118 = private unnamed_addr constant [20 x i8] c"java/lang/Exception\00", align 1
@.str.119 = private unnamed_addr constant [16 x i8] c"java/lang/Float\00", align 1
@.str.120 = private unnamed_addr constant [23 x i8] c"java/lang/CharSequence\00", align 1
@.str.121 = private unnamed_addr constant [18 x i8] c"java/lang/Integer\00", align 1
@.str.122 = private unnamed_addr constant [15 x i8] c"java/lang/Long\00", align 1
@.str.123 = private unnamed_addr constant [17 x i8] c"java/lang/Object\00", align 1
@.str.124 = private unnamed_addr constant [27 x i8] c"java/lang/RuntimeException\00", align 1
@.str.125 = private unnamed_addr constant [16 x i8] c"java/lang/Short\00", align 1
@.str.126 = private unnamed_addr constant [17 x i8] c"java/lang/String\00", align 1
@.str.127 = private unnamed_addr constant [17 x i8] c"java/lang/Thread\00", align 1
@.str.128 = private unnamed_addr constant [35 x i8] c"mono/java/lang/RunnableImplementor\00", align 1
@.str.129 = private unnamed_addr constant [42 x i8] c"java/lang/Thread$UncaughtExceptionHandler\00", align 1
@.str.130 = private unnamed_addr constant [20 x i8] c"java/lang/Throwable\00", align 1
@.str.131 = private unnamed_addr constant [29 x i8] c"java/lang/ClassCastException\00", align 1
@.str.132 = private unnamed_addr constant [15 x i8] c"java/lang/Enum\00", align 1
@.str.133 = private unnamed_addr constant [16 x i8] c"java/lang/Error\00", align 1
@.str.134 = private unnamed_addr constant [35 x i8] c"java/lang/IllegalArgumentException\00", align 1
@.str.135 = private unnamed_addr constant [32 x i8] c"java/lang/IllegalStateException\00", align 1
@.str.136 = private unnamed_addr constant [36 x i8] c"java/lang/IndexOutOfBoundsException\00", align 1
@.str.137 = private unnamed_addr constant [19 x i8] c"java/lang/Runnable\00", align 1
@.str.138 = private unnamed_addr constant [17 x i8] c"java/lang/System\00", align 1
@.str.139 = private unnamed_addr constant [23 x i8] c"java/lang/LinkageError\00", align 1
@.str.140 = private unnamed_addr constant [31 x i8] c"java/lang/NoClassDefFoundError\00", align 1
@.str.141 = private unnamed_addr constant [31 x i8] c"java/lang/NullPointerException\00", align 1
@.str.142 = private unnamed_addr constant [17 x i8] c"java/lang/Number\00", align 1
@.str.143 = private unnamed_addr constant [39 x i8] c"java/lang/ReflectiveOperationException\00", align 1
@.str.144 = private unnamed_addr constant [28 x i8] c"java/lang/SecurityException\00", align 1
@.str.145 = private unnamed_addr constant [28 x i8] c"java/lang/StackTraceElement\00", align 1
@.str.146 = private unnamed_addr constant [31 x i8] c"java/lang/UnsatisfiedLinkError\00", align 1
@.str.147 = private unnamed_addr constant [40 x i8] c"java/lang/UnsupportedOperationException\00", align 1
@.str.148 = private unnamed_addr constant [25 x i8] c"mono/android/TypeManager\00", align 1
@.str.149 = private unnamed_addr constant [35 x i8] c"crc6487f41b6f8dfb94e3/BootReceiver\00", align 1
@.str.150 = private unnamed_addr constant [35 x i8] c"crc6487f41b6f8dfb94e3/MainActivity\00", align 1
@.str.151 = private unnamed_addr constant [50 x i8] c"crc6487f41b6f8dfb94e3/MainActivity_ChannelAdapter\00", align 1
@.str.152 = private unnamed_addr constant [40 x i8] c"crc64a1b52d66edecd4d3/UncaughtCrashPeer\00", align 1
@.str.153 = private unnamed_addr constant [38 x i8] c"crc64a1b52d66edecd4d3/LiveSurfaceView\00", align 1
@.str.154 = private unnamed_addr constant [38 x i8] c"crc64a1b52d66edecd4d3/LiveTextureView\00", align 1
@.str.155 = private unnamed_addr constant [28 x i8] c"org/videolan/libvlc/AWindow\00", align 1
@.str.156 = private unnamed_addr constant [44 x i8] c"org/videolan/libvlc/AWindow$SurfaceCallback\00", align 1
@.str.157 = private unnamed_addr constant [29 x i8] c"org/videolan/libvlc/IVLCVout\00", align 1
@.str.158 = private unnamed_addr constant [38 x i8] c"org/videolan/libvlc/IVLCVout$Callback\00", align 1
@.str.159 = private unnamed_addr constant [54 x i8] c"org/videolan/libvlc/IVLCVout$OnNewVideoLayoutListener\00", align 1
@.str.160 = private unnamed_addr constant [43 x i8] c"crc64fd41ab9b8a8239c2/LayoutChangeListener\00", align 1

;TypeMapModule
@.TypeMapModule.0_assembly_name = private unnamed_addr constant [13 x i8] c"Mono.Android\00", align 1
@.TypeMapModule.1_assembly_name = private unnamed_addr constant [22 x i8] c"TaoLive.UI.Android.Tv\00", align 1
@.TypeMapModule.2_assembly_name = private unnamed_addr constant [34 x i8] c"LibVLCSharp.Android.AWindowModern\00", align 1
@.TypeMapModule.3_assembly_name = private unnamed_addr constant [12 x i8] c"LibVLCSharp\00", align 1

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
