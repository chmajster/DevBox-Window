from pathlib import Path


def replace(path, old, new):
    target = Path(path)
    text = target.read_text(encoding='utf-8')
    if text.count(old) != 1:
        raise RuntimeError(f'Expected one exact patch anchor in {path}')
    target.write_text(text.replace(old, new), encoding='utf-8', newline='\n')

path = 'src/DevBox.Core/Services/ArchiveSafety.cs'
replace(path, '                long entryBytes = 0;\n                int read;', '                long entryBytes = 0;\n                uint crc32 = uint.MaxValue;\n                int read;')
replace(path, '                    target.Write(buffer, 0, read);', '                    crc32 = UpdateCrc32(crc32, buffer.AsSpan(0, read));\n                    target.Write(buffer, 0, read);')
replace(path, '                    throw new InvalidDataException($"{packageName} archive contains a truncated entry: {entry.FullName}");', '                    throw new InvalidDataException($"{packageName} archive contains a truncated entry: {entry.FullName}");\n                // Some framework versions cap reads at the declared size. CRC32\n                // also detects silently truncated or otherwise corrupted payloads.\n                if (~crc32 != entry.Crc32)\n                    throw new InvalidDataException($"{packageName} archive entry failed CRC32 validation: {entry.FullName}");')
replace(path, '    private static async Task CopyToWithLimitAsync(', '''    private static readonly uint[] Crc32Table = CreateCrc32Table();

    private static uint[] CreateCrc32Table()
    {
        var table = new uint[256];
        for (var index = 0; index < table.Length; index++)
        {
            var value = (uint)index;
            for (var bit = 0; bit < 8; bit++)
                value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320u : 0u);
            table[index] = value;
        }
        return table;
    }

    private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
            crc = Crc32Table[(byte)(crc ^ value)] ^ (crc >> 8);
        return crc;
    }

    private static async Task CopyToWithLimitAsync(''')

path = 'src/DevBox.Core/Services/LogReader.cs'
replace(path, '            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)', '            .Where(IsRegularLogFile)')
replace(path, '    public IReadOnlyList<string> ReadTail(', '''    internal static bool IsRegularLogFile(string path)
    {
        try
        {
            return (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0;
        }
        // A process may rotate a log between enumeration and attribute lookup.
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public IReadOnlyList<string> ReadTail(''')
replace('CHANGELOG.md', '- ZIP extraction enforces actual decompressed-byte limits before every write and rejects entries whose streamed size differs from their declared size.', '- ZIP extraction enforces actual decompressed-byte limits before every write, rejects mismatched sizes, and verifies CRC32 to detect corrupted or silently truncated entries even when the framework caps reads at the declared length.')
print('Applied ZIP checksum validation and rotation-safe log filtering.')
