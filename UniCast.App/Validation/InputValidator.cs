using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.RegularExpressions;

namespace UniCast.App.Validation
{
    /// <summary>
    /// DÜZELTME v19: Genel Input Validation sistemi
    /// Fluent validation, attribute-based validation, custom rules
    /// </summary>
    public static class InputValidator
    {
        #region Quick Validators

        /// <summary>
        /// String boş değil mi
        /// </summary>
        public static bool IsNotEmpty(string? value) => !string.IsNullOrWhiteSpace(value);

        /// <summary>
        /// Email formatı doğru mu
        /// </summary>
        public static bool IsValidEmail(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return Regex.IsMatch(value, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
        }

        /// <summary>
        /// URL formatı doğru mu
        /// </summary>
        public static bool IsValidUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        /// <summary>
        /// RTMP URL formatı doğru mu
        /// </summary>
        public static bool IsValidRtmpUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == "rtmp" || uri.Scheme == "rtmps");
        }

        /// <summary>
        /// Stream key formatı doğru mu (boş değil, minimum uzunluk)
        /// </summary>
        public static bool IsValidStreamKey(string? value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length >= 8;
        }

        /// <summary>
        /// Sayı aralıkta mı
        /// </summary>
        public static bool IsInRange(int value, int min, int max) => value >= min && value <= max;

        /// <summary>
        /// Sayı aralıkta mı
        /// </summary>
        public static bool IsInRange(double value, double min, double max) => value >= min && value <= max;

        #endregion

        #region Fluent Validator

        /// <summary>
        /// Fluent validation başlat
        /// </summary>
        public static Validator<T> For<T>(T value) => new(value);

        #endregion

        #region Stream Settings Validation

        /// <summary>
        /// Video ayarlarını doğrula
        /// </summary>
        public static ValidationResult ValidateVideoSettings(int width, int height, int fps, int bitrate)
        {
            var errors = new List<string>();

            if (!IsInRange(width, 640, 3840))
                errors.Add($"Genişlik 640-3840 arasında olmalı (şu an: {width})");

            if (!IsInRange(height, 360, 2160))
                errors.Add($"Yükseklik 360-2160 arasında olmalı (şu an: {height})");

            if (!IsInRange(fps, 15, 60))
                errors.Add($"FPS 15-60 arasında olmalı (şu an: {fps})");

            if (!IsInRange(bitrate, 500, 50000))
                errors.Add($"Bitrate 500-50000 kbps arasında olmalı (şu an: {bitrate})");

            // Aspect ratio kontrolü
            var aspectRatio = (double)width / height;
            if (!IsInRange(aspectRatio, 1.0, 2.5))
                errors.Add($"En boy oranı desteklenmiyor: {aspectRatio:F2}");

            return new ValidationResult
            {
                IsValid = errors.Count == 0,
                Errors = errors
            };
        }

        /// <summary>
        /// Audio ayarlarını doğrula
        /// </summary>
        public static ValidationResult ValidateAudioSettings(int bitrate, int sampleRate, int channels)
        {
            var errors = new List<string>();

            var validBitrates = new[] { 64, 96, 128, 160, 192, 256, 320 };
            if (!validBitrates.Contains(bitrate))
                errors.Add($"Audio bitrate geçersiz: {bitrate}. Geçerli değerler: {string.Join(", ", validBitrates)}");

            var validSampleRates = new[] { 44100, 48000 };
            if (!validSampleRates.Contains(sampleRate))
                errors.Add($"Sample rate geçersiz: {sampleRate}. Geçerli değerler: {string.Join(", ", validSampleRates)}");

            if (!IsInRange(channels, 1, 2))
                errors.Add($"Kanal sayısı 1 veya 2 olmalı (şu an: {channels})");

            return new ValidationResult
            {
                IsValid = errors.Count == 0,
                Errors = errors
            };
        }

