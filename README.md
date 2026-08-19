# PDF转换器

专为 Windows 10/11 x64 打造的专业 PDF 转换、OCR 与授权文档去水印工具。完全离线运行，采用 .NET 8、WPF 和 MVVM 构建，使用 MIT 许可证。

应用不会上传文档，不覆盖原文件。转换结果使用唯一文件名；去水印结果默认增加 `_无水印` 后缀。

## 立即下载 PDF转换器

**[下载安装版（MSI，推荐）](https://github.com/tian-yu200/PDF-Converter/releases/latest/download/PDFConverter.Installer.msi)**

**[下载免安装版（ZIP）](https://github.com/tian-yu200/PDF-Converter/releases/latest/download/PDFConverter-win-x64.zip)**

安装版双击 MSI 即可安装。免安装版解压 ZIP 后运行 `PDFConverter.exe`。由于安装包暂未购买代码签名证书，Windows 首次运行时可能显示 SmartScreen 提示。

[查看全部版本与校验文件](https://github.com/tian-yu200/PDF-Converter/releases)

## 功能

| 输入 | 输出 | 实现方式 |
|---|---|---|
| PDF | DOCX | Microsoft Word PDF 重排；扫描件可先 OCR |
| PDF | PPTX | 固定 16:9 高保真视觉模式，每页使用一张高清 PNG |
| PDF | 直接编辑 | 页面内缩放、平移、框选，添加/移动/编辑文字、公式和图片；支持普通、原生、安全三种保存 |
| 扫描 PDF | 可搜索 PDF、DOCX | Tesseract `chi_sim+eng` |
| DOCX/XLSX/PPTX | PDF | 隐藏的独立 Office STA Worker |
| JPG/PNG/BMP/TIFF | PDF | PDFsharp |
| PDF | JPG/PNG | 150/200/300 DPI 逐页渲染，多页自动编号 |
| TXT | PDF | UTF-8、UTF-16、GB18030 检测，A4 自动换行分页 |

去水印支持 PDF、DOCX、XLSX、PPTX、JPG、PNG、BMP、TIFF。自动检测只提供候选，必须由用户勾选原生对象或手动框选区域后才会处理。

## v0.6.0 新功能：可视化 PDF 编辑

最新版新增独立的 PDF 编辑工作区，可直接打开 PDF，在页面预览上完成编辑并另存为新文件：

- 支持翻页、放大、缩小、适合窗口和平移查看，缩放时自动请求更高清的页面预览。
- 可点击现有文字块进行替换，也可在页面任意位置添加新文字并调整字号。
- 支持输入 LaTeX 公式，提供语法检查和实时公式预览，再放置到 PDF 页面。
- 支持插入 JPG、PNG、BMP、TIFF 图片，并在页面上拖动、调整位置和尺寸。
- 编辑元素可选择、移动、缩放、删除，并支持撤销和重做。
- 所有保存操作都会生成新的 PDF 副本，不覆盖原文件。

编辑完成后可根据用途选择三种保存方式：

- **普通保存**：以覆盖层方式写入文字、公式和图片，兼容性最好，适合一般编辑。
- **原生保存**：在受支持的简单 PDF 内容流中删除被替换的原始字形，再写入新内容；遇到无法安全定位的复杂结构会主动停止。
- **安全保存**：将编辑过的页面以 300 DPI 重建，适合希望降低原文字残留和被恢复风险的场景。

原生保存对复杂 PDF 的具体限制见下方“当前实现范围”。建议先保留源文件，并在发布或发送前检查生成结果。

## 安全边界

- 仅处理用户有权修改的文件。
- 拒绝只读、加密、无法读取、带数字签名或检测到权限限制的文档。
- 不移除数字签名，不绕过密码、DRM 或访问权限。
- Office Worker 在独立进程中运行；取消或超时时，只终止该任务创建的 Office 实例。
- 临时文件位于 `%LocalAppData%\DocConvert\Temp`，超过 24 小时的残留会在启动时清理。

## 系统要求

- Windows 10/11 x64。
- DOCX/PDF 路线需要 Microsoft Word；XLSX/PDF 需要 Excel；PPTX/PDF 需要 PowerPoint。
- 图片转 PDF、TXT 转 PDF、PDF 渲染、PDF 转 PPTX、OCR 和部分原生去水印不依赖 Microsoft Office。

PDF 转 PPTX 提供 150/200/300 DPI 清晰度。每张幻灯片只放置一张按比例完整居中的 PDF 页面图，不裁切、不拉伸，也不叠加 OCR 文字，因此适合投影、展示和讲解；页面内容不能逐元素编辑。

Word 的 PDF 重排能力取决于本机 Office 版本、首次运行状态、受保护视图和 PDF 复杂度。应用会在 5 分钟后终止无响应任务并清理它创建的 Word 实例。

## 开发

```powershell
dotnet restore DocConvert.sln
dotnet build DocConvert.sln -c Release --no-restore
dotnet test DocConvert.sln -c Release --no-build
```

自包含发布：

```powershell
dotnet publish src\DocConvert.App\DocConvert.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false `
  -o artifacts\publish\win-x64
```

构建 MSI：

```powershell
powershell -ExecutionPolicy Bypass -File installer\GeneratePayload.ps1 `
  -PublishDir artifacts\publish\win-x64 `
  -OutputFile installer\Payload.wxs
dotnet build installer\DocConvert.Installer.wixproj -c Release `
  -p:PublishDir="$((Resolve-Path artifacts\publish\win-x64).Path)" `
  -p:OutputPath="$pwd\artifacts\installer\"
```

安装程序为每用户 x64 MSI，默认安装到 `%LocalAppData%\Programs\DocConvert`，包含开始菜单快捷方式和可选桌面快捷方式。

## 当前实现范围

- PDF 去水印目前对用户框选区域进行 300 DPI 渲染和 OpenCV 修复，只重建受影响页；尚未实现 PDFLexer 内容流级注释或 XObject 定向删除。
- PDF 直接编辑支持页面缩放、平移、拖拽框选和文字/公式预编辑。普通保存以覆盖方式兼容性最高；原生保存会在受支持的简单文字内容流中删除选中字形并写入替换文字；安全保存会把编辑页重建为 300 DPI 图像。
- 原生保存当前保守拒绝带名称树或标签结构、加密、旋转页面、Form XObject 文字、倾斜/旋转文字，以及无法唯一映射到原始字形的复杂 PDF；被拒绝时不会生成最终文件，请改用普通保存或安全保存。
- 栅格化后的 PDF 处理页目前不会重新添加隐藏 OCR 文字层。
- 当前预览支持 PDF 翻页和多帧 TIFF 逐帧选择；尚未提供画笔、橡皮擦和前后对比视图。
- Office 去水印按 Open XML 原生结构检测和删除，当前没有 Word/Excel/PowerPoint 的视觉预览。
- PDF 转 PPTX 以展示效果为优先，当前不提供文字、图片、表格或图形的原生可编辑重建。
- 图片批量任务逐文件生成 PDF；尚未提供在一个任务中将多张图片合并成单个 PDF 的界面。

## 开源说明

本项目参考 FileConverter 的任务/引擎适配思路，以及 Stirling-PDF 的功能分类、批处理和错误反馈方式，但没有复制或链接这两个项目的源代码。完整依赖与许可证见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
