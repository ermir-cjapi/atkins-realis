using System.ComponentModel.DataAnnotations;

namespace Orb.Automate.Agents.HR.Api.Options;

/// <summary>Configuration for Assist | HR business telemetry classification.</summary>
public sealed class TelemetryOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Telemetry";

    /// <summary>Gets the classifier taxonomy version.</summary>
    [Required]
    public string TaxonomyVersion { get; init; } = "1.1";
}