        /// <summary>
        /// Stream hedefini doğrula
        /// </summary>
        public static ValidationResult ValidateStreamTarget(string? name, string? rtmpUrl, string? streamKey)
        {
            var errors = new List<string>();

            if (!IsNotEmpty(name))
                errors.Add("Platform adı boş olamaz");

            if (!IsValidRtmpUrl(rtmpUrl))
                errors.Add("RTMP URL geçersiz. rtmp:// veya rtmps:// ile başlamalı");

            if (!IsValidStreamKey(streamKey))
                errors.Add("Stream key en az 8 karakter olmalı");

            return new ValidationResult
            {
                IsValid = errors.Count == 0,
                Errors = errors
            };
        }

        #endregion

        #region Platform Specific Validation

        /// <summary>
        /// YouTube channel ID doğrula
        /// </summary>
        public static bool IsValidYouTubeChannelId(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            // UC + 22 karakter
            return Regex.IsMatch(value, @"^UC[\w-]{22}$");
        }

        /// <summary>
        /// Twitch username doğrula
        /// </summary>
        public static bool IsValidTwitchUsername(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            // 4-25 karakter, alfanumerik ve underscore
            return Regex.IsMatch(value, @"^[a-zA-Z0-9_]{4,25}$");
        }

        /// <summary>
        /// TikTok username doğrula
        /// </summary>
        public static bool IsValidTikTokUsername(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            // @ opsiyonel, 2-24 karakter
            var cleaned = value.TrimStart('@');
            return Regex.IsMatch(cleaned, @"^[\w.]{2,24}$");
        }

        /// <summary>
        /// Instagram username doğrula
        /// </summary>
        public static bool IsValidInstagramUsername(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            // 1-30 karakter, alfanumerik, nokta ve underscore
            var cleaned = value.TrimStart('@');
            return Regex.IsMatch(cleaned, @"^[a-zA-Z0-9_.]{1,30}$");
        }

