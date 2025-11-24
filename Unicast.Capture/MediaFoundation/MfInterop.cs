using System;
using System.Runtime.InteropServices;

namespace UniCast.Capture.MediaFoundation
{
    internal static class MfInterop
    {
        // --- GUID sabitleri ---
        public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE = new("cddbc873-58f1-4a2f-9eaa-8d5d3aabcace");
        public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID = new("8ac3587a-4ae7-42d8-99e0-0a6012f0e3d3");
        public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_AUDCAP_GUID = new("bc9d118e-8e7a-4a74-aec1-8f3efc3a9e8d");
        public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME = new("60d0e559-52f8-4fa8-b3e8-a1428a0bcb77");
        public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK = new("58f0aad8-22bf-4f8a-bf3c-bf8cfb8c7b37");

        // --- HRESULT yardımcıları ---
        public static void Check(int hr)
        {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }

        // --- IMFActivate / IMFAttributes Tanımları ---
        // Bu arayüz hem IMFAttributes hem IMFActivate metotlarını içerir.
        [ComImport, Guid("7f8c8b88-79f7-4f2f-a6b2-3c0d4f3e8e15"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFActivate
        {
            // IMFAttributes (v-table sırası önemlidir)
            int GetItem([In] ref Guid guidKey, IntPtr pValue);
            int GetItemType([In] ref Guid guidKey, out int pType);
            int CompareItem([In] ref Guid guidKey, IntPtr Value, out bool pbResult);
            int Compare([In] IntPtr pTheirs, int MatchType, out bool pbResult);
            int GetUINT32([In] ref Guid guidKey, out int punValue);
            int GetUINT64([In] ref Guid guidKey, out long punValue);
            int GetDouble([In] ref Guid guidKey, out double pfValue);
            int GetGUID([In] ref Guid guidKey, out Guid pguidValue);
            int GetStringLength([In] ref Guid guidKey, out int pcchLength);
            int GetString([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, int cchBufSize, out int pcchLength);
            int GetAllocatedString([In] ref Guid guidKey, out IntPtr ppwszValue, out int pcchLength);
            int GetBlobSize([In] ref Guid guidKey, out int pcbBlobSize);
            int GetBlob([In] ref Guid guidKey, [Out] byte[] pBuf, int cbBufSize, out int pcbBlobSize);
            int GetAllocatedBlob([In] ref Guid guidKey, out IntPtr ip, out int pcbSize);
            int GetUnknown([In] ref Guid guidKey, [In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
            int SetItem([In] ref Guid guidKey, IntPtr Value);
            int DeleteItem([In] ref Guid guidKey);
            int DeleteAllItems();
            int SetUINT32([In] ref Guid guidKey, int unValue);
            int SetUINT64([In] ref Guid guidKey, long unValue);
            int SetDouble([In] ref Guid guidKey, double fValue);
            int SetGUID([In] ref Guid guidKey, [In] ref Guid guidValue);
            int SetString([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
            int SetBlob([In] ref Guid guidKey, [In] byte[] pBuf, int cbBufSize);
            int SetUnknown([In] ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int LockStore();
            int UnlockStore();
            int GetCount(out int pcItems);
            int GetItemByIndex(int unIndex, out Guid pguidKey, IntPtr pValue);
            int CopyAllItems([MarshalAs(UnmanagedType.Interface)] out object ppDest);

            // IMFActivate metotları
            int ActivateObject([In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
            int ShutdownObject();
            int DetachObject();
        }

        // --- P/Invoke Tanımları (HATA DÜZELTME: internal yapıldı) ---

        [DllImport("Mfplat.dll", ExactSpelling = true)]
        internal static extern int MFStartup(int version, int dwFlags);

        [DllImport("Mfplat.dll", ExactSpelling = true)]
        internal static extern int MFShutdown();

        [DllImport("Mfplat.dll", ExactSpelling = true)]
        internal static extern int MFCreateAttributes(out IntPtr ppMFAttributes, int cInitialSize);

        [DllImport("Mf.dll", ExactSpelling = true)]
        internal static extern int MFEnumDeviceSources(IntPtr pAttributes, out IntPtr ppActivate, out int pcActivate);

        // --- Helper Metotlar (HATA DÜZELTME: Eksik metotlar eklendi) ---

        /// <summary>
        /// Verilen IntPtr (IMFAttributes) üzerinde GUID set eder.
        /// </summary>
        internal static void IMFAttributes_SetGUID(IntPtr pAttributes, Guid key, Guid value)
        {
            // IntPtr'ı C# Interface'ine çevir (RCW)
            var attrs = (IMFActivate)Marshal.GetObjectForIUnknown(pAttributes);
            try
            {
                int hr = attrs.SetGUID(ref key, ref value);
                Check(hr);
            }
            finally
            {
                // COM nesnesini serbest bırakmak genelde GC'ye bırakılır ama manuel de yapılabilir.
                // Marshal.ReleaseComObject(attrs);
            }
        }

        /// <summary>
        /// Verilen IntPtr (IMFAttributes) üzerinden string okur.
        /// </summary>
        internal static string GetString(IntPtr pAttributes, Guid key)
        {
            var attrs = (IMFActivate)Marshal.GetObjectForIUnknown(pAttributes);
            IntPtr pStr = IntPtr.Zero;
            int length = 0;
            try
            {
                // GetAllocatedString hafızayı kendisi ayırır, CoTaskMemFree ile silinmesi gerekir.
                int hr = attrs.GetAllocatedString(ref key, out pStr, out length);
                if (hr < 0 || pStr == IntPtr.Zero) return string.Empty;

                return Marshal.PtrToStringUni(pStr) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                if (pStr != IntPtr.Zero) Marshal.FreeCoTaskMem(pStr);
            }
        }
    }
}