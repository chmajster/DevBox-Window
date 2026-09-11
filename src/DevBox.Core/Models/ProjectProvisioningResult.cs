namespace DevBox.Core.Models;

public sealed record ProjectProvisioningResult(
    SiteDefinition Site,
    DevBoxProjectManifest Manifest,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> Warnings,
    bool DatabaseCreated = false);
