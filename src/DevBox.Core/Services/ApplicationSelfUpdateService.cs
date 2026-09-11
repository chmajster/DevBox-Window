using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text.Json;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class ApplicationSelfUpdateService : IDisposable
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/chmajster/DevBox-Window/releases/latest";
    private readonly string _rootPath;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public ApplicationSelfUpdateService(string rootPath, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DevBox-Windows-Updater", "1.0"));
    }

    public async Task<SelfUpdatePackage> DownloadLatestInstallerAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(currentVersion);

        using var releaseResponse = await _httpClient.GetAsync(LatestReleaseUrl, cancellationToken).ConfigureAwait(false);
        releaseResponse.EnsureSuccessStatusCode();
        await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(releaseStream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var root = document.RootElement;
        var version = ApplicationUpdateService.ParseReleaseVersion(root.GetProperty("tag_name").GetString());
        if (version <= currentVersion)
            throw new InvalidOperationException("No newer stable DevBox release is available.");

        var releaseUrl = ValidateGitHubUrl(root.GetProperty("html_url").GetString(), "release URL");
        var expectedInstallerName = GetExpectedInstallerAssetName(version);

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub release does not contain an assets array.");

        string? installerUrl = null;
        string? checksumsUrl = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
            if (name is null || url is null)
                continue;

            if (name.Equals(expectedInstallerName, StringComparison.OrdinalIgnoreCase))
                installerUrl = ValidateGitHubUrl(url, "installer download URL");
            else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                checksumsUrl = ValidateGitHubUrl(url, "checksum download URL");
        }

        if (installerUrl is null)
            throw new InvalidDataException($"Release is missing expected installer '{expectedInstallerName}'.");
        if (checksumsUrl is null)
            throw new InvalidDataException("Release is missing SHA256SUMS.txt.");

        var checksums = await _httpClient.GetStringAsync(checksumsUrl, cancellationToken).ConfigureAwait(false);
        var expectedSha256 = ParseChecksum(checksums, expectedInstallerName);

        var updateDirectory = Path.Combine(_rootPath, "tmp", "updates", version.ToString(3));
        Directory.CreateDirectory(updateDirectory);
        var installerPath = Path.Combine(updateDirectory, expectedInstallerName);
        var temporaryPath = installerPath + $".{Guid.NewGuid():N}.download";

        try
        {
            using var response = await _httpClient.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);

            VerifySha256(temporaryPath, expectedSha256);
            VerifyAuthenticodeSignature(temporaryPath);
            File.Move(temporaryPath, installerPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        return new SelfUpdatePackage(version, installerPath, expectedInstallerName, expectedSha256, releaseUrl);
    }

    internal static string GetExpectedInstallerAssetName(Version version, Architecture? architecture = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        var effectiveArchitecture = architecture ?? RuntimeInformation.ProcessArchitecture;
        var rid = effectiveArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException($"DevBox self-update does not support {effectiveArchitecture}.")
        };
        return $"DevBox-{version.ToString(3)}-{rid}-setup.exe";
    }

    internal static string ParseChecksum(string checksumFile, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        foreach (var rawLine in checksumFile.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = rawLine.IndexOfAny([' ', '\t']);
            if (separator <= 0)
                continue;
            var hash = rawLine[..separator].Trim();
            var listedName = rawLine[separator..].Trim().TrimStart('*');
            if (!listedName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                throw new InvalidDataException($"SHA256SUMS.txt contains an invalid hash for '{fileName}'.");
            return hash.ToLowerInvariant();
        }
        throw new InvalidDataException($"SHA256SUMS.txt does not contain '{fileName}'.");
    }

    internal static void VerifySha256(string path, string expectedSha256)
    {
        var expected = Convert.FromHexString(expectedSha256);
        using var stream = File.OpenRead(path);
        var actual = SHA256.HashData(stream);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new InvalidDataException("Downloaded DevBox installer failed SHA-256 verification.");
    }

    internal static void VerifyAuthenticodeSignature(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Authenticode verification requires Windows.");
        VerifyWinTrust(path);

        var currentPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath))
            throw new InvalidDataException("DevBox could not determine the running executable for publisher verification.");
        VerifyWinTrust(currentPath);

        using var downloadedSigner = GetSignerCertificate(path);
        using var currentSigner = GetSignerCertificate(currentPath);
        if (!downloadedSigner.SubjectName.RawData.AsSpan().SequenceEqual(currentSigner.SubjectName.RawData))
            throw new InvalidDataException(
                $"Downloaded installer publisher '{downloadedSigner.Subject}' does not match the running DevBox publisher '{currentSigner.Subject}'.");
    }

    private static X509Certificate2 GetSignerCertificate(string path)
    {
        try
        {
            using var certificate = X509Certificate.CreateFromSignedFile(path);
            return new X509Certificate2(certificate);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException($"Unable to read the Authenticode signer certificate from '{Path.GetFileName(path)}'.", ex);
        }
    }

    private static void VerifyWinTrust(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Signed file was not found for Authenticode verification.", path);

        var filePathPtr = Marshal.StringToCoTaskMemUni(Path.GetFullPath(path));
        var fileInfo = new WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = filePathPtr
        };
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);
        var trustData = new WinTrustData
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
            UiChoice = 2,
            RevocationChecks = 1,
            UnionChoice = 1,
            FileInfo = fileInfoPtr,
            StateAction = 1,
            ProviderFlags = 0
        };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            var status = WinVerifyTrust(IntPtr.Zero, action, ref trustData);
            if (status != 0)
                throw new InvalidDataException($"DevBox file does not have a valid trusted Authenticode signature (0x{status:X8}).");
        }
        finally
        {
            trustData.StateAction = 2;
            _ = WinVerifyTrust(IntPtr.Zero, action, ref trustData);
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPtr);
            Marshal.FreeHGlobal(fileInfoPtr);
            Marshal.FreeCoTaskMem(filePathPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true, PreserveSig = true)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid actionId, ref WinTrustData trustData);

    private static string ValidateGitHubUrl(string? value, string description)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"GitHub release contains an invalid {description}.");
        return uri.AbsoluteUri;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
