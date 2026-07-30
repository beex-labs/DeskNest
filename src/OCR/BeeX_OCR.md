# BeeX_OCR

BeeX_OCR 是一个轻量 Windows OCR 小工具，交互方式参考 PowerToys Text Extractor，并沿用 BeeX_ClearWindow 的窗口模板、Logo 位置、弹窗样式和托盘右键菜单风格。

除独立使用外，BeeX_OCR 还作为 **BeeX DeskNest 的截图 OCR 侧车**：DeskNest 截图工具条上的「截圖辨識」按钮通过常驻进程协议调用本项目，DeskNest 主程序 exe 不增加任何 OCR 依赖体积。

## 功能

- 框选屏幕区域并识别文字。
- 识别图片文件、剪贴板图片。
- 识别结果显示在主窗口，并自动复制到剪贴板。
- **数学公式识别**：本地 PP-FormulaNet_plus-S 模型，输出 LaTeX（`--formula-file`）。
- 在窗口内翻译识别结果，并可复制译文。
- 关闭窗口时缩放到右下角托盘。

## 引擎架构（v2）

| 能力 | 模型 | 运行时 | 产物 |
|---|---|---|---|
| 文字检测 | PP-OCRv5_mobile_det（截屏场景精度几无损失，速度快一个量级） | MKL + oneDNN | BeeX_OCR.exe |
| 文字识别 | PP-OCRv5_server_rec（高精度档） | MKL + oneDNN | BeeX_OCR.exe |
| 公式识别 | PP-FormulaNet_plus-S（En-BLEU 88.7，输出 LaTeX） | openblas | BeeX_Formula.exe |

模型不再内嵌进 exe，统一放在 exe 同目录 `models\` 下（开发环境为项目根 `models-src\`，用 `scripts\download-models.ps1` 下载）。

**为什么是两个 exe**：PP-FormulaNet 的 PIR 推理图在带 oneDNN 的 MKL 运行时下必然崩溃（`onednn_op.scale` 抛 `dnnl::error`），且 oneDNN 替换无法通过 `OneDnnEnabled`、`DeletePass`、`FLAGS_*`、算子白名单或 ONNX 后端绕开（均已实测排除）；而文字 OCR 的 server 识别模型在无 oneDNN 的 openblas 下慢约 10 倍（2s → 19s）。因此文字与公式各用一个运行时互斥的侧车进程，共享同一份代码与模型目录。

公式识别为纯 C# 实现（无 Python 依赖）：UniMERNet 预处理（裁边 → 等比缩放 384×384 → 黑边填充 → 灰度归一化）→ Paddle 原生推理 → GPT-2 风格 ByteLevel BPE 解码（词表内嵌于模型 inference.yml）。

## CLI

```powershell
BeeX_OCR.exe --ocr-file 图片.png [--ocr-out 结果.txt] [--ocr-repeat N] [--ocr-debug]
BeeX_OCR.exe --formula-file 公式图.png [--ocr-out 结果.txt]
BeeX_OCR.exe --serve [--serve-role ocr|formula]   # DeskNest 侧车常驻模式
```

`--serve` 行协议（stdin/stdout，UTF-8）：启动完成输出 `READY`；请求 `OCR\t图片路径\t结果文件` 或 `FORMULA\t图片路径\t结果文件`，响应 `OK\t耗时ms` 或 `ERR\t错误信息`；`EXIT` 退出。

## 运行 / 构建

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\download-models.ps1   # 首次：下载模型到 models-src
dotnet run --project .\BeeX_OCR.csproj
dotnet build .\BeeX_OCR.csproj
```

## 发布

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

发布后在 `releases\<时间戳>\portable-net8` 生成侧车布局：

```
portable-net8\
├── BeeX_OCR.exe        （MKL 运行时，文字 OCR）
├── BeeX_Formula.exe    （openblas 运行时，公式识别）
└── models\
    ├── PP-OCRv5_mobile_det\
    ├── PP-OCRv5_server_rec\
    └── PP-FormulaNet_plus-S\
```

把整个目录拷贝到 DeskNest 便携目录的 `ocr\` 子目录即可启用 DeskNest 截图 OCR。

## 实测性能（参考）

- 文字 OCR（760×240 截屏样张，MKL）：约 2.0 s/次，常驻模式首次 READY 约 2 s。
- 公式识别（openblas）：单次进程含模型加载约 6.4 s；常驻模式下模型只加载一次。

翻译功能默认使用在线翻译接口，需要网络连接；长文本会自动分段翻译后合并，翻译失败不会影响 OCR 识别和复制结果。
