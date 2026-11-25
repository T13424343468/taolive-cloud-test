using Android.App;
using Android.OS;

namespace TaoLive.Phone;

/// <summary>手机净壳占位：M1 不验收，工程立起即可（W5 班接手势/播放页）。</summary>
[Activity(Label = "TaoLive", MainLauncher = true)]
public class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Android.Resource.Layout.SimpleListItem1);
    }
}
