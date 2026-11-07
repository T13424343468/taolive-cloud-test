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

@assembly_image_cache = dso_local local_unnamed_addr global [179 x ptr] zeroinitializer, align 4

; Each entry maps hash of an assembly name to an index into the `assembly_image_cache` array
@assembly_image_cache_hashes = dso_local local_unnamed_addr constant [537 x i32] [
	i32 u0x0027eb9e, ; 0: System.Net.NetworkInformation.dll => 69
	i32 u0x00345a11, ; 1: lib_System.Net.Requests.dll.so => 73
	i32 u0x009b21bb, ; 2: System.Net.NameResolution.dll => 68
	i32 u0x00e0bbf7, ; 3: lib_System.Xml.XmlSerializer.dll.so => 163
	i32 u0x00efe298, ; 4: System.Runtime.Intrinsics.dll => 109
	i32 u0x02139ac3, ; 5: System.IO.FileSystem.DriveInfo => 48
	i32 u0x025a8054, ; 6: System.Net.WebSockets.dll => 81
	i32 u0x028aa24d, ; 7: System.Threading.Thread => 146
	i32 u0x03f75868, ; 8: System.Diagnostics.StackTrace => 30
	i32 u0x0410f24b, ; 9: System.Security.Cryptography.Primitives => 125
	i32 u0x04e7b0a1, ; 10: System.Runtime.CompilerServices.VisualC.dll => 103
	i32 u0x056606a6, ; 11: lib_System.Collections.NonGeneric.dll.so => 10
	i32 u0x065dd880, ; 12: lib_System.Linq.Queryable.dll.so => 61
	i32 u0x06ee56d3, ; 13: lib_System.Net.Mail.dll.so => 67
	i32 u0x06ffddbc, ; 14: System.Runtime.InteropServices => 108
	i32 u0x074aea82, ; 15: System.Threading.Channels.dll => 140
	i32 u0x0772c6a7, ; 16: lib_System.Diagnostics.TextWriterTraceListener.dll.so => 31
	i32 u0x0881c32f, ; 17: System.Net.WebHeaderCollection => 78
	i32 u0x08f064cf, ; 18: System.Security.Cryptography.Primitives.dll => 125
	i32 u0x097ed3c0, ; 19: System.ComponentModel.Annotations => 13
	i32 u0x0a81994f, ; 20: System.ServiceProcess => 133
	i32 u0x0afca281, ; 21: System.ValueTuple.dll => 152
	i32 u0x0b0de1c3, ; 22: lib_System.Xml.XPath.XDocument.dll.so => 160
	i32 u0x0b63b1e1, ; 23: lib_System.Net.Http.Json.dll.so => 64
	i32 u0x0ba8e231, ; 24: lib_System.Net.ServicePoint.dll.so => 75
	i32 u0x0c38ff48, ; 25: System.ComponentModel => 18
	i32 u0x0cfa66a6, ; 26: lib_System.IO.Compression.FileSystem.dll.so => 44
	i32 u0x0d1f8edb, ; 27: System.Diagnostics.Debug => 26
	i32 u0x0dc10265, ; 28: Microsoft.CSharp.dll => 1
	i32 u0x0dcb05c4, ; 29: System.Linq.Parallel => 60
	i32 u0x0dd133ce, ; 30: System.Globalization => 42
	i32 u0x0eb2f8c5, ; 31: System.Reflection.Emit.Lightweight => 92
	i32 u0x0ec71be0, ; 32: lib_System.Security.SecureString.dll.so => 130
	i32 u0x107abf20, ; 33: System.Threading.Timer.dll => 148
	i32 u0x10c1d9f6, ; 34: lib_System.Data.DataSetExtensions.dll.so => 23
	i32 u0x1159791e, ; 35: System.IO.Pipes.AccessControl.dll => 55
	i32 u0x11d123fd, ; 36: System.Net.Ping.dll => 70
	i32 u0x132b30dd, ; 37: System.Numerics => 84
	i32 u0x136bf828, ; 38: lib_System.Runtime.dll.so => 117
	i32 u0x14eaf2a7, ; 39: lib_System.ComponentModel.Annotations.dll.so => 13
	i32 u0x15766b7b, ; 40: System.ServiceModel.Web => 132
	i32 u0x15e184df, ; 41: lib_System.Runtime.Loader.dll.so => 110
	i32 u0x15ebe147, ; 42: System.IO.Pipes => 56
	i32 u0x1658bf94, ; 43: System.Transactions.Local => 150
	i32 u0x16646418, ; 44: System.Net.ServicePoint.dll => 75
	i32 u0x16a510e1, ; 45: System.Threading.Thread.dll => 146
	i32 u0x16fe439a, ; 46: System.Memory.dll => 63
	i32 u0x1766c1f7, ; 47: System.Threading.ThreadPool.dll => 147
	i32 u0x17969339, ; 48: _Microsoft.Android.Resource.Designer => 178
	i32 u0x180c08d0, ; 49: WindowsBase => 166
	i32 u0x198cd3eb, ; 50: lib_System.Security.Cryptography.Encoding.dll.so => 123
	i32 u0x1a61054f, ; 51: System.Collections => 12
	i32 u0x1ae969b2, ; 52: System.Security.Cryptography.X509Certificates => 126
	i32 u0x1b317bfd, ; 53: System.Web.HttpUtility.dll => 153
	i32 u0x1b5932ea, ; 54: lib_Mono.Android.Runtime.dll.so => 171
	i32 u0x1b611806, ; 55: System.Runtime.Serialization.Primitives.dll => 114
	i32 u0x1bc4415d, ; 56: mscorlib => 167
	i32 u0x1bc6ffe7, ; 57: lib_Java.Interop.dll.so => 169
	i32 u0x1bff388e, ; 58: System.dll => 165
	i32 u0x1c78d08a, ; 59: lib_System.Private.Uri.dll.so => 87
	i32 u0x1d4d8185, ; 60: lib_System.Runtime.Serialization.dll.so => 116
	i32 u0x1dbae811, ; 61: System.ObjectModel => 85
	i32 u0x1f1dceb7, ; 62: lib_System.Security.Cryptography.Primitives.dll.so => 125
	i32 u0x1f443e2d, ; 63: lib_System.AppContext.dll.so => 6
	i32 u0x1f6088c2, ; 64: System.Transactions.dll => 151
	i32 u0x1f9b4faa, ; 65: System.Linq.Queryable => 61
	i32 u0x20303736, ; 66: System.IO.FileSystem.dll => 51
	i32 u0x2080b118, ; 67: System.Runtime.Extensions => 104
	i32 u0x20924146, ; 68: System.Runtime.Serialization.Xml => 115
	i32 u0x20bbb280, ; 69: System.Globalization.Calendars => 40
	i32 u0x22697083, ; 70: System.Security.Cryptography.Cng => 121
	i32 u0x2386616a, ; 71: lib_System.ServiceModel.Web.dll.so => 132
	i32 u0x2397454a, ; 72: lib_System.Collections.Specialized.dll.so => 11
	i32 u0x23d83352, ; 73: System.IO.IsolatedStorage.dll => 52
	i32 u0x23eaab34, ; 74: lib_System.Core.dll.so => 21
	i32 u0x24154ecb, ; 75: System.IO.Compression.FileSystem => 44
	i32 u0x2450d7cc, ; 76: TaoLive.UI.Android.Tv.dll => 0
	i32 u0x2459aaf0, ; 77: lib_System.Net.Sockets.dll.so => 76
	i32 u0x2493d7b9, ; 78: System.Security.Cryptography.Algorithms => 120
	i32 u0x262968a7, ; 79: lib_System.Reflection.Extensions.dll.so => 94
	i32 u0x2660a755, ; 80: System.Net => 82
	i32 u0x27787397, ; 81: System.Text.Encodings.Web.dll => 137
	i32 u0x27b53050, ; 82: lib_System.Data.Common.dll.so => 22
	i32 u0x2814a96c, ; 83: System.Collections.Concurrent => 8
	i32 u0x282acf5e, ; 84: lib_System.IO.FileSystem.dll.so => 51
	i32 u0x28bdabca, ; 85: System.Net.Security => 74
	i32 u0x29293ff5, ; 86: System.Xml.Linq.dll => 156
	i32 u0x295a9e3d, ; 87: System.Windows => 155
	i32 u0x296c7566, ; 88: lib_System.Xml.dll.so => 164
	i32 u0x29af2b3b, ; 89: System.Reflection.Emit => 93
	i32 u0x29be9df3, ; 90: System.IO.Compression.ZipFile => 45
	i32 u0x2b15ed29, ; 91: System.Runtime.Loader.dll => 110
	i32 u0x2ba1ca8c, ; 92: lib_System.Security.dll.so => 131
	i32 u0x2bd14e96, ; 93: System.Security.SecureString.dll => 130
	i32 u0x2cd6293c, ; 94: System.Diagnostics.Contracts.dll => 25
	i32 u0x2d322560, ; 95: lib_System.Xml.XmlDocument.dll.so => 162
	i32 u0x2d445acd, ; 96: System.Net.Requests => 73
	i32 u0x2d745423, ; 97: System.IO.Pipes.dll => 56
	i32 u0x2e394f87, ; 98: System.IO.Compression => 46
	i32 u0x2eec5558, ; 99: lib_System.Reflection.dll.so => 98
	i32 u0x2f0fe5eb, ; 100: lib_System.Reflection.DispatchProxy.dll.so => 90
	i32 u0x2ff6fb9f, ; 101: System.Data.Common => 22
	i32 u0x30a0e95c, ; 102: lib_System.Threading.Thread.dll.so => 146
	i32 u0x311247b5, ; 103: System.Private.Uri.dll => 87
	i32 u0x317d5b75, ; 104: System.IO.Compression.Brotli => 43
	i32 u0x31a103c6, ; 105: System.Xml.XPath.dll => 161
	i32 u0x31b69d60, ; 106: System.Net.Quic => 72
	i32 u0x340ac0b8, ; 107: Microsoft.VisualBasic => 3
	i32 u0x34505120, ; 108: System.Globalization.dll => 42
	i32 u0x3463c971, ; 109: System.Net.Http.Json => 64
	i32 u0x34a66c56, ; 110: lib_System.IO.Pipes.dll.so => 56
	i32 u0x35e25008, ; 111: System.ComponentModel.Primitives.dll => 16
	i32 u0x3612ff2c, ; 112: lib_System.IO.dll.so => 58
	i32 u0x362ae188, ; 113: lib_LibVLCSharp.dll.so => 174
	i32 u0x364e69a3, ; 114: System.IO.MemoryMappedFiles.dll => 53
	i32 u0x36e9595b, ; 115: lib_System.Transactions.dll.so => 151
	i32 u0x370eff4f, ; 116: lib_System.Globalization.Extensions.dll.so => 41
	i32 u0x3787b992, ; 117: lib_System.ComponentModel.DataAnnotations.dll.so => 14
	i32 u0x38c136f7, ; 118: System.Runtime.InteropServices.JavaScript.dll => 106
	i32 u0x39481653, ; 119: lib_mscorlib.dll.so => 167
	i32 u0x3a20ecf3, ; 120: System.Diagnostics.Tracing => 34
	i32 u0x3a2aaa1d, ; 121: System.Xml.XDocument => 159
	i32 u0x3acd0267, ; 122: System.Private.DataContractSerialization.dll => 86
	i32 u0x3ad7b407, ; 123: System.Diagnostics.Tools => 32
	i32 u0x3b2c715c, ; 124: System.Collections.dll => 12
	i32 u0x3b458447, ; 125: lib_System.Threading.Tasks.Dataflow.dll.so => 142
	i32 u0x3b45fb35, ; 126: System.IO.FileSystem => 51
	i32 u0x3bb6bd33, ; 127: System.IO.UnmanagedMemoryStream.dll => 57
	i32 u0x3cbffa41, ; 128: System.Drawing => 36
	i32 u0x3dc84a49, ; 129: System.Drawing.Primitives.dll => 35
	i32 u0x3e444eb4, ; 130: System.Linq.Expressions.dll => 59
	i32 u0x3e5c42fd, ; 131: lib_System.Reflection.TypeExtensions.dll.so => 97
	i32 u0x3ebd41f6, ; 132: lib_System.Collections.dll.so => 12
	i32 u0x3ecd3024, ; 133: lib_System.Resources.Reader.dll.so => 99
	i32 u0x408b17f4, ; 134: System.ComponentModel.TypeConverter => 17
	i32 u0x41761b2c, ; 135: System => 165
	i32 u0x4232ae7b, ; 136: lib_System.Reflection.Emit.dll.so => 93
	i32 u0x42be2972, ; 137: lib_System.Text.Encodings.Web.dll.so => 137
	i32 u0x441f18e1, ; 138: lib_System.Security.Cryptography.OpenSsl.dll.so => 124
	i32 u0x444e5c8e, ; 139: lib_System.ComponentModel.TypeConverter.dll.so => 17
	i32 u0x44549c93, ; 140: lib_System.Net.WebProxy.dll.so => 79
	i32 u0x4474042c, ; 141: lib_System.Numerics.Vectors.dll.so => 83
	i32 u0x44845810, ; 142: lib_System.Net.Http.dll.so => 65
	i32 u0x44c3958b, ; 143: lib_System.Private.DataContractSerialization.dll.so => 86
	i32 u0x45bde382, ; 144: lib_System.Windows.dll.so => 155
	i32 u0x45c677b2, ; 145: System.Web.dll => 154
	i32 u0x466ae52b, ; 146: lib_System.Threading.Overlapped.dll.so => 141
	i32 u0x470b60dd, ; 147: LibVLCSharp.Android.AWindowModern => 175
	i32 u0x47a87de7, ; 148: lib_System.Resources.Writer.dll.so => 101
	i32 u0x480a69ad, ; 149: System.Diagnostics.Process => 29
	i32 u0x48aa6be3, ; 150: System.IO.IsolatedStorage => 52
	i32 u0x49654709, ; 151: lib_System.Threading.Timer.dll.so => 148
	i32 u0x4aaf6f7c, ; 152: Microsoft.Win32.Registry => 5
	i32 u0x4b863c7a, ; 153: lib_System.Private.Xml.Linq.dll.so => 88
	i32 u0x4bb12d98, ; 154: lib_System.Runtime.Serialization.Xml.dll.so => 115
	i32 u0x4e08a30b, ; 155: System.Private.DataContractSerialization => 86
	i32 u0x4eed2679, ; 156: System.Linq => 62
	i32 u0x4f97822f, ; 157: System.Runtime.Serialization.Json.dll => 113
	i32 u0x514d38cd, ; 158: System.IO => 58
	i32 u0x523dc4c1, ; 159: System.Resources.ResourceManager => 100
	i32 u0x533678bd, ; 160: lib_System.Private.CoreLib.dll.so => 173
	i32 u0x53701274, ; 161: lib_System.IO.FileSystem.Watcher.dll.so => 50
	i32 u0x53936ab4, ; 162: System.Configuration.dll => 19
	i32 u0x53f80ba6, ; 163: System.Runtime.Serialization.Formatters.dll => 112
	i32 u0x5423e47b, ; 164: System.Runtime.CompilerServices.Unsafe => 102
	i32 u0x54246761, ; 165: lib_System.Diagnostics.Tools.dll.so => 32
	i32 u0x5498bac9, ; 166: lib_Microsoft.VisualBasic.dll.so => 3
	i32 u0x54ca50cb, ; 167: System.Runtime.CompilerServices.VisualC => 103
	i32 u0x557217fe, ; 168: lib_System.Numerics.dll.so => 84
	i32 u0x557b5293, ; 169: System.Runtime.Handles => 105
	i32 u0x55d10363, ; 170: System.Net.Quic.dll => 72
	i32 u0x55dfaca3, ; 171: lib_Microsoft.Win32.Primitives.dll.so => 4
	i32 u0x568cd628, ; 172: System.Formats.Asn1.dll => 38
	i32 u0x569fcb36, ; 173: System.Diagnostics.Tools.dll => 32
	i32 u0x56c018af, ; 174: lib_System.IO.UnmanagedMemoryStream.dll.so => 57
	i32 u0x56e36530, ; 175: System.Runtime.Extensions.dll => 104
	i32 u0x56e7a7ad, ; 176: System.Net.Security.dll => 74
	i32 u0x5718a9ef, ; 177: System.Collections.Immutable.dll => 9
	i32 u0x57201017, ; 178: System.Security.Cryptography.OpenSsl => 124
	i32 u0x57261233, ; 179: System.IO.Compression.dll => 46
	i32 u0x5833866d, ; 180: System.Collections.Immutable => 9
	i32 u0x583e844f, ; 181: System.IO.Compression.Brotli.dll => 43
	i32 u0x58a57897, ; 182: Microsoft.Win32.Primitives => 4
	i32 u0x596b5b3a, ; 183: lib_System.Drawing.Primitives.dll.so => 35
	i32 u0x5b9331b6, ; 184: System.Diagnostics.TextWriterTraceListener => 31
	i32 u0x5bf8ca0f, ; 185: System.Text.RegularExpressions.dll => 139
	i32 u0x5bfdbb43, ; 186: System.Reflection.Emit.dll => 93
	i32 u0x5c680b40, ; 187: System.Reflection.Extensions.dll => 94
	i32 u0x5d552ab7, ; 188: System.IO.FileSystem.Primitives => 49
	i32 u0x5d5a6c40, ; 189: System.Threading.Tasks.Dataflow.dll => 142
	i32 u0x5dccd455, ; 190: System.Runtime.Serialization.Json => 113
	i32 u0x5e2d7514, ; 191: System.Threading.Overlapped => 141
	i32 u0x5e2e3abe, ; 192: lib_Microsoft.VisualBasic.Core.dll.so => 2
	i32 u0x5e7321d2, ; 193: lib_System.ComponentModel.Primitives.dll.so => 16
	i32 u0x5ef2ee25, ; 194: System.Runtime.Serialization.dll => 116
	i32 u0x5f6f0b5b, ; 195: System.Xml.Serialization => 158
	i32 u0x5fa7b851, ; 196: System.Net.WebClient => 77
	i32 u0x6078995d, ; 197: System.Net.WebSockets.Client.dll => 80
	i32 u0x60892624, ; 198: lib_System.Formats.Tar.dll.so => 39
	i32 u0x60b33958, ; 199: System.Dynamic.Runtime => 37
	i32 u0x61b9038d, ; 200: System.Net.Http.dll => 65
	i32 u0x61c036ca, ; 201: System.Text.RegularExpressions => 139
	i32 u0x61d59e0e, ; 202: System.ComponentModel.EventBasedAsync.dll => 15
	i32 u0x62021776, ; 203: lib_System.IO.Compression.dll.so => 46
	i32 u0x620a8774, ; 204: lib_System.Xml.ReaderWriter.dll.so => 157
	i32 u0x625755ef, ; 205: lib_WindowsBase.dll.so => 166
	i32 u0x62c6282e, ; 206: System.Runtime => 117
	i32 u0x63dee9da, ; 207: System.IO.FileSystem.DriveInfo.dll => 48
	i32 u0x63fca3d0, ; 208: System.Net.Primitives.dll => 71
	i32 u0x640c0103, ; 209: System.Net.WebSockets => 81
	i32 u0x641f3e5a, ; 210: System.Security.Cryptography => 127
	i32 u0x64d1e4f5, ; 211: System.Reflection.Metadata => 95
	i32 u0x6525abc9, ; 212: System.Security.Cryptography.Csp => 122
	i32 u0x654b1498, ; 213: lib_System.Transactions.Local.dll.so => 150
	i32 u0x656b7698, ; 214: System.Diagnostics.Debug.dll => 26
	i32 u0x6670b12e, ; 215: lib_System.Security.AccessControl.dll.so => 118
	i32 u0x66e27484, ; 216: System.Reflection.dll => 98
	i32 u0x66ffb0f8, ; 217: System.Diagnostics.FileVersionInfo.dll => 28
	i32 u0x67577fe1, ; 218: lib_System.Runtime.CompilerServices.VisualC.dll.so => 103
	i32 u0x67fe8db2, ; 219: System.Transactions.Local.dll => 150
	i32 u0x68139a0d, ; 220: System.IO.Pipelines.dll => 54
	i32 u0x6816ab6a, ; 221: Mono.Android.Export => 170
	i32 u0x6853a83d, ; 222: Microsoft.Win32.Primitives.dll => 4
	i32 u0x68cc9d1e, ; 223: System.Resources.Reader.dll => 99
	i32 u0x68f61ae4, ; 224: lib_System.Formats.Asn1.dll.so => 38
	i32 u0x69239124, ; 225: System.Diagnostics.TraceSource.dll => 33
	i32 u0x693efa35, ; 226: lib_System.Net.WebHeaderCollection.dll.so => 78
	i32 u0x6942234e, ; 227: System.Reflection.Extensions => 94
	i32 u0x69ae5451, ; 228: lib_System.Runtime.InteropServices.JavaScript.dll.so => 106
	i32 u0x69dc03cc, ; 229: System.Core.dll => 21
	i32 u0x69ec0683, ; 230: System.Globalization.Extensions.dll => 41
	i32 u0x6a216153, ; 231: Mono.Android.Runtime.dll => 171
	i32 u0x6a539b49, ; 232: lib_System.Runtime.Extensions.dll.so => 104
	i32 u0x6afaf338, ; 233: lib_System.Threading.dll.so => 149
	i32 u0x6c687fa7, ; 234: Microsoft.VisualBasic.Core => 2
	i32 u0x6ca29590, ; 235: lib_TaoLive.BLL.dll.so => 176
	i32 u0x6cbab720, ; 236: System.Text.Encoding.Extensions => 135
	i32 u0x6cc30c8c, ; 237: System.Runtime.Serialization.Formatters => 112
	i32 u0x6ec71a65, ; 238: System.Linq.Expressions => 59
	i32 u0x6f7a29e4, ; 239: System.Reflection.Primitives => 96
	i32 u0x7009e4c3, ; 240: System.Formats.Tar.dll => 39
	i32 u0x70972b6d, ; 241: System.Diagnostics.Contracts => 25
	i32 u0x70a66bdd, ; 242: System.Reflection.Metadata.dll => 95
	i32 u0x7124cf39, ; 243: System.Reflection.DispatchProxy => 90
	i32 u0x71490522, ; 244: System.Resources.ResourceManager.dll => 100
	i32 u0x71dc7c8b, ; 245: System.Collections.NonGeneric.dll => 10
	i32 u0x731dd955, ; 246: lib_Mono.Android.dll.so => 172
	i32 u0x739bd4a8, ; 247: System.Private.Xml.Linq => 88
	i32 u0x73b20433, ; 248: lib_System.IO.FileSystem.Primitives.dll.so => 49
	i32 u0x73fbecbe, ; 249: lib_System.Memory.dll.so => 63
	i32 u0x74126030, ; 250: lib_System.Net.WebClient.dll.so => 77
	i32 u0x74a1c5bb, ; 251: System.Resources.Writer => 101
	i32 u0x7593c33f, ; 252: lib_System.IO.FileSystem.AccessControl.dll.so => 47
	i32 u0x77ec19b4, ; 253: System.Buffers.dll => 7
	i32 u0x784d3e49, ; 254: lib_System.Net.dll.so => 82
	i32 u0x79eb68ee, ; 255: System.Private.Xml => 89
	i32 u0x7aca0819, ; 256: System.Windows.dll => 155
	i32 u0x7b350579, ; 257: lib__Microsoft.Android.Resource.Designer.dll.so => 178
	i32 u0x7b6f419e, ; 258: System.Diagnostics.TraceSource => 33
	i32 u0x7b8f6ff7, ; 259: lib_System.Runtime.Serialization.Json.dll.so => 113
	i32 u0x7bf8cdab, ; 260: System.Runtime.dll => 117
	i32 u0x7c51ebd4, ; 261: lib_System.Net.HttpListener.dll.so => 66
	i32 u0x7c9bf920, ; 262: System.Numerics.Vectors => 83
	i32 u0x7d702d52, ; 263: lib_System.Text.Encoding.dll.so => 136
	i32 u0x7ec9ffe9, ; 264: System.Console => 20
	i32 u0x7fb38cd2, ; 265: System.Collections.Specialized => 11
	i32 u0x7fc7a41e, ; 266: System.Xml.XmlSerializer.dll => 163
	i32 u0x7fd90a71, ; 267: lib_System.Text.Encoding.CodePages.dll.so => 134
	i32 u0x7ff65cf5, ; 268: Microsoft.VisualBasic.dll => 3
	i32 u0x802a7166, ; 269: lib_System.Diagnostics.FileVersionInfo.dll.so => 28
	i32 u0x80f2f56e, ; 270: lib_System.Runtime.Serialization.Formatters.dll.so => 112
	i32 u0x8115bdf3, ; 271: lib_System.Resources.ResourceManager.dll.so => 100
	i32 u0x816751d8, ; 272: lib_System.Diagnostics.DiagnosticSource.dll.so => 27
	i32 u0x81a110ae, ; 273: lib_System.ComponentModel.EventBasedAsync.dll.so => 15
	i32 u0x82364da2, ; 274: lib_System.Buffers.dll.so => 7
	i32 u0x82b6c85e, ; 275: System.ObjectModel.dll => 85
	i32 u0x82bb5429, ; 276: lib_System.Linq.Expressions.dll.so => 59
	i32 u0x82c1cf3e, ; 277: lib_System.Net.Quic.dll.so => 72
	i32 u0x832ec206, ; 278: lib_System.Diagnostics.StackTrace.dll.so => 30
	i32 u0x8334206b, ; 279: System.Net.Http => 65
	i32 u0x8471e4ec, ; 280: System.Threading.Tasks.Parallel => 144
	i32 u0x857e4dd2, ; 281: lib_System.Net.WebSockets.dll.so => 81
	i32 u0x863c6ac5, ; 282: System.Xml.Serialization.dll => 158
	i32 u0x867c9c52, ; 283: System.Globalization.Extensions => 41
	i32 u0x8702d9a2, ; 284: System.Security.AccessControl.dll => 118
	i32 u0x881f94da, ; 285: lib_netstandard.dll.so => 168
	i32 u0x88acefcd, ; 286: System.ServiceModel.Web.dll => 132
	i32 u0x88d8bfaa, ; 287: System.Net.Sockets => 76
	i32 u0x88ffe49e, ; 288: System.Net.Mail => 67
	i32 u0x896b7878, ; 289: System.Private.CoreLib.dll => 173
	i32 u0x89cc6fee, ; 290: LibVLCSharp => 174
	i32 u0x8a52059a, ; 291: System.Threading.Tasks.Parallel.dll => 144
	i32 u0x8b804dbf, ; 292: System.Runtime.InteropServices.RuntimeInformation.dll => 107
	i32 u0x8bbaa2cd, ; 293: System.ValueTuple => 152
	i32 u0x8c20f140, ; 294: lib_System.Console.dll.so => 20
	i32 u0x8c40e0db, ; 295: System.Net.Primitives => 71
	i32 u0x8d24e767, ; 296: System.Xml.ReaderWriter.dll => 157
	i32 u0x8d52d3de, ; 297: lib_System.Threading.Tasks.dll.so => 145
	i32 u0x8dc6dbce, ; 298: System.Security.Cryptography.Csp.dll => 122
	i32 u0x8e114655, ; 299: System.Security.Principal.Windows.dll => 128
	i32 u0x8f24faee, ; 300: System.Web.HttpUtility => 153
	i32 u0x8f4e087a, ; 301: lib_System.Web.dll.so => 154
	i32 u0x8f8c64e2, ; 302: lib_System.Private.Xml.dll.so => 89
	i32 u0x905355ed, ; 303: System.Threading.Tasks.Dataflow => 142
	i32 u0x90e50509, ; 304: lib_System.Reflection.Primitives.dll.so => 96
	i32 u0x912896e5, ; 305: System.Console.dll => 20
	i32 u0x9130f5e7, ; 306: System.ComponentModel.DataAnnotations.dll => 14
	i32 u0x924edee6, ; 307: System.Text.Encoding.dll => 136
	i32 u0x928c75ca, ; 308: System.Net.Sockets.dll => 76
	i32 u0x92916334, ; 309: System.Linq.Parallel.dll => 60
	i32 u0x93554fdc, ; 310: netstandard.dll => 168
	i32 u0x93918882, ; 311: Java.Interop.dll => 169
	i32 u0x940d5c2f, ; 312: System.ComponentModel.EventBasedAsync => 15
	i32 u0x94147f61, ; 313: System.Net.ServicePoint => 75
	i32 u0x9438d78e, ; 314: lib_System.Text.Json.dll.so => 138
	i32 u0x94798bc5, ; 315: System.AppContext.dll => 6
	i32 u0x95178668, ; 316: System.Data.DataSetExtensions => 23
	i32 u0x974b89a2, ; 317: System.Reflection.Emit.Lightweight.dll => 92
	i32 u0x98ba5a04, ; 318: Microsoft.CSharp => 1
	i32 u0x9930ee42, ; 319: System.Text.Encodings.Web => 137
	i32 u0x9a1756ac, ; 320: System.Text.Encoding.Extensions.dll => 135
	i32 u0x9a20430d, ; 321: System.Net.Ping => 70
	i32 u0x9a5a3337, ; 322: System.Threading.ThreadPool => 147
	i32 u0x9b24ab96, ; 323: lib_System.Runtime.Serialization.Primitives.dll.so => 114
	i32 u0x9b5e5b1c, ; 324: lib_System.Diagnostics.Contracts.dll.so => 25
	i32 u0x9bfe3a41, ; 325: System.Private.Xml.dll => 89
	i32 u0x9c165ff9, ; 326: System.Reflection.TypeExtensions.dll => 97
	i32 u0x9c97ad4a, ; 327: System.Diagnostics.TextWriterTraceListener.dll => 31
	i32 u0x9cc03a58, ; 328: System.IO.Compression.ZipFile.dll => 45
	i32 u0x9cd341b2, ; 329: lib_System.Threading.Tasks.Parallel.dll.so => 144
	i32 u0x9ec4cf01, ; 330: System.Runtime.Loader => 110
	i32 u0x9ecf752a, ; 331: System.Xml.XDocument.dll => 159
	i32 u0x9ee22cc0, ; 332: System.Drawing.Primitives => 35
	i32 u0x9f2b7e4c, ; 333: lib_TaoLive.UI.Android.Tv.dll.so => 0
	i32 u0x9f7ea921, ; 334: lib_System.Runtime.InteropServices.dll.so => 108
	i32 u0x9f8c6f40, ; 335: System.Data.Common.dll => 22
	i32 u0xa026a50c, ; 336: System.Runtime.Serialization.Xml.dll => 115
	i32 u0xa090e36a, ; 337: System.IO.dll => 58
	i32 u0xa0fb56af, ; 338: lib_System.Text.RegularExpressions.dll.so => 139
	i32 u0xa1d8b647, ; 339: System.Threading.Tasks.dll => 145
	i32 u0xa1fd7d9f, ; 340: System.Security.Claims => 119
	i32 u0xa21f5a1f, ; 341: System.Security.Cryptography.Cng.dll => 121
	i32 u0xa262a30f, ; 342: System.Runtime.Numerics.dll => 111
	i32 u0xa30769e5, ; 343: System.Threading.Channels => 140
	i32 u0xa35f8f92, ; 344: System.IO.Pipes.AccessControl => 55
	i32 u0xa3c818c7, ; 345: lib_System.Net.WebSockets.Client.dll.so => 80
	i32 u0xa3cc7fa7, ; 346: System.Runtime.InteropServices.JavaScript => 106
	i32 u0xa493aa02, ; 347: lib_System.Collections.Concurrent.dll.so => 8
	i32 u0xa4d4aaf8, ; 348: lib_System.Security.Cryptography.Cng.dll.so => 121
	i32 u0xa4db22c6, ; 349: System.Text.Encoding.CodePages.dll => 134
	i32 u0xa553c739, ; 350: lib_System.ValueTuple.dll.so => 152
	i32 u0xa5c5753c, ; 351: lib_System.Collections.Immutable.dll.so => 9
	i32 u0xa6133c7f, ; 352: lib_System.IO.FileSystem.DriveInfo.dll.so => 48
	i32 u0xa668c988, ; 353: lib_System.Net.NameResolution.dll.so => 68
	i32 u0xa715dd7e, ; 354: System.Xml.XPath.XDocument.dll => 160
	i32 u0xa8032c67, ; 355: lib_Microsoft.Win32.Registry.dll.so => 5
	i32 u0xa80db4e1, ; 356: System.Xml.dll => 164
	i32 u0xa81b119f, ; 357: lib_System.Security.Cryptography.dll.so => 127
	i32 u0xa8282c09, ; 358: System.ServiceProcess.dll => 133
	i32 u0xa85a7b6c, ; 359: System.Xml.XmlDocument => 162
	i32 u0xa9d96f9b, ; 360: System.Threading.Overlapped.dll => 141
	i32 u0xaa2b531f, ; 361: lib_System.Globalization.dll.so => 42
	i32 u0xaa88e550, ; 362: Mono.Android.Export.dll => 170
	i32 u0xab606289, ; 363: System.Globalization.Calendars.dll => 40
	i32 u0xac1dd496, ; 364: System.Net.dll => 82
	i32 u0xacd6baa9, ; 365: System.IO.UnmanagedMemoryStream => 57
	i32 u0xace3f9b4, ; 366: System.Dynamic.Runtime.dll => 37
	i32 u0xace7ba82, ; 367: lib_System.Security.Principal.Windows.dll.so => 128
	i32 u0xacf080de, ; 368: System.Reflection => 98
	i32 u0xad2a79b6, ; 369: mscorlib.dll => 167
	i32 u0xad6f1e8a, ; 370: System.Private.CoreLib => 173
	i32 u0xae037813, ; 371: System.Numerics.Vectors.dll => 83
	i32 u0xaf06273c, ; 372: System.Resources.Reader => 99
	i32 u0xaf3a6b91, ; 373: lib_System.Diagnostics.Debug.dll.so => 26
	i32 u0xaf4af872, ; 374: System.Diagnostics.StackTrace.dll => 30
	i32 u0xaf624531, ; 375: System.Xml.XPath.XDocument => 160
	i32 u0xb0682092, ; 376: System.ComponentModel.dll => 18
	i32 u0xb0ed41f3, ; 377: System.Security.Principal.Windows => 128
	i32 u0xb128f886, ; 378: System.Security.Cryptography.Algorithms.dll => 120
	i32 u0xb1a434a2, ; 379: lib_System.Xml.Linq.dll.so => 156
	i32 u0xb294d40b, ; 380: lib_System.Net.Ping.dll.so => 70
	i32 u0xb33d5570, ; 381: LibVLCSharp.Android.AWindowModern.dll => 175
	i32 u0xb434b64b, ; 382: WindowsBase.dll => 166
	i32 u0xb514b305, ; 383: _Microsoft.Android.Resource.Designer.dll => 178
	i32 u0xb58d85d9, ; 384: lib_System.Runtime.Handles.dll.so => 105
	i32 u0xb6490b5e, ; 385: lib_Mono.Android.Export.dll.so => 170
	i32 u0xb65adef9, ; 386: Mono.Android.Runtime => 171
	i32 u0xb660be12, ; 387: System.ComponentModel.Primitives => 16
	i32 u0xb755818f, ; 388: System.Threading.Tasks => 145
	i32 u0xb7e7c341, ; 389: lib_System.Globalization.Calendars.dll.so => 40
	i32 u0xb838e2b0, ; 390: System.Security.Cryptography.X509Certificates.dll => 126
	i32 u0xb8c22b7f, ; 391: System.Security.Claims.dll => 119
	i32 u0xb8fd311b, ; 392: System.Formats.Asn1 => 38
	i32 u0xb979e222, ; 393: System.Runtime.Serialization => 116
	i32 u0xba0dbf1c, ; 394: System.IO.FileSystem.AccessControl.dll => 47
	i32 u0xba4127cb, ; 395: System.Threading.Tasks.Extensions => 143
	i32 u0xbaa520e7, ; 396: lib_System.ObjectModel.dll.so => 85
	i32 u0xbab301d1, ; 397: System.Security.AccessControl => 118
	i32 u0xbb95ee37, ; 398: System.Diagnostics.Tracing.dll => 34
	i32 u0xbc4c6465, ; 399: System.Reflection.Primitives.dll => 96
	i32 u0xbc652da7, ; 400: System.IO.MemoryMappedFiles => 53
	i32 u0xbcc610a0, ; 401: lib_System.Reflection.Metadata.dll.so => 95
	i32 u0xbddce8a2, ; 402: lib_System.Security.Principal.dll.so => 129
	i32 u0xbe3f07c2, ; 403: lib_System.Runtime.CompilerServices.Unsafe.dll.so => 102
	i32 u0xbe4755f4, ; 404: System.Security.SecureString => 130
	i32 u0xbe592c0c, ; 405: System.Web => 154
	i32 u0xbefef58f, ; 406: System.Data.dll => 24
	i32 u0xbf506931, ; 407: System.Xml.XmlDocument.dll => 162
	i32 u0xbff2e236, ; 408: System.Threading => 149
	i32 u0xc04c3c0a, ; 409: System.Runtime.Handles.dll => 105
	i32 u0xc1c6ebf4, ; 410: System.Reflection.DispatchProxy.dll => 90
	i32 u0xc1cd9713, ; 411: TaoLive.Model.dll => 177
	i32 u0xc2a37b91, ; 412: System.Linq.Queryable.dll => 61
	i32 u0xc2a993fa, ; 413: System.Threading.Tasks.Extensions.dll => 143
	i32 u0xc3428433, ; 414: lib_System.Reflection.Emit.ILGeneration.dll.so => 91
	i32 u0xc35f7fa4, ; 415: System.Resources.Writer.dll => 101
	i32 u0xc37f65ce, ; 416: Microsoft.Win32.Registry.dll => 5
	i32 u0xc3888e16, ; 417: System.ComponentModel.Annotations.dll => 13
	i32 u0xc3ba1d80, ; 418: lib_System.Security.Cryptography.Csp.dll.so => 122
	i32 u0xc4251ff9, ; 419: System.Security.Cryptography.Encoding => 123
	i32 u0xc4684d9e, ; 420: lib_System.Security.Cryptography.Algorithms.dll.so => 120
	i32 u0xc4a8494a, ; 421: System.Text.Encoding => 136
	i32 u0xc4e76306, ; 422: System.Diagnostics.FileVersionInfo => 28
	i32 u0xc5b097e4, ; 423: System.Net.Requests.dll => 73
	i32 u0xc5b79d28, ; 424: System.Data => 24
	i32 u0xc69f3b41, ; 425: lib_System.Data.dll.so => 24
	i32 u0xc821fc10, ; 426: lib_System.ComponentModel.dll.so => 18
	i32 u0xc82afec1, ; 427: System.Text.Json => 138
	i32 u0xc8a662e9, ; 428: Java.Interop => 169
	i32 u0xc8d10307, ; 429: lib_System.Diagnostics.TraceSource.dll.so => 33
	i32 u0xca5de1fa, ; 430: System.Runtime.CompilerServices.Unsafe.dll => 102
	i32 u0xcae37e41, ; 431: System.Security.Cryptography.OpenSsl.dll => 124
	i32 u0xcb5af55c, ; 432: lib_System.Reflection.Emit.Lightweight.dll.so => 92
	i32 u0xcbab0a1b, ; 433: lib_TaoLive.Model.dll.so => 177
	i32 u0xcc6479a0, ; 434: System.Xml => 164
	i32 u0xcc7d82b4, ; 435: netstandard => 168
	i32 u0xcd5a809f, ; 436: System.Formats.Tar => 39
	i32 u0xce3fa116, ; 437: lib_System.Diagnostics.Process.dll.so => 29
	i32 u0xcef19b37, ; 438: System.ComponentModel.TypeConverter.dll => 17
	i32 u0xcf3163e6, ; 439: Mono.Android => 172
	i32 u0xcfbaacae, ; 440: System.Text.Json.dll => 138
	i32 u0xcfd0c798, ; 441: System.Transactions => 151
	i32 u0xd128d608, ; 442: System.Xml.Linq => 156
	i32 u0xd1854eb4, ; 443: System.Security.dll => 131
	i32 u0xd2757232, ; 444: System.Configuration => 19
	i32 u0xd2ff69f1, ; 445: System.Net.HttpListener => 66
	i32 u0xd398dec9, ; 446: TaoLive.BLL.dll => 176
	i32 u0xd4045e1b, ; 447: lib_System.dll.so => 165
	i32 u0xd404ddfe, ; 448: lib_System.Runtime.Intrinsics.dll.so => 109
	i32 u0xd432d20b, ; 449: System.Threading.Timer => 148
	i32 u0xd457e5c9, ; 450: lib_Microsoft.CSharp.dll.so => 1
	i32 u0xd4d2575b, ; 451: System.IO.FileSystem.AccessControl => 47
	i32 u0xd505225a, ; 452: lib_System.Xml.XPath.dll.so => 161
	i32 u0xd62c2dd3, ; 453: lib_LibVLCSharp.Android.AWindowModern.dll.so => 175
	i32 u0xd67a52b3, ; 454: System.Net.WebSockets.Client => 80
	i32 u0xd684ce80, ; 455: TaoLive.UI.Android.Tv => 0
	i32 u0xd6b443a3, ; 456: TaoLive.BLL => 176
	i32 u0xd715a361, ; 457: System.Linq.dll => 62
	i32 u0xd804d57a, ; 458: System.Runtime.InteropServices.RuntimeInformation => 107
	i32 u0xd889aee8, ; 459: lib_System.Threading.Channels.dll.so => 140
	i32 u0xd8dbab5d, ; 460: System.IO.FileSystem.Primitives.dll => 49
	i32 u0xd943a729, ; 461: System.ComponentModel.DataAnnotations => 14
	i32 u0xda2f27df, ; 462: System.Net.NetworkInformation => 69
	i32 u0xdae8aa5e, ; 463: Mono.Android.dll => 172
	i32 u0xdc5370c5, ; 464: lib_System.Web.HttpUtility.dll.so => 153
	i32 u0xdc96bdf5, ; 465: System.Net.WebProxy.dll => 79
	i32 u0xdd864306, ; 466: System.Runtime.Intrinsics => 109
	i32 u0xde7354ab, ; 467: System.Net.NameResolution => 68
	i32 u0xdecad304, ; 468: System.Net.Http.Json.dll => 64
	i32 u0xdf1b1ecd, ; 469: lib_System.ServiceProcess.dll.so => 133
	i32 u0xdf6f3870, ; 470: System.Diagnostics.DiagnosticSource => 27
	i32 u0xdf9a7f42, ; 471: System.Xml.XPath => 161
	i32 u0xdfd65a5d, ; 472: lib_System.Diagnostics.Tracing.dll.so => 34
	i32 u0xe12bf655, ; 473: LibVLCSharp.dll => 174
	i32 u0xe12f62fc, ; 474: lib_System.Threading.ThreadPool.dll.so => 147
	i32 u0xe1a41194, ; 475: lib_System.Xml.XDocument.dll.so => 159
	i32 u0xe1eea3e4, ; 476: lib_System.IO.Compression.ZipFile.dll.so => 45
	i32 u0xe2098b0b, ; 477: System.Collections.NonGeneric => 10
	i32 u0xe2513246, ; 478: lib_System.Runtime.Numerics.dll.so => 111
	i32 u0xe2a3f2e8, ; 479: System.Collections.Specialized.dll => 11
	i32 u0xe34ee011, ; 480: lib_System.IO.Pipelines.dll.so => 54
	i32 u0xe3774f52, ; 481: lib_System.IO.MemoryMappedFiles.dll.so => 53
	i32 u0xe3a54a09, ; 482: System.Net.WebProxy => 79
	i32 u0xe3c7860c, ; 483: lib_System.Security.Claims.dll.so => 119
	i32 u0xe3df9d2b, ; 484: System.Security.Cryptography.dll => 127
	i32 u0xe4436460, ; 485: System.Numerics.dll => 84
	i32 u0xe52378b9, ; 486: System.Net.Mail.dll => 67
	i32 u0xe56ef253, ; 487: System.Runtime.InteropServices.dll => 108
	i32 u0xe6b14171, ; 488: System.Net.HttpListener.dll => 66
	i32 u0xe6e179fa, ; 489: System.Security.Principal => 129
	i32 u0xe6f98713, ; 490: System.Security.Cryptography.Encoding.dll => 123
	i32 u0xe797fcc1, ; 491: System.Net.WebHeaderCollection.dll => 78
	i32 u0xe7c01049, ; 492: TaoLive.Model => 177
	i32 u0xe839deed, ; 493: System.Collections.Concurrent.dll => 8
	i32 u0xe89260c1, ; 494: Microsoft.VisualBasic.Core.dll => 2
	i32 u0xe92ace5f, ; 495: lib_System.Linq.Parallel.dll.so => 60
	i32 u0xe9b2d35e, ; 496: System.IO.Compression.FileSystem.dll => 44
	i32 u0xea0092d6, ; 497: lib_System.Threading.Tasks.Extensions.dll.so => 143
	i32 u0xea213423, ; 498: System.Xml.ReaderWriter => 157
	i32 u0xea4780ec, ; 499: System.Security.Principal.dll => 129
	i32 u0xeaf244cc, ; 500: lib_System.IO.Pipes.AccessControl.dll.so => 55
	i32 u0xeb2ecede, ; 501: System.Data.DataSetExtensions.dll => 23
	i32 u0xeb5560c9, ; 502: lib_System.Runtime.InteropServices.RuntimeInformation.dll.so => 107
	i32 u0xebac8bfe, ; 503: System.Text.Encoding.CodePages => 134
	i32 u0xebb0254b, ; 504: lib_System.Net.NetworkInformation.dll.so => 69
	i32 u0xed1090ae, ; 505: lib_System.Net.Primitives.dll.so => 71
	i32 u0xedf6669b, ; 506: lib_System.Drawing.dll.so => 36
	i32 u0xee9f991d, ; 507: System.Diagnostics.Process.dll => 29
	i32 u0xeeefb9c8, ; 508: lib_System.Dynamic.Runtime.dll.so => 37
	i32 u0xefd01a89, ; 509: System.IO.Pipelines => 54
	i32 u0xeff49a63, ; 510: System.Memory => 63
	i32 u0xeff49c4a, ; 511: lib_System.Text.Encoding.Extensions.dll.so => 135
	i32 u0xf09122fc, ; 512: lib_System.IO.IsolatedStorage.dll.so => 52
	i32 u0xf1ad867b, ; 513: System.Reflection.Emit.ILGeneration => 91
	i32 u0xf27f60d1, ; 514: System.Private.Xml.Linq.dll => 88
	i32 u0xf2ce3c98, ; 515: System.Threading.dll => 149
	i32 u0xf42589bc, ; 516: lib_System.Security.Cryptography.X509Certificates.dll.so => 126
	i32 u0xf45985cf, ; 517: System.Drawing.dll => 36
	i32 u0xf462c30d, ; 518: System.Private.Uri => 87
	i32 u0xf47b0a29, ; 519: lib_System.Configuration.dll.so => 19
	i32 u0xf53cb11d, ; 520: lib_System.Net.Security.dll.so => 74
	i32 u0xf60736e2, ; 521: System.IO.FileSystem.Watcher => 50
	i32 u0xf6318da0, ; 522: System.AppContext => 6
	i32 u0xf73be021, ; 523: System.Reflection.Emit.ILGeneration.dll => 91
	i32 u0xf76edc75, ; 524: System.Core => 21
	i32 u0xf7e95c85, ; 525: System.Xml.XmlSerializer => 163
	i32 u0xf807b767, ; 526: System.Reflection.TypeExtensions => 97
	i32 u0xf83dd773, ; 527: System.IO.FileSystem.Watcher.dll => 50
	i32 u0xf93ba7d4, ; 528: System.Runtime.Serialization.Primitives => 114
	i32 u0xf97c5a99, ; 529: System.Security => 131
	i32 u0xfa21f6af, ; 530: System.Net.WebClient.dll => 77
	i32 u0xfa50891f, ; 531: lib_System.Linq.dll.so => 62
	i32 u0xfb1dad5d, ; 532: System.Diagnostics.DiagnosticSource.dll => 27
	i32 u0xfbc4b67c, ; 533: lib_System.IO.Compression.Brotli.dll.so => 43
	i32 u0xfdf2741f, ; 534: System.Buffers => 7
	i32 u0xfecef6ea, ; 535: System.Runtime.Numerics => 111
	i32 u0xff912ee3 ; 536: lib_System.Xml.Serialization.dll.so => 158
], align 4

