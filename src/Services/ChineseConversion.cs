using System.Runtime.InteropServices;
using System.Text;

namespace BeeX.DeskNest;

/// <summary>Windows API 简繁中文转换工具</summary>
internal static class ChineseConversion
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]
    static extern int LCMapStringEx(string? locale,uint flags,string src,int srcLen,[Out]StringBuilder dst,int dstLen,IntPtr version,IntPtr reserved,IntPtr sortHandle);
    const uint LCMAP_SIMPLIFIED_CHINESE=0x02000000;
    const uint LCMAP_TRADITIONAL_CHINESE=0x04000000;

    /// <summary>繁体→简体</summary>
    public static string ToSimplified(string s)
    {
        if(string.IsNullOrEmpty(s))return s;
        var sb=new StringBuilder(s.Length+1);
        var r=LCMapStringEx("zh-CN",LCMAP_SIMPLIFIED_CHINESE,s,s.Length,sb,sb.Capacity,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        return r>0?sb.ToString(0,r):s;
    }
    /// <summary>简体→繁体</summary>
    public static string ToTraditional(string s)
    {
        if(string.IsNullOrEmpty(s))return s;
        var sb=new StringBuilder(s.Length+1);
        var r=LCMapStringEx("zh-TW",LCMAP_TRADITIONAL_CHINESE,s,s.Length,sb,sb.Capacity,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        return r>0?sb.ToString(0,r):s;
    }
    /// <summary>生成两个语言版本的变体（原版 + 转换版）</summary>
    public static IEnumerable<(string title,string artist,string variant)> LrcVariants(string title,string artist)
    {
        yield return (title,artist,"原版");
        // 如果包含中文，生成简繁互转版本
        if(ContainsChinese(title)||ContainsChinese(artist))
        {
            var tS=ToSimplified(title);var aS=ToSimplified(artist);
            if(tS!=title||aS!=artist)yield return (tS,aS,"简体");
            var tT=ToTraditional(title);var aT=ToTraditional(artist);
            if(tT!=title||aT!=artist)yield return (tT,aT,"繁体");
        }
    }
    public static bool ContainsChinese(string s)=>s.Any(c=>c>=0x4E00&&c<=0x9FFF);
}
