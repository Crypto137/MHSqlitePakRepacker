using Dapper;
using K4os.Compression.LZ4;
using System.Data.SQLite;

namespace MHSqlitePakRepacker
{
    public static class PakRepacker
    {
        private const string CalligraphyPakFileName = "Calligraphy.sip";
        private const string ResourcePakFileName = "mu_cdata.sip";

        private static readonly byte[] CompressionBuffer = new byte[1024 * 1024 * 8];

        public static bool Repack(string filePath, string outputDirectory)
        {
            if (File.Exists(filePath) == false)
            {
                Console.WriteLine($"{filePath} not found");
                return false;
            }

            // Read and convert
            Console.WriteLine($"Reading SQLite pak {Path.GetFileName(filePath)}...");

            if (ReadSqlitePak(filePath, out PakFile calligraphyPak, out PakFile resourcePak) == false)
            {
                Console.WriteLine("Failed to read SQLite pak");
                return false;
            }

            Console.WriteLine("Finished reading SQLite pak");

            // Write converted paks
            Console.WriteLine($"Writing converted paks to {outputDirectory}...");

            if (WritePaks(outputDirectory, calligraphyPak, resourcePak) == false)
            {
                Console.WriteLine("Failed to write converted paks");
                return false;
            }

            Console.WriteLine("Finished writing converted paks");
            return true;
        }

        private static bool ReadSqlitePak(string filePath, out PakFile calligraphyPak, out PakFile resourcePak)
        {
            calligraphyPak = new();
            resourcePak = new();

            if (File.Exists(filePath) == false)
                return false;

            uint pakHash = Djb2(File.OpenRead(filePath));
            Console.WriteLine($"SQLite pak hash = 0x{pakHash:X8}");

            try
            {
                using SQLiteConnection connection = new($"Data Source={filePath}");

                SqliteVersion version = connection.QueryFirst<SqliteVersion>("SELECT v as Version, s as Description FROM ver");
                if (version == null)
                {
                    Console.WriteLine("Failed to read SQLite pak version");
                    return false;
                }

                switch (version.Version)
                {
                    case 1.5f:
                    case 1.6f:
                        Console.WriteLine($"Found known SQLite pak version: {version}");
                        break;

                    default:
                        Console.WriteLine($"Found unknown SQLite pak version: {version}\nPlease report this to the developers of this tool.");
                        return false;
                }

                IEnumerable<SqlitePakEntry> entries = connection.Query<SqlitePakEntry>("SELECT i as Id, n as Name, b as Blob, l as Length, s as SavedTime FROM data_tbl");
                foreach (SqlitePakEntry entry in entries)
                {
                    PostProcessEntry(entry, pakHash, version);

                    PakFile pakFile = entry.Name.StartsWith("Calligraphy", StringComparison.Ordinal) ? calligraphyPak : resourcePak;
                    pakFile.AddEntry((ulong)entry.Id, entry.Name, entry.Blob, entry.Length, entry.SavedTime);
                }

                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
                return false;
            }
        }

        private static bool WritePaks(string outputDirectory, PakFile calligraphyPak, PakFile resourcePak)
        {
            if (Directory.Exists(outputDirectory) == false)
                Directory.CreateDirectory(outputDirectory);

            if (WritePak(outputDirectory, CalligraphyPakFileName, calligraphyPak) == false)
                return false;

            if (WritePak(outputDirectory, ResourcePakFileName, resourcePak) == false)
                return false;

            return true;
        }

        private static bool WritePak(string outputDirectory, string fileName, PakFile pakFile)
        {
            Console.WriteLine($"Writing {fileName} ({pakFile.EntryCount} entries)...");
            string filePath = Path.Combine(outputDirectory, fileName);

            try
            {
                using FileStream fs = File.OpenWrite(filePath);
                pakFile.WriteToStream(fs);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
                Console.WriteLine($"Failed to write {fileName}");
                return false;
            }

            return true;
        }

        private static void PostProcessEntry(SqlitePakEntry entry, uint pakHash, SqliteVersion pakVersion)
        {
            // Apply fix to Mods/EnemyBoosts/Sets/EndGameNoAnimAffixes.prototype for version 1.10.0.643
            if (pakHash == 0xB5D5C89E && entry.Id == 3858647238819518931)
            {
                // Remove the extra 0x0D byte at index 4 to fix corrupted Calligraphy prototype file header
                byte[] header = [0x50, 0x54, 0x50, 0x0A];
                int payloadLength = entry.Blob.Length - 5;

                byte[] fixedBlob = new byte[header.Length + payloadLength];
                Array.Copy(header, fixedBlob, 4);
                Array.Copy(entry.Blob, 5, fixedBlob, 4, payloadLength);

                entry.Blob = fixedBlob;
                entry.Length--;

                Console.WriteLine("Applied fix to Calligraphy/Mods/EnemyBoosts/Sets/EndGameNoAnimAffixes.prototype for version 1.10.0.643");
            }

            // Older SQLite paks are uncompressed
            if (pakVersion.Version < 1.6f)
            {
                int compressedSize = LZ4Codec.Encode(entry.Blob, CompressionBuffer);
                entry.Blob = CompressionBuffer.AsSpan(0, compressedSize).ToArray();
            }
        }

        private static uint Djb2(Stream stream)
        {
            uint hash = 5381;

            int b;
            while ((b = stream.ReadByte()) != -1)
                hash = (hash << 5) + hash + (uint)b;

            return hash;
        }
    }
}
