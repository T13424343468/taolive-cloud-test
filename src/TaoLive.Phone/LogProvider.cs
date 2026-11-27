using Android.Content;
using Android.Database;
using Android.OS;

namespace TaoLive.Phone;

/// <summary>
/// vc111 诊断出口（v12/v26 原型「打开源文件」按钮）：最小只读 ContentProvider——
/// 仅暴露本应用日志文件（FilesDir 与外部应用目录白名单内文件），供系统查看器经 content URI 打开；
/// 不写不删不列目录，白名单外一律拒绝。目标框架无 AndroidX，FileProvider 不可用，自实现最小面。
/// </summary>
[ContentProvider(new[] { "com.taolive.phone.logs" }, Exported = false, GrantUriPermissions = true)]
public class LogProvider : ContentProvider
{
    private static readonly string[] AllowedNames = { "taolive_crash.log", "taolive_diag.txt" };

    public override bool OnCreate() => true;

    public override ParcelFileDescriptor? OpenFile(Android.Net.Uri uri, string mode)
    {
        if (mode != "r") throw new Java.Lang.SecurityException("只读");
        var name = uri.LastPathSegment ?? "";
        if (Array.IndexOf(AllowedNames, name) < 0)
            throw new Java.IO.FileNotFoundException("文件不存在");
        var inner = new Java.IO.File(Context!.FilesDir!, name);
        var file = inner.Exists() ? inner : new Java.IO.File(Context.GetExternalFilesDir(null)!, name);
        if (!file.Exists()) throw new Java.IO.FileNotFoundException("暂无日志文件");
        return ParcelFileDescriptor.Open(file, ParcelFileMode.ReadOnly);
    }

    public override string? GetType(Android.Net.Uri uri) => "text/plain";

    public override ICursor? Query(Android.Net.Uri uri, string[]? projection, string? selection,
        string[]? selectionArgs, string? sortOrder) => null;

    public override Android.Net.Uri? Insert(Android.Net.Uri uri, ContentValues? values) => null;

    public override int Delete(Android.Net.Uri uri, string? selection, string[]? selectionArgs) => 0;

    public override int Update(Android.Net.Uri uri, ContentValues? values, string? selection, string[]? selectionArgs) => 0;
}
