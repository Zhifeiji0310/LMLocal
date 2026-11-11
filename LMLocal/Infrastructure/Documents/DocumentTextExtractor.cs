using System;
using System.IO;
using System.Linq;
using System.Text;
using NPOI.HWPF;
using NPOI.HWPF.Extractor;
using NPOI.HSSF.UserModel;
using NPOI.SS.Extractor;
using NPOI.XWPF.UserModel;
using NPOI.XWPF.Extractor;
using NPOI.XSSF.UserModel;
using NPOI.XSSF.Extractor;
using NPOI.HSLF.UserModel;
using NPOI.HSLF.Extractor;
using NPOI.XSLF.UserModel;
using NPOI.XSLF.Extractor;
using UglyToad.PdfPig;

namespace LMLocal.Infrastructure.Documents
{
    internal static class DocumentTextExtractor
    {
        public static string Extract(string fileName, string base64)
        {
            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("Document name and content are required.");

            byte[] bytes = Convert.FromBase64String(base64);
            if (bytes.Length == 0 || bytes.Length > 10 * 1024 * 1024)
                throw new InvalidDataException("Document is empty or exceeds the 10 MB limit.");

            string extension = Path.GetExtension(fileName).ToLowerInvariant();
            using (var stream = new MemoryStream(bytes))
            {
                switch (extension)
                {
                    case ".doc":
                        using (var document = new HWPFDocument(stream))
                        using (var extractor = new WordExtractor(document))
                            return extractor.Text;
                    case ".docx":
                        using (var document = new XWPFDocument(stream))
                        using (var extractor = new XWPFWordExtractor(document))
                            return extractor.Text;
                    case ".xls":
                        using (var workbook = new HSSFWorkbook(stream))
                        using (var extractor = new ExcelExtractor(workbook))
                            return extractor.Text;
                    case ".xlsx":
                        using (var workbook = new XSSFWorkbook(stream))
                        using (var extractor = new XSSFExcelExtractor(workbook))
                            return extractor.Text;
                    case ".ppt":
                        using (var show = new HSLFSlideShow(stream))
                        using (var extractor = new PowerPointExtractor(show))
                            return extractor.Text;
                    case ".pptx":
                        using (var show = new XMLSlideShow(stream))
                        using (var extractor = new XSLFPowerPointExtractor(show))
                            return extractor.Text;
                    case ".pdf":
                        using (var pdf = PdfDocument.Open(stream))
                        {
                            var builder = new StringBuilder();
                            foreach (var page in pdf.GetPages())
                            {
                                builder.AppendLine("## Page " + page.Number);
                                builder.AppendLine(page.Text);
                            }
                            return builder.ToString();
                        }
                    default:
                        throw new NotSupportedException("Unsupported document format: " + extension);
                }
            }
        }
    }
}