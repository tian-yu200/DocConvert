# PDF 内容流原生编辑研究记录

记录日期：2026-08-19

范围：核对 `tmp/stage3-research/` 的源码/包元数据、`tmp/stage3-poc/` 的本地 PoC 与产品验收产物，并以官方 GitHub/NuGet 一手来源交叉验证。

## 结论

第一版原生编辑采用 **PdfLexer 0.1.27 删除原字形 + 现有 PDFsharp 追加替换内容**。PdfPig 继续负责文字与边界提取；PDF4QT 仅作成熟编辑器架构参考，不引入其 C++/Qt 运行时。原生保存只面向可唯一定位的简单水平文字，其余文档明确拒绝并引导使用普通覆盖保存或安全栅格化保存。

## 项目、许可证与用途

| 项目 | 许可证 | 上游定位 | 本产品用途与判断 | 一手来源 |
|---|---|---|---|---|
| PDF4QT | MIT；上游说明于 2025-04-27 从 LGPLv3 改为 MIT | PDF 渲染库及查看器、编辑器、页面工具和 CLI；构建要求 C++20、Qt 6.9+ | 功能完整，适合参考编辑器分层和交互；与当前 .NET 8/WPF 技术栈差异大，不直接集成 | [README](https://github.com/JakubMelka/PDF4QT/blob/master/README.md)、[LICENSE](https://github.com/JakubMelka/PDF4QT/blob/master/LICENSE)、[仓库](https://github.com/JakubMelka/PDF4QT) |
| PDFLexer | MIT | 面向 .NET 的 PDF 解析和修改，公开可变页面内容模型；上游仍将 `Mutable Content` 标为 WIP | 承担受限范围内的内容流解析、字形删除和页面重写 | [v0.1.27 README](https://github.com/pdflexer/pdflexer/blob/v0.1.27/README.md)、[LICENSE](https://github.com/pdflexer/pdflexer/blob/v0.1.27/LICENSE)、[NuGet 0.1.27](https://www.nuget.org/packages/PdfLexer/0.1.27) |
| PdfPig | Apache-2.0 | 读取 PDF 并提取文字、单词和其他页面内容，也支持基础 PDF 创建 | 继续用于单词文本和边界提取、保存结果文字层验收；不负责重写原内容流 | [README](https://github.com/UglyToad/PdfPig/blob/master/README.md)、[LICENSE](https://github.com/UglyToad/PdfPig/blob/master/LICENSE)、[NuGet 0.1.11](https://www.nuget.org/packages/PdfPig/0.1.11) |

本地对应证据：`src/DocConvert.Infrastructure.Windows/DocConvert.Infrastructure.Windows.csproj`、`tmp/stage3-research/tag-0.1.27/README.md`、`tmp/stage3-research/source/LICENSE`。

## 为何选择 PdfLexer 0.1.27

不能只按 SemVer 数字判断新旧：

- `0.1.27` 包含 `net8.0`、`net10.0` 资产，NuGet 注册信息显示其于 2026-05-14 发布；包固定到提交 `7c9c352...`，与当前 `net8.0-windows` 产品和 PoC 直接兼容。[NuGet 注册元数据](https://api.nuget.org/v3/registration5-gz-semver2/pdflexer/index.json)、[提交](https://github.com/pdflexer/pdflexer/commit/7c9c3524b3cfcad3346edb12d310a3c073a3724d)、[标签](https://github.com/pdflexer/pdflexer/tree/v0.1.27)
- `1.0.0` 包只有 `net7.0` 资产，当前 NuGet 注册信息将其列为未列出；其包固定提交 `e96db95...` 的日期为 2023-06-25，因此它实际属于更早的代码/API 代际。[NuGet 1.0.0](https://www.nuget.org/packages/PdfLexer/1.0.0)、[提交](https://github.com/pdflexer/pdflexer/commit/e96db95ef853379739caa5d427e79129cc2a1b3c)
- `0.1.27` 提供本实现已验证的 `GetContentNodes`、`TextContent`、`GlyphOrShift` 和 `CachedContentMutation` 路径；选择依据是目标框架、当前 API 与 PoC，而不是版本号大小。[TextContent.cs](https://github.com/pdflexer/pdflexer/blob/v0.1.27/src/PdfLexer/Content/Model/TextContent.cs)、[CachedContentMutation.cs](https://github.com/pdflexer/pdflexer/blob/v0.1.27/src/PdfLexer/Content/Model/CachedContentMutation.cs)

本地包证据：`tmp/stage3-research/pdflexer-0.1.27-package/PdfLexer.nuspec` 与 `tmp/stage3-research/pdflexer-package/PdfLexer.nuspec`。

## 原生局部字形删除方案

1. 保留选中文字时的 `OriginalText` 和原始归一化选区；替换框后续移动或缩放不改变源选区。
2. 将源选区转换为 PDF 页面坐标，用 `CopyArea(target)?.Text` 与 `OriginalText` 严格比较，并要求全页只匹配一个 `TextContent` 节点；零个或多个匹配都拒绝保存。
3. 按 `GetGlyphBoundingBoxes()` 的顺序遍历每个 `TextSegment.Glyphs`。命中选区的字形不写回；其字宽、字符间距、词间距及既有 `TJ` 数值位移累积为补偿位移，在下一个保留字形前重新写入，保证尾随文字位置不前移。
4. 保留原 `LineMatrix`、段图形状态和未选字形；若节点全部删空则返回 `null`。通过 `CachedContentMutation` 替换且只允许一个节点发生改变，再重写页面内容流。
5. 在删除后的中间 PDF 上追加替换文字、图片或公式，不绘制白色遮盖矩形。

上游 `TextContent.Split(rect).Outside` 会通过裁剪保留部分原字形，文字提取器仍可能读到被裁掉的字符，因此不能把它当作真实删除；最终实现直接重建字形列表。相关一手实现：[TextContent.SplitInternal](https://github.com/pdflexer/pdflexer/blob/v0.1.27/src/PdfLexer/Content/Model/TextContent.cs)、[内容节点重写](https://github.com/pdflexer/pdflexer/blob/v0.1.27/src/PdfLexer/Content/Model/CachedContentMutation.cs)。

本地产品实现：`src/DocConvert.Infrastructure.Windows/PdfEditingService.cs` 中的 `RemoveOriginalText`、`RemoveSelectedGlyphs` 和 `GetGlyphAdvance`。

## 已知保存风险

PdfLexer 上游明确说明 `PdfDocument.SaveTo()` 是以页面为中心的重写：

- 现有目录 `/Names` 不保留，可能影响命名目标、附件/嵌入文件、JavaScript 等名称树功能。
- 现有 `/StructTreeRoot` 在未显式重建时不保留，可能破坏标签 PDF、阅读顺序和无障碍结构。
- 加密 PDF 重写时不保留原加密设置，可能改变密码、权限和安全策略。

一手来源：[PdfLexer v0.1.27 README - Current Save Behavior](https://github.com/pdflexer/pdflexer/blob/v0.1.27/README.md#current-save-behavior)。由于可变内容仍为 WIP，当前方案必须采用白名单式能力范围，而不能把 PdfLexer 当作无损通用 PDF 往返保存器。

## 当前产品的保守拒绝范围

所有保存模式的公共安全入口拒绝：文件不存在或只读、检测到签名标记、加密/权限受限/损坏而无法被 PdfPig 打开、没有编辑内容、输入输出同路径、编辑页码越界。

原生内容保存额外拒绝：

- 没有文字替换对象；
- 文档目录含 `/Names` 或 `/StructTreeRoot`；
- 页面带旋转；
- 页面包含或混用 Form XObject；
- 缺少原文字或原始选区；
- 选区不能唯一映射到一个原文字节点；
- 字形与边界数量不一致，或实际命中文字与 `OriginalText` 不同；
- 字号为零，或文字不是水平、未倾斜状态。

当前公共安全入口会在修改前显式拒绝带 `/Encrypt` 的 PDF，并通过 PDFsharp 安全设置进行二次检查；因此，即使文件可用空用户口令读取，也不会进入会移除原加密设置的保存路径。上游风险依据：[保存行为](https://github.com/pdflexer/pdflexer/blob/v0.1.27/README.md#current-save-behavior)。

本地拒绝逻辑：`src/DocConvert.Infrastructure.Windows/DocumentSafety.cs`、`src/DocConvert.Infrastructure.Windows/PdfEditingService.cs`。

## PoC 与产品验收

### PoC

受控两页 PDF 的原生删除 PoC 已通过：目标原文字从内容流/文字层消失，第二页内容保持不变，两页均能重新打开并渲染。早期 PoC 源码未单独留档，同一路径的 `Program.cs` 已演进为下节的产品验收脚本；该轮现存证据为：

- 删除产物：`tmp/stage3-poc/output/native-removed.pdf`
- 渲染产物：`tmp/stage3-poc/output/render/page-0001.png`、`page-0002.png`
- 依赖版本：`tmp/stage3-poc/Stage3PdfLexerPoc.csproj`

### 产品验收

2026-08-19 的受控验收通过，脚本在所有断言完成后输出 `PRODUCT VALIDATION PASS`：

- `NativeContent`：`ORIGINAL` 不再可提取，尾随 `TEXT` 与新增 `REPLACED` 保留，第二页 `UNTOUCHED PAGE` 保留。
- `Overlay`：原 `ORIGINAL` 仍在文字层，同时存在 `REPLACED`。
- `SecureRasterized`：编辑页原文字层消失，未编辑第二页仍保留文字层。
- 三种输出均保持两页，并生成非空页面渲染图；Native 与 Overlay 的受控页面渲染哈希一致，第二页在三种模式下的渲染哈希一致。

验收程序与断言：`tmp/stage3-poc/Program.cs`、`tests/DocConvert.Tests/PdfEditingTests.cs`。产物位于 `tmp/stage3-poc/output/product-validation/`。文字提取验收所依据的上游能力见 [PdfPig README](https://github.com/UglyToad/PdfPig/blob/master/README.md)，内容流改写所依据的上游能力见 [PdfLexer v0.1.27](https://github.com/pdflexer/pdflexer/tree/v0.1.27)。

本记录依据已有脚本和现存产物汇总；本次文档任务未重新构建、运行测试或改写 PoC 产物。
