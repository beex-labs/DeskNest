// 离线合成动态壁纸阶段一测试视频：颜色分段 + 叠加测试图卡，MediaFoundation 输出 1080p MP4。
// 用法：dotnet run --project tools\TestWallpaperGen [输出目录，默认 D:\BeeX\TestWallpapers]
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.UI;

var outDir = args.Length > 0 ? args[0] : @"D:\BeeX\TestWallpapers";
Directory.CreateDirectory(outDir);
var folder = await StorageFolder.GetFolderFromPathAsync(outDir);

// A：品牌橙/青/紫/藏青；B：红/黄/绿/蓝。每段 1.5s，共 6s；结尾回卷首色便于检验无缝循环。
await RenderAsync("TestVideo_A_ColorCycle.mp4", [(255, 138, 0), (0, 184, 169), (124, 77, 255), (13, 19, 33)]);
await RenderAsync("TestVideo_B_ColorCycle.mp4", [(220, 40, 40), (250, 200, 0), (40, 180, 80), (40, 90, 220)]);

// 若测试图卡已生成，再产出一条"图卡 + 颜色闪段"混合视频：既能核对对位/裁切，又能一眼看出播放/暂停。
var card = Path.Combine(outDir, "TestImage_A_1080p.png");
if (File.Exists(card))
{
    var comp = new MediaComposition();
    var imgFile = await StorageFile.GetFileFromPathAsync(card);
    for (int i = 0; i < 3; i++)
    {
        comp.Clips.Add(await MediaClip.CreateFromImageFileAsync(imgFile, TimeSpan.FromSeconds(1.5)));
        comp.Clips.Add(MediaClip.CreateFromColor(Color.FromArgb(255, 255, 138, 0), TimeSpan.FromSeconds(0.5)));
    }
    await SaveAsync(comp, "TestVideo_C_CardBlink.mp4");
}

Console.WriteLine($"Done. Output: {outDir}");

async Task RenderAsync(string name, (byte R, byte G, byte B)[] colors)
{
    var comp = new MediaComposition();
    foreach (var (r, g, b) in colors)
        comp.Clips.Add(MediaClip.CreateFromColor(Color.FromArgb(255, r, g, b), TimeSpan.FromSeconds(1.5)));
    await SaveAsync(comp, name);
}

async Task SaveAsync(MediaComposition comp, string name)
{
    var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
    var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
    var result = await comp.RenderToFileAsync(file, MediaTrimmingPreference.Precise, profile);
    if (result != TranscodeFailureReason.None) throw new InvalidOperationException($"Render failed: {result}");
    Console.WriteLine($"  video -> {Path.Combine(outDir, name)}");
}