@assembly_image_cache_indices = dso_local local_unnamed_addr constant [537 x i32] [
	i32 69, i32 73, i32 68, i32 163, i32 109, i32 48, i32 81, i32 146,
	i32 30, i32 125, i32 103, i32 10, i32 61, i32 67, i32 108, i32 140,
	i32 31, i32 78, i32 125, i32 13, i32 133, i32 152, i32 160, i32 64,
	i32 75, i32 18, i32 44, i32 26, i32 1, i32 60, i32 42, i32 92,
	i32 130, i32 148, i32 23, i32 55, i32 70, i32 84, i32 117, i32 13,
	i32 132, i32 110, i32 56, i32 150, i32 75, i32 146, i32 63, i32 147,
	i32 178, i32 166, i32 123, i32 12, i32 126, i32 153, i32 171, i32 114,
	i32 167, i32 169, i32 165, i32 87, i32 116, i32 85, i32 125, i32 6,
	i32 151, i32 61, i32 51, i32 104, i32 115, i32 40, i32 121, i32 132,
	i32 11, i32 52, i32 21, i32 44, i32 0, i32 76, i32 120, i32 94,
	i32 82, i32 137, i32 22, i32 8, i32 51, i32 74, i32 156, i32 155,
	i32 164, i32 93, i32 45, i32 110, i32 131, i32 130, i32 25, i32 162,
	i32 73, i32 56, i32 46, i32 98, i32 90, i32 22, i32 146, i32 87,
	i32 43, i32 161, i32 72, i32 3, i32 42, i32 64, i32 56, i32 16,
	i32 58, i32 174, i32 53, i32 151, i32 41, i32 14, i32 106, i32 167,
	i32 34, i32 159, i32 86, i32 32, i32 12, i32 142, i32 51, i32 57,
	i32 36, i32 35, i32 59, i32 97, i32 12, i32 99, i32 17, i32 165,
	i32 93, i32 137, i32 124, i32 17, i32 79, i32 83, i32 65, i32 86,
	i32 155, i32 154, i32 141, i32 175, i32 101, i32 29, i32 52, i32 148,
	i32 5, i32 88, i32 115, i32 86, i32 62, i32 113, i32 58, i32 100,
	i32 173, i32 50, i32 19, i32 112, i32 102, i32 32, i32 3, i32 103,
	i32 84, i32 105, i32 72, i32 4, i32 38, i32 32, i32 57, i32 104,
	i32 74, i32 9, i32 124, i32 46, i32 9, i32 43, i32 4, i32 35,
	i32 31, i32 139, i32 93, i32 94, i32 49, i32 142, i32 113, i32 141,
	i32 2, i32 16, i32 116, i32 158, i32 77, i32 80, i32 39, i32 37,
	i32 65, i32 139, i32 15, i32 46, i32 157, i32 166, i32 117, i32 48,
	i32 71, i32 81, i32 127, i32 95, i32 122, i32 150, i32 26, i32 118,
	i32 98, i32 28, i32 103, i32 150, i32 54, i32 170, i32 4, i32 99,
	i32 38, i32 33, i32 78, i32 94, i32 106, i32 21, i32 41, i32 171,
	i32 104, i32 149, i32 2, i32 176, i32 135, i32 112, i32 59, i32 96,
	i32 39, i32 25, i32 95, i32 90, i32 100, i32 10, i32 172, i32 88,
	i32 49, i32 63, i32 77, i32 101, i32 47, i32 7, i32 82, i32 89,
	i32 155, i32 178, i32 33, i32 113, i32 117, i32 66, i32 83, i32 136,
	i32 20, i32 11, i32 163, i32 134, i32 3, i32 28, i32 112, i32 100,
	i32 27, i32 15, i32 7, i32 85, i32 59, i32 72, i32 30, i32 65,
	i32 144, i32 81, i32 158, i32 41, i32 118, i32 168, i32 132, i32 76,
	i32 67, i32 173, i32 174, i32 144, i32 107, i32 152, i32 20, i32 71,
	i32 157, i32 145, i32 122, i32 128, i32 153, i32 154, i32 89, i32 142,
	i32 96, i32 20, i32 14, i32 136, i32 76, i32 60, i32 168, i32 169,
	i32 15, i32 75, i32 138, i32 6, i32 23, i32 92, i32 1, i32 137,
	i32 135, i32 70, i32 147, i32 114, i32 25, i32 89, i32 97, i32 31,
	i32 45, i32 144, i32 110, i32 159, i32 35, i32 0, i32 108, i32 22,
	i32 115, i32 58, i32 139, i32 145, i32 119, i32 121, i32 111, i32 140,
	i32 55, i32 80, i32 106, i32 8, i32 121, i32 134, i32 152, i32 9,
	i32 48, i32 68, i32 160, i32 5, i32 164, i32 127, i32 133, i32 162,
	i32 141, i32 42, i32 170, i32 40, i32 82, i32 57, i32 37, i32 128,
	i32 98, i32 167, i32 173, i32 83, i32 99, i32 26, i32 30, i32 160,
	i32 18, i32 128, i32 120, i32 156, i32 70, i32 175, i32 166, i32 178,
	i32 105, i32 170, i32 171, i32 16, i32 145, i32 40, i32 126, i32 119,
	i32 38, i32 116, i32 47, i32 143, i32 85, i32 118, i32 34, i32 96,
	i32 53, i32 95, i32 129, i32 102, i32 130, i32 154, i32 24, i32 162,
	i32 149, i32 105, i32 90, i32 177, i32 61, i32 143, i32 91, i32 101,
	i32 5, i32 13, i32 122, i32 123, i32 120, i32 136, i32 28, i32 73,
	i32 24, i32 24, i32 18, i32 138, i32 169, i32 33, i32 102, i32 124,
	i32 92, i32 177, i32 164, i32 168, i32 39, i32 29, i32 17, i32 172,
	i32 138, i32 151, i32 156, i32 131, i32 19, i32 66, i32 176, i32 165,
	i32 109, i32 148, i32 1, i32 47, i32 161, i32 175, i32 80, i32 0,
	i32 176, i32 62, i32 107, i32 140, i32 49, i32 14, i32 69, i32 172,
	i32 153, i32 79, i32 109, i32 68, i32 64, i32 133, i32 27, i32 161,
	i32 34, i32 174, i32 147, i32 159, i32 45, i32 10, i32 111, i32 11,
	i32 54, i32 53, i32 79, i32 119, i32 127, i32 84, i32 67, i32 108,
	i32 66, i32 129, i32 123, i32 78, i32 177, i32 8, i32 2, i32 60,
	i32 44, i32 143, i32 157, i32 129, i32 55, i32 23, i32 107, i32 134,
	i32 69, i32 71, i32 36, i32 29, i32 37, i32 54, i32 63, i32 135,
	i32 52, i32 91, i32 88, i32 149, i32 126, i32 36, i32 87, i32 19,
	i32 74, i32 50, i32 6, i32 91, i32 21, i32 163, i32 97, i32 50,
	i32 114, i32 131, i32 77, i32 62, i32 27, i32 43, i32 7, i32 111,
	i32 158
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
