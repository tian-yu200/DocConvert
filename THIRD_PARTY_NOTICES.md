# Third-Party Notices

DocConvert is licensed under the MIT License. The following pinned components are used by or shipped with version 0.1.0.

| Component | Version | Purpose | License | Source |
|---|---:|---|---|---|
| CommunityToolkit.Mvvm | 8.4.2 | WPF MVVM source generators and commands | MIT | https://github.com/CommunityToolkit/dotnet |
| DocumentFormat.OpenXml | 3.5.1 | Create and modify DOCX, XLSX and PPTX packages | MIT | https://github.com/dotnet/Open-XML-SDK |
| PDFtoImage | 5.4.0 | PDF page rendering API | MIT | https://github.com/sungaila/PDFtoImage |
| bblanchon.PDFium.Win32 | 152.0.7961 | Windows PDFium native renderer used by PDFtoImage | BSD-3-Clause | https://github.com/bblanchon/pdfium-binaries |
| PdfPig | 0.1.11 | PDF text and layout extraction | Apache-2.0 | https://github.com/UglyToad/PdfPig |
| PDFsharp | 6.2.3 | PDF creation, import and page assembly | MIT | https://github.com/empira/PDFsharp |
| OpenCvSharp4 | 4.13.0.20260627 | .NET bindings for image masks and inpainting | Apache-2.0 | https://github.com/shimat/opencvsharp |
| OpenCvSharp4.runtime.win | 4.13.0.20260627 | Windows OpenCV native runtime | Apache-2.0 | https://github.com/shimat/opencvsharp |
| Tesseract .NET wrapper | 5.2.0 | Local OCR integration | Apache-2.0 | https://github.com/charlesw/tesseract |
| Tesseract OCR | 5.x runtime supplied by the wrapper | OCR engine | Apache-2.0 | https://github.com/tesseract-ocr/tesseract |
| tessdata_fast eng | repository snapshot downloaded 2026-08-17, SHA-256 `7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2` | English OCR model | Apache-2.0 | https://github.com/tesseract-ocr/tessdata_fast |
| tessdata_fast chi_sim | repository snapshot downloaded 2026-08-17, SHA-256 `A5FCB6F0DB1E1D6D8522F39DB4E848F05984669172E584E8D76B6B3141E1F730` | Simplified Chinese OCR model | Apache-2.0 | https://github.com/tesseract-ocr/tessdata_fast |
| SkiaSharp | 4.150.1 | Transitive raster graphics dependency | MIT | https://github.com/mono/SkiaSharp |
| WiX Toolset SDK | 5.0.2 | MSI build tooling; not loaded by the application | MS-RL | https://github.com/wixtoolset/wix |
| WiX Toolset UI extension | 5.0.2 | Standard feature-selection installer UI | MS-RL | https://github.com/wixtoolset/wix |

The installer and release ZIP contain the applicable license files and notices supplied by NuGet packages where those packages include them. Upstream copyright notices remain the property of their respective owners.

## Reference-only projects

- FileConverter: https://github.com/Tichau/FileConverter, GPL-3.0. Referenced only for high-level Windows conversion task and engine-adapter design. No source code was copied, linked, or redistributed.
- Stirling-PDF: https://github.com/Stirling-Tools/Stirling-PDF. Referenced only for high-level feature grouping, batch workflow and error-feedback ideas. No source code was copied, linked, or redistributed. Review the current upstream license before reusing any code.

DocConvert intentionally does not include PyMuPDF, iText, Ghostscript, or other AGPL/commercial dual-license conversion engines.
