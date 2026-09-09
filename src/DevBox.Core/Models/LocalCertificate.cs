namespace DevBox.Core.Models;

public sealed record LocalCertificate(
    string Domain,
    string CertificatePath,
    string PrivateKeyPath,
    string Thumbprint,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter);
