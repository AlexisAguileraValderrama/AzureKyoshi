using System.ComponentModel.DataAnnotations;

namespace AzureKyoshi.Shared.Models;

public sealed record FolderDto(
    Guid Id,
    string Name,
    string ColorHex,
    string? CertificationCode,
    DateTime CreatedAtUtc,
    int ModuleCount);

public sealed class CreateFolderRequest
{
    [Required, StringLength(80, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [StringLength(20)]
    public string ColorHex { get; set; } = "#0078D4";

    [StringLength(20)]
    public string? CertificationCode { get; set; }
}

public sealed class UpdateFolderRequest
{
    [Required, StringLength(80, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [StringLength(20)]
    public string ColorHex { get; set; } = "#0078D4";

    [StringLength(20)]
    public string? CertificationCode { get; set; }
}
