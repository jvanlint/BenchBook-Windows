using System.Runtime.InteropServices;

namespace BenchBook.Pdf;

/// <summary>
/// The PDFium functions BenchBook uses, from the pdfium.dll in the
/// bblanchon.PDFium.Win32 package (x64 and Arm64). PDFium isn't thread-safe,
/// so every call goes through <see cref="Lock"/>.
/// </summary>
public static partial class Pdfium
{
    private const string Dll = "pdfium";

    public static readonly object Lock = new();
    private static bool _initialised;

    public static void EnsureInitialised()
    {
        lock (Lock)
        {
            if (_initialised) return;
            FPDF_InitLibrary();
            _initialised = true;
        }
    }

    public const uint ErrorPassword = 4;
    public const int RenderAnnotations = 0x01, RenderLcdText = 0x02;
    public const int FontTrueType = 2;
    public const int FillNone = 0, FillWinding = 2;
    public const int LineCapRound = 1, LineJoinRound = 1;

    [LibraryImport(Dll)] public static partial void FPDF_InitLibrary();
    [LibraryImport(Dll)] public static partial uint FPDF_GetLastError();

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr FPDF_LoadMemDocument64(IntPtr data, nuint size, string? password);

    [LibraryImport(Dll)] public static partial void FPDF_CloseDocument(IntPtr document);
    [LibraryImport(Dll)] public static partial int FPDF_GetPageCount(IntPtr document);
    [LibraryImport(Dll)] public static partial IntPtr FPDF_LoadPage(IntPtr document, int index);
    [LibraryImport(Dll)] public static partial void FPDF_ClosePage(IntPtr page);
    [LibraryImport(Dll)] public static partial float FPDF_GetPageWidthF(IntPtr page);
    [LibraryImport(Dll)] public static partial float FPDF_GetPageHeightF(IntPtr page);
    [LibraryImport(Dll)] public static partial int FPDFPage_GetRotation(IntPtr page);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPage_GetCropBox(IntPtr page, out float left, out float bottom, out float right, out float top);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPage_GetMediaBox(IntPtr page, out float left, out float bottom, out float right, out float top);

    // Rendering.
    [LibraryImport(Dll)] public static partial IntPtr FPDFBitmap_Create(int width, int height, int alpha);
    [LibraryImport(Dll)] public static partial void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [LibraryImport(Dll)] public static partial IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
    [LibraryImport(Dll)] public static partial int FPDFBitmap_GetStride(IntPtr bitmap);
    [LibraryImport(Dll)] public static partial void FPDFBitmap_Destroy(IntPtr bitmap);
    [LibraryImport(Dll)] public static partial void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    // Text search.
    [LibraryImport(Dll)] public static partial IntPtr FPDFText_LoadPage(IntPtr page);
    [LibraryImport(Dll)] public static partial void FPDFText_ClosePage(IntPtr textPage);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FPDFText_FindStart(IntPtr textPage, string findWhat, uint flags, int startIndex);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFText_FindNext(IntPtr handle);

    [LibraryImport(Dll)] public static partial int FPDFText_GetSchResultIndex(IntPtr handle);
    [LibraryImport(Dll)] public static partial int FPDFText_GetSchCount(IntPtr handle);
    [LibraryImport(Dll)] public static partial void FPDFText_FindClose(IntPtr handle);
    [LibraryImport(Dll)] public static partial int FPDFText_CountRects(IntPtr textPage, int startIndex, int count);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFText_GetRect(IntPtr textPage, int index, out double left, out double top, out double right, out double bottom);

    // Drawing into pages, for the marked-up copy.
    [LibraryImport(Dll)] public static partial IntPtr FPDFPageObj_CreateNewPath(float x, float y);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool FPDFPath_MoveTo(IntPtr path, float x, float y);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool FPDFPath_LineTo(IntPtr path, float x, float y);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool FPDFPath_BezierTo(IntPtr path, float x1, float y1, float x2, float y2, float x3, float y3);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool FPDFPath_Close(IntPtr path);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool FPDFPath_SetDrawMode(IntPtr path, int fillMode, [MarshalAs(UnmanagedType.Bool)] bool stroke);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool FPDFPageObj_SetStrokeColor(IntPtr obj, uint r, uint g, uint b, uint a);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool FPDFPageObj_SetFillColor(IntPtr obj, uint r, uint g, uint b, uint a);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool FPDFPageObj_SetStrokeWidth(IntPtr obj, float width);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool FPDFPageObj_SetLineCap(IntPtr obj, int cap);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool FPDFPageObj_SetLineJoin(IntPtr obj, int join);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void FPDFPageObj_SetBlendMode(IntPtr obj, string mode);

    [LibraryImport(Dll)] public static partial void FPDFPageObj_Transform(IntPtr obj, double a, double b, double c, double d, double e, double f);
    [LibraryImport(Dll)] public static partial void FPDFPage_InsertObject(IntPtr page, IntPtr obj);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool FPDFPage_GenerateContent(IntPtr page);
    [LibraryImport(Dll)] public static partial IntPtr FPDFPage_New(IntPtr document, int index, double width, double height);
    [LibraryImport(Dll)] public static partial IntPtr FPDFText_LoadFont(IntPtr document, IntPtr data, uint size, int fontType, [MarshalAs(UnmanagedType.Bool)] bool cid);
    [LibraryImport(Dll)] public static partial void FPDFFont_Close(IntPtr font);
    [LibraryImport(Dll)] public static partial IntPtr FPDFPageObj_CreateTextObj(IntPtr document, IntPtr font, float size);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFText_SetText(IntPtr textObject, string text);

    // Saving.
    [StructLayout(LayoutKind.Sequential)]
    public struct FileWrite
    {
        public int Version;
        public IntPtr WriteBlock;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int WriteBlockHandler(IntPtr self, IntPtr data, uint size); // C "unsigned long" is 32 bits on Windows.

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDF_SaveAsCopy(IntPtr document, ref FileWrite writer, uint flags);

    // Making PDFs from scratch, for tests.
    [LibraryImport(Dll)] public static partial IntPtr FPDF_CreateNewDocument();

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr FPDFPageObj_NewTextObj(IntPtr document, string font, float size);

    [LibraryImport(Dll)] public static partial void FPDFPage_SetRotation(IntPtr page, int rotate);
    [LibraryImport(Dll)] public static partial void FPDFPage_SetCropBox(IntPtr page, float left, float bottom, float right, float top);
    [LibraryImport(Dll)] public static partial int FPDFPage_CountObjects(IntPtr page);
    [LibraryImport(Dll)] public static partial int FPDFText_CountChars(IntPtr textPage);
    [LibraryImport(Dll)] public static partial uint FPDFText_GetUnicode(IntPtr textPage, int index);
}
