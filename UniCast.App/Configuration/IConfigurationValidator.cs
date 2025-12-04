namespace UniCast.App.Configuration
{
    /// <summary>
    /// Configuration validation service interface.
    /// Validates application settings on startup.
    /// </summary>
    public interface IConfigurationValidator
    {
        /// <summary>
        /// Add a validation rule
        /// </summary>
        /// <param name="rule">Rule to add</param>
        void AddRule(IConfigurationRule rule);

        /// <summary>
        /// Validate all rules
        /// </summary>
        /// <returns>Validation result with errors and warnings</returns>
        ValidationResult Validate();

        /// <summary>
        /// Validate and throw if critical errors exist
        /// </summary>
        void ValidateOrThrow();
    }
}