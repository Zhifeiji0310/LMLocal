using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using LMLocal.Core.Common;

namespace LMLocal.Infrastructure.Documents
{
    /// <summary>
    /// Extracts Office and PDF text through the locally installed Microsoft Office applications.
    /// Uses late-bound COM so no Office interop NuGet packages are required.
    /// </summary>
    internal static class DocumentTextExtractor
    {
        private const int MaxFileSizeBytes = 10 * 1024 * 1024;

        public static string Extract(string fileName, string base64)
        {
            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("Document name and content are required.");

            InternalLogger.Info("DocumentTextExtractor: decoding attachment. Name=" + fileName +
                ", Base64Length=" + base64.Length);
            byte[] bytes = Convert.FromBase64String(base64);
            InternalLogger.Info("DocumentTextExtractor: attachment decoded. Name=" + fileName +
                ", SizeBytes=" + bytes.Length);
            if (bytes.Length == 0 || bytes.Length > MaxFileSizeBytes)
                throw new InvalidDataException("Document is empty or exceeds the 10 MB limit.");

            string extension = Path.GetExtension(fileName).ToLowerInvariant();
            switch (extension)
            {
                case ".doc":
                case ".docx":
                case ".pdf":
                case ".xls":
                case ".xlsx":
                case ".ppt":
                case ".pptx":
                    break;
                default:
                    throw new NotSupportedException("Unsupported document format: " + extension);
            }

            string tempPath = Path.Combine(
                Path.GetTempPath(),
                "LMLocal_" + Guid.NewGuid().ToString("N") + extension);

            File.WriteAllBytes(tempPath, bytes);
            InternalLogger.Info("DocumentTextExtractor: temporary file created. Name=" + fileName +
                ", Extension=" + extension + ", TempPath=" + tempPath);
            try
            {
                string extracted = RunInSta(() => ExtractWithOffice(tempPath, extension));
                InternalLogger.Info("DocumentTextExtractor: Office extraction returned. Name=" + fileName +
                    ", TextLength=" + (extracted == null ? 0 : extracted.Length));
                return extracted;
            }
            catch (Exception ex)
            {
                InternalLogger.Error("DocumentTextExtractor: extraction pipeline failed. Name=" + fileName +
                    ", Extension=" + extension, ex);
                throw;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                    // Best-effort cleanup. The OS will eventually clean the temp file.
                }
            }
        }

