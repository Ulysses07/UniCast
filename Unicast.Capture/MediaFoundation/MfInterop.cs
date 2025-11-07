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

        // --- IMFActivate / IMFAttributes minimal tanımlar ---
        [ComImport, Guid("7f8c8b88-79f7-4f2f-a6b2-3c0d4f3e8e15"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFActivate
        {
            // IMFAttributes (v-table inheritance)
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

            // IMFActivate özel üyeleri (v-table devamı)
            int ActivateObject([In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
            int ShutdownObject();
            int DetachObject();
        }

        [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
        internal class MFAttributes { }

        [DllImport("Mfplat.dll", ExactSpelling = true)]
        private static extern int MFStartup(int version, int dwFlags);
        [DllImport("Mfplat.dll", ExactSpelling = true)]
        private static extern int MFShutdown();

        [DllImport("Mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateAttributes(out IntPtr ppMFAttributes, int cInitialSize);

        [DllImport("Mf.dll", ExactSpelling = true)]
        private static extern int MFEnumDeviceSources(IntPtr pAttributes, out IntPtr ppActivate, out int pcActivate);

        internal static void Startup() => Check(MFStartup(0x20070, 0));   // MF version
        internal static void Shutdown() => Check(MFShutdown());

        internal static IntPtr CreateAttributes()
        {
            Check(MFCreateAttributes(out var p, 2));
            return p;
        }

        // IMFAttributes.SetGUID wrapper
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetGUID_Delegate(IntPtr pThis, ref Guid guidKey, ref Guid guidValue);

        internal static void SetGUID(IntPtr pAttributes, Guid key, Guid value)
        {
            var vtbl = Marshal.ReadIntPtr(pAttributes);
            // IMFAttributes vtable: SetGUID  ?  (basit yaklaşım: 16. giriş civarı)
            // Güvenli yol: Managed IMFAttributes yazıp QueryInterface; ama minimal için Invoke:
            // Pratikte bu çağrı pek çoğu için çalışır, fakat stabilite için kapsamlı interop tercih edilir.
            // Basitleştirmek adına az riskli hack yerine alternate yol:
            // -> Aşağıda MediaFoundationCaptureService içinde Windows API Code Pack alternatifi yok.
            // Bu yüzden burada SetGUID'u reflection ile çağırmak yerine COM marshal kullanalım:
            // Daha güvenlisi: Marshal.GetObjectForIUnknown ve dynamic cast, ama basit yaklaşım yeterli.

            // Pratik: bir kez deneyip başarısız olursa exception fırlat.
            // UYARI: Prod ortamda tam IMFAttributes interop sınıfı oluşturmak önerilir.
            throw new NotSupportedException("SetGUID interop minimalist mock. Aşağıdaki yüksek seviye yol kullanıldı.");
        }

        internal static void EnumDeviceSourcesVideoOrAudio(Guid sourceTypeGuid, out IMFActivate[] devices)
        {
            // Burada: Windows Media Foundation için en stabil yöntem,
            // IMFAttributes oluşturup MFEnumDeviceSources ile IMFActivate listesi almak.
            // Minimal interop ile Attributes setlemek karmaşıklaştığı için,
            // daha pratik bir yol: MF API'si yerine WinRT DeviceInformation kullanmak
            // ve FFmpeg'e dshow-friendly *FriendlyName* ile gitmek.

            // Bu helper, CaptureService içinde kullanılmıyor; WinRT yolu tercih edildi.
            devices = Array.Empty<IMFActivate>();
        }
    }
}
