# 文档转换器 DocConvert

面向 Windows 10/11 x64 的离线文档转换与去水印桌面工具，采用 .NET 8、WPF 和 MVVM 构建，使用 MIT 许可证。

应用不会上传文档，不覆盖原文件。转换结果使用唯一文件名；去水印结果默认增加 `_无水印` 后缀。

## 下载 Windows 版

**[下载安装版（MSI）](https://github.com/tian-yu200/DocConvert/releases/latest/download/DocConvert.Installer.msi)**

**[下载免安装版（ZIP）](https://github.com/tian-yu200/DocConvert/releases/latest/download/DocConvert-win-x64.zip)**

安装版双击 MSI 即可安装。免安装版解压 ZIP 后运行 `DocConvert.exe`。由于安装包暂未购买代码签名证书，Windows 首次运行时可能显示 SmartScreen 提示。

[查看全部版本与校验文件](https://github.com/tian-yu200/DocConvert/releases)

## 功能

| 输入 | 输出 | 实现方式 |
|---|---|---|
| PDF | DOCX | Microsoft Word PDF 重排；扫描件可先 OCR |
| PDF | PPTX | 固定 16:9 高保真视觉模式，每页使用一张高清 PNG |
| 扫描 PDF | 可搜索 PDF、DOCX | Tesseract `chi_sim+eng` |
| DOCX/XLSX/PPTX | PDF | 隐藏的独立 Office STA Worker |
| JPG/PNG/BMP/TIFF | PDF | PDFsharp |
| TXT | PDF | UTF-8、UTF-16、GB18030 检测，A4 自动换行分页 |

去水印支持 PDF、DOCX、XLSX、PPTX、JPG、PNG、BMP、TIFF。自动检测只提供候选，必须由用户勾选原生对象或手动框选区域后才会处理。

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

- PDF 去水印目前对用户框选区域进行 300 DPI 渲染和 OpenCV 修复，只重建受影响页；尚未实现 PDFLexer 内容流级文字、注释或 XObject 定向删除。
- 栅格化后的 PDF 处理页目前不会重新添加隐藏 OCR 文字层。
- 当前预览支持 PDF 翻页和多帧 TIFF 逐帧选择；尚未提供画笔、橡皮擦和前后对比视图。
- Office 去水印按 Open XML 原生结构检测和删除，当前没有 Word/Excel/PowerPoint 的视觉预览。
- PDF 转 PPTX 以展示效果为优先，当前不提供文字、图片、表格或图形的原生可编辑重建。
- 图片批量任务逐文件生成 PDF；尚未提供在一个任务中将多张图片合并成单个 PDF 的界面。

## 开源说明

本项目参考 FileConverter 的任务/引擎适配思路，以及 Stirling-PDF 的功能分类、批处理和错误反馈方式，但没有复制或链接这两个项目的源代码。完整依赖与许可证见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
