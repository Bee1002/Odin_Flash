using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Odin_Flash.Util
{
    public enum FirmwarePackageSlot
    {
        Unknown,
        BL,
        AP,
        CP,
        CSC
    }

    /// <summary>Clasifica archivos .tar / .tar.md5 Samsung por nombre de archivo y carpetas del path.</summary>
    public static class FirmwarePackageClassifier
    {
        public static bool IsFirmwareArchive(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;

            if (path.EndsWith(".tar.md5", StringComparison.OrdinalIgnoreCase))
                return true;

            var ext = Path.GetExtension(path);
            return ext.Equals(".tar", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".md5", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Archivo + carpetas padre (ej. …/AP/firmware.tar.md5 o …/_AP_….tar.md5).</summary>
        public static FirmwarePackageSlot Classify(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return FirmwarePackageSlot.Unknown;

            var fromFile = ClassifyFileName(Path.GetFileName(filePath));
            if (fromFile != FirmwarePackageSlot.Unknown)
                return fromFile;

            var dir = Path.GetDirectoryName(filePath);
            while (!string.IsNullOrEmpty(dir))
            {
                var fromFolder = ClassifyFolderName(Path.GetFileName(dir));
                if (fromFolder != FirmwarePackageSlot.Unknown)
                    return fromFolder;

                dir = Path.GetDirectoryName(dir);
            }

            return FirmwarePackageSlot.Unknown;
        }

        private static FirmwarePackageSlot ClassifyFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return FirmwarePackageSlot.Unknown;

            var name = fileName.ToUpperInvariant();

            // AP antes que BL: algunos nombres combinados incluyen ambos tokens.
            if (ContainsToken(name, "_AP_", "_PDA_") || EndsWithSlotPrefix(name, "AP"))
                return FirmwarePackageSlot.AP;

            if (ContainsToken(name, "_BL_", "_BOOTLOADER_") || EndsWithSlotPrefix(name, "BL"))
                return FirmwarePackageSlot.BL;

            if (ContainsToken(name, "_CP_", "_MODEM_", "_PHONE_") || EndsWithSlotPrefix(name, "CP"))
                return FirmwarePackageSlot.CP;

            if (ContainsToken(name, "_CSC_", "_HOME_CSC_") || EndsWithSlotPrefix(name, "CSC"))
                return FirmwarePackageSlot.CSC;

            return FirmwarePackageSlot.Unknown;
        }

        /// <summary>True si el nombre es variante HOME_CSC (conserva userdata; no usar en flash de taller).</summary>
        public static bool IsHomeCscPackage(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            var name = Path.GetFileName(filePath).ToUpperInvariant();
            return name.IndexOf("HOME_CSC", StringComparison.Ordinal) >= 0;
        }

        /// <summary>Un TAR por ranura; en CSC prefiere CSC_ sobre HOME_CSC_ si vienen ambos.</summary>
        public static IEnumerable<string> SelectPreferredPathPerSlot(IEnumerable<string> paths)
        {
            if (paths == null)
                yield break;

            var groups = paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .GroupBy(Classify);

            foreach (var group in groups)
            {
                if (group.Key == FirmwarePackageSlot.Unknown)
                    continue;

                var chosen = PickPreferredForSlot(group.Key, group);
                if (chosen != null)
                    yield return chosen;
            }
        }

        private static string PickPreferredForSlot(FirmwarePackageSlot slot, IEnumerable<string> candidates)
        {
            var list = candidates.ToList();
            if (list.Count == 0)
                return null;
            if (list.Count == 1)
                return list[0];

            if (slot == FirmwarePackageSlot.CSC)
            {
                var standardCsc = list.Where(p => !IsHomeCscPackage(p)).ToList();
                if (standardCsc.Count > 0)
                    return standardCsc.OrderByDescending(SafeFileLength).First();
            }

            return list.OrderByDescending(SafeFileLength).First();
        }

        private static long SafeFileLength(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0L;
            }
        }

        private static FirmwarePackageSlot ClassifyFolderName(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName))
                return FirmwarePackageSlot.Unknown;

            var token = folderName.Trim().ToUpperInvariant();

            if (token == "AP" || token == "PDA" || token.Contains("AP_") || token.EndsWith("_AP", StringComparison.Ordinal))
                return FirmwarePackageSlot.AP;

            if (token == "BL" || token == "BOOTLOADER" || token.Contains("BOOTLOADER") || token.EndsWith("_BL", StringComparison.Ordinal))
                return FirmwarePackageSlot.BL;

            if (token == "CP" || token == "MODEM" || token == "PHONE" || token.Contains("MODEM"))
                return FirmwarePackageSlot.CP;

            if (token == "CSC" || token == "HOME_CSC" || token.Contains("HOME_CSC") || token.Contains("_CSC"))
                return FirmwarePackageSlot.CSC;

            return FirmwarePackageSlot.Unknown;
        }

        private static bool ContainsToken(string name, params string[] tokens)
        {
            foreach (var token in tokens)
            {
                if (name.IndexOf(token, StringComparison.Ordinal) >= 0)
                    return true;
            }

            return false;
        }

        /// <summary>Coincide con BL.tar.md5, AP.tar, CSC_firmware.tar.md5, etc.</summary>
        private static bool EndsWithSlotPrefix(string upperFileName, string slotPrefix)
        {
            if (!upperFileName.StartsWith(slotPrefix, StringComparison.Ordinal))
                return false;

            if (upperFileName.Length == slotPrefix.Length)
                return true;

            var next = upperFileName[slotPrefix.Length];
            return next == '.' || next == '_' || next == '-';
        }
    }
}