        private static string RunInSta(Func<string> action)
        {
            string result = null;
            Exception error = null;

            var thread = new Thread(() =>
            {
                try
                {
                    result = action();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });

            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            InternalLogger.Info("DocumentTextExtractor: starting Office COM work on STA thread.");
            thread.Start();
            thread.Join();

            if (error != null)
            {
                InternalLogger.Error("DocumentTextExtractor: Office COM work failed on STA thread.", error);
                throw new InvalidOperationException("Microsoft Office failed to extract the document text.", error);
            }

            return result ?? string.Empty;
        }

        private static string ExtractWithOffice(string filePath, string extension)
        {
            switch (extension)
            {
                case ".doc":
                case ".docx":
                case ".pdf":
                    return ExtractWordText(filePath);
                case ".xls":
                case ".xlsx":
                    return ExtractExcelText(filePath);
                case ".ppt":
                case ".pptx":
                    return ExtractPowerPointText(filePath);
                default:
                    throw new NotSupportedException("Unsupported document format: " + extension);
            }
        }

        private static string ExtractWordText(string filePath)
        {
            dynamic application = null;
            dynamic documents = null;
            dynamic document = null;

            try
            {
                application = CreateOfficeApplication("Word.Application");
                application.Visible = false;
                application.DisplayAlerts = 0;

                documents = application.Documents;
                // Word can open PDF and convert its textual content into a document.
                document = documents.Open(filePath, false, true, false);
                return Convert.ToString(document.Content.Text, CultureInfo.InvariantCulture) ?? string.Empty;
            }
            finally
            {
                CloseWordDocument(document);
                ReleaseComObject(documents);
                QuitOfficeApplication(application);
            }
        }

        private static string ExtractExcelText(string filePath)
        {
            dynamic application = null;
            dynamic workbooks = null;
            dynamic workbook = null;
            var builder = new StringBuilder();

            try
            {
                application = CreateOfficeApplication("Excel.Application");
                application.Visible = false;
                application.DisplayAlerts = false;
                application.AskToUpdateLinks = false;

                workbooks = application.Workbooks;
                workbook = workbooks.Open(filePath, 0, true);

                int worksheetCount = Convert.ToInt32(workbook.Worksheets.Count, CultureInfo.InvariantCulture);
                for (int sheetIndex = 1; sheetIndex <= worksheetCount; sheetIndex++)
                {
                    dynamic worksheet = null;
                    dynamic usedRange = null;
                    try
                    {
                        worksheet = workbook.Worksheets[sheetIndex];
                        builder.AppendLine("## Sheet: " +
                            Convert.ToString(worksheet.Name, CultureInfo.InvariantCulture));

                        usedRange = worksheet.UsedRange;
                        object values = usedRange.Value2;
                        AppendExcelValues(builder, values);
                        builder.AppendLine();
                    }
                    finally
                    {
                        ReleaseComObject(usedRange);
                        ReleaseComObject(worksheet);
                    }
                }

                return builder.ToString();
            }
            finally
            {
                CloseExcelWorkbook(workbook);
                ReleaseComObject(workbooks);
                QuitOfficeApplication(application);
            }
        }

        private static void AppendExcelValues(StringBuilder builder, object values)
        {
            if (values == null)
                return;

            var array = values as Array;
            if (array == null)
            {
                string scalar = Convert.ToString(values, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(scalar))
                    builder.AppendLine(scalar);
                return;
            }

            if (array.Rank == 2)
            {
                int rowLower = array.GetLowerBound(0);
                int rowUpper = array.GetUpperBound(0);
                int columnLower = array.GetLowerBound(1);
                int columnUpper = array.GetUpperBound(1);

                for (int row = rowLower; row <= rowUpper; row++)
                {
                    var cells = new StringBuilder();
                    for (int column = columnLower; column <= columnUpper; column++)
                    {
                        object value = array.GetValue(row, column);
                        string cellText = Convert.ToString(value, CultureInfo.InvariantCulture);
                        if (cells.Length > 0)
                            cells.Append('\t');
                        cells.Append(cellText);
                    }

                    if (!string.IsNullOrWhiteSpace(cells.ToString()))
                        builder.AppendLine(cells.ToString());
                }
            }
            else
            {
                foreach (object value in array)
                {
                    string text = Convert.ToString(value, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(text))
                        builder.AppendLine(text);
                }
            }
        }

        private static string ExtractPowerPointText(string filePath)
        {
            dynamic application = null;
            dynamic presentations = null;
            dynamic presentation = null;
            var builder = new StringBuilder();

            try
            {
                application = CreateOfficeApplication("PowerPoint.Application");
                application.DisplayAlerts = 1;

                presentations = application.Presentations;
                presentation = presentations.Open(filePath, true, false, false);

                int slideCount = Convert.ToInt32(presentation.Slides.Count, CultureInfo.InvariantCulture);
                for (int slideIndex = 1; slideIndex <= slideCount; slideIndex++)
                {
                    dynamic slide = null;
                    try
                    {
                        slide = presentation.Slides[slideIndex];
                        builder.AppendLine("## Slide " + slideIndex);

                        int shapeCount = Convert.ToInt32(slide.Shapes.Count, CultureInfo.InvariantCulture);
                        for (int shapeIndex = 1; shapeIndex <= shapeCount; shapeIndex++)
                        {
                            dynamic shape = null;
                            try
                            {
                                shape = slide.Shapes[shapeIndex];
                                try
                                {
                                    if (Convert.ToInt32(shape.HasTextFrame, CultureInfo.InvariantCulture) != 0 &&
                                        Convert.ToInt32(shape.TextFrame.HasText, CultureInfo.InvariantCulture) != 0)
                                    {
                                        string text = Convert.ToString(
                                            shape.TextFrame.TextRange.Text,
                                            CultureInfo.InvariantCulture);
                                        if (!string.IsNullOrWhiteSpace(text))
                                            builder.AppendLine(text);
                                    }
                                }
                                catch
                                {
                                    // Some PowerPoint shapes do not expose a readable text frame.
                                }
                            }
                            finally
                            {
                                ReleaseComObject(shape);
                            }
                        }

                        builder.AppendLine();
                    }
                    finally
                    {
                        ReleaseComObject(slide);
                    }
                }

                return builder.ToString();
            }
            finally
            {
                ClosePowerPointPresentation(presentation);
                ReleaseComObject(presentations);
                QuitOfficeApplication(application);
            }
        }

        private static dynamic CreateOfficeApplication(string progId)
        {
            InternalLogger.Info("DocumentTextExtractor: resolving Office COM application. ProgId=" + progId);
            Type officeType = Type.GetTypeFromProgID(progId);
            if (officeType == null)
                throw new InvalidOperationException(
                    "Microsoft Office application is not registered: " + progId);

            var application = Activator.CreateInstance(officeType);
            InternalLogger.Info("DocumentTextExtractor: Office COM application created. ProgId=" + progId);
            return application;
        }

        private static void CloseWordDocument(dynamic document)
        {
            if (document == null) return;
            try { document.Close(false); }
            catch { /* Best-effort COM cleanup. */ }
            finally { ReleaseComObject(document); }
        }

        private static void CloseExcelWorkbook(dynamic workbook)
        {
            if (workbook == null) return;
            try { workbook.Close(false); }
            catch { /* Best-effort COM cleanup. */ }
            finally { ReleaseComObject(workbook); }
        }

        private static void ClosePowerPointPresentation(dynamic presentation)
        {
            if (presentation == null) return;
            try { presentation.Close(); }
            catch { /* Best-effort COM cleanup. */ }
            finally { ReleaseComObject(presentation); }
        }

        private static void QuitOfficeApplication(dynamic application)
        {
            if (application == null) return;
            try { application.Quit(false); }
            catch
            {
                try { application.Quit(); }
                catch { /* Best-effort COM cleanup. */ }
            }
            finally { ReleaseComObject(application); }
        }

        private static void ReleaseComObject(object instance)
        {
            if (instance == null)
                return;

            try
            {
                if (Marshal.IsComObject(instance))
                    Marshal.FinalReleaseComObject(instance);
            }
            catch
            {
                // Ignore cleanup failures.
            }
        }
    }
}