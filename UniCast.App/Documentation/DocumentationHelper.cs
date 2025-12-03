using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace UniCast.App.Documentation
{
    /// <summary>
    /// DÜZELTME v20: XML Documentation Helper
    /// Kod dokümantasyonu standartları ve yardımcıları
    /// 
    /// <para>
    /// Bu dosya kod dokümantasyonu için standartları ve örnekleri içerir.
    /// Tüm public API'ler için XML documentation zorunludur.
    /// </para>
    /// 
    /// <example>
    /// Temel kullanım:
    /// <code>
    /// /// &lt;summary&gt;
    /// /// Metod açıklaması buraya yazılır.
    /// /// &lt;/summary&gt;
    /// /// &lt;param name="paramName"&gt;Parametre açıklaması&lt;/param&gt;
    /// /// &lt;returns&gt;Dönüş değeri açıklaması&lt;/returns&gt;
    /// /// &lt;exception cref="ArgumentNullException"&gt;Fırlatılma durumu&lt;/exception&gt;
    /// public string MyMethod(string paramName) { }
    /// </code>
    /// </example>
    /// </summary>
    public static class DocumentationHelper
    {
        #region Documentation Standards

        /*
         * ============================================
         * XML DOCUMENTATION STANDARTLARI
         * ============================================
         * 
         * 1. TÜM PUBLIC MEMBERS İÇİN ZORUNLU:
         *    - Classes
         *    - Interfaces
         *    - Methods
         *    - Properties
         *    - Events
         *    - Enums ve enum values
         * 
         * 2. KULLANILACAK TAGLAR:
         *    <summary>      - Kısa açıklama (zorunlu)
         *    <param>        - Parametre açıklaması (zorunlu)
         *    <returns>      - Dönüş değeri (void değilse zorunlu)
         *    <exception>    - Fırlatılabilecek exception'lar
         *    <remarks>      - Detaylı açıklama
         *    <example>      - Kullanım örneği
         *    <see>          - Başka tipe referans
         *    <seealso>      - İlgili diğer tipler
         *    <para>         - Paragraf ayırıcı
         *    <code>         - Kod bloğu
         *    <c>            - Inline kod
         *    <value>        - Property değeri açıklaması
         *    <typeparam>    - Generic type parametresi
         * 
         * 3. DİL: Türkçe tercih edilir, teknik terimler İngilizce kalabilir
         * 
         * 4. UZUNLUK: Summary max 2-3 cümle, detaylar remarks'ta
         */

        #endregion

        #region Documentation Examples

        /// <summary>
        /// ÖRNEK 1: Basit metod dokümantasyonu
        /// </summary>
        /// <param name="value">İşlenecek değer</param>
        /// <returns>İşlenmiş değer</returns>
        public static string ExampleSimpleMethod(string value)
        {
            return value?.Trim() ?? string.Empty;
        }

        /// <summary>
        /// ÖRNEK 2: Exception içeren metod
        /// </summary>
        /// <param name="value">Null olamayan değer</param>
        /// <returns>İşlenmiş değer</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="value"/> null olduğunda fırlatılır.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="value"/> boş string olduğunda fırlatılır.
        /// </exception>
        public static string ExampleWithExceptions(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Değer boş olamaz", nameof(value));
            return value.Trim();
        }

        /// <summary>
        /// ÖRNEK 3: Generic metod
        /// </summary>
        /// <typeparam name="T">Koleksiyon eleman tipi</typeparam>
        /// <param name="items">İşlenecek koleksiyon</param>
        /// <param name="predicate">Filtreleme koşulu</param>
        /// <returns>Filtrelenmiş koleksiyon</returns>
        /// <remarks>
        /// Bu metod lazy evaluation kullanır. 
        /// Sonuç iterate edilene kadar filtreleme yapılmaz.
        /// </remarks>
        /// <example>
        /// <code>
        /// var numbers = new[] { 1, 2, 3, 4, 5 };
        /// var evens = ExampleGenericMethod(numbers, n => n % 2 == 0);
        /// // evens: [2, 4]
        /// </code>
        /// </example>
        public static IEnumerable<T> ExampleGenericMethod<T>(
            IEnumerable<T> items,
            Func<T, bool> predicate)
        {
            return items.Where(predicate);
        }

        /// <summary>
        /// ÖRNEK 4: Async metod
        /// </summary>
        /// <param name="path">Dosya yolu</param>
        /// <param name="ct">İptal token'ı</param>
        /// <returns>
        /// Dosya içeriğini içeren <see cref="System.Threading.Tasks.Task{TResult}"/>.
        /// </returns>
        /// <exception cref="FileNotFoundException">Dosya bulunamadığında</exception>
        /// <exception cref="OperationCanceledException">İşlem iptal edildiğinde</exception>
        public static async System.Threading.Tasks.Task<string> ExampleAsyncMethod(
            string path,
            System.Threading.CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return await System.IO.File.ReadAllTextAsync(path, ct);
        }

        #endregion

        #region Property Documentation Examples

        /// <summary>
        /// ÖRNEK 5: Property dokümantasyonu
        /// </summary>
        /// <value>
        /// Varsayılan değer: <c>100</c>
        /// Geçerli aralık: 1-1000
        /// </value>
        /// <remarks>
        /// Bu değer uygulama ayarlarından da yüklenebilir.
        /// </remarks>
        public static int ExampleProperty { get; set; } = 100;

        #endregion

        #region Class Documentation Example

        /*
         * ÖRNEK 6: Sınıf dokümantasyonu
         * 
         * /// <summary>
         * /// Kullanıcı yönetimi için temel servis.
         * /// </summary>
         * /// <remarks>
         * /// <para>
         * /// Bu servis kullanıcı CRUD işlemlerini yönetir.
         * /// Thread-safe implementasyon içerir.
         * /// </para>
         * /// <para>
         * /// Singleton pattern kullanır, <see cref="Instance"/> üzerinden erişin.
         * /// </para>
         * /// </remarks>
         * /// <example>
         * /// <code>
         * /// var user = await UserService.Instance.GetUserAsync(userId);
         * /// </code>
         * /// </example>
         * /// <seealso cref="IUserService"/>
         * /// <seealso cref="UserRepository"/>
         * public sealed class UserService : IUserService
         * {
         *     // ...
         * }
         */

        #endregion

        #region Enum Documentation Example

        /*
         * ÖRNEK 7: Enum dokümantasyonu
         * 
         * /// <summary>
         * /// Stream durumu
         * /// </summary>
         * public enum StreamStatus
         * {
         *     /// <summary>Başlatılmadı</summary>
         *     NotStarted = 0,
         *     
         *     /// <summary>Bağlanıyor</summary>
         *     Connecting = 1,
         *     
         *     /// <summary>Yayında</summary>
         *     Live = 2,
         *     
         *     /// <summary>Duraklatıldı</summary>
         *     Paused = 3,
         *     
         *     /// <summary>Durduruldu</summary>
         *     Stopped = 4,
         *     
         *     /// <summary>Hata</summary>
         *     Error = 5
         * }
         */

        #endregion

        #region Documentation Analyzer

        /// <summary>
        /// Assembly'deki documentation coverage'ı hesapla
        /// </summary>
        /// <param name="assembly">Analiz edilecek assembly</param>
        /// <returns>Documentation coverage yüzdesi</returns>
        public static DocumentationReport AnalyzeDocumentation(Assembly assembly)
        {
            var publicTypes = assembly.GetExportedTypes();
            var report = new DocumentationReport();

            foreach (var type in publicTypes)
            {
                report.TotalItems++;

                // Type'ın XML doc'u var mı?
                // Not: Runtime'da XML doc'a erişim için XML dosyası gerekli
                // Bu sadece bir örnek analiz

                var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

                foreach (var member in members)
                {
                    if (member.DeclaringType != type) continue;

                    report.TotalItems++;

                    // Member adına göre basit kontrol (gerçek implementasyonda XML dosya parse edilmeli)
                    if (member.Name.StartsWith("get_") || member.Name.StartsWith("set_"))
                        continue;

                    if (HasBasicDocumentation(member))
                    {
                        report.DocumentedItems++;
                    }
                    else
                    {
                        report.UndocumentedMembers.Add($"{type.Name}.{member.Name}");
                    }
                }
            }

            return report;
        }

        private static bool HasBasicDocumentation(MemberInfo member)
        {
            // Basit kontrol - gerçek implementasyonda XML dosya kontrol edilmeli
            // Bu örnek her zaman false döner çünkü runtime'da XML doc erişilemiyor
            return false;
        }

        #endregion

        #region Generate Documentation Template

        /// <summary>
        /// Metod için documentation template oluştur
        /// </summary>
        /// <param name="method">Metod bilgisi</param>
        /// <returns>XML documentation template</returns>
        public static string GenerateMethodDocTemplate(MethodInfo method)
        {
            var sb = new StringBuilder();
            sb.AppendLine("/// <summary>");
            sb.AppendLine($"/// TODO: {method.Name} açıklaması");
            sb.AppendLine("/// </summary>");

            foreach (var param in method.GetParameters())
            {
                sb.AppendLine($"/// <param name=\"{param.Name}\">TODO: {param.Name} açıklaması</param>");
            }

            if (method.ReturnType != typeof(void))
            {
                sb.AppendLine($"/// <returns>TODO: Dönüş değeri açıklaması</returns>");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Sınıf için documentation template oluştur
        /// </summary>
        public static string GenerateClassDocTemplate(Type type)
        {
            var sb = new StringBuilder();
            sb.AppendLine("/// <summary>");
            sb.AppendLine($"/// TODO: {type.Name} sınıfı açıklaması");
            sb.AppendLine("/// </summary>");
            sb.AppendLine("/// <remarks>");
            sb.AppendLine("/// TODO: Detaylı açıklama");
            sb.AppendLine("/// </remarks>");

            if (type.IsGenericType)
            {
                foreach (var typeParam in type.GetGenericArguments())
                {
                    sb.AppendLine($"/// <typeparam name=\"{typeParam.Name}\">TODO: Type parameter açıklaması</typeparam>");
                }
            }

            return sb.ToString();
        }

        #endregion
    }

    #region Report Types

    /// <summary>
    /// Documentation coverage raporu
    /// </summary>
    public class DocumentationReport
    {
        /// <summary>Toplam public member sayısı</summary>
        public int TotalItems { get; set; }

        /// <summary>Dokümante edilmiş member sayısı</summary>
        public int DocumentedItems { get; set; }

        /// <summary>Dokümante edilmemiş member listesi</summary>
        public List<string> UndocumentedMembers { get; } = new();

        /// <summary>Coverage yüzdesi</summary>
        public double CoveragePercent => TotalItems > 0
            ? (double)DocumentedItems / TotalItems * 100
            : 0;

        public override string ToString()
        {
            return $"Documentation Coverage: {CoveragePercent:F1}% ({DocumentedItems}/{TotalItems})";
        }
    }

    #endregion

    #region XML Doc Standards Reference

    /*
     * ============================================
     * QUICK REFERENCE - XML DOC TAGS
     * ============================================
     * 
     * ZORUNLU TAGLAR:
     * ---------------
     * <summary>         Kısa açıklama
     * <param>           Her parametre için
     * <returns>         void olmayan metodlar için
     * 
     * ÖNERİLEN TAGLAR:
     * ----------------
     * <exception>       Fırlatılan exception'lar
     * <remarks>         Detaylı açıklama
     * <example>         Kullanım örneği
     * 
     * REFERANS TAGLAR:
     * ----------------
     * <see>             Inline referans
     * <seealso>         İlgili tipler
     * <paramref>        Parametre referansı
     * <typeparamref>    Type parameter referansı
     * 
     * FORMATLAMA TAGLAR:
     * ------------------
     * <para>            Paragraf
     * <code>            Kod bloğu
     * <c>               Inline kod
     * <list>            Liste
     * 
     * ÖZEL TAGLAR:
     * ------------
     * <inheritdoc/>     Üst sınıftan miras al
     * <value>           Property değeri
     * <typeparam>       Generic type parameter
     * 
     * ============================================
     */

    #endregion
}