        #endregion
    }

    #region Fluent Validator

    public class Validator<T>
    {
        private readonly T _value;
        private readonly List<string> _errors = new();
        private string _propertyName = "Value";

        public Validator(T value)
        {
            _value = value;
        }

        public Validator<T> Named(string name)
        {
            _propertyName = name;
            return this;
        }

        public Validator<T> NotNull()
        {
            if (_value == null)
                _errors.Add($"{_propertyName} null olamaz");
            return this;
        }

        public Validator<T> NotEmpty()
        {
            if (_value is string str && string.IsNullOrWhiteSpace(str))
                _errors.Add($"{_propertyName} boş olamaz");
            return this;
        }

        public Validator<T> MinLength(int min)
        {
            if (_value is string str && str.Length < min)
                _errors.Add($"{_propertyName} en az {min} karakter olmalı");
            return this;
        }

        public Validator<T> MaxLength(int max)
        {
            if (_value is string str && str.Length > max)
                _errors.Add($"{_propertyName} en fazla {max} karakter olabilir");
            return this;
        }

        public Validator<T> InRange(int min, int max)
        {
            if (_value is int num && (num < min || num > max))
                _errors.Add($"{_propertyName} {min}-{max} arasında olmalı");
            return this;
        }

        public Validator<T> InRange(double min, double max)
        {
            if (_value is double num && (num < min || num > max))
                _errors.Add($"{_propertyName} {min}-{max} arasında olmalı");
            return this;
        }

        public Validator<T> Matches(string pattern, string? message = null)
        {
            if (_value is string str && !Regex.IsMatch(str, pattern))
                _errors.Add(message ?? $"{_propertyName} geçersiz format");
            return this;
        }

        public Validator<T> IsEmail()
        {
            if (_value is string str && !InputValidator.IsValidEmail(str))
                _errors.Add($"{_propertyName} geçerli bir email adresi olmalı");
            return this;
        }

        public Validator<T> IsUrl()
        {
            if (_value is string str && !InputValidator.IsValidUrl(str))
                _errors.Add($"{_propertyName} geçerli bir URL olmalı");
            return this;
        }

        public Validator<T> IsRtmpUrl()
        {
            if (_value is string str && !InputValidator.IsValidRtmpUrl(str))
                _errors.Add($"{_propertyName} geçerli bir RTMP URL olmalı");
            return this;
        }

        public Validator<T> Must(Func<T, bool> predicate, string message)
        {
            if (!predicate(_value))
                _errors.Add(message);
            return this;
        }

        public ValidationResult Validate()
        {
            return new ValidationResult
            {
                IsValid = _errors.Count == 0,
                Errors = _errors
            };
        }

        public void ValidateAndThrow()
        {
            if (_errors.Count > 0)
                throw new ValidationException(string.Join("; ", _errors));
        }

        public bool IsValid => _errors.Count == 0;
        public IReadOnlyList<string> Errors => _errors;
    }

    #endregion

    #region Result Types

    public class ValidationResult
    {
        public bool IsValid { get; init; }
        public List<string> Errors { get; init; } = new();

        public static ValidationResult Success() => new() { IsValid = true };

        public static ValidationResult Failure(params string[] errors) => new()
        {
            IsValid = false,
            Errors = errors.ToList()
        };

        public string ErrorMessage => string.Join("; ", Errors);

        public void ThrowIfInvalid()
        {
            if (!IsValid)
                throw new ValidationException(ErrorMessage);
        }
    }

    public class ValidationException : Exception
    {
        public ValidationException(string message) : base(message) { }
    }

    #endregion

    #region Model Validation

    /// <summary>
    /// DataAnnotations ile model validation
    /// </summary>
    public static class ModelValidator
    {
        public static ValidationResult Validate<T>(T model) where T : class
        {
            var context = new ValidationContext(model);
            var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();

            var isValid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
                model, context, results, validateAllProperties: true);

            return new ValidationResult
            {
                IsValid = isValid,
                Errors = results.Select(r => r.ErrorMessage ?? "Validation error").ToList()
            };
        }

        public static void ValidateAndThrow<T>(T model) where T : class
        {
            var result = Validate(model);
            result.ThrowIfInvalid();
        }
    }

    #endregion

    #region Custom Validation Attributes

    /// <summary>
    /// RTMP URL validation attribute
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class RtmpUrlAttribute : ValidationAttribute
    {
        protected override System.ComponentModel.DataAnnotations.ValidationResult? IsValid(
            object? value, ValidationContext validationContext)
        {
            if (value is string str && !InputValidator.IsValidRtmpUrl(str))
            {
                return new System.ComponentModel.DataAnnotations.ValidationResult(
                    "Geçerli bir RTMP URL giriniz");
            }
            return System.ComponentModel.DataAnnotations.ValidationResult.Success;
        }
    }

    /// <summary>
    /// Stream key validation attribute
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class StreamKeyAttribute : ValidationAttribute
    {
        public int MinLength { get; set; } = 8;

        protected override System.ComponentModel.DataAnnotations.ValidationResult? IsValid(
            object? value, ValidationContext validationContext)
        {
            if (value is string str && str.Length < MinLength)
            {
                return new System.ComponentModel.DataAnnotations.ValidationResult(
                    $"Stream key en az {MinLength} karakter olmalı");
            }
            return System.ComponentModel.DataAnnotations.ValidationResult.Success;
        }
    }

    /// <summary>
    /// Bitrate range validation attribute
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class BitrateRangeAttribute : ValidationAttribute
    {
        public int Min { get; set; } = 500;
        public int Max { get; set; } = 50000;

        protected override System.ComponentModel.DataAnnotations.ValidationResult? IsValid(
            object? value, ValidationContext validationContext)
        {
            if (value is int bitrate && (bitrate < Min || bitrate > Max))
            {
                return new System.ComponentModel.DataAnnotations.ValidationResult(
                    $"Bitrate {Min}-{Max} arasında olmalı");
            }
            return System.ComponentModel.DataAnnotations.ValidationResult.Success;
        }
    }

    #endregion
}
