using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace HSHONAD_Launcher
{
    public sealed record HostsFileEntry(int LineNumber, string Line);

    public sealed record HostsFileInspection(
        string FilePath,
        IReadOnlyList<HostsFileEntry> Entries);

    public sealed record HostsFileRepairResult(
        string FilePath,
        int RemovedHostNames,
        string? BackupPath,
        bool Changed);

    /// <summary>
    /// Inspects and safely removes explicit hosts-file overrides for the HSH API
    /// names. It does not redirect these names to localhost or another IP.
    /// </summary>
    public static class HostsFileManager
    {
        // HOSTS_FILE: Windows hosts file; the Unix path is retained for completeness.
        private static readonly string HOSTS_FILE = OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "drivers", "etc", "hosts")
            : "/etc/hosts";

        // TARGET_DOMAINS: only these exact host names are managed by this feature.
        private static readonly string[] TARGET_DOMAINS =
        {
            "api.homesweethomegame.com",
            "hshsapi.homesweethomegame.com",
            "logapi.homesweethomegame.com",
            "boatltpk"
        };

        private static readonly HashSet<string> TargetDomainSet =
            new HashSet<string>(
                TARGET_DOMAINS.Select(NormalizeHost),
                StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<string> TargetDomains =>
            Array.AsReadOnly(TARGET_DOMAINS);

        public static HostsFileInspection Inspect()
        {
            var (content, _) = ReadHostsFile();
            string[] lines = SplitLines(content);
            var matches = new List<HostsFileEntry>();

            for (int i = 0; i < lines.Length; i++)
            {
                if (ContainsTargetDomain(lines[i]))
                    matches.Add(new HostsFileEntry(i + 1, lines[i]));
            }

            return new HostsFileInspection(HOSTS_FILE, matches);
        }

        /// <summary>
        /// Removes only un-commented host aliases that exactly match TARGET_DOMAINS.
        /// Other aliases on a shared line, comments, and all unrelated lines are kept.
        /// A timestamped backup is created before any write.
        /// </summary>
        public static HostsFileRepairResult RemoveOverrides()
        {
            var (content, encoding) = ReadHostsFile();
            string newline = content.Contains("\r\n", StringComparison.Ordinal)
                ? "\r\n"
                : "\n";
            bool hadFinalNewline =
                content.EndsWith("\n", StringComparison.Ordinal) ||
                content.EndsWith("\r", StringComparison.Ordinal);

            string[] sourceLines = SplitLines(content);
            var outputLines = new List<string>(sourceLines.Length);
            int removedCount = 0;

            foreach (string line in sourceLines)
            {
                int removedFromLine = RemoveTargetsFromLine(line, out string? updatedLine);
                removedCount += removedFromLine;

                if (removedFromLine == 0)
                {
                    outputLines.Add(line);
                }
                else if (updatedLine != null)
                {
                    outputLines.Add(updatedLine);
                }
            }

            if (removedCount == 0)
            {
                return new HostsFileRepairResult(
                    HOSTS_FILE, 0, null, false);
            }

            string updatedContent = string.Join(newline, outputLines);
            if (hadFinalNewline && !updatedContent.EndsWith(newline, StringComparison.Ordinal))
                updatedContent += newline;

            string backupPath = HOSTS_FILE + ".hshonad-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + ".bak";
            string tempPath = HOSTS_FILE + ".hshonad-" +
                Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                File.Copy(HOSTS_FILE, backupPath, overwrite: false);
                File.WriteAllText(tempPath, updatedContent, encoding);
                File.Move(tempPath, HOSTS_FILE, overwrite: true);
            }
            catch
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch { }
                throw;
            }

            return new HostsFileRepairResult(
                HOSTS_FILE, removedCount, backupPath, true);
        }

        private static (string Content, Encoding Encoding) ReadHostsFile()
        {
            if (!File.Exists(HOSTS_FILE))
                throw new FileNotFoundException(
                    "ไม่พบไฟล์ Hosts ของระบบ", HOSTS_FILE);

            using var reader = new StreamReader(
                HOSTS_FILE, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                detectEncodingFromByteOrderMarks: true);

            string content = reader.ReadToEnd();
            return (content, reader.CurrentEncoding);
        }

        private static string[] SplitLines(string content) =>
            content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        private static bool ContainsTargetDomain(string line)
        {
            if (string.IsNullOrWhiteSpace(line) ||
                line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                return false;

            int commentIndex = line.IndexOf('#');
            string data = commentIndex >= 0 ? line[..commentIndex] : line;
            string[] tokens = data.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            return tokens.Skip(1).Any(host =>
                TargetDomainSet.Contains(NormalizeHost(host)));
        }

        private static int RemoveTargetsFromLine(
            string line, out string? updatedLine)
        {
            updatedLine = line;

            if (string.IsNullOrWhiteSpace(line) ||
                line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                return 0;

            int commentIndex = line.IndexOf('#');
            string data = commentIndex >= 0 ? line[..commentIndex] : line;
            string comment = commentIndex >= 0 ? line[commentIndex..] : "";
            string[] tokens = data.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length < 2)
                return 0;

            var keptHosts = new List<string>();
            int removed = 0;

            for (int i = 1; i < tokens.Length; i++)
            {
                if (TargetDomainSet.Contains(NormalizeHost(tokens[i])))
                    removed++;
                else
                    keptHosts.Add(tokens[i]);
            }

            if (removed == 0)
                return 0;

            if (keptHosts.Count == 0)
            {
                // Preserve an inline comment rather than discarding user notes.
                updatedLine = comment.Length > 0 ? comment.TrimStart() : null;
                return removed;
            }

            string indent = line[..(line.Length - line.TrimStart().Length)];
            string rebuilt = indent + tokens[0] + "\t" + string.Join(" ", keptHosts);

            if (comment.Length > 0)
                rebuilt += " " + comment.TrimStart();

            updatedLine = rebuilt;
            return removed;
        }

        private static string NormalizeHost(string host) =>
            host.Trim().TrimEnd('.').ToLowerInvariant();
    }
}
