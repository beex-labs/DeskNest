using System.Runtime.InteropServices;
using System.Text;

namespace BeeX.DeskNest;

/// <summary>Simplified/Traditional Chinese conversion helper via Windows API.</summary>
internal static class ChineseConversion
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]
    static extern int LCMapStringEx(string? locale,uint flags,string src,int srcLen,[Out]StringBuilder dst,int dstLen,IntPtr version,IntPtr reserved,IntPtr sortHandle);
    const uint LCMAP_SIMPLIFIED_CHINESE=0x02000000;
    const uint LCMAP_TRADITIONAL_CHINESE=0x04000000;

    /// <summary>Traditional -> Simplified.</summary>
    public static string ToSimplified(string s)
    {
        if(string.IsNullOrEmpty(s))return s;
        var sb=new StringBuilder(s.Length+1);
        var r=LCMapStringEx("zh-CN",LCMAP_SIMPLIFIED_CHINESE,s,s.Length,sb,sb.Capacity,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        return r>0?sb.ToString(0,r):s;
    }
    /// <summary>Simplified -> Traditional.</summary>
    public static string ToTraditional(string s)
    {
        if(string.IsNullOrEmpty(s))return s;
        var sb=new StringBuilder(s.Length+1);
        var r=LCMapStringEx("zh-TW",LCMAP_TRADITIONAL_CHINESE,s,s.Length,sb,sb.Capacity,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        return r>0?sb.ToString(0,r):s;
    }
    /// <summary>Generates variants in both language versions (original + converted).</summary>
    public static IEnumerable<(string title,string artist,string variant)> LrcVariants(string title,string artist)
    {
        yield return (title,artist,"原版");
        // If it contains Chinese, generate simplified/traditional cross-converted versions
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
